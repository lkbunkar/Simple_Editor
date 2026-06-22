using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace Passpix
{
    public partial class App : Application
    {
        // Global inference session for background removal
        public static Microsoft.ML.OnnxRuntime.InferenceSession? OnnxSession { get; private set; }
        public static Task<bool>? ModelLoadingTask { get; private set; }

        public App()
        {
            // Register domain-wide unhandled exception handler
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                string msg = ex != null ? $"{ex.Message}\n\nStack Trace:\n{ex.StackTrace}" : "Unknown domain crash.";
                MessageBox.Show($"Fatal Unhandled Domain Exception:\n{msg}", "Application Crash", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            // Register dispatcher thread unhandled exception handler
            DispatcherUnhandledException += (s, e) =>
            {
                MessageBox.Show($"Dispatcher Unhandled Exception:\n{e.Exception.Message}\n\nStack Trace:\n{e.Exception.StackTrace}", "Runtime Error", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true; // Prevents process termination if possible
            };
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                // Initialize settings service and apply global styles before window initialization
                Services.SettingsService.Instance.LoadSettings();
                Services.SettingsService.Instance.ApplySettings();

                base.OnStartup(e);

                // Trigger model loading on a background thread
                ModelLoadingTask = Task.Run(() =>
                {
                    try
                    {
                        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                        string modelPath = Path.Combine(baseDir, "assets", "u2net.onnx");

                        // Fallback to project root if running inside bin folder during development
                        if (!File.Exists(modelPath))
                        {
                            modelPath = Path.Combine(Directory.GetParent(baseDir)?.Parent?.Parent?.FullName ?? "", "assets", "u2net.onnx");
                        }

                        if (File.Exists(modelPath))
                        {
                            OnnxSession = new Microsoft.ML.OnnxRuntime.InferenceSession(modelPath);
                            return true;
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"ONNX Model not found at: {modelPath}");
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error loading ONNX model: {ex.Message}");
                        return false;
                    }
                });

                // Temporarily set shutdown mode to explicit to prevent closing during splash transition
                var oldMode = Current.ShutdownMode;
                Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                // Show Splash Window (modal, blocks thread during its 1.5s lifecycle but runs dispatch loop)
                var splash = new SplashWindow();
                splash.ShowDialog();

                // Now create and show the Main Window
                var mainWindow = new MainWindow();
                Current.MainWindow = mainWindow;
                mainWindow.Show();

                // Restore shutdown mode
                Current.ShutdownMode = oldMode;
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(@"C:\Users\lkbunkar\Desktop\Simple_Editor\crash_log.txt", ex.ToString());
                }
                catch {}
                MessageBox.Show($"Exception during Application Startup:\n{ex.Message}\n\nStack Trace:\n{ex.StackTrace}", "Startup Crash", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(-1);
            }
        }
    }
}
