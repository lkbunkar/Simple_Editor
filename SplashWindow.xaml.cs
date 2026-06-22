using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;

namespace Passpix
{
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
            Loaded += SplashWindow_Loaded;
        }

        private async void SplashWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Start Fade-In Animation
            if (Resources["FadeInStoryboard"] is Storyboard fadeIn)
            {
                fadeIn.Begin(this);
            }

            // Wait 1.5 seconds (1500 milliseconds)
            await Task.Delay(1500);

            // Start Fade-Out Animation
            if (Resources["FadeOutStoryboard"] is Storyboard fadeOut)
            {
                fadeOut.Completed += (s, ev) => Close();
                fadeOut.Begin(this);
            }
            else
            {
                Close();
            }
        }
    }
}
