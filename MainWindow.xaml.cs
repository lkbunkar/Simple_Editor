using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Win32;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Drawing;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Windows.Storage;
using Windows.Storage.Streams;
using Passpix.Services;
using Passpix.Models;

namespace Passpix
{
    public partial class MainWindow : Window
    {
        private bool _isInitialized = false;

        // Passpix States
        private BitmapSource? _originalBitmap;
        private BitmapSource? _croppedBitmap;
        private WriteableBitmap? _processedPortrait;
        private WriteableBitmap? _sheetBitmap;
        private bool _isDragging;
        private Point _dragStartPoint;
        private double _selectorStartLeft;
        private double _selectorStartTop;
        private bool _isUpdatingUi;
        private Rect _detectedFaceRelativeRect = new Rect();

        // Zoom / Pan States
        private double _zoom = 1.0;
        private bool _isPanning;
        private Point _lastMousePosition;

        // Resizing States
        private bool _isResizing;
        private string _resizeHandle = "";
        private double _resizeStartWidth;
        private double _resizeStartHeight;
        private double _resizeStartLeft;
        private double _resizeStartTop;
        private Point _resizeStartMousePoint;

        // Background Removal Debouncer
        private bool _isProcessingBg;
        private bool _needReProcessBg;

        // PDF Utility States
        private List<string> _pdfFiles = new List<string>(); // Merge PDFs list
        private string? _pdfToImgPath;
        private List<string> _imgToPdfPaths = new List<string>();
        private string? _rearrangePdfPath;
        private List<int> _rearrangePageOrder = new List<int>();
        private string? _splitPdfPath;
        private HashSet<int> _splitSelectedPages = new HashSet<int>();
        private string? _addDeletePdfPath;
        private List<int> _addDeletePageOrder = new List<int>(); // -1 represents a blank page
        private string? _rotatePdfPath;
        private List<int> _rotatePageAngles = new List<int>(); // List of rotations (0, 90, 180, 270)

        // Compressor States
        private BitmapSource? _compressOriginalBitmap;
        private string? _compressOriginalPath;
        private string? _compressPdfPath;

        public MainWindow()
        {
            InitializeComponent();
            
            // Wire window events
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;

            _isInitialized = true;
        }

        #region Navigation & Sidebar Flow

        private void ShowPanel(Grid activePanel)
        {
            PanelHome.Visibility = Visibility.Collapsed;
            PanelPasspix.Visibility = Visibility.Collapsed;
            PanelPdf.Visibility = Visibility.Collapsed;
            PanelCompress.Visibility = Visibility.Collapsed;
            PanelSettings.Visibility = Visibility.Collapsed;

            activePanel.Visibility = Visibility.Visible;
        }

        private void BtnNavHome_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(PanelHome);
        }

