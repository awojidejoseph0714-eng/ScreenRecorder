using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;

namespace ScreenRecorder
{
    public partial class FirstRunWindow : Window
    {
        private int _currentSlide = 1;
        private readonly AppSettings _settings;

        public FirstRunWindow(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;
        }

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                this.DragMove();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSlide > 1)
            {
                _currentSlide--;
                UpdateSlides();
            }
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSlide < 3)
            {
                _currentSlide++;
                UpdateSlides();
            }
        }

        private void UpdateSlides()
        {
            PanelSlide1.Visibility = _currentSlide == 1 ? Visibility.Visible : Visibility.Collapsed;
            PanelSlide2.Visibility = _currentSlide == 2 ? Visibility.Visible : Visibility.Collapsed;
            PanelSlide3.Visibility = _currentSlide == 3 ? Visibility.Visible : Visibility.Collapsed;

            BtnBack.Visibility = _currentSlide > 1 ? Visibility.Visible : Visibility.Collapsed;
            
            if (_currentSlide == 3)
            {
                BtnNext.Visibility = Visibility.Collapsed;
                BtnStart.Visibility = Visibility.Visible;
            }
            else
            {
                BtnNext.Visibility = Visibility.Visible;
                BtnStart.Visibility = Visibility.Collapsed;
            }
        }

        private void ChkAgree_Toggle(object sender, RoutedEventArgs e)
        {
            BtnStart.IsEnabled = ChkAgree.IsChecked == true;
            if (ChkAgree.IsChecked == true)
            {
                BtnStart.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x40, 0x80, 0x40)); // Green tint
            }
            else
            {
                BtnStart.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44)); // Default gray
            }
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            // Update settings
            _settings.HasAcceptedTerms = true;

            // Configure autostart if requested
            ConfigureAutostart(ChkAutostart.IsChecked == true);

            _settings.Save();

            this.DialogResult = true;
            this.Close();
        }

        private void ConfigureAutostart(bool enable)
        {
            try
            {
                string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(runKey, true))
                {
                    if (key != null)
                    {
                        if (enable)
                        {
                            string appPath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                            if (!string.IsNullOrEmpty(appPath))
                            {
                                // Append parameter --background to start minimized in tray on boot
                                key.SetValue("ScreenRecorder", $"\"{appPath}\" --background");
                            }
                        }
                        else
                        {
                            key.DeleteValue("ScreenRecorder", false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not configure Startup shortcut: {ex.Message}", "Settings Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
