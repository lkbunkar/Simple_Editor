using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;

namespace Passpix
{
    public partial class ColorPickerDialog : Window
    {
        private bool _isUpdating = false;
        private double _hue = 0.0;        // 0 to 360
        private double _saturation = 1.0; // 0 to 1
        private double _value = 1.0;      // 0 to 1

        private bool _initialColorSet = false;
        private Color _initialColor;

        private bool _isDraggingSatVal = false;
        private bool _isDraggingHue = false;

        public Color SelectedColor
        {
            get
            {
                return ColorFromAhsv(1.0, _hue, _saturation, _value);
            }
            set
            {
                if (!_initialColorSet)
                {
                    _initialColor = value;
                    PreviewCurrentColor.Background = new SolidColorBrush(value);
                    _initialColorSet = true;
                }
                UpdateHsvFromColor(value);
                UpdateUiFromHsv();
            }
        }

        public ColorPickerDialog()
        {
            InitializeComponent();
            SelectedColor = Colors.White;
        }

        private void UpdateHsvFromColor(Color color)
        {
            double r = color.R / 255.0;
            double g = color.G / 255.0;
            double b = color.B / 255.0;

            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;

            _hue = 0;
            if (delta > 0)
            {
                if (max == r)
                {
                    _hue = 60.0 * (((g - b) / delta) % 6);
                }
                else if (max == g)
                {
                    _hue = 60.0 * (((b - r) / delta) + 2);
                }
                else if (max == b)
                {
                    _hue = 60.0 * (((r - g) / delta) + 4);
                }
                if (_hue < 0) _hue += 360.0;
            }

            _saturation = max == 0 ? 0 : delta / max;
            _value = max;
        }

        private void UpdateUiFromHsv()
        {
            _isUpdating = true;

            Color currentColor = SelectedColor;

            // 1. Base hue background color
            Color baseColor = ColorFromAhsv(1.0, _hue, 1.0, 1.0);
            SatValColorBackground.Background = new SolidColorBrush(baseColor);

            // 2. Position SatVal Marker
            Canvas.SetLeft(SatValMarker, _saturation * 256.0 - 6.0);
            Canvas.SetTop(SatValMarker, (1.0 - _value) * 256.0 - 6.0);

            // 3. Position Hue Indicators
            double hueY = (_hue / 360.0) * 256.0;
            Canvas.SetTop(HueIndicatorLeft, hueY);
            Canvas.SetTop(HueIndicatorRight, hueY);

            // 4. Input TextBoxes with Caret Preservation
            UpdateTextBox(TxtRed, currentColor.R.ToString());
            UpdateTextBox(TxtGreen, currentColor.G.ToString());
            UpdateTextBox(TxtBlue, currentColor.B.ToString());
            UpdateTextBox(TxtHex, $"#{currentColor.R:X2}{currentColor.G:X2}{currentColor.B:X2}");

            // 5. New Color Preview
            PreviewNewColor.Background = new SolidColorBrush(currentColor);

            _isUpdating = false;
        }

        private void UpdateTextBox(System.Windows.Controls.TextBox tb, string newText)
        {
            if (tb.Text != newText)
            {
                int selectionStart = tb.SelectionStart;
                int selectionLength = tb.SelectionLength;
                tb.Text = newText;
                tb.SelectionStart = Math.Min(selectionStart, tb.Text.Length);
                tb.SelectionLength = Math.Min(selectionLength, tb.Text.Length);
            }
        }

        public static Color ColorFromAhsv(double a, double h, double s, double v)
        {
            double r = 0, g = 0, b = 0;
            if (s == 0)
            {
                r = v;
                g = v;
                b = v;
            }
            else
            {
                double hue = h == 360.0 ? 0.0 : h / 60.0;
                int i = (int)Math.Floor(hue);
                double f = hue - i;
                double p = v * (1.0 - s);
                double q = v * (1.0 - (s * f));
                double t = v * (1.0 - (s * (1.0 - f)));

                switch (i)
                {
                    case 0: r = v; g = t; b = p; break;
                    case 1: r = q; g = v; b = p; break;
                    case 2: r = p; g = v; b = t; break;
                    case 3: r = p; g = q; b = v; break;
                    case 4: r = t; g = p; b = v; break;
                    default: r = v; g = p; b = q; break;
                }
            }
            return Color.FromArgb((byte)(a * 255), (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }

        // Drag handlers for Saturation/Value Square
        private void SatValSquare_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSatVal = true;
            SatValGrid.CaptureMouse();
            UpdateSatValFromMouse(e.GetPosition(SatValGrid));
        }

        private void SatValSquare_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingSatVal)
            {
                UpdateSatValFromMouse(e.GetPosition(SatValGrid));
            }
        }

        private void SatValSquare_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingSatVal)
            {
                SatValGrid.ReleaseMouseCapture();
                _isDraggingSatVal = false;
            }
        }

        private void UpdateSatValFromMouse(Point point)
        {
            double x = point.X;
            double y = point.Y;

            x = Math.Max(0, Math.Min(x, 256.0));
            y = Math.Max(0, Math.Min(y, 256.0));

            _saturation = x / 256.0;
            _value = 1.0 - (y / 256.0);

            UpdateUiFromHsv();
        }

        // Drag handlers for vertical Hue Track
        private void HueTrack_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingHue = true;
            HueTrack.CaptureMouse();
            UpdateHueFromMouse(e.GetPosition(HueTrack));
        }

        private void HueTrack_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingHue)
            {
                UpdateHueFromMouse(e.GetPosition(HueTrack));
            }
        }

        private void HueTrack_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingHue)
            {
                HueTrack.ReleaseMouseCapture();
                _isDraggingHue = false;
            }
        }

        private void UpdateHueFromMouse(Point point)
        {
            double y = point.Y;

            y = Math.Max(0, Math.Min(y, 256.0));

            _hue = (y / 256.0) * 360.0;
            if (_hue >= 360.0) _hue = 359.9;

            UpdateUiFromHsv();
        }

        // TextBox text changed handlers
        private void TxtRGB_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_isUpdating) return;

            byte.TryParse(TxtRed.Text, out byte r);
            byte.TryParse(TxtGreen.Text, out byte g);
            byte.TryParse(TxtBlue.Text, out byte b);

            Color col = Color.FromRgb(r, g, b);
            UpdateHsvFromColor(col);
            UpdateUiFromHsv();
        }

        private void TxtHex_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_isUpdating) return;

            string hex = TxtHex.Text.Trim();
            if (hex.StartsWith("#"))
            {
                hex = hex.Substring(1);
            }

            if (hex.Length == 6)
            {
                try
                {
                    byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                    byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                    byte b = Convert.ToByte(hex.Substring(4, 2), 16);

                    Color col = Color.FromRgb(r, g, b);
                    UpdateHsvFromColor(col);
                    UpdateUiFromHsv();
                }
                catch
                {
                    // Ignore editing errors
                }
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