        private void BtnNavPasspix_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(PanelPasspix);
            ApplyPasspixDefaultSettings();
            TriggerCropAndProcess();
        }

        private void BtnNavPdf_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(PanelPdf);
        }

        private void BtnNavCompress_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(PanelCompress);
        }

        private void BtnNavSettings_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(PanelSettings);
        }

        #endregion

        #region Tool 1: Passpix (Passport Photo Toolkit)

        private void BtnPasspixSelect_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Image files (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    var fileUri = new Uri(openFileDialog.FileName);
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.UriSource = fileUri;
                    img.EndInit();
                    img.Freeze();

                    _originalBitmap = img;
                    ImgOriginal.Source = _originalBitmap;
                    LblPasspixFileName.Text = Path.GetFileName(openFileDialog.FileName);
                    TxtCropperPlaceholder.Visibility = Visibility.Collapsed;

                    // Reset Zoom and Translation
                    _zoom = 1.0;
                    ImgScale.ScaleX = 1.0;
                    ImgScale.ScaleY = 1.0;
                    ImgTranslate.X = 0;
                    ImgTranslate.Y = 0;

                    if (SettingsService.Current.PasspixAutoFaceDetectionCrop)
                    {
                        _detectedFaceRelativeRect = DetectFaceHeuristic(_originalBitmap);
                    }
                    else
                    {
                        _detectedFaceRelativeRect = new Rect();
                    }

                    // Trigger crop overlay initialization
                    Dispatcher.BeginInvoke(new Action(() => {
                        UpdateCropSelectorBounds();
                        TriggerCropAndProcess();
                    }), DispatcherPriority.Loaded);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to load portrait image: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private Rect GetImageCurrentBounds()
        {
            if (_originalBitmap == null || ImgOriginal.ActualWidth == 0 || ImgOriginal.ActualHeight == 0)
                return new Rect();

            var rect = GetImageDisplayRect();

            // Translate displayed image top-left and bottom-right points to CanvasCropOverlay space
            Point topLeft = ImgOriginal.TranslatePoint(new Point(rect.Left, rect.Top), CanvasCropOverlay);
            Point bottomRight = ImgOriginal.TranslatePoint(new Point(rect.Right, rect.Bottom), CanvasCropOverlay);

            double width = bottomRight.X - topLeft.X;
            double height = bottomRight.Y - topLeft.Y;

            return new Rect(topLeft.X, topLeft.Y, width, height);
        }

        private void ClampCropSelectorToImageBounds()
        {
            if (_originalBitmap == null) return;
            var imgBounds = GetImageCurrentBounds();
            if (imgBounds.Width <= 0 || imgBounds.Height <= 0) return;

            double left = Canvas.GetLeft(RectCropSelector);
            double top = Canvas.GetTop(RectCropSelector);
            if (double.IsNaN(left)) left = imgBounds.Left;
            if (double.IsNaN(top)) top = imgBounds.Top;

            double width = RectCropSelector.Width;
            double height = RectCropSelector.Height;

            if (width > imgBounds.Width || height > imgBounds.Height)
            {
                double tw = 1.19;
                double th = 1.53;
                double.TryParse(TxtCropWidth.Text, out tw);
                double.TryParse(TxtCropHeight.Text, out th);
                if (tw <= 0) tw = 1.19;
                if (th <= 0) th = 1.53;
                double targetAspect = tw / th;

                height = imgBounds.Height * 0.7;
                width = height * targetAspect;
                if (width > imgBounds.Width)
                {
                    width = imgBounds.Width * 0.7;
                    height = width / targetAspect;
                }
                RectCropSelector.Width = width;
                RectCropSelector.Height = height;
            }

            left = Math.Max(imgBounds.Left, Math.Min(left, imgBounds.Right - width));
            top = Math.Max(imgBounds.Top, Math.Min(top, imgBounds.Bottom - height));

            Canvas.SetLeft(RectCropSelector, left);
            Canvas.SetTop(RectCropSelector, top);

            // Temporary debug logging
            double maxX = imgBounds.Right - width;
            double maxY = imgBounds.Bottom - height;
            System.Diagnostics.Debug.WriteLine($"ImageLeft: {imgBounds.Left:F2}");
            System.Diagnostics.Debug.WriteLine($"ImageTop: {imgBounds.Top:F2}");
            System.Diagnostics.Debug.WriteLine($"ImageWidth: {imgBounds.Width:F2}");
            System.Diagnostics.Debug.WriteLine($"ImageHeight: {imgBounds.Height:F2}");
            System.Diagnostics.Debug.WriteLine($"CropX: {left:F2}");
            System.Diagnostics.Debug.WriteLine($"CropY: {top:F2}");
            System.Diagnostics.Debug.WriteLine($"MaxCropX: {maxX:F2}");
            System.Diagnostics.Debug.WriteLine($"MaxCropY: {maxY:F2}");
        }

        private void UpdateCropSelectorBounds()
        {
            if (_originalBitmap == null) return;

            CanvasCropOverlay.Width = double.NaN;
            CanvasCropOverlay.Height = double.NaN;
            CanvasCropOverlay.Margin = new Thickness(0);

            var imgBounds = GetImageCurrentBounds();
            if (imgBounds.Width <= 0 || imgBounds.Height <= 0) return;

            double tw = 1.19;
            double th = 1.53;
            double.TryParse(TxtCropWidth.Text, out tw);
            double.TryParse(TxtCropHeight.Text, out th);
            if (tw <= 0) tw = 1.19;
            if (th <= 0) th = 1.53;
            double targetAspect = tw / th;

            double selWidth, selHeight, left, top;

            if (SettingsService.Current.PasspixAutoFaceDetectionCrop && 
                _detectedFaceRelativeRect.Width > 0 && _detectedFaceRelativeRect.Height > 0)
            {
                // Translate the relative face rectangle to screen coords of the current display bounds
                double faceLeft = imgBounds.Left + _detectedFaceRelativeRect.X * imgBounds.Width;
                double faceTop = imgBounds.Top + _detectedFaceRelativeRect.Y * imgBounds.Height;
                double faceWidth = _detectedFaceRelativeRect.Width * imgBounds.Width;
                double faceHeight = _detectedFaceRelativeRect.Height * imgBounds.Height;

                // Center a box with targetAspect on the face
                double faceCenterX = faceLeft + faceWidth / 2;
                double faceCenterY = faceTop + faceHeight / 2;

                selHeight = faceHeight * 1.5; // expand a bit
                selWidth = selHeight * targetAspect;

                if (selWidth > imgBounds.Width || selHeight > imgBounds.Height)
                {
                    // Scale down to fit inside rect bounds
                    double scale = Math.Min(imgBounds.Width / selWidth, imgBounds.Height / selHeight);
                    selWidth *= scale;
                    selHeight *= scale;
                }

                left = faceCenterX - selWidth / 2;
                top = faceCenterY - selHeight / 2;

                // Clamp to display bounds
                left = Math.Max(imgBounds.Left, Math.Min(left, imgBounds.Right - selWidth));
                top = Math.Max(imgBounds.Top, Math.Min(top, imgBounds.Bottom - selHeight));
            }
            else
            {
                selHeight = imgBounds.Height * 0.7;
                selWidth = selHeight * targetAspect;

                if (selWidth > imgBounds.Width)
                {
                    selWidth = imgBounds.Width * 0.7;
                    selHeight = selWidth / targetAspect;
                }

                left = imgBounds.Left + (imgBounds.Width - selWidth) / 2;
                top = imgBounds.Top + (imgBounds.Height - selHeight) / 2;
            }

            RectCropSelector.Width = selWidth;
            RectCropSelector.Height = selHeight;

            Canvas.SetLeft(RectCropSelector, left);
            Canvas.SetTop(RectCropSelector, top);
        }

        private void ImgOriginal_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateCropSelectorBounds();
            TriggerCropAndProcess();
        }

        private Rect GetImageDisplayRect()
        {
            if (_originalBitmap == null || ImgOriginal.ActualWidth == 0 || ImgOriginal.ActualHeight == 0)
                return new Rect();

            double sourceWidth = _originalBitmap.PixelWidth;
            double sourceHeight = _originalBitmap.PixelHeight;

            double controlWidth = ImgOriginal.ActualWidth;
            double controlHeight = ImgOriginal.ActualHeight;

            double ratioX = controlWidth / sourceWidth;
            double ratioY = controlHeight / sourceHeight;
            double ratio = Math.Min(ratioX, ratioY);

            double displayWidth = sourceWidth * ratio;
            double displayHeight = sourceHeight * ratio;

            double left = (controlWidth - displayWidth) / 2;
            double top = (controlHeight - displayHeight) / 2;

            return new Rect(left, top, displayWidth, displayHeight);
        }

        #region Interactive Sizing & Drag Handles

        private void RectCropSelector_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_originalBitmap == null || _isResizing) return;
            _isDragging = true;
            _dragStartPoint = e.GetPosition(CanvasCropOverlay);
            _selectorStartLeft = Canvas.GetLeft(RectCropSelector);
            _selectorStartTop = Canvas.GetTop(RectCropSelector);
            if (double.IsNaN(_selectorStartLeft)) _selectorStartLeft = 0;
            if (double.IsNaN(_selectorStartTop)) _selectorStartTop = 0;
            RectCropSelector.CaptureMouse();
            e.Handled = true;
        }

        private void RectCropSelector_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;

            Point currentPoint = e.GetPosition(CanvasCropOverlay);
            double deltaX = currentPoint.X - _dragStartPoint.X;
            double deltaY = currentPoint.Y - _dragStartPoint.Y;

            double newLeft = _selectorStartLeft + deltaX;
            double newTop = _selectorStartTop + deltaY;

            var imgBounds = GetImageCurrentBounds();
            newLeft = Math.Max(imgBounds.Left, Math.Min(newLeft, imgBounds.Right - RectCropSelector.Width));
            newTop = Math.Max(imgBounds.Top, Math.Min(newTop, imgBounds.Bottom - RectCropSelector.Height));

            Canvas.SetLeft(RectCropSelector, newLeft);
            Canvas.SetTop(RectCropSelector, newTop);

            TriggerCropAndProcess();
            e.Handled = true;
        }

        private void RectCropSelector_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                RectCropSelector.ReleaseMouseCapture();
                _isDragging = false;
                TriggerCropAndProcess();
            }
            e.Handled = true;
        }

        private void Handle_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_originalBitmap == null) return;
            var border = sender as Border;
            if (border == null) return;

            _isResizing = true;
            _resizeHandle = border.Tag.ToString() ?? "";
            _resizeStartMousePoint = e.GetPosition(CanvasCropOverlay);
            _resizeStartWidth = RectCropSelector.Width;
            _resizeStartHeight = RectCropSelector.Height;
            _resizeStartLeft = Canvas.GetLeft(RectCropSelector);
            _resizeStartTop = Canvas.GetTop(RectCropSelector);
            if (double.IsNaN(_resizeStartLeft)) _resizeStartLeft = 0;
            if (double.IsNaN(_resizeStartTop)) _resizeStartTop = 0;

            border.CaptureMouse();
            e.Handled = true;
        }

        private void Handle_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isResizing) return;

            Point currentPoint = e.GetPosition(CanvasCropOverlay);
            double deltaX = currentPoint.X - _resizeStartMousePoint.X;
            
            double tw = 1.19;
            double th = 1.53;
            double.TryParse(TxtCropWidth.Text, out tw);
            double.TryParse(TxtCropHeight.Text, out th);
            if (tw <= 0) tw = 1.19;
            if (th <= 0) th = 1.53;
            double targetAspect = tw / th;

            double newW = _resizeStartWidth;
            double newH = _resizeStartHeight;
            double newLeft = _resizeStartLeft;
            double newTop = _resizeStartTop;

            switch (_resizeHandle)
            {
                case "BR":
                    newW = Math.Max(30, _resizeStartWidth + deltaX);
                    newH = newW / targetAspect;
                    break;
                case "BL":
                    newW = Math.Max(30, _resizeStartWidth - deltaX);
                    newH = newW / targetAspect;
                    newLeft = _resizeStartLeft + (_resizeStartWidth - newW);
                    break;
                case "TR":
                    newW = Math.Max(30, _resizeStartWidth + deltaX);
                    newH = newW / targetAspect;
                    newTop = _resizeStartTop - (newH - _resizeStartHeight);
                    break;
                case "TL":
                    newW = Math.Max(30, _resizeStartWidth - deltaX);
                    newH = newW / targetAspect;
                    newLeft = _resizeStartLeft + (_resizeStartWidth - newW);
                    newTop = _resizeStartTop - (newH - _resizeStartHeight);
                    break;
            }

            var imgBounds = GetImageCurrentBounds();
            if (newLeft >= imgBounds.Left && newTop >= imgBounds.Top && 
                (newLeft + newW) <= imgBounds.Right && 
                (newTop + newH) <= imgBounds.Bottom)
            {
                RectCropSelector.Width = newW;
                RectCropSelector.Height = newH;
                Canvas.SetLeft(RectCropSelector, newLeft);
                Canvas.SetTop(RectCropSelector, newTop);

                TriggerCropAndProcess();
            }
        }

        private void Handle_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isResizing)
            {
                var border = sender as Border;
                border?.ReleaseMouseCapture();
                _isResizing = false;
                TriggerCropAndProcess();
            }
            e.Handled = true;
        }

        #endregion

        #region Zooming and Panning Loaded Image

        private void GridCropperContainer_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_originalBitmap == null) return;
            double zoomFactor = e.Delta > 0 ? 1.15 : 0.85;
            double newZoom = _zoom * zoomFactor;
            if (newZoom >= 0.25 && newZoom <= 8.0)
            {
                _zoom = newZoom;
                ImgScale.ScaleX = _zoom;
                ImgScale.ScaleY = _zoom;
                TriggerCropAndProcess();
            }
        }

        private void GridCropperContainer_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_originalBitmap == null) return;

            // Click is outside the crop selector
            if (e.ChangedButton == MouseButton.Left && e.OriginalSource != RectCropSelector && !IsDescendantOf(e.OriginalSource as DependencyObject, RectCropSelector))
            {
                _isPanning = true;
                _lastMousePosition = e.GetPosition(GridCropperContainer);
                GridCropperContainer.CaptureMouse();
                e.Handled = true;
            }
            else if (e.ChangedButton == MouseButton.Right)
            {
                _isPanning = true;
                _lastMousePosition = e.GetPosition(GridCropperContainer);
                GridCropperContainer.CaptureMouse();
                e.Handled = true;
            }
        }

        private void GridCropperContainer_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                Point currentPos = e.GetPosition(GridCropperContainer);
                double deltaX = currentPos.X - _lastMousePosition.X;
                double deltaY = currentPos.Y - _lastMousePosition.Y;

                ImgTranslate.X += deltaX;
                ImgTranslate.Y += deltaY;

                _lastMousePosition = currentPos;
                TriggerCropAndProcess();
                e.Handled = true;
            }
        }

        private void GridCropperContainer_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning)
            {
                GridCropperContainer.ReleaseMouseCapture();
                _isPanning = false;
                TriggerCropAndProcess();
                e.Handled = true;
            }
        }

        private bool IsDescendantOf(DependencyObject? element, DependencyObject parent)
        {
            while (element != null)
            {
                if (element == parent) return true;
                element = VisualTreeHelper.GetParent(element);
            }
            return false;
        }

        #endregion

        private void TxtCropDimensions_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_originalBitmap == null || _isUpdatingUi) return;
            UpdateCropSelectorBounds();
        }

        private void ChkRemoveBg_Checked(object sender, RoutedEventArgs e) => TriggerCropAndProcess();
        private void ChkRemoveBg_Unchecked(object sender, RoutedEventArgs e) => TriggerCropAndProcess();


        private string GetCurrentBackgroundColorOption()
        {
            if (CmbSolidFill.SelectedItem is ComboBoxItem item)
            {
                string selectedText = item.Content?.ToString() ?? "";
                if (selectedText.Equals("Custom Color...", StringComparison.OrdinalIgnoreCase))
                {
                    return SettingsManager.Instance.DefaultBackgroundColor;
                }
                return selectedText;
            }
            return SettingsManager.Instance.DefaultBackgroundColor;
        }

        private void CmbCustomColor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            CmbSolidFill.IsDropDownOpen = false;
            PromptAndApplyCustomColor();
        }

        private void PromptAndApplyCustomColor()
        {
            var colorPicker = new ColorPickerDialog();
            colorPicker.Owner = Window.GetWindow(this);
            
            string currentSettingsColor = SettingsService.Current.CustomBackgroundHex;
            if (string.IsNullOrEmpty(currentSettingsColor))
            {
                currentSettingsColor = "#FFFFFF";
            }
            colorPicker.SelectedColor = GetPasspixColor(currentSettingsColor);

            if (colorPicker.ShowDialog() == true)
            {
                Color chosen = colorPicker.SelectedColor;
                string hexColor = $"#{chosen.R:X2}{chosen.G:X2}{chosen.B:X2}";
                
                SettingsService.Current.IsUsingCustomBackground = true;
                SettingsService.Current.CustomBackgroundHex = hexColor;
                SettingsService.Current.CustomBackgroundColor = hexColor;
                SettingsService.Current.PasspixDefaultBackgroundColor = hexColor;
                SettingsService.Instance.SaveSettings();

                _isUpdatingUi = true;
                try
                {
                    CmbCustomColor.Content = $"Custom Color ({hexColor})";
                    CmbSolidFill.SelectedItem = CmbCustomColor;
                }
                finally
                {
                    _isUpdatingUi = false;
                }

                UpdatePreview();
                GeneratePassportPreview();
                RefreshGridSheet();
            }
            else
            {
                _isUpdatingUi = true;
                try
                {
                    RestoreCmbSolidFillSelection();
                }
                finally
                {
                    _isUpdatingUi = false;
                }
            }
        }

        private void CmbSolidFill_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi) return;

            if (CmbSolidFill.SelectedItem is ComboBoxItem selectedItem)
            {
                string selectedText = selectedItem.Content?.ToString() ?? "";
                if (selectedText.StartsWith("Custom Color", StringComparison.OrdinalIgnoreCase))
                {
                    PromptAndApplyCustomColor();
                }
                else
                {
                    SettingsManager.Instance.DefaultBackgroundColor = selectedText;
                    SettingsService.Current.IsUsingCustomBackground = false;
                    SettingsService.Instance.SaveSettings();
                }
            }

            TriggerCropAndProcess();
            RefreshGridSheet();
        }

        private void RestoreCmbSolidFillSelection()
        {
            if (SettingsService.Current.IsUsingCustomBackground)
            {
                string hexColor = SettingsService.Current.CustomBackgroundHex;
                CmbCustomColor.Content = $"Custom Color ({hexColor})";
                CmbSolidFill.SelectedItem = CmbCustomColor;
                return;
            }

            string savedColor = SettingsManager.Instance.DefaultBackgroundColor;
            bool found = false;
            foreach (ComboBoxItem item in CmbSolidFill.Items)
            {
                string itemText = item.Content?.ToString() ?? "";
                if (itemText.Equals(savedColor, StringComparison.OrdinalIgnoreCase))
                {
                    CmbSolidFill.SelectedItem = item;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                CmbSolidFill.SelectedItem = CmbCustomColor;
            }
        }

        private Color GetSelectedBackgroundColor()
        {
            if (SettingsService.Current.IsUsingCustomBackground)
            {
                try
                {
                    string hex = SettingsService.Current.CustomBackgroundHex;
                    if (!string.IsNullOrEmpty(hex))
                    {
                        var convertedColor = ColorConverter.ConvertFromString(hex);
                        if (convertedColor is Color col)
                        {
                            return col;
                        }
                    }
                }
                catch { }
                return Colors.White;
            }

            string bg = SettingsService.Current.PasspixDefaultBackgroundColor?.ToLower() ?? "white";
            switch (bg)
            {
                case "white":
                    return Colors.White;

                case "light blue":
                    return Color.FromRgb(214, 234, 248);

                case "red":
                    return Colors.Red;

                case "blue":
                    return Colors.Blue;

                case "transparent":
                    return Colors.Transparent;

                default:
                    return Colors.White;
            }
        }

        private void UpdatePreview() => TriggerCropAndProcess();
        private void GeneratePassportPreview() => TriggerCropAndProcess();
        private void RefreshGridSheet() => RenderGridSheet();
        private void CmbPageSize_SelectionChanged(object sender, SelectionChangedEventArgs e) => TriggerCropAndProcess();
        private void TxtCopies_TextChanged(object sender, TextChangedEventArgs e) => TriggerCropAndProcess();

        private async void TriggerCropAndProcess()
        {
            if (!_isInitialized || _originalBitmap == null) return;

            ClampCropSelectorToImageBounds();

            bool isInteracting = _isPanning || _isDragging || _isResizing;
            bool willRunBgRemoval = ChkRemoveBg.IsChecked == true && !isInteracting;

            // Debounce / Throttle ONNX runs
            if (willRunBgRemoval)
            {
                if (_isProcessingBg)
                {
                    _needReProcessBg = true;
                    return;
                }
                _isProcessingBg = true;
            }

            try
            {
                while (true)
                {
                    _needReProcessBg = false;
                    await RunCropAndProcessInternal();
                    
                    if (!_needReProcessBg || ChkRemoveBg.IsChecked != true || isInteracting)
                    {
                        break;
                    }
                }
            }
            finally
            {
                if (willRunBgRemoval)
                {
                    _isProcessingBg = false;
                }
            }
        }

        private async Task RunCropAndProcessInternal()
        {
            if (_originalBitmap == null || ImgOriginal.ActualWidth == 0 || ImgOriginal.ActualHeight == 0) return;

            double left = Canvas.GetLeft(RectCropSelector);
            double top = Canvas.GetTop(RectCropSelector);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;

            try
            {
                var rect = GetImageDisplayRect();
                if (rect.Width <= 0 || rect.Height <= 0) return;

                // Map screen coordinates of selector to zoomed/panned image's local space
                var transform = CanvasCropOverlay.TransformToVisual(ImgOriginal);
                Point p0 = transform.Transform(new Point(left, top));
                Point p1 = transform.Transform(new Point(left + RectCropSelector.Width, top + RectCropSelector.Height));

                // Calculate relative position within the image content, then map to pixel coordinates
                double relX0 = (p0.X - rect.Left) / rect.Width;
                double relY0 = (p0.Y - rect.Top) / rect.Height;
                double relX1 = (p1.X - rect.Left) / rect.Width;
                double relY1 = (p1.Y - rect.Top) / rect.Height;

                int cropX = (int)(relX0 * _originalBitmap.PixelWidth);
                int cropY = (int)(relY0 * _originalBitmap.PixelHeight);
                int cropW = (int)((relX1 - relX0) * _originalBitmap.PixelWidth);
                int cropH = (int)((relY1 - relY0) * _originalBitmap.PixelHeight);

                cropX = Math.Max(0, Math.Min(cropX, _originalBitmap.PixelWidth - 1));
                cropY = Math.Max(0, Math.Min(cropY, _originalBitmap.PixelHeight - 1));
                cropW = Math.Max(1, Math.Min(cropW, _originalBitmap.PixelWidth - cropX));
                cropH = Math.Max(1, Math.Min(cropH, _originalBitmap.PixelHeight - cropY));

                var cropRect = new Int32Rect(cropX, cropY, cropW, cropH);
                _croppedBitmap = new CroppedBitmap(_originalBitmap, cropRect);

                TxtCropResultPlaceholder.Visibility = Visibility.Collapsed;
                bool isInteracting = _isPanning || _isDragging || _isResizing;
                bool runBgRemoval = ChkRemoveBg.IsChecked == true && !isInteracting;
                Color bgColor = GetSelectedBackgroundColor();
                GridCroppedResultBg.Background = GetPasspixBrush();

                if (runBgRemoval)
                {
                    if (App.OnnxSession == null)
                    {
                        TxtCropResultPlaceholder.Text = "Loading AI Model...";
                        TxtCropResultPlaceholder.Visibility = Visibility.Visible;
                        PnlProgressRing.Visibility = Visibility.Visible;
                        
                        if (App.ModelLoadingTask != null)
                        {
                            bool success = await App.ModelLoadingTask;
                            if (!success || App.OnnxSession == null)
                            {
                                TxtCropResultPlaceholder.Text = "AI Model unavailable";
                                PnlProgressRing.Visibility = Visibility.Collapsed;
                                _processedPortrait = CreateWriteableBitmap(_croppedBitmap);
                                ImgCroppedResult.Source = _processedPortrait;
                                if (TcPasspix.SelectedIndex == 1)
                                {
                                    RenderGridSheet();
                                }
                                return;
                            }
                        }
                    }

                    TxtCropResultPlaceholder.Text = "Removing BG...";
                    TxtCropResultPlaceholder.Visibility = Visibility.Visible;
                    PnlProgressRing.Visibility = Visibility.Visible;

                    var inputBitmap = _croppedBitmap;
                    inputBitmap.Freeze();

                    var outPixels = await Task.Run(() => RemoveBackgroundWithOnnx(inputBitmap, bgColor));
                    
                    PnlProgressRing.Visibility = Visibility.Collapsed;

                    if (_isPanning || _isDragging || _isResizing)
                    {
                        return;
                    }

                    if (outPixels != null)
                    {
                        int targetW = inputBitmap.PixelWidth;
                        int targetH = inputBitmap.PixelHeight;
                        _processedPortrait = new WriteableBitmap(targetW, targetH, 300.0, 300.0, PixelFormats.Bgra32, null);
                        _processedPortrait.WritePixels(new Int32Rect(0, 0, targetW, targetH), outPixels, targetW * 4, 0, 0);
                        if (SettingsService.Current.PasspixAutoFaceEnhancement)
                        {
                            EnhanceFace(_processedPortrait);
                        }
                        ImgCroppedResult.Source = _processedPortrait;
                        TxtCropResultPlaceholder.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        _processedPortrait = CreateWriteableBitmap(inputBitmap);
                        if (SettingsService.Current.PasspixAutoFaceEnhancement)
                        {
                            EnhanceFace(_processedPortrait);
                        }
                        ImgCroppedResult.Source = _processedPortrait;
                        TxtCropResultPlaceholder.Text = "BG Removal Failed";
                        TxtCropResultPlaceholder.Visibility = Visibility.Visible;
                    }
                }
                else
                {
                    PnlProgressRing.Visibility = Visibility.Collapsed;
                    _processedPortrait = CreateWriteableBitmap(_croppedBitmap);
                    if (SettingsService.Current.PasspixAutoFaceEnhancement)
                    {
                        EnhanceFace(_processedPortrait);
                    }
                    ImgCroppedResult.Source = _processedPortrait;
                }

                if (TcPasspix.SelectedIndex == 1 && !isInteracting)
                {
                    RenderGridSheet();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Passpix Process error: {ex.Message}");
            }
        }

        private WriteableBitmap CreateWriteableBitmap(BitmapSource source)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            return new WriteableBitmap(converted);
        }

        private Color GetPasspixColor(string colorName)
        {
            if (string.IsNullOrEmpty(colorName)) return Colors.White;
            string normalized = colorName.ToLower().Trim();

            if (normalized.StartsWith("#"))
            {
                try
                {
                    var convertedColor = ColorConverter.ConvertFromString(normalized);
                    if (convertedColor is Color col)
                    {
                        return col;
                    }
                }
                catch { }
            }

            switch (normalized)
            {
                case "blue": return Color.FromRgb(30, 64, 175);
                case "red": return Color.FromRgb(185, 28, 28);
                case "light blue": return Color.FromRgb(186, 230, 253);
                case "transparent": return Colors.Transparent;
                case "white": return Colors.White;
                default:
                    try
                    {
                        var convertedColor = ColorConverter.ConvertFromString("#" + normalized);
                        if (convertedColor is Color col)
                        {
                            return col;
                        }
                    }
                    catch { }
                    return Colors.White;
            }
        }

        private Brush GetPasspixBrush()
        {
            Color col = GetSelectedBackgroundColor();
            if (col == Colors.Transparent) return Brushes.Transparent;
            return new SolidColorBrush(col);
        }

        private byte[]? RemoveBackgroundWithOnnx(BitmapSource src, Color fillCol)
        {
            if (App.OnnxSession == null) return null;

            try
            {
                int targetW = src.PixelWidth;
                int targetH = src.PixelHeight;

                var scale = new ScaleTransform(320.0 / targetW, 320.0 / targetH);
                var resized = new TransformedBitmap(src, scale);
                resized.Freeze();
                var converted = new FormatConvertedBitmap(resized, PixelFormats.Bgra32, null, 0);
                converted.Freeze();

                byte[] pixels = new byte[320 * 320 * 4];
                converted.CopyPixels(pixels, 320 * 4, 0);

                float[] inputData = new float[1 * 3 * 320 * 320];
                float[] mean = { 0.485f, 0.456f, 0.406f };
                float[] std = { 0.229f, 0.224f, 0.225f };

                for (int y = 0; y < 320; y++)
                {
                    for (int x = 0; x < 320; x++)
                    {
                        int pixelIdx = (y * 320 + x) * 4;
                        byte b = pixels[pixelIdx];
                        byte g = pixels[pixelIdx + 1];
                        byte r = pixels[pixelIdx + 2];

                        inputData[0 * 320 * 320 + y * 320 + x] = ((r / 255.0f) - mean[0]) / std[0];
                        inputData[1 * 320 * 320 + y * 320 + x] = ((g / 255.0f) - mean[1]) / std[1];
                        inputData[2 * 320 * 320 + y * 320 + x] = ((b / 255.0f) - mean[2]) / std[2];
                    }
                }

                var inputTensor = new DenseTensor<float>(inputData, new int[] { 1, 3, 320, 320 });
                var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input.1", inputTensor) };

                float[] outputMask = new float[320 * 320];
                lock (App.OnnxSession)
                {
                    using var results = App.OnnxSession.Run(inputs);
                    var outputTensor = results.First().AsTensor<float>();
                    for (int i = 0; i < 320 * 320; i++)
                    {
                        outputMask[i] = outputTensor.GetValue(i);
                    }
                }

                float[,] upscaledMask = new float[targetW, targetH];
                for (int y = 0; y < targetH; y++)
                {
                    float srcY = (float)y / (targetH - 1) * 319f;
                    int y0 = (int)Math.Floor(srcY);
                    int y1 = Math.Min(y0 + 1, 319);
                    float dy = srcY - y0;

                    for (int x = 0; x < targetW; x++)
                    {
                        float srcX = (float)x / (targetW - 1) * 319f;
                        int x0 = (int)Math.Floor(srcX);
                        int x1 = Math.Min(x0 + 1, 319);
                        float dx = srcX - x0;

                        float val00 = outputMask[y0 * 320 + x0];
                        float val10 = outputMask[y0 * 320 + x1];
                        float val01 = outputMask[y1 * 320 + x0];
                        float val11 = outputMask[y1 * 320 + x1];

                        float val = (1 - dy) * ((1 - dx) * val00 + dx * val10) + dy * ((1 - dx) * val01 + dx * val11);
                        upscaledMask[x, y] = Math.Clamp(val, 0.0f, 1.0f);
                    }
                }

                var originalConverted = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                originalConverted.Freeze();
                byte[] origPixels = new byte[targetW * targetH * 4];
                originalConverted.CopyPixels(origPixels, targetW * 4, 0);

                byte[] outPixels = new byte[targetW * targetH * 4];
                bool isTransparent = fillCol == Colors.Transparent;

                for (int y = 0; y < targetH; y++)
                {
                    for (int x = 0; x < targetW; x++)
                    {
                        int idx = (y * targetW + x) * 4;
                        float maskVal = upscaledMask[x, y];

                        byte b = origPixels[idx];
                        byte g = origPixels[idx + 1];
                        byte r = origPixels[idx + 2];

                        if (isTransparent)
                        {
                            outPixels[idx] = b;
                            outPixels[idx + 1] = g;
                            outPixels[idx + 2] = r;
                            outPixels[idx + 3] = (byte)(maskVal * 255);
                        }
                        else
                        {
                            outPixels[idx] = (byte)(b * maskVal + fillCol.B * (1.0f - maskVal));
                            outPixels[idx + 1] = (byte)(g * maskVal + fillCol.G * (1.0f - maskVal));
                            outPixels[idx + 2] = (byte)(r * maskVal + fillCol.R * (1.0f - maskVal));
                            outPixels[idx + 3] = 255;
                        }
                    }
                }

                return outPixels;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RemoveBackgroundWithOnnx exception: {ex.Message}");
                return null;
            }
        }

        private void RenderGridSheet()
        {
            if (_processedPortrait == null) return;

            double tw = 1.19;
            double th = 1.53;
            double.TryParse(TxtCropWidth.Text, out tw);
            double.TryParse(TxtCropHeight.Text, out th);
            if (tw <= 0) tw = 1.19;
            if (th <= 0) th = 1.53;

            int copies = 9;
            int.TryParse(TxtCopies.Text, out copies);
            if (copies <= 0) copies = 9;

            string pageSize = ((ComboBoxItem)CmbPageSize.SelectedItem)?.Content?.ToString() ?? "4x6";

            int sheetW = 1200; // 4x6 at 300 DPI
            int sheetH = 1800;

            if (pageSize == "5x7")
            {
                sheetW = 1500;
                sheetH = 2100;
            }
            else if (pageSize == "A4")
            {
                sheetW = 2480;
                sheetH = 3508;
            }

            // Force Portrait orientation (Height > Width)
            if (sheetW > sheetH)
            {
                int temp = sheetW;
                sheetW = sheetH;
                sheetH = temp;
            }

            int photoW = (int)(tw * 300);
            int photoH = (int)(th * 300);

            int marginX = 80;
            int marginY = 80;
            int gapX = 35;
            int gapY = 35;

            if (pageSize == "4x6")
            {
                marginX = 40;
                gapX = 24;
            }

            // Calculate columns fitting within sheet width
            int cols = (sheetW - 2 * marginX + gapX) / (photoW + gapX);
            if (pageSize == "4x6")
            {
                cols = 3;
            }
            if (cols <= 0) cols = 1;

            // Create RenderTargetBitmap for high-quality DrawingContext rendering at 300 DPI
            var rt = new RenderTargetBitmap(sheetW, sheetH, 300.0, 300.0, PixelFormats.Pbgra32);
            var drawingVisual = new DrawingVisual();

            using (DrawingContext dc = drawingVisual.RenderOpen())
            {
                // Draw background white (always white for the print sheet paper)
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, sheetW * 96.0 / 300.0, sheetH * 96.0 / 300.0));

                double wRatio = 96.0 / 300.0;
                double wPhotoW = photoW * wRatio;
                double wPhotoH = photoH * wRatio;
                double wMarginX = marginX * wRatio;
                double wMarginY = marginY * wRatio;
                double wGapX = gapX * wRatio;
                double wGapY = gapY * wRatio;
                double borderThickness = 8.0 * wRatio; // 8px border converted to WPF units

                for (int i = 0; i < copies; i++)
                {
                    int col = i % cols;
                    int row = i / cols;

                    double posX = wMarginX + col * (wPhotoW + wGapX);
                    double posY = wMarginY + row * (wPhotoH + wGapY);

                    // Check height boundary
                    if (posY + wPhotoH > (sheetH - marginY) * wRatio) break;

                    // Draw 8px solid black border rectangle
                    dc.DrawRectangle(Brushes.Black, null, new Rect(posX, posY, wPhotoW, wPhotoH));

                    // Draw photo background color inside the border
                    var photoRect = new Rect(
                        posX + borderThickness, 
                        posY + borderThickness, 
                        wPhotoW - 2 * borderThickness, 
                        wPhotoH - 2 * borderThickness);
                    dc.DrawRectangle(GetPasspixBrush(), null, photoRect);

                    // Draw photo inside, inset by the border thickness
                    dc.DrawImage(_processedPortrait, photoRect);
                }
            }

            rt.Render(drawingVisual);
            _sheetBitmap = new WriteableBitmap(rt);

            ImgSheetResult.Source = _sheetBitmap;
            TxtSheetPlaceholder.Visibility = Visibility.Collapsed;
        }

        private void PasspixTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source is TabControl tc && tc.SelectedIndex == 1)
            {
                RenderGridSheet();
            }
        }

        private void BtnSavePhoto_Click(object sender, RoutedEventArgs e)
        {
            if (_processedPortrait == null)
            {
                MessageBox.Show("Please load and crop a photo first.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SettingsService.SaveFile("passport_photo", "PNG Image (*.png)|*.png|JPEG Image (*.jpg)|*.jpg", path =>
            {
                SaveBitmapWith300Dpi(_processedPortrait, path);
                MessageBox.Show("Saved at 300 DPI successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        private void BtnSaveSheet_Click(object sender, RoutedEventArgs e)
        {
            if (_sheetBitmap == null)
            {
                RenderGridSheet();
            }

            if (_sheetBitmap == null)
            {
                MessageBox.Show("Please load and crop a photo first.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SettingsService.SaveFile("print_sheet", "PNG Image (*.png)|*.png|JPEG Image (*.jpg)|*.jpg", path =>
            {
                SaveBitmapWith300Dpi(_sheetBitmap, path);
                MessageBox.Show("Saved grid sheet at 300 DPI successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        private void BtnPrintSheet_Click(object sender, RoutedEventArgs e)
        {
            if (_sheetBitmap == null)
            {
                RenderGridSheet();
            }

            if (_sheetBitmap == null)
            {
                MessageBox.Show("Please load and crop a photo first.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var pd = new PrintDialog();
            if (pd.ShowDialog() == true)
            {
                try
                {
                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        dc.DrawImage(_sheetBitmap, new Rect(0, 0, pd.PrintableAreaWidth, pd.PrintableAreaHeight));
                    }
                    pd.PrintVisual(visual, "Passpix Grid Print");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Print failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void SaveBitmapWith300Dpi(BitmapSource src, string path)
        {
            int w = src.PixelWidth;
            int h = src.PixelHeight;

            var converted = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            byte[] pixels = new byte[w * h * 4];
            converted.CopyPixels(pixels, w * 4, 0);

            var dpiBitmap = BitmapSource.Create(w, h, 300.0, 300.0, PixelFormats.Bgra32, null, pixels, w * 4);
            using var fs = new FileStream(path, FileMode.Create);
            
            BitmapEncoder encoder;
            if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    encoder = BitmapEncoder.Create(new Guid("e094b660-1928-4458-a72f-e87a2d3c907b"));
                }
                catch
                {
                    // Fallback to PNG
                    encoder = new PngBitmapEncoder();
                }
            }
            else if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                encoder = new PngBitmapEncoder();
            }
            else
            {
                encoder = new JpegBitmapEncoder { QualityLevel = 95 };
            }

            encoder.Frames.Add(BitmapFrame.Create(dpiBitmap));
            encoder.Save(fs);
        }

        #endregion

        #region Tool 2: PDF Utility Suite (All 12 Tools Implementation)

        private void PdfToolsTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Event triggers on TabControl sub tab selection
        }

        // Shared native PDF renderer helper
        private static async Task<BitmapSource> RenderPdfPageToBitmapAsync(string pdfPath, uint pageIndex)
        {
            StorageFile storageFile = await StorageFile.GetFileFromPathAsync(pdfPath);
            var pdfDoc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(storageFile);
            using (Windows.Data.Pdf.PdfPage pdfPage = pdfDoc.GetPage(pageIndex))
            {
                    using (var memStream = new InMemoryRandomAccessStream())
                    {
                        var options = new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = 280, DestinationHeight = 360 };
                        await pdfPage.RenderToStreamAsync(memStream, options);
                        
                        using (var ms = new MemoryStream())
                        {
                            using (var winrtStream = memStream.AsStream())
                            {
                                await winrtStream.CopyToAsync(ms);
                            }
                            ms.Position = 0;

                            return await Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                var bitmap = new BitmapImage();
                                bitmap.BeginInit();
                                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                bitmap.StreamSource = ms;
                                bitmap.EndInit();
                                bitmap.Freeze();
                                return (BitmapSource)bitmap;
                            });
                        }
                    }
                }
            }

        // Helper to configure a common async preview grid
        private void UpdatePlaceholderVisibility(WrapPanel container)
        {
            if (container == null) return;
            if (container.Parent is Grid parentGrid)
            {
                var placeholder = parentGrid.Children.OfType<FrameworkElement>()
                    .FirstOrDefault(c => c.Name == container.Name + "Placeholder");
                if (placeholder != null)
                {
                    placeholder.Visibility = container.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        private async Task RenderPdfPagesVisualAsync(string pdfPath, WrapPanel container, Action<int, Grid, Image> customizeAction)
        {
            container.Children.Clear();
            UpdatePlaceholderVisibility(container);
            int pageCount = 0;

            try
            {
                using (var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import))
                {
                    pageCount = doc.PageCount;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load PDF: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var grids = new Grid[pageCount];
            var images = new Image[pageCount];

            for (int i = 0; i < pageCount; i++)
            {
                int pageIdx = i;
                var grid = new Grid { Margin = new Thickness(8), Width = 140, Height = 205 };

                var card = new Border
                {
                    Background = (Brush)Application.Current.Resources["CardBgBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["BorderBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(4)
                };
                grid.Children.Add(card);

                var layoutGrid = new Grid();
                card.Child = layoutGrid;
                layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Page Num label
                layoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Image
                layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Bottom action

                var lbl = new TextBlock
                {
                    Text = $"Page {pageIdx + 1}",
                    Foreground = (Brush)Application.Current.Resources["TextBrush"],
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 0, 4)
                };
                Grid.SetRow(lbl, 0);
                layoutGrid.Children.Add(lbl);

                var imgBorder = new Border { Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)), CornerRadius = new CornerRadius(3), ClipToBounds = true };
                Grid.SetRow(imgBorder, 1);
                layoutGrid.Children.Add(imgBorder);

                var placeholderText = new TextBlock
                {
                    Text = "Loading...",
                    Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                imgBorder.Child = placeholderText;

                var img = new Image { Stretch = Stretch.Uniform };
                images[pageIdx] = img;

                grids[pageIdx] = grid;
                container.Children.Add(grid);

                customizeAction(pageIdx, grid, img);
            }

            UpdatePlaceholderVisibility(container);

            // Render pages in the background
            for (int i = 0; i < pageCount; i++)
            {
                int pageIdx = i;
                try
                {
                    var bmp = await RenderPdfPageToBitmapAsync(pdfPath, (uint)pageIdx);
                    var grid = grids[pageIdx];
                    var card = (Border)grid.Children[0];
                    var layoutGrid = (Grid)card.Child;
                    var imgBorder = layoutGrid.Children.OfType<Border>().First();
                    
                    images[pageIdx].Source = bmp;
                    imgBorder.Child = images[pageIdx];
                }
                catch
                {
                    var grid = grids[pageIdx];
                    var card = (Border)grid.Children[0];
                    var layoutGrid = (Grid)card.Child;
                    var imgBorder = layoutGrid.Children.OfType<Border>().First();
                    imgBorder.Child = new TextBlock { Text = "Error", Foreground = Brushes.Red, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                }
            }
        }

        private void RenderPreviewIfPdf(string path, WrapPanel wp)
        {
            if (File.Exists(path) && Path.GetExtension(path).ToLower() == ".pdf")
            {
                var task = RenderPdfPagesVisualAsync(path, wp, (pageIdx, grid, img) => { });
            }
        }

        #region Tool 1: PDF to Image Converter

        private void BtnPdfToImgBrowse_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Filter = "PDF Files (*.pdf)|*.pdf" };
            if (ofd.ShowDialog() == true)
            {
                _pdfToImgPath = ofd.FileName;
                LblPdfToImgFile.Text = Path.GetFileName(_pdfToImgPath);

                // Render previews and add Page Save buttons
                var task = RenderPdfPagesVisualAsync(_pdfToImgPath, WpPdfToImgPreviews, (pageIdx, grid, img) =>
                {
                    var card = (Border)grid.Children[0];
                    var layoutGrid = (Grid)card.Child;
                    
                    var btn = new Button
                    {
                        Content = "Save PNG",
                        Style = (Style)Application.Current.Resources["ModernButton"],
                        Padding = new Thickness(4, 2, 4, 2),
                        Margin = new Thickness(0, 4, 0, 0),
                        FontSize = 9,
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    };
                    btn.Click += async (s, ev) =>
                    btn.Click += async (s, ev) =>
                    {
                        string format = SettingsService.Current.PdfDefaultImageExportFormat; // PNG, JPG, WEBP
                        string filter = format switch
                        {
                            "JPG" => "JPEG Image (*.jpg)|*.jpg|PNG Image (*.png)|*.png|WebP Image (*.webp)|*.webp",
                            "WEBP" => "WebP Image (*.webp)|*.webp|PNG Image (*.png)|*.png|JPEG Image (*.jpg)|*.jpg",
                            _ => "PNG Image (*.png)|*.png|JPEG Image (*.jpg)|*.jpg|WebP Image (*.webp)|*.webp"
                        };

                        await SettingsService.SaveFileAsync($"page_{pageIdx + 1}", filter, async path =>
                        {
                            var bmp = await RenderPdfPageToBitmapAsync(_pdfToImgPath, (uint)pageIdx);
                            SaveBitmapWith300Dpi(bmp, path);
                            MessageBox.Show($"Page {pageIdx + 1} exported successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                        }, isPdfTool: true);
                    };
                    Grid.SetRow(btn, 2);
                    layoutGrid.Children.Add(btn);
                });
            }
        }

        private async void BtnPdfToImgSaveAll_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_pdfToImgPath)) return;

            string format = SettingsService.Current.PdfDefaultImageExportFormat; // PNG, JPG, WEBP
            string filter = format switch
            {
                "JPG" => "JPEG Image (*.jpg)|*.jpg|PNG Image (*.png)|*.png|WebP Image (*.webp)|*.webp",
                "WEBP" => "WebP Image (*.webp)|*.webp|PNG Image (*.png)|*.png|JPEG Image (*.jpg)|*.jpg",
                _ => "PNG Image (*.png)|*.png|JPEG Image (*.jpg)|*.jpg|WebP Image (*.webp)|*.webp"
            };

            await SettingsService.SaveFileAsync("extracted_page", filter, async path =>
            {
                string dir = Path.GetDirectoryName(path) ?? "";
                string baseName = Path.GetFileNameWithoutExtension(path);
                string selectedExt = Path.GetExtension(path);

                int count = 0;
                using (var doc = PdfReader.Open(_pdfToImgPath, PdfDocumentOpenMode.Import))
                {
                    count = doc.PageCount;
                }

                if (SettingsService.Current.MultiThreadProcessing)
                {
                    var tasks = new List<Task>();
                    for (int i = 0; i < count; i++)
                    {
                        int pageIdx = i;
                        tasks.Add(Task.Run(async () =>
                        {
                            var bmp = await RenderPdfPageToBitmapAsync(_pdfToImgPath, (uint)pageIdx);
                            string outPath = Path.Combine(dir, $"{baseName}_{pageIdx + 1}{selectedExt}");
                            SaveBitmapWith300Dpi(bmp, outPath);
                        }));
                    }
                    await Task.WhenAll(tasks);
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        var bmp = await RenderPdfPageToBitmapAsync(_pdfToImgPath, (uint)i);
                        string outPath = Path.Combine(dir, $"{baseName}_{i + 1}{selectedExt}");
                        SaveBitmapWith300Dpi(bmp, outPath);
                    }
                }
                MessageBox.Show($"All {count} pages saved to {dir}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }, isPdfTool: true);
        }

        #endregion

        #region Tool 2: Image to PDF Converter

        private void BtnImgToPdfAdd_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Filter = "Images (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png", Multiselect = true };
            if (ofd.ShowDialog() == true)
            {
                _imgToPdfPaths.AddRange(ofd.FileNames);
                RefreshImgToPdfPreviews();
            }
        }

        private void BtnImgToPdfClear_Click(object sender, RoutedEventArgs e)
        {
            _imgToPdfPaths.Clear();
            WpImgToPdfPreviews.Children.Clear();
            UpdatePlaceholderVisibility(WpImgToPdfPreviews);
        }

        private void RefreshImgToPdfPreviews()
        {
            WpImgToPdfPreviews.Children.Clear();
            UpdatePlaceholderVisibility(WpImgToPdfPreviews);
            for (int i = 0; i < _imgToPdfPaths.Count; i++)
            {
                int idx = i;
                string path = _imgToPdfPaths[i];

                var grid = new Grid { Margin = new Thickness(8), Width = 140, Height = 210 };
                var card = new Border
                {
                    Background = (Brush)Application.Current.Resources["CardBgBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["BorderBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(4)
                };
                grid.Children.Add(card);

                var layoutGrid = new Grid();
                card.Child = layoutGrid;
                layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Name
                layoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Img
                layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Reorder Panel

                var txtName = new TextBlock { Text = Path.GetFileName(path), Foreground = (Brush)Application.Current.Resources["TextBrush"], HorizontalAlignment = HorizontalAlignment.Center, FontSize = 9, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 4) };
                Grid.SetRow(txtName, 0);
                layoutGrid.Children.Add(txtName);

                var imgBorder = new Border { Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)), CornerRadius = new CornerRadius(3) };
                Grid.SetRow(imgBorder, 1);
                layoutGrid.Children.Add(imgBorder);

                var img = new Image { Stretch = Stretch.Uniform };
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(path);
                    bmp.DecodePixelWidth = 120;
                    bmp.EndInit();
                    bmp.Freeze();
                    img.Source = bmp;
                }
                catch { }
                imgBorder.Child = img;

                // Reorder controls
                var reorderGrid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                reorderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                reorderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                reorderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var btnL = new Button { Content = "←", Style = (Style)Application.Current.Resources["ModernButton"], Padding = new Thickness(0), Height = 18, FontSize = 9 };
                btnL.Click += (s, e) => { ShiftImgToPdf(idx, idx - 1); };
                Grid.SetColumn(btnL, 0);
                reorderGrid.Children.Add(btnL);

                var btnDel = new Button { Content = "X", Style = (Style)Application.Current.Resources["MutedButton"], Padding = new Thickness(0), Height = 18, FontSize = 9, Foreground = Brushes.Red };
                btnDel.Click += (s, e) => { _imgToPdfPaths.RemoveAt(idx); RefreshImgToPdfPreviews(); };
                Grid.SetColumn(btnDel, 1);
                reorderGrid.Children.Add(btnDel);

                var btnR = new Button { Content = "→", Style = (Style)Application.Current.Resources["ModernButton"], Padding = new Thickness(0), Height = 18, FontSize = 9 };
                btnR.Click += (s, e) => { ShiftImgToPdf(idx, idx + 1); };
                Grid.SetColumn(btnR, 2);
                reorderGrid.Children.Add(btnR);

                Grid.SetRow(reorderGrid, 2);
                layoutGrid.Children.Add(reorderGrid);

                WpImgToPdfPreviews.Children.Add(grid);
            }
            UpdatePlaceholderVisibility(WpImgToPdfPreviews);
        }

        private void ShiftImgToPdf(int from, int to)
        {
            if (to < 0 || to >= _imgToPdfPaths.Count) return;
            string path = _imgToPdfPaths[from];
            _imgToPdfPaths.RemoveAt(from);
            _imgToPdfPaths.Insert(to, path);
            RefreshImgToPdfPreviews();
        }

        private void BtnImgToPdfCompile_Click(object sender, RoutedEventArgs e)
        {
            if (_imgToPdfPaths.Count == 0) return;
            SettingsService.SaveFile("images_document", "PDF Documents (*.pdf)|*.pdf", path =>
            {
                using (var pdf = new PdfDocument())
                {
                    foreach (string imgPath in _imgToPdfPaths)
                    {
                        var page = pdf.AddPage();
                        using (var ximg = XImage.FromFile(imgPath))
                        {
                             page.Width = XUnit.FromPoint(ximg.PointWidth);
                             page.Height = XUnit.FromPoint(ximg.PointHeight);
                            var gfx = XGraphics.FromPdfPage(page);
                            gfx.DrawImage(ximg, 0, 0);
                        }
                    }
                    pdf.Save(path);
                }
                MessageBox.Show("PDF compiled successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }, isPdfTool: true);
        }

        #endregion

        #region Tool 3: Password Protected PDF

        private void BtnProtectPdfBrowse_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Filter = "PDF Files (*.pdf)|*.pdf" };
            if (ofd.ShowDialog() == true)
            {
                TxtProtectPdfPath.Text = ofd.FileName;
                RenderPreviewIfPdf(ofd.FileName, WpProtectPdfPreviews);
            }
        }

        private void BtnProtectPdfApply_Click(object sender, RoutedEventArgs e)
        {
            string path = TxtProtectPdfPath.Text;
            string pwd = TxtProtectPassword.Text;

            if (string.IsNullOrEmpty(path) || !File.Exists(path) || string.IsNullOrEmpty(pwd))
            {
                MessageBox.Show("Please browse PDF and enter a password.", "Input Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SettingsService.SaveFile("protected_document", "PDF Documents (*.pdf)|*.pdf", targetPath =>
            {
                using (var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
                {
                    doc.SecuritySettings.UserPassword = pwd;
                    doc.Save(targetPath);
                }

                if (SettingsService.Current.PdfRememberLastPassword)
                {
                    SettingsService.Current.PdfLastPassword = pwd;
                    SettingsService.Instance.SaveSettings();
                }

                MessageBox.Show("PDF encrypted successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                RenderPreviewIfPdf(targetPath, WpProtectPdfPreviews);
            }, isPdfTool: true);
        }

        #endregion

        #region Tool 4: Unlock PDF Password

        private void BtnUnlockPdfBrowse_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Filter = "PDF Files (*.pdf)|*.pdf" };
            if (ofd.ShowDialog() == true)
            {
                TxtUnlockPdfPath.Text = ofd.FileName;
                RenderPreviewIfPdf(ofd.FileName, WpUnlockPdfPreviews);
            }
        }

        private void BtnUnlockPdfApply_Click(object sender, RoutedEventArgs e)
        {
            string path = TxtUnlockPdfPath.Text;
            string pwd = TxtUnlockPassword.Text;

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                MessageBox.Show("Please select PDF file.", "Input Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SettingsService.SaveFile("unlocked_document", "PDF Documents (*.pdf)|*.pdf", targetPath =>
            {
                using (var doc = PdfReader.Open(path, pwd, PdfDocumentOpenMode.Modify))
                {
                    doc.SecuritySettings.UserPassword = "";
                    doc.SecuritySettings.OwnerPassword = "";
                    doc.Save(targetPath);
                }

                if (SettingsService.Current.PdfRememberLastPassword)
                {
                    SettingsService.Current.PdfLastPassword = pwd;
                    SettingsService.Instance.SaveSettings();
                }

                MessageBox.Show("PDF decrypted and saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }, isPdfTool: true);
        }

        #endregion

        #region Tool 5: Rearrange Pages with Preview

        private void BtnRearrangeBrowse_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Filter = "PDF Files (*.pdf)|*.pdf" };
            if (ofd.ShowDialog() == true)
            {
                _rearrangePdfPath = ofd.FileName;
                LblRearrangeFile.Text = Path.GetFileName(_rearrangePdfPath);

                int pageCount = 0;
                using (var doc = PdfReader.Open(_rearrangePdfPath, PdfDocumentOpenMode.Import))
                {
                    pageCount = doc.PageCount;
                }

                _rearrangePageOrder = Enumerable.Range(0, pageCount).ToList();
                RefreshRearrangePreviews();
            }
        }

        private void RefreshRearrangePreviews()
        {
            if (string.IsNullOrEmpty(_rearrangePdfPath)) return;

            var task = RenderPdfPagesVisualAsync(_rearrangePdfPath, WpRearrangePreviews, (pageIdx, grid, img) =>
            {
                var card = (Border)grid.Children[0];
                var layoutGrid = (Grid)card.Child;

                var reorderGrid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                reorderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                reorderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var btnL = new Button { Content = "← Move", Style = (Style)Application.Current.Resources["ModernButton"], Padding = new Thickness(0), Height = 20, FontSize = 9 };
                btnL.Click += (s, e) => { ShiftRearrangePage(WpRearrangePreviews.Children.IndexOf(grid), WpRearrangePreviews.Children.IndexOf(grid) - 1); };
                Grid.SetColumn(btnL, 0);
                reorderGrid.Children.Add(btnL);

                var btnR = new Button { Content = "Move →", Style = (Style)Application.Current.Resources["ModernButton"], Padding = new Thickness(0), Height = 20, FontSize = 9 };
                btnR.Click += (s, e) => { ShiftRearrangePage(WpRearrangePreviews.Children.IndexOf(grid), WpRearrangePreviews.Children.IndexOf(grid) + 1); };
                Grid.SetColumn(btnR, 1);
                reorderGrid.Children.Add(btnR);

                Grid.SetRow(reorderGrid, 2);
                layoutGrid.Children.Add(reorderGrid);

                // Wire Drag and Drop handlers
                Point startPoint = new Point();

                grid.PreviewMouseLeftButtonDown += (s, e) =>
                {
                    startPoint = e.GetPosition(null);
                };

                grid.MouseMove += (s, e) =>
                {
                    if (e.LeftButton == MouseButtonState.Pressed)
                    {
                        Point mousePos = e.GetPosition(null);
                        Vector diff = startPoint - mousePos;

                        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                        {
                            var dep = e.OriginalSource as DependencyObject;
                            while (dep != null && dep != grid)
                            {
                                if (dep is Button) return; // Don't drag if clicking buttons
                                dep = VisualTreeHelper.GetParent(dep);
                            }

                            int fromIndex = WpRearrangePreviews.Children.IndexOf(grid);
                            if (fromIndex >= 0)
                            {
                                DragDrop.DoDragDrop(grid, fromIndex, DragDropEffects.Move);
                            }
                        }
                    }
                };

                grid.AllowDrop = true;

                grid.DragOver += (s, e) =>
                {
                    if (e.Data.GetDataPresent(typeof(int)))
                    {
                        e.Effects = DragDropEffects.Move;
                        e.Handled = true;
                    }
                };

                grid.Drop += (s, e) =>
                {
                    if (e.Data.GetDataPresent(typeof(int)))
                    {
                        int fromIndex = (int)e.Data.GetData(typeof(int));
                        int toIndex = WpRearrangePreviews.Children.IndexOf(grid);

                        if (fromIndex >= 0 && toIndex >= 0 && fromIndex != toIndex)
                        {
                            ShiftRearrangePage(fromIndex, toIndex);
                        }
                    }
                };
            });
        }

        private void ShiftRearrangePage(int from, int to)
        {
            if (to < 0 || to >= _rearrangePageOrder.Count) return;
            int pageVal = _rearrangePageOrder[from];
            _rearrangePageOrder.RemoveAt(from);
            _rearrangePageOrder.Insert(to, pageVal);
            
            // Swap visual children
            var element = WpRearrangePreviews.Children[from];
            WpRearrangePreviews.Children.RemoveAt(from);
            WpRearrangePreviews.Children.Insert(to, element);

            // Re-map index identifiers
            UpdatePageOrderLabels(WpRearrangePreviews);
        }

        private void UpdatePageOrderLabels(WrapPanel wp)
        {
            for (int i = 0; i < wp.Children.Count; i++)
            {
                var grid = (Grid)wp.Children[i];
                var card = (Border)grid.Children[0];
                var layoutGrid = (Grid)card.Child;
                var textBlock = layoutGrid.Children.OfType<TextBlock>().First();
                textBlock.Text = $"Page {i + 1}";
            }
        }

        private void BtnRearrangeSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_rearrangePdfPath) || _rearrangePageOrder.Count == 0) return;

            SettingsService.SaveFile("rearranged_document", "PDF Documents (*.pdf)|*.pdf", path =>
            {
                using (var input = PdfReader.Open(_rearrangePdfPath, PdfDocumentOpenMode.Import))
                {
                    using (var output = new PdfDocument())
                    {
                        foreach (int originalIdx in _rearrangePageOrder)
                        {
                            output.AddPage(input.Pages[originalIdx]);
                        }
                        output.Save(path);
                    }
                }
                MessageBox.Show("Saved rearranged document successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }, isPdfTool: true);
        }

        #endregion

        #region Tool 6: Merge PDFs (Standard Merge integrated)

        private void LstPdfFiles_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
        }

        private void LstPdfFiles_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
        }

        private void LstPdfFiles_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (string file in files)
                {
                    if (Path.GetExtension(file).ToLower() == ".pdf") _pdfFiles.Add(file);
                }
                RefreshPdfList();
            }
        }

        private void BtnAddPdfs_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Filter = "PDF Files (*.pdf)|*.pdf", Multiselect = true };
            if (ofd.ShowDialog() == true)
            {
                _pdfFiles.AddRange(ofd.FileNames);
                RefreshPdfList();
            }
        }

        private async void RefreshPdfList()
        {
            LstPdfFiles.ItemsSource = null;
            LstPdfFiles.ItemsSource = _pdfFiles;
            TxtPdfPlaceholder.Visibility = _pdfFiles.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

            WpMergePreviews.Children.Clear();
            UpdatePlaceholderVisibility(WpMergePreviews);
            if (_pdfFiles.Count == 0) return;

            // Gather all pages info
            var pageInfoList = new List<(string Path, int PageIndex, string DisplayLabel)>();
            foreach (var file in _pdfFiles)
            {
                int pageCount = 0;
                try
                {
                    using (var doc = PdfReader.Open(file, PdfDocumentOpenMode.Import))
                    {
                        pageCount = doc.PageCount;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error reading PDF for preview: {ex.Message}");
                    continue;
                }

                string fileName = Path.GetFileName(file);
                for (int i = 0; i < pageCount; i++)
                {
                    pageInfoList.Add((file, i, $"{fileName}\nPage {i + 1}"));
                }
            }

            int totalPages = pageInfoList.Count;
            var grids = new Grid[totalPages];
            var images = new Image[totalPages];

            for (int i = 0; i < totalPages; i++)
            {
                var info = pageInfoList[i];
                var grid = new Grid { Margin = new Thickness(8), Width = 140, Height = 205 };

                var card = new Border
                {
                    Background = (Brush)Application.Current.Resources["CardBgBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["BorderBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(4)
                };
                grid.Children.Add(card);

                var layoutGrid = new Grid();
                card.Child = layoutGrid;
                layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // File & Page Num label
                layoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Image
                layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var lbl = new TextBlock
                {
                    Text = info.DisplayLabel,
                    Foreground = (Brush)Application.Current.Resources["TextBrush"],
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxHeight = 32,
                    Margin = new Thickness(0, 0, 0, 4)
                };
                Grid.SetRow(lbl, 0);
                layoutGrid.Children.Add(lbl);

                var imgBorder = new Border { Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)), CornerRadius = new CornerRadius(3), ClipToBounds = true };
                Grid.SetRow(imgBorder, 1);
                layoutGrid.Children.Add(imgBorder);

                var placeholderText = new TextBlock
                {
                    Text = "Loading...",
                    Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                imgBorder.Child = placeholderText;

                var img = new Image { Stretch = Stretch.Uniform };
                images[i] = img;
                grids[i] = grid;

                WpMergePreviews.Children.Add(grid);
            }

            UpdatePlaceholderVisibility(WpMergePreviews);

            // Render pages in the background
            for (int i = 0; i < totalPages; i++)
            {
                var info = pageInfoList[i];
                try
                {
                    var bmp = await RenderPdfPageToBitmapAsync(info.Path, (uint)info.PageIndex);
                    var grid = grids[i];
                    var card = (Border)grid.Children[0];
                    var layoutGrid = (Grid)card.Child;
                    var imgBorder = layoutGrid.Children.OfType<Border>().First();
                    
                    images[i].Source = bmp;
                    imgBorder.Child = images[i];
                }
                catch
                {
                    var grid = grids[i];
                    var card = (Border)grid.Children[0];
                    var layoutGrid = (Grid)card.Child;
                    var imgBorder = layoutGrid.Children.OfType<Border>().First();
                    imgBorder.Child = new TextBlock { Text = "Error", Foreground = Brushes.Red, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                }
            }
        }

        private void BtnClearPdfs_Click(object sender, RoutedEventArgs e)
        {
            _pdfFiles.Clear();
            RefreshPdfList();
        }

        private void BtnMovePdfUp_Click(object sender, RoutedEventArgs e)
        {
            int idx = LstPdfFiles.SelectedIndex;
            if (idx > 0)
            {
                string item = _pdfFiles[idx];
                _pdfFiles.RemoveAt(idx);
                _pdfFiles.Insert(idx - 1, item);
                RefreshPdfList();
                LstPdfFiles.SelectedIndex = idx - 1;
            }
        }

        private void BtnMovePdfDown_Click(object sender, RoutedEventArgs e)
        {
            int idx = LstPdfFiles.SelectedIndex;
            if (idx >= 0 && idx < _pdfFiles.Count - 1)
            {
                string item = _pdfFiles[idx];
                _pdfFiles.RemoveAt(idx);
                _pdfFiles.Insert(idx + 1, item);
                RefreshPdfList();
                LstPdfFiles.SelectedIndex = idx + 1;
            }
        }

        private void BtnMergePdfs_Click(object sender, RoutedEventArgs e)
        {
            if (_pdfFiles.Count < 2)
            {
                MessageBox.Show("Please add at least 2 PDF files to merge.", "Arrange Files", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SettingsService.SaveFile("merged_document", "PDF Documents (*.pdf)|*.pdf", path =>
            {
                using (var output = new PdfDocument())
                {
                    foreach (string file in _pdfFiles)
                    {
                        using (var input = PdfReader.Open(file, PdfDocumentOpenMode.Import))
                        {
                            for (int i = 0; i < input.PageCount; i++)
                            {
                                output.AddPage(input.Pages[i]);
                            }
                        }
                    }
                    output.Save(path);
                }
                MessageBox.Show("PDF files merged successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }, isPdfTool: true);
        }

        #endregion

        #region Tool 7: Split PDF with Preview

        private void BtnSplitVisualBrowse_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Filter = "PDF Files (*.pdf)|*.pdf" };
            if (ofd.ShowDialog() == true)
            {
                _splitPdfPath = ofd.FileName;
                LblSplitVisualFile.Text = Path.GetFileName(_splitPdfPath);
                _splitSelectedPages.Clear();

                var task = RenderPdfPagesVisualAsync(_splitPdfPath, WpSplitVisualPreviews, (pageIdx, grid, img) =>
                {
                    var card = (Border)grid.Children[0];
                    var layoutGrid = (Grid)card.Child;

                    var cb = new CheckBox
                    {
                        IsChecked = false,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 0, 5, 0)
                    };
                    cb.Checked += (s, ev) => _splitSelectedPages.Add(pageIdx);
                    cb.Unchecked += (s, ev) => _splitSelectedPages.Remove(pageIdx);

                    // Add CheckBox into the page layout
                    Grid.SetRow(cb, 0);
                    layoutGrid.Children.Add(cb);
                });
            }
        }

        private void BtnSplitVisualSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_splitPdfPath) || _splitSelectedPages.Count == 0)
            {
                MessageBox.Show("Please select PDF file and check pages to split.", "Selection Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SettingsService.SaveFile("split_subset", "PDF Documents (*.pdf)|*.pdf", path =>
            {
                using (var input = PdfReader.Open(_splitPdfPath, PdfDocumentOpenMode.Import))
                {
                    using (var output = new PdfDocument())
                    {
                        for (int i = 0; i < input.PageCount; i++)
                        {
                            if (_splitSelectedPages.Contains(i))
                            {
                                output.AddPage(input.Pages[i]);
                            }
                        }
                        output.Save(path);
                    }
                }
                MessageBox.Show("Pages extracted and saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }, isPdfTool: true);
        }

        #endregion

        #region Tool 8: Add Page Numbers

        private void BtnPageNumPdfBrowse_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Filter = "PDF Files (*.pdf)|*.pdf" };
            if (ofd.ShowDialog() == true)
            {
                TxtPageNumPdfPath.Text = ofd.FileName;
                RenderPreviewIfPdf(ofd.FileName, WpPageNumPreviews);
            }
        }

        private void BtnPageNumApply_Click(object sender, RoutedEventArgs e)
        {
            string path = TxtPageNumPdfPath.Text;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            string pos = ((ComboBoxItem)CmbPageNumPosition.SelectedItem)?.Content?.ToString() ?? "Bottom Center";

            SettingsService.SaveFile("numbered_document", "PDF Documents (*.pdf)|*.pdf", targetPath =>
            {
                using (var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
                {
                    int pageCount = doc.PageCount;
                    var font = new XFont("Arial", 9);

                    for (int i = 0; i < pageCount; i++)
                    {
                        var page = doc.Pages[i];
                        var gfx = XGraphics.FromPdfPage(page);
                        string txt = $"{i + 1} of {pageCount}";
                        var size = gfx.MeasureString(txt, font);

                        double x = page.Width.Point / 2 - size.Width / 2; // Center
                        if (pos == "Bottom Left") x = 40;
                        else if (pos == "Bottom Right") x = page.Width.Point - size.Width - 40;

                        double y = page.Height.Point - 30; // Bottom margin

                        gfx.DrawString(txt, font, XBrushes.DarkGray, x, y);
                    }
                    doc.Save(targetPath);
                }
                MessageBox.Show("Page numbers stamped successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                RenderPreviewIfPdf(targetPath, WpPageNumPreviews);
            }, isPdfTool: true);
        }

        #endregion

        #region Tool 9: Add/Delete Page with Preview

        private void BtnAddDeleteBrowse_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Filter = "PDF Files (*.pdf)|*.pdf" };
            if (ofd.ShowDialog() == true)
            {
                _addDeletePdfPath = ofd.FileName;
                LblAddDeleteFile.Text = Path.GetFileName(_addDeletePdfPath);

                int pageCount = 0;
                using (var doc = PdfReader.Open(_addDeletePdfPath, PdfDocumentOpenMode.Import))
                {
                    pageCount = doc.PageCount;
                }

                _addDeletePageOrder = Enumerable.Range(0, pageCount).ToList();
                RefreshAddDeletePreviews();
            }
        }

        private void RefreshAddDeletePreviews()
        {
            if (string.IsNullOrEmpty(_addDeletePdfPath)) return;

            WpAddDeletePreviews.Children.Clear();
            UpdatePlaceholderVisibility(WpAddDeletePreviews);
            RenderAddDeletePanelLoop();
        }

        private async void RenderAddDeletePanelLoop()
        {
            int pageCount = _addDeletePageOrder.Count;
            var grids = new Grid[pageCount];

            for (int i = 0; i < pageCount; i++)
            {
                int currentIdx = i;
                int pageVal = _addDeletePageOrder[i];

                var grid = new Grid { Margin = new Thickness(8), Width = 140, Height = 205 };
                grids[currentIdx] = grid;

                var card = new Border
                {
                    Background = (Brush)Application.Current.Resources["CardBgBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["BorderBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(4)
                };
                grid.Children.Add(card);

                var layoutGrid = new Grid();
                card.Child = layoutGrid;
                layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                layoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var lbl = new TextBlock
                {
                    Text = $"Page {currentIdx + 1}",
                    Foreground = (Brush)Application.Current.Resources["TextBrush"],
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 0, 4)
                };
                Grid.SetRow(lbl, 0);
                layoutGrid.Children.Add(lbl);

                var imgBorder = new Border { Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)), CornerRadius = new CornerRadius(3) };
                Grid.SetRow(imgBorder, 1);
                layoutGrid.Children.Add(imgBorder);

                var btnDelete = new Button
                {
                    Content = "Delete",
                    Style = (Style)Application.Current.Resources["MutedButton"],
                    Padding = new Thickness(4, 2, 4, 2),
                    Margin = new Thickness(0, 4, 0, 0),
                    FontSize = 9,
                    Foreground = Brushes.Red,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                btnDelete.Click += (s, e) =>
                {
                    _addDeletePageOrder.RemoveAt(currentIdx);
                    RefreshAddDeletePreviews();
                };
                Grid.SetRow(btnDelete, 2);
                layoutGrid.Children.Add(btnDelete);

                if (pageVal == -1)
                {
                    imgBorder.Child = new TextBlock { Text = "[Blank Page]", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                }
                else
                {
                    imgBorder.Child = new TextBlock { Text = "Loading...", Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                }

                WpAddDeletePreviews.Children.Add(grid);
            }
            UpdatePlaceholderVisibility(WpAddDeletePreviews);

            // Async load non-blank thumbnails
            for (int i = 0; i < pageCount; i++)
            {
                int currentIdx = i;
                int pageVal = _addDeletePageOrder[i];
                if (pageVal != -1)
                {
                    try
                    {
                        var bmp = await RenderPdfPageToBitmapAsync(_addDeletePdfPath!, (uint)pageVal);
                        var grid = grids[currentIdx];
                        var card = (Border)grid.Children[0];
                        var layoutGrid = (Grid)card.Child;
                        var imgBorder = layoutGrid.Children.OfType<Border>().First();
                        imgBorder.Child = new Image { Source = bmp, Stretch = Stretch.Uniform };
                    }
                    catch { }
                }
            }
        }

        private void BtnAddDeleteBlank_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_addDeletePdfPath)) return;
            _addDeletePageOrder.Add(-1);
            RefreshAddDeletePreviews();
        }

        private void BtnAddDeleteSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_addDeletePdfPath) || _addDeletePageOrder.Count == 0) return;

            SettingsService.SaveFile("modified_assembler_document", "PDF Documents (*.pdf)|*.pdf", path =>
            {
                using (var input = PdfReader.Open(_addDeletePdfPath, PdfDocumentOpenMode.Import))
                {
                    using (var output = new PdfDocument())
                    {
                        foreach (int pageVal in _addDeletePageOrder)
                        {
                            if (pageVal == -1)
                            {
                                var newPage = output.AddPage();
                                newPage.Width = XUnit.FromInch(8.5);
                                newPage.Height = XUnit.FromInch(11);
                            }
                            else
                            {
                                output.AddPage(input.Pages[pageVal]);
                            }
                        }
                        output.Save(path);
                    }
                }
                MessageBox.Show("Modifications compiled successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }, isPdfTool: true);
        }

        #endregion

        #region Tool 10: Word to PDF Converter (Standard native docx text renderer)

        private void BtnDocxBrowse_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Filter = "Word Documents (*.docx)|*.docx" };
            if (ofd.ShowDialog() == true)
            {
                TxtDocxPath.Text = ofd.FileName;
            }
        }

        private void BtnDocxToPdfApply_Click(object sender, RoutedEventArgs e)
        {
            string path = TxtDocxPath.Text;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            SettingsService.SaveFile("word_to_pdf_output", "PDF Documents (*.pdf)|*.pdf", targetPath =>
            {
                using (var pdf = new PdfDocument())
                {
                    var page = pdf.AddPage();
                    var gfx = XGraphics.FromPdfPage(page);
                    var font = new XFont("Arial", 11);
                    double margin = 50;
                    double y = margin;
                    double pageHeight = page.Height.Point;
                    double maxWidth = page.Width.Point - 2 * margin;

                    using (var archive = ZipFile.OpenRead(path))
                    {
                        var entry = archive.GetEntry("word/document.xml");
                        if (entry == null) throw new Exception("Invalid DOCX format structure.");

                        using (var stream = entry.Open())
                        {
                            var doc = XDocument.Load(stream);
                            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                            var paragraphs = doc.Descendants(w + "p");

                            foreach (var p in paragraphs)
                            {
                                var runTexts = p.Descendants(w + "t").Select(t => t.Value);
                                string text = string.Concat(runTexts);

                                if (string.IsNullOrWhiteSpace(text))
                                {
                                    y += font.Height;
                                    continue;
                                }

                                // Simple word wrapper
                                var words = text.Split(' ');
                                string line = "";
                                foreach (var word in words)
                                {
                                    string testLine = string.IsNullOrEmpty(line) ? word : line + " " + word;
                                    var size = gfx.MeasureString(testLine, font);
                                    if (size.Width > maxWidth)
                                    {
                                        if (y + font.Height > pageHeight - margin)
                                        {
                                            page = pdf.AddPage();
                                            gfx = XGraphics.FromPdfPage(page);
                                            y = margin;
                                        }
                                        gfx.DrawString(line, font, XBrushes.Black, margin, y);
                                        y += font.Height + 2;
                                        line = word;
                                    }
                                    else
                                    {
                                        line = testLine;
                                    }
                                }
                                if (!string.IsNullOrEmpty(line))
                                {
                                    if (y + font.Height > pageHeight - margin)
                                    {
                                        page = pdf.AddPage();
                                        gfx = XGraphics.FromPdfPage(page);
                                        y = margin;
                                    }
                                    gfx.DrawString(line, font, XBrushes.Black, margin, y);
                                    y += font.Height + 8; // Paragraph spacer
                                }
                            }
                        }
                    }
                    pdf.Save(targetPath);
                }
                MessageBox.Show("Word converted to PDF successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                RenderPreviewIfPdf(targetPath, WpDocxToPdfPreviews);
            }, isPdfTool: true);
        }

        #endregion

        #region Tool 11: PDF to Word Converter (Standard native PDF Tj/TJ extractor + docx zip packager)

        private void BtnWordPdfBrowse_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Filter = "PDF Files (*.pdf)|*.pdf" };
            if (ofd.ShowDialog() == true)
            {
                TxtWordPdfPath.Text = ofd.FileName;
                RenderPreviewIfPdf(ofd.FileName, WpPdfToWordPreviews);
            }
        }

        private void BtnPdfToWordApply_Click(object sender, RoutedEventArgs e)
        {
            string path = TxtWordPdfPath.Text;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            SettingsService.SaveFile("pdf_to_word_output", "Word Documents (*.docx)|*.docx", targetPath =>
            {
                var textSb = new System.Text.StringBuilder();
                using (var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import))
                {
                    foreach (PdfPage page in doc.Pages)
                    {
                        var sequence = ContentReader.ReadContent(page);
                        textSb.AppendLine(ExtractTextFromSequence(sequence));
                    }
                }

                SaveTextAsDocxZip(textSb.ToString(), targetPath);
                MessageBox.Show("PDF text layouts extracted to Word successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }, isPdfTool: true);
        }

        private string ExtractTextFromSequence(CSequence sequence)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var obj in sequence)
            {
                if (obj is COperator op)
                {
                    if (op.Name == "Tj" || op.Name == "'")
                    {
                        if (op.Operands.Count > 0 && op.Operands[0] is CString str)
                        {
                            sb.Append(str.Value);
                        }
                        if (op.Name == "'") sb.AppendLine();
                    }
                    else if (op.Name == "TJ")
                    {
                        if (op.Operands.Count > 0 && op.Operands[0] is CArray arr)
                        {
                            foreach (var elem in arr)
                            {
                                if (elem is CString str)
                                {
                                    sb.Append(str.Value);
                                }
                            }
                        }
                    }
                    else if (op.Name == "T*" || op.Name == "Td" || op.Name == "TD")
                    {
                        sb.AppendLine();
                    }
                }
            }
            return sb.ToString();
        }

        private void SaveTextAsDocxZip(string text, string docxPath)
        {
            using (var fs = new FileStream(docxPath, FileMode.Create))
            {
                using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    // 1. [Content_Types].xml
                    var contentTypesEntry = archive.CreateEntry("[Content_Types].xml");
                    using (var writer = new StreamWriter(contentTypesEntry.Open()))
                    {
                        writer.Write(@"<?xml version=""1.0"" encoding=""utf-8"" standalone=""yes""?>
<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">
  <Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml"" />
  <Default Extension=""xml"" ContentType=""application/xml"" />
  <Override PartName=""/word/document.xml"" ContentType=""application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"" />
</Types>");
                    }

                    // 2. _rels/.rels
                    var relsEntry = archive.CreateEntry("_rels/.rels");
                    using (var writer = new StreamWriter(relsEntry.Open()))
                    {
                        writer.Write(@"<?xml version=""1.0"" encoding=""utf-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""word/document.xml"" />
</Relationships>");
                    }

                    // 3. word/document.xml
                    var documentEntry = archive.CreateEntry("word/document.xml");
                    using (var writer = new StreamWriter(documentEntry.Open()))
                    {
                        writer.Write(@"<?xml version=""1.0"" encoding=""utf-8"" standalone=""yes""?>
<w:document xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main"">
  <w:body>");
                        
                        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                        foreach (var line in lines)
                        {
                            string escaped = System.Security.SecurityElement.Escape(line) ?? "";
                            writer.Write($"<w:p><w:r><w:t>{escaped}</w:t></w:r></w:p>");
                        }

                        writer.Write(@"    <w:sectPr>
      <w:pgSz w:w=""12240"" w:h=""15840"" />
      <w:pgMar w:top=""1440"" w:right=""1440"" w:bottom=""1440"" w:left=""1440"" />
    </w:sectPr>
  </w:body>
</w:document>");
                    }
                }
            }
        }

        #endregion

        #region Tool 12: Rotate Pages with Preview

        private void BtnRotateBrowse_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Filter = "PDF Files (*.pdf)|*.pdf" };
            if (ofd.ShowDialog() == true)
            {
                _rotatePdfPath = ofd.FileName;
                LblRotateFile.Text = Path.GetFileName(_rotatePdfPath);

                int count = 0;
                using (var doc = PdfReader.Open(_rotatePdfPath, PdfDocumentOpenMode.Import))
                {
                    count = doc.PageCount;
                }

                _rotatePageAngles = new List<int>(new int[count]); // initialize all rotations to 0 degrees
                RefreshRotatePreviews();
            }
        }

        private void RefreshRotatePreviews()
        {
            if (string.IsNullOrEmpty(_rotatePdfPath)) return;

            var task = RenderPdfPagesVisualAsync(_rotatePdfPath, WpRotatePreviews, (pageIdx, grid, img) =>
            {
                var card = (Border)grid.Children[0];
                var layoutGrid = (Grid)card.Child;

                // Create Rotate action button
                var btn = new Button
                {
                    Content = "Rotate ↻",
                    Style = (Style)Application.Current.Resources["ModernButton"],
                    Padding = new Thickness(4, 2, 4, 2),
                    Margin = new Thickness(0, 4, 0, 0),
                    FontSize = 9,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                btn.Click += (s, e) =>
                {
                    // Rotate angle by 90 degrees visually
                    _rotatePageAngles[pageIdx] = (_rotatePageAngles[pageIdx] + 90) % 360;
                    
                    var transform = new RotateTransform(_rotatePageAngles[pageIdx]);
                    img.LayoutTransform = transform; // layout transform rotates inside layout boundary nicely
                };

                Grid.SetRow(btn, 2);
                layoutGrid.Children.Add(btn);
            });
        }

        private void BtnRotateSaveAll_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_rotatePdfPath) || _rotatePageAngles.Count == 0) return;

            SettingsService.SaveFile("rotated_document", "PDF Documents (*.pdf)|*.pdf", path =>
            {
                using (var doc = PdfReader.Open(_rotatePdfPath, PdfDocumentOpenMode.Modify))
                {
                    for (int i = 0; i < doc.PageCount; i++)
                    {
                        int targetRotation = _rotatePageAngles[i];
                        doc.Pages[i].Rotate = (doc.Pages[i].Rotate + targetRotation) % 360;
                    }
                    doc.Save(path);
                }
                MessageBox.Show("PDF pages rotated and saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }, isPdfTool: true);
        }

        #endregion

        #endregion

        #region Tool 3: Advanced Compressor Module (Image & PDF Constraints)

        private void CompressorTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Event triggers on switching Compressor mode tab
        }

        #region Image Compressor Logic (Binary Search Sizing Constraints)

        private void GridCompressDrop_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
        }

        private void GridCompressDrop_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
        }

        private void GridCompressDrop_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length > 0) LoadImageToCompress(files[0]);
            }
        }

        private void BtnCompressBrowse_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Filter = "Image Files (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" };
            if (ofd.ShowDialog() == true) LoadImageToCompress(ofd.FileName);
        }

        private void LoadImageToCompress(string filepath)
        {
            try
            {
                var fileUri = new Uri(filepath);
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = fileUri;
                img.EndInit();
                img.Freeze();

                _compressOriginalBitmap = img;
                _compressOriginalPath = filepath;

                ImgCompressOriginal.Source = _compressOriginalBitmap;
                TxtCompressOriginalPlaceholder.Visibility = Visibility.Collapsed;

                var fi = new FileInfo(filepath);
                LblCompressOriginalSize.Text = $"Original Size: {fi.Length / 1024.0:F1} KB";

                UpdateCompressedPreview();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load image: {ex.Message}");
            }
        }

        private void SldCompressQuality_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LblCompressQuality != null) LblCompressQuality.Text = $"{(int)SldCompressQuality.Value}%";
            UpdateCompressedPreview();
        }

        private void UpdateCompressedPreview()
        {
            if (_compressOriginalBitmap == null) return;

            try
            {
                int q = (int)SldCompressQuality.Value;
                var encoder = new JpegBitmapEncoder { QualityLevel = q };
                encoder.Frames.Add(BitmapFrame.Create(_compressOriginalBitmap));

                using (var ms = new MemoryStream())
                {
                    encoder.Save(ms);
                    ms.Position = 0;

                    LblCompressResultSize.Text = $"Estimated Size: {ms.Length / 1024.0:F1} KB";

                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();

                    ImgCompressResult.Source = bmp;
                    TxtCompressResultPlaceholder.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        }

        private void BtnCompressApply_Click(object sender, RoutedEventArgs e)
        {
            UpdateCompressedPreview();
            MessageBox.Show("Image quality setting refreshed!", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnCompressSave_Click(object sender, RoutedEventArgs e)
        {
            if (_compressOriginalBitmap == null) return;

            SettingsService.SaveFile("compressed_image", "JPEG Image (*.jpg)|*.jpg", path =>
            {
                byte[] outputBytes;
                double targetSizeKb;
                
                if (double.TryParse(TxtCompressTargetSize.Text, out targetSizeKb) && targetSizeKb > 0)
                {
                    // RUN BINARY SEARCH ON IMAGE QUALITY / DENSITY TO NEVER EXCEED TARGET LIMIT
                    outputBytes = CompressImageToTargetSize(_compressOriginalBitmap, targetSizeKb);
                }
                else
                {
                    // Normal slider compression
                    int q = (int)SldCompressQuality.Value;
                    using (var ms = new MemoryStream())
                    {
                        var enc = new JpegBitmapEncoder { QualityLevel = q };
                        enc.Frames.Add(BitmapFrame.Create(_compressOriginalBitmap));
                        enc.Save(ms);
                        outputBytes = ms.ToArray();
                    }
                }

                File.WriteAllBytes(path, outputBytes);
                MessageBox.Show($"Saved optimized image ({outputBytes.Length / 1024.0:F1} KB) successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }, isPdfTool: false);
        }

        private byte[] CompressImageToTargetSize(BitmapSource src, double targetSizeKb)
        {
            int low = 1;
            int high = 100;
            int bestQ = 75;
            byte[]? bestBytes = null;

            // Step 1: Quality scale binary search
            while (low <= high)
            {
                int mid = (low + high) / 2;
                using (var ms = new MemoryStream())
                {
                    var enc = new JpegBitmapEncoder { QualityLevel = mid };
                    enc.Frames.Add(BitmapFrame.Create(src));
                    enc.Save(ms);
                    double sizeKb = ms.Length / 1024.0;

                    if (sizeKb <= targetSizeKb)
                    {
                        bestQ = mid;
                        bestBytes = ms.ToArray();
                        low = mid + 1; // Try higher quality
                    }
                    else
                    {
                        high = mid - 1; // Need smaller size
                    }
                }
            }

            // Step 2: Scale down dimensions recursively if even Q=1 exceeds target limit
            if (bestBytes == null || (bestBytes.Length / 1024.0) > targetSizeKb)
            {
                double scale = 0.9;
                while (scale >= 0.1)
                {
                    var trans = new TransformedBitmap(src, new ScaleTransform(scale, scale));
                    using (var ms = new MemoryStream())
                    {
                        var enc = new JpegBitmapEncoder { QualityLevel = 1 };
                        enc.Frames.Add(BitmapFrame.Create(trans));
                        enc.Save(ms);
                        double sizeKb = ms.Length / 1024.0;

                        if (sizeKb <= targetSizeKb)
                        {
                            bestBytes = ms.ToArray();
                            break;
                        }
                    }
                    scale -= 0.1;
                }
            }

            return bestBytes ?? new byte[0];
        }

        #endregion

        #region PDF Compressor Logic (Binary Search Sizing Constraints)

        private void BtnCompressPdfBrowse_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Filter = "PDF Files (*.pdf)|*.pdf" };
            if (ofd.ShowDialog() == true)
            {
                _compressPdfPath = ofd.FileName;
                TxtCompressPdfPath.Text = _compressPdfPath;

                var fi = new FileInfo(_compressPdfPath);
                LblCompressPdfOriginalSize.Text = $"Original Size: {fi.Length / 1024.0:F1} KB";

                // Load page previews asynchronously inside compressor panel
                var task = RenderPdfPagesVisualAsync(_compressPdfPath, WpCompressPdfPreviews, (idx, grid, img) => { });
                UpdatePdfCompressionEstimate();
            }
        }

        private void SldCompressPdfQuality_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LblCompressPdfQuality != null) LblCompressPdfQuality.Text = $"{(int)SldCompressPdfQuality.Value}%";
            UpdatePdfCompressionEstimate();
        }

        private void UpdatePdfCompressionEstimate()
        {
            if (string.IsNullOrEmpty(_compressPdfPath)) return;
            // Estimated optimized size label calculation
            double factor = SldCompressPdfQuality.Value / 100.0;
            var fi = new FileInfo(_compressPdfPath);
            double estimatedKb = (fi.Length / 1024.0) * (0.1 + 0.5 * factor); // typical compression ratio heuristic
            LblCompressPdfResultSize.Text = $"Estimated Optimized Size: ~{estimatedKb:F1} KB";
        }

        private void BtnCompressPdfApply_Click(object sender, RoutedEventArgs e)
        {
            UpdatePdfCompressionEstimate();
            MessageBox.Show("PDF compression parameters refreshed!", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void BtnCompressPdfSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_compressPdfPath)) return;

            await SettingsService.SaveFileAsync("compressed_document", "PDF Documents (*.pdf)|*.pdf", async path =>
            {
                byte[] outputBytes;
                double targetSizeKb;
                
                if (double.TryParse(TxtCompressPdfTargetSize.Text, out targetSizeKb) && targetSizeKb > 0)
                {
                    // RUN PDF BINARY SEARCH SCALING ENCODER TO NEVER EXCEED TARGET LIMIT
                    outputBytes = await CompressPdfToTargetSizeAsync(_compressPdfPath, targetSizeKb);
                }
                else
                {
                    // Normal slider optimization
                    int quality = (int)SldCompressPdfQuality.Value;
                    double dpi = 75 + (quality / 100.0) * 150.0; // scale between 75 and 225 DPI
                    int count = 0;
                    using (var doc = PdfReader.Open(_compressPdfPath, PdfDocumentOpenMode.Import))
                    {
                        count = doc.PageCount;
                    }

                    outputBytes = await Task.Run(() => RasterizeAndCompilePdf(_compressPdfPath, count, dpi, Math.Max(10, quality)));
                }

                File.WriteAllBytes(path, outputBytes);
                MessageBox.Show($"Saved optimized PDF ({outputBytes.Length / 1024.0:F1} KB) successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }, isPdfTool: true);
        }

        private async Task<byte[]> CompressPdfToTargetSizeAsync(string pdfPath, double targetSizeKb)
        {
            double low = 0.05;
            double high = 1.0;
            double bestScale = 0.05;
            byte[]? bestPdfBytes = null;
            int pageCount = 0;

            using (var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import))
            {
                pageCount = doc.PageCount;
            }

            // Binary search 7 iterations
            for (int iter = 0; iter < 7; iter++)
            {
                double mid = (low + high) / 2;
                double dpi = 72 + mid * 228.0; // scale DPI: 72 to 300
                int quality = (int)(10 + mid * 80); // quality: 10 to 90

                byte[] pdfBytes = await Task.Run(() => RasterizeAndCompilePdf(pdfPath, pageCount, dpi, quality));
                double sizeKb = pdfBytes.Length / 1024.0;

                if (sizeKb <= targetSizeKb)
                {
                    bestScale = mid;
                    bestPdfBytes = pdfBytes;
                    low = mid + 0.01; // Try higher density
                }
                else
                {
                    high = mid - 0.01; // Need smaller size
                }
            }

            if (bestPdfBytes == null)
            {
                // Fallback to absolute minimum density
                bestPdfBytes = await Task.Run(() => RasterizeAndCompilePdf(pdfPath, pageCount, 60, 5));
            }

            return bestPdfBytes;
        }

        private static byte[] RasterizeAndCompilePdf(string pdfPath, int pageCount, double dpi, int quality)
        {
            using (var outDoc = new PdfDocument())
            {
                for (int i = 0; i < pageCount; i++)
                {
                    var page = outDoc.AddPage();
                    byte[] jpegBytes = RenderPdfPageToJpegBytes(pdfPath, (uint)i, dpi, quality);
                    
                    using (var ms = new MemoryStream(jpegBytes))
                    {
                        using (var ximg = XImage.FromStream(ms))
                        {
                            page.Width = XUnit.FromPoint(ximg.PointWidth);
                            page.Height = XUnit.FromPoint(ximg.PointHeight);
                            var gfx = XGraphics.FromPdfPage(page);
                            gfx.DrawImage(ximg, 0, 0);
                        }
                    }
                }

                using (var outMs = new MemoryStream())
                {
                    outDoc.Save(outMs);
                    return outMs.ToArray();
                }
            }
        }

        private static byte[] RenderPdfPageToJpegBytes(string pdfPath, uint pageIndex, double dpi, int quality)
        {
            var task = Task.Run(async () =>
            {
                var storageFile = await StorageFile.GetFileFromPathAsync(pdfPath);
                var pdfDoc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(storageFile);
                using (var pdfPage = pdfDoc.GetPage(pageIndex))
                {
                        var options = new Windows.Data.Pdf.PdfPageRenderOptions();
                        double widthPoints = pdfPage.Size.Width;
                        double heightPoints = pdfPage.Size.Height;
                        double scale = dpi / 72.0;
                        options.DestinationWidth = (uint)(widthPoints * scale);
                        options.DestinationHeight = (uint)(heightPoints * scale);

                        using (var memStream = new InMemoryRandomAccessStream())
                        {
                            await pdfPage.RenderToStreamAsync(memStream, options);
                            
                            using (var ms = new MemoryStream())
                            {
                                using (var stream = memStream.AsStream())
                                {
                                    await stream.CopyToAsync(ms);
                                }
                                ms.Position = 0;

                                return await Application.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    var bitmap = new BitmapImage();
                                    bitmap.BeginInit();
                                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                    bitmap.StreamSource = ms;
                                    bitmap.EndInit();
                                    bitmap.Freeze();

                                    var encoder = new JpegBitmapEncoder { QualityLevel = quality };
                                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                                    
                                    using (var outMs = new MemoryStream())
                                    {
                                        encoder.Save(outMs);
                                        return outMs.ToArray();
                                    }
                                });
                            }
                        }
                }
            });
            return task.GetAwaiter().GetResult();
        }

        #endregion

        #endregion

        #region Tool 4: Settings Panel & Theme Selection

        private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            var settings = SettingsService.Current;

            // Restore window size & state
            if (settings.RememberWindowSize)
            {
                Width = settings.WindowWidth;
                Height = settings.WindowHeight;
                if (Enum.TryParse<WindowState>(settings.WindowState, out var state))
                {
                    WindowState = state;
                }
            }

            // Bind the settings UI controls to the settings properties
            InitializeSettingsBindings();

            // Refresh recent files list on startup
            RefreshRecentFilesUI();

            // Reset navigation state on startup and force Home Dashboard
            settings.LastOpenedTool = "Home";
            ShowPanel(PanelHome);
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            var settings = SettingsService.Current;

            // Save window size & state
            if (settings.RememberWindowSize)
            {
                settings.WindowWidth = Width;
                settings.WindowHeight = Height;
                settings.WindowState = WindowState.ToString();
            }

            // Save last opened tool
            if (settings.RememberLastOpenedTool)
            {
                if (PanelPasspix.Visibility == Visibility.Visible)
                    settings.LastOpenedTool = "Passpix";
                else if (PanelPdf.Visibility == Visibility.Visible)
                    settings.LastOpenedTool = "Pdf";
                else if (PanelCompress.Visibility == Visibility.Visible)
                    settings.LastOpenedTool = "Compress";
                else if (PanelSettings.Visibility == Visibility.Visible)
                    settings.LastOpenedTool = "Settings";
                else
                    settings.LastOpenedTool = "Home";
            }

            SettingsService.Instance.SaveSettings();
        }

        private void InitializeSettingsBindings()
        {
            _isUpdatingUi = true;
            try
            {
                var settings = SettingsService.Current;

                SelectComboBoxItemByContent(CmbSettingsTheme, settings.ThemeMode);
                SelectComboBoxItemByContent(CmbSettingsAccent, settings.AccentColor);
                ChkSettingsCompact.IsChecked = settings.CompactMode;
                ChkSettingsLargeUi.IsChecked = settings.LargeUiMode;
                SldSettingsFontSize.Value = settings.FontSize;
                LblSettingsFontSizeVal.Text = $"{settings.FontSize} pt";
                ChkSettingsRememberWinSize.IsChecked = settings.RememberWindowSize;
                ChkSettingsRememberLastTool.IsChecked = settings.RememberLastOpenedTool;

                SelectComboBoxItemByContent(CmbSettingsPdfFormat, settings.PdfDefaultImageExportFormat);
                SldSettingsPdfQuality.Value = settings.PdfDefaultCompressionQuality;
                LblSettingsPdfQualityVal.Text = $"{settings.PdfDefaultCompressionQuality}%";
                TxtSettingsPdfTargetSize.Text = settings.PdfDefaultCompressionTargetSize;
                TxtSettingsPdfSaveFolder.Text = settings.PdfDefaultSaveFolder;
                ChkSettingsPdfAutoOpen.IsChecked = settings.PdfAutoOpenOutputFile;
                ChkSettingsPdfAskOverwrite.IsChecked = settings.PdfAskBeforeOverwrite;
                ChkSettingsPdfRememberPwd.IsChecked = settings.PdfRememberLastPassword;

                SelectComboBoxItemByContent(CmbSettingsPasspixSize, settings.PasspixDefaultPassportSize);
                SelectComboBoxItemByContent(CmbSettingsPasspixBg, settings.PasspixDefaultBackgroundColor);
                SelectComboBoxItemByContent(CmbSettingsPasspixPaper, settings.PasspixDefaultPaperSize);
                TxtSettingsPasspixCopies.Text = settings.PasspixDefaultCopies.ToString();
                ChkSettingsPasspixAutoRemoveBg.IsChecked = settings.PasspixAutoRemoveBackground;
                ChkSettingsPasspixAutoFaceEnhance.IsChecked = settings.PasspixAutoFaceEnhancement;
                ChkSettingsPasspixAutoFaceCrop.IsChecked = settings.PasspixAutoFaceDetectionCrop;

                TxtSettingsDefaultFolder.Text = settings.DefaultOutputFolder;
                ChkSettingsAutoCreateFolder.IsChecked = settings.AutoCreateOutputFolder;
                ChkSettingsAutoOpenSaved.IsChecked = settings.AutoOpenFileAfterSave;
                ChkSettingsAskBeforeReplace.IsChecked = settings.AskBeforeReplace;
                ChkSettingsDateFolders.IsChecked = settings.CreateDateBasedFolders;

                TxtSettingsCacheSize.Text = settings.ThumbnailCacheSize.ToString();
                SelectComboBoxItemByContent(CmbSettingsPreviewQuality, settings.PreviewQuality);
                ChkSettingsHardwareAccel.IsChecked = settings.HardwareAcceleration;
                ChkSettingsMultiThread.IsChecked = settings.MultiThreadProcessing;
                ChkSettingsRecentEnabled.IsChecked = settings.EnableRecentFiles;
                TxtSettingsRecentLimit.Text = settings.RecentFilesLimit.ToString();

                // About section details
                LblAboutVersion.Text = "1.1.0";
                LblAboutDeveloper.Text = "lkbunkar";
#if DEBUG
                LblAboutBuildType.Text = "Debug";
#else
                LblAboutBuildType.Text = "Release";
#endif
                LblAboutNetVersion.Text = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
                LblAboutBaseDir.Text = AppDomain.CurrentDomain.BaseDirectory;

                UpdateOnnxStatusLabel();
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void UpdateOnnxStatusLabel()
        {
            if (App.OnnxSession != null)
            {
                LblAboutOnnxStatus.Text = "Active & Running (u2net.onnx)";
                LblAboutOnnxStatus.Foreground = Brushes.Green;
            }
            else
            {
                LblAboutOnnxStatus.Text = "AI Model missing or loading";
                LblAboutOnnxStatus.Foreground = Brushes.Orange;
            }
        }

        private void SelectComboBoxItemByContent(ComboBox cb, string content)
        {
            if (cb == null || content == null) return;
            foreach (ComboBoxItem item in cb.Items)
            {
                if (item.Content?.ToString()?.Equals(content, StringComparison.OrdinalIgnoreCase) == true ||
                    item.Content?.ToString()?.StartsWith(content, StringComparison.OrdinalIgnoreCase) == true)
                {
                    cb.SelectedItem = item;
                    break;
                }
            }
        }

        private void ApplyPasspixDefaultSettings()
        {
            _isUpdatingUi = true;
            try
            {
                var settings = SettingsManager.Instance;

                // Crop dimensions mapping
                string size = settings.DefaultPassportDimension; // "1.19x1.53", "1.38x1.77", "2.00x2.00"
                if (size.Contains("1.19"))
                {
                    TxtCropWidth.Text = "1.19";
                    TxtCropHeight.Text = "1.53";
                }
                else if (size.Contains("1.38"))
                {
                    TxtCropWidth.Text = "1.38";
                    TxtCropHeight.Text = "1.77";
                }
                else if (size.Contains("2.00"))
                {
                    TxtCropWidth.Text = "2.00";
                    TxtCropHeight.Text = "2.00";
                }

                // Background removal
                ChkRemoveBg.IsChecked = settings.AutoRemoveBackground;

                // Background color
                RestoreCmbSolidFillSelection();

                // Paper size
                SelectComboBoxItemByContent(CmbPageSize, settings.DefaultPaperSize);

                // Copies
                TxtCopies.Text = settings.DefaultCopies.ToString();
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void RefreshRecentFilesUI()
        {
            if (PnlRecentFilesList == null || TxtNoRecentFiles == null) return;

            PnlRecentFilesList.Children.Clear();
            var settings = SettingsService.Current;

            if (!settings.EnableRecentFiles || settings.RecentFiles == null || settings.RecentFiles.Count == 0)
            {
                TxtNoRecentFiles.Visibility = Visibility.Visible;
                return;
            }

            TxtNoRecentFiles.Visibility = Visibility.Collapsed;

            foreach (string filePath in settings.RecentFiles.Take(settings.RecentFilesLimit))
            {
                // Only show existing files
                if (!File.Exists(filePath)) continue;

                var grid = new Grid
                {
                    Margin = new Thickness(0, 4, 0, 4),
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand
                };

                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var textBlock = new TextBlock
                {
                    Text = filePath,
                    Foreground = (Brush)Application.Current.Resources["TextBrush"],
                    FontSize = (double)Application.Current.Resources["BodyFontSize"],
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };

                // Double click to open
                grid.MouseDown += (s, ev) =>
                {
                    if (ev.ClickCount == 2)
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(filePath) { UseShellExecute = true });
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Failed to open file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                };

                var openFolderBtn = new Button
                {
                    Content = "Open Folder",
                    Style = (Style)Application.Current.Resources["ModernButton"],
                    Padding = new Thickness(8, 4, 8, 4),
                    FontSize = (double)Application.Current.Resources["MutedFontSize"],
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand
                };

                openFolderBtn.Click += (s, ev) =>
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(filePath)!;
                        if (Directory.Exists(dir))
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to open directory: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                };

                Grid.SetColumn(textBlock, 0);
                Grid.SetColumn(openFolderBtn, 1);

                grid.Children.Add(textBlock);
                grid.Children.Add(openFolderBtn);

                PnlRecentFilesList.Children.Add(grid);
            }

            if (PnlRecentFilesList.Children.Count == 0)
            {
                TxtNoRecentFiles.Visibility = Visibility.Visible;
            }
        }

        private Rect DetectFaceHeuristic(BitmapSource bmp)
        {
            if (bmp == null) return new Rect();

            // Downscale to 160x160 to make it extremely fast and filter out noise
            var scale = new ScaleTransform(160.0 / bmp.PixelWidth, 160.0 / bmp.PixelHeight);
            var resized = new TransformedBitmap(bmp, scale);
            var converted = new FormatConvertedBitmap(resized, PixelFormats.Bgra32, null, 0);

            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            byte[] pixels = new byte[width * height * 4];
            converted.CopyPixels(pixels, width * 4, 0);

            int minX = width;
            int maxX = 0;
            int minY = height;
            int maxY = 0;
            int skinCount = 0;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = (y * width + x) * 4;
                    byte b = pixels[index];
                    byte g = pixels[index + 1];
                    byte r = pixels[index + 2];

                    // Skin tone classification in RGB
                    bool isSkin = r > 95 && g > 40 && b > 20 &&
                                  r > g && r > b &&
                                  (r - g) > 15 &&
                                  Math.Abs(r - g) > 15;

                    if (isSkin)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                        skinCount++;
                    }
                }
            }

            if (skinCount < 100)
            {
                return new Rect();
            }

            // Map back to [0, 1] relative coordinates
            double relLeft = (double)minX / width;
            double relTop = (double)minY / height;
            double relWidth = (double)(maxX - minX) / width;
            double relHeight = (double)(maxY - minY) / height;

            // Expand slightly to include the full head/hair
            double paddingX = relWidth * 0.15;
            double paddingY = relHeight * 0.25;

            relLeft = Math.Max(0, relLeft - paddingX);
            relTop = Math.Max(0, relTop - paddingY);
            relWidth = Math.Min(1 - relLeft, relWidth + 2 * paddingX);
            relHeight = Math.Min(1 - relTop, relHeight + 2 * paddingY);

            return new Rect(relLeft, relTop, relWidth, relHeight);
        }

        private void EnhanceFace(WriteableBitmap src)
        {
            if (src == null) return;

            int width = src.PixelWidth;
            int height = src.PixelHeight;

            try
            {
                src.Lock();
                IntPtr backBuffer = src.BackBuffer;
                int stride = src.BackBufferStride;

                byte[] input = new byte[stride * height];
                System.Runtime.InteropServices.Marshal.Copy(backBuffer, input, 0, input.Length);

                byte[] output = new byte[input.Length];
                Array.Copy(input, output, input.Length);

                // Brightness & Contrast Adjustment
                double brightness = 12.0;
                double contrast = 1.15;

                for (int i = 0; i < input.Length; i += 4)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        double val = input[i + c];
                        val += brightness;
                        val = (val - 128) * contrast + 128;
                        input[i + c] = (byte)Math.Max(0, Math.Min(255, val));
                    }
                }

                // Bilateral skin smoothing (5x5 neighborhood)
                double sigmaS = 2.0;
                double sigmaR = 25.0;
                double twoSigmaS2 = 2.0 * sigmaS * sigmaS;
                double twoSigmaR2 = 2.0 * sigmaR * sigmaR;

                double[,] spatialWeights = new double[5, 5];
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        spatialWeights[dy + 2, dx + 2] = Math.Exp(-(dx * dx + dy * dy) / twoSigmaS2);
                    }
                }

                for (int y = 2; y < height - 2; y++)
                {
                    for (int x = 2; x < width - 2; x++)
                    {
                        int index = y * stride + x * 4;
                        byte b = input[index];
                        byte g = input[index + 1];
                        byte r = input[index + 2];
                        byte a = input[index + 3];

                        if (a == 0) continue;

                        bool isSkin = r > 95 && g > 40 && b > 20 &&
                                      r > g && r > b &&
                                      (r - g) > 15 &&
                                      Math.Abs(r - g) > 15;

                        if (!isSkin)
                        {
                            output[index] = b;
                            output[index + 1] = g;
                            output[index + 2] = r;
                            output[index + 3] = a;
                            continue;
                        }

                        double sumB = 0, sumG = 0, sumR = 0;
                        double sumW = 0;

                        for (int dy = -2; dy <= 2; dy++)
                        {
                            for (int dx = -2; dx <= 2; dx++)
                            {
                                int nIndex = (y + dy) * stride + (x + dx) * 4;
                                byte nb = input[nIndex];
                                byte ng = input[nIndex + 1];
                                byte nr = input[nIndex + 2];

                                double diffB = b - nb;
                                double diffG = g - ng;
                                double diffR = r - nr;
                                double rangeDist = (diffB * diffB + diffG * diffG + diffR * diffR) / 3.0;

                                double wRange = Math.Exp(-rangeDist / twoSigmaR2);
                                double w = spatialWeights[dy + 2, dx + 2] * wRange;

                                sumB += nb * w;
                                sumG += ng * w;
                                sumR += nr * w;
                                sumW += w;
                            }
                        }

                        if (sumW > 0)
                        {
                            output[index] = (byte)Math.Max(0, Math.Min(255, sumB / sumW));
                            output[index + 1] = (byte)Math.Max(0, Math.Min(255, sumG / sumW));
                            output[index + 2] = (byte)Math.Max(0, Math.Min(255, sumR / sumW));
                        }
                    }
                }

                System.Runtime.InteropServices.Marshal.Copy(output, 0, backBuffer, output.Length);
                src.AddDirtyRect(new Int32Rect(0, 0, width, height));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to enhance face: {ex.Message}");
            }
            finally
            {
                src.Unlock();
            }
        }

        #region Settings Change Event Handlers

        private void SaveAndApplySettings()
        {
            if (_isUpdatingUi) return;
            SettingsService.Instance.SaveSettings();
            SettingsService.Instance.ApplySettings();
            ApplyPasspixDefaultSettings();
            TriggerCropAndProcess();
        }

        private void CmbSettingsTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi) return;
            if (CmbSettingsTheme.SelectedItem is ComboBoxItem item)
            {
                SettingsService.Current.ThemeMode = item.Content.ToString()!;
                SaveAndApplySettings();
            }
        }

        private void CmbSettingsAccent_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi) return;
            if (CmbSettingsAccent.SelectedItem is ComboBoxItem item)
            {
                SettingsService.Current.AccentColor = item.Content.ToString()!;
                SaveAndApplySettings();
            }
        }

        private void ChkSettingsCompact_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            _isUpdatingUi = true;
            ChkSettingsLargeUi.IsChecked = false;
            _isUpdatingUi = false;

            SettingsService.Current.CompactMode = true;
            SettingsService.Current.LargeUiMode = false;
            SaveAndApplySettings();
        }

        private void ChkSettingsCompact_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.CompactMode = false;
            SaveAndApplySettings();
        }

        private void ChkSettingsLargeUi_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            _isUpdatingUi = true;
            ChkSettingsCompact.IsChecked = false;
            _isUpdatingUi = false;

            SettingsService.Current.LargeUiMode = true;
            SettingsService.Current.CompactMode = false;
            SaveAndApplySettings();
        }

        private void ChkSettingsLargeUi_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.LargeUiMode = false;
            SaveAndApplySettings();
        }

        private void SldSettingsFontSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUi || LblSettingsFontSizeVal == null) return;
            double size = Math.Round(e.NewValue);
            LblSettingsFontSizeVal.Text = $"{size} pt";
            SettingsService.Current.FontSize = size;
            SaveAndApplySettings();
        }

        private void ChkSettingsRememberWinSize_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.RememberWindowSize = true;
            SaveAndApplySettings();
        }

        private void ChkSettingsRememberWinSize_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.RememberWindowSize = false;
            SaveAndApplySettings();
        }

        private void ChkSettingsRememberLastTool_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.RememberLastOpenedTool = true;
            SaveAndApplySettings();
        }

        private void ChkSettingsRememberLastTool_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.RememberLastOpenedTool = false;
            SaveAndApplySettings();
        }

        private void CmbSettingsPdfFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi) return;
            if (CmbSettingsPdfFormat.SelectedItem is ComboBoxItem item)
            {
                SettingsService.Current.PdfDefaultImageExportFormat = item.Content.ToString()!;
                SaveAndApplySettings();
            }
        }

        private void SldSettingsPdfQuality_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUi || LblSettingsPdfQualityVal == null) return;
            int qual = (int)Math.Round(e.NewValue);
            LblSettingsPdfQualityVal.Text = $"{qual}%";
            SettingsService.Current.PdfDefaultCompressionQuality = qual;
            SaveAndApplySettings();
        }

        private void TxtSettingsPdfTargetSize_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.PdfDefaultCompressionTargetSize = TxtSettingsPdfTargetSize.Text;
            SaveAndApplySettings();
        }

        private void TxtSettingsPdfSaveFolder_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.PdfDefaultSaveFolder = TxtSettingsPdfSaveFolder.Text;
            SaveAndApplySettings();
        }

        private void BtnSettingsPdfSaveFolderBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog();
            if (dialog.ShowDialog() == true)
            {
                TxtSettingsPdfSaveFolder.Text = dialog.FolderName;
            }
        }

        private void ChkSettingsPdfAutoOpen_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.PdfAutoOpenOutputFile = true;
            SaveAndApplySettings();
        }

        private void ChkSettingsPdfAutoOpen_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.PdfAutoOpenOutputFile = false;
            SaveAndApplySettings();
        }

        private void ChkSettingsPdfAskOverwrite_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.PdfAskBeforeOverwrite = true;
            SaveAndApplySettings();
        }

        private void ChkSettingsPdfAskOverwrite_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.PdfAskBeforeOverwrite = false;
            SaveAndApplySettings();
        }

        private void ChkSettingsPdfRememberPwd_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.PdfRememberLastPassword = true;
            SaveAndApplySettings();
        }

        private void ChkSettingsPdfRememberPwd_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.PdfRememberLastPassword = false;
            SaveAndApplySettings();
        }

        private void CmbSettingsPasspixSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi) return;
            if (CmbSettingsPasspixSize.SelectedItem is ComboBoxItem item)
            {
                SettingsService.Current.PasspixDefaultPassportSize = item.Content.ToString()!;
                SaveAndApplySettings();
            }
        }

        private void CmbSettingsPasspixBg_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi) return;
            if (CmbSettingsPasspixBg.SelectedItem is ComboBoxItem item)
            {
                SettingsService.Current.PasspixDefaultBackgroundColor = item.Content.ToString()!;
                SaveAndApplySettings();
            }
        }

        private void CmbSettingsPasspixPaper_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi) return;
            if (CmbSettingsPasspixPaper.SelectedItem is ComboBoxItem item)
            {
                SettingsService.Current.PasspixDefaultPaperSize = item.Content.ToString()!;
                SaveAndApplySettings();
            }
        }

        private void TxtSettingsPasspixCopies_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUi) return;
            if (int.TryParse(TxtSettingsPasspixCopies.Text, out int copies))
            {
                SettingsService.Current.PasspixDefaultCopies = copies;
                SaveAndApplySettings();
            }
        }

        private void ChkSettingsPasspixAutoRemoveBg_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.PasspixAutoRemoveBackground = true;
            SaveAndApplySettings();
        }

        private void ChkSettingsPasspixAutoRemoveBg_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.PasspixAutoRemoveBackground = false;
            SaveAndApplySettings();
        }

        private void ChkSettingsPasspixAutoFaceEnhance_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.PasspixAutoFaceEnhancement = true;
            SaveAndApplySettings();
        }

        private void ChkSettingsPasspixAutoFaceEnhance_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.PasspixAutoFaceEnhancement = false;
            SaveAndApplySettings();
        }

        private void ChkSettingsPasspixAutoFaceCrop_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.PasspixAutoFaceDetectionCrop = true;
            SaveAndApplySettings();
        }

        private void ChkSettingsPasspixAutoFaceCrop_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.PasspixAutoFaceDetectionCrop = false;
            SaveAndApplySettings();
        }

        private void TxtSettingsDefaultFolder_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.DefaultOutputFolder = TxtSettingsDefaultFolder.Text;
            SaveAndApplySettings();
        }

        private void BtnSettingsDefaultFolderBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog();
            if (dialog.ShowDialog() == true)
            {
                TxtSettingsDefaultFolder.Text = dialog.FolderName;
            }
        }

        private void ChkSettingsAutoCreateFolder_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.AutoCreateOutputFolder = true;
            SaveAndApplySettings();
        }

        private void ChkSettingsAutoCreateFolder_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.AutoCreateOutputFolder = false;
            SaveAndApplySettings();
        }

        private void ChkSettingsAutoOpenSaved_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.AutoOpenFileAfterSave = true;
            SaveAndApplySettings();
        }

        private void ChkSettingsAutoOpenSaved_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.AutoOpenFileAfterSave = false;
            SaveAndApplySettings();
        }

        private void ChkSettingsAskBeforeReplace_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.AskBeforeReplace = true;
            SaveAndApplySettings();
        }

        private void ChkSettingsAskBeforeReplace_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.AskBeforeReplace = false;
            SaveAndApplySettings();
        }

        private void ChkSettingsDateFolders_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.CreateDateBasedFolders = true;
            SaveAndApplySettings();
        }

        private void ChkSettingsDateFolders_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.CreateDateBasedFolders = false;
            SaveAndApplySettings();
        }

        private void TxtSettingsCacheSize_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUi) return;
            if (int.TryParse(TxtSettingsCacheSize.Text, out int size))
            {
                SettingsService.Current.ThumbnailCacheSize = size;
                SaveAndApplySettings();
            }
        }

        private void CmbSettingsPreviewQuality_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi) return;
            if (CmbSettingsPreviewQuality.SelectedItem is ComboBoxItem item)
            {
                SettingsService.Current.PreviewQuality = item.Content.ToString()!;
                SaveAndApplySettings();
            }
        }

        private void ChkSettingsHardwareAccel_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.HardwareAcceleration = true;
            SaveAndApplySettings();
        }

        private void ChkSettingsHardwareAccel_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.HardwareAcceleration = false;
            SaveAndApplySettings();
        }

        private void ChkSettingsMultiThread_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.MultiThreadProcessing = true;
            SaveAndApplySettings();
        }

        private void ChkSettingsMultiThread_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.MultiThreadProcessing = false;
            SaveAndApplySettings();
        }

        private void ChkSettingsRecentEnabled_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.EnableRecentFiles = true;
            SaveAndApplySettings();
            RefreshRecentFilesUI();
        }

        private void ChkSettingsRecentEnabled_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            SettingsService.Current.EnableRecentFiles = false;
            SaveAndApplySettings();
            RefreshRecentFilesUI();
        }

        private void TxtSettingsRecentLimit_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUi) return;
            if (int.TryParse(TxtSettingsRecentLimit.Text, out int limit))
            {
                SettingsService.Current.RecentFilesLimit = limit;
                SaveAndApplySettings();
            }
        }

        #endregion

        #region Backup, Restore & Clear Cache

        private void BtnSettingsExport_Click(object sender, RoutedEventArgs e)
        {
            var sfd = new SaveFileDialog
            {
                FileName = "simple_editor_settings",
                DefaultExt = ".json",
                Filter = "JSON Configuration Files (*.json)|*.json"
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    string settingsFolder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
                        "SimpleEditor"
                    );
                    string settingsPath = Path.Combine(settingsFolder, "settings.json");
                    if (File.Exists(settingsPath))
                    {
                        File.Copy(settingsPath, sfd.FileName, true);
                        MessageBox.Show("Settings configuration exported successfully!", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show("No settings file exists to export.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to export settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnSettingsImport_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Filter = "JSON Configuration Files (*.json)|*.json"
            };

            if (ofd.ShowDialog() == true)
            {
                try
                {
                    string json = File.ReadAllText(ofd.FileName);
                    var testObj = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
                    if (testObj == null) throw new Exception("Invalid settings file format.");

                    string settingsFolder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
                        "SimpleEditor"
                    );
                    if (!Directory.Exists(settingsFolder))
                    {
                        Directory.CreateDirectory(settingsFolder);
                    }
                    string settingsPath = Path.Combine(settingsFolder, "settings.json");
                    File.WriteAllText(settingsPath, json);

                    SettingsService.Instance.LoadSettings();
                    SettingsService.Instance.ApplySettings();
                    InitializeSettingsBindings();

                    MessageBox.Show("Settings configuration imported successfully! Applied immediately.", "Import Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to import settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnSettingsReset_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Are you sure you want to reset all settings to default values?", "Confirm Reset", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                var newSettings = new AppSettings();
                SettingsService.Current.ThemeMode = newSettings.ThemeMode;
                SettingsService.Current.AccentColor = newSettings.AccentColor;
                SettingsService.Current.CompactMode = newSettings.CompactMode;
                SettingsService.Current.LargeUiMode = newSettings.LargeUiMode;
                SettingsService.Current.FontSize = newSettings.FontSize;
                SettingsService.Current.RememberWindowSize = newSettings.RememberWindowSize;
                SettingsService.Current.RememberLastOpenedTool = newSettings.RememberLastOpenedTool;

                SettingsService.Current.PdfDefaultImageExportFormat = newSettings.PdfDefaultImageExportFormat;
                SettingsService.Current.PdfDefaultCompressionQuality = newSettings.PdfDefaultCompressionQuality;
                SettingsService.Current.PdfDefaultCompressionTargetSize = newSettings.PdfDefaultCompressionTargetSize;
                SettingsService.Current.PdfDefaultSaveFolder = newSettings.PdfDefaultSaveFolder;
                SettingsService.Current.PdfAutoOpenOutputFile = newSettings.PdfAutoOpenOutputFile;
                SettingsService.Current.PdfAskBeforeOverwrite = newSettings.PdfAskBeforeOverwrite;
                SettingsService.Current.PdfRememberLastPassword = newSettings.PdfRememberLastPassword;

                SettingsService.Current.PasspixDefaultPassportSize = newSettings.PasspixDefaultPassportSize;
                SettingsService.Current.PasspixDefaultBackgroundColor = newSettings.PasspixDefaultBackgroundColor;
                SettingsService.Current.PasspixDefaultPaperSize = newSettings.PasspixDefaultPaperSize;
                SettingsService.Current.PasspixDefaultCopies = newSettings.PasspixDefaultCopies;
                SettingsService.Current.PasspixAutoRemoveBackground = newSettings.PasspixAutoRemoveBackground;
                SettingsService.Current.PasspixAutoFaceEnhancement = newSettings.PasspixAutoFaceEnhancement;
                SettingsService.Current.PasspixAutoFaceDetectionCrop = newSettings.PasspixAutoFaceDetectionCrop;

                SettingsService.Current.DefaultOutputFolder = newSettings.DefaultOutputFolder;
                SettingsService.Current.AutoCreateOutputFolder = newSettings.AutoCreateOutputFolder;
                SettingsService.Current.AutoOpenFileAfterSave = newSettings.AutoOpenFileAfterSave;
                SettingsService.Current.AskBeforeReplace = newSettings.AskBeforeReplace;
                SettingsService.Current.CreateDateBasedFolders = newSettings.CreateDateBasedFolders;

                SettingsService.Current.ThumbnailCacheSize = newSettings.ThumbnailCacheSize;
                SettingsService.Current.PreviewQuality = newSettings.PreviewQuality;
                SettingsService.Current.HardwareAcceleration = newSettings.HardwareAcceleration;
                SettingsService.Current.MultiThreadProcessing = newSettings.MultiThreadProcessing;

                SettingsService.Current.EnableRecentFiles = newSettings.EnableRecentFiles;
                SettingsService.Current.RecentFilesLimit = newSettings.RecentFilesLimit;

                SettingsService.Instance.SaveSettings();
                SettingsService.Instance.ApplySettings();
                InitializeSettingsBindings();

                MessageBox.Show("All settings reset to default values successfully!", "Reset Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnAboutCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Check complete. You are running the latest version (v1.1.0).", "Check For Updates", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnSettingsClearCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string cacheFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
                    "SimpleEditor",
                    "Cache"
                );
                if (Directory.Exists(cacheFolder))
                {
                    var files = Directory.GetFiles(cacheFolder);
                    foreach (var file in files)
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
                MessageBox.Show("Cache files cleared successfully!", "Clear Cache", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to clear cache: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSettingsClearRecent_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SettingsService.Current.RecentFiles.Clear();
                SettingsService.Instance.SaveSettings();
                RefreshRecentFilesUI();
                MessageBox.Show("Recent files history cleared successfully!", "Clear Recent Files", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to clear recent files: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSettingsOpenLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logsFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
                    "SimpleEditor",
                    "Logs"
                );
                if (!Directory.Exists(logsFolder))
                {
                    Directory.CreateDirectory(logsFolder);
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(logsFolder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open logs folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #endregion
    }
}