using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace ScreenRecorder
{
    public partial class SettingsWindow : Window
    {
        private AppSettings _settings;
        public bool IsSaved { get; private set; } = false;

        public SettingsWindow(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;

            LoadSettingsIntoUi();
            UpdateDiskSpaceEstimate();
        }

        private void LoadSettingsIntoUi()
        {
            TxtOutputDir.Text = _settings.OutputDir;
            SldBufferHours.Value = _settings.BufferHours;
            ChkEnableOcr.IsChecked = _settings.EnableOcr;
            ChkShowBorder.IsChecked = _settings.ShowBorderIndicator;

            // Load Excluded Apps (one per line)
            TxtExcludedApps.Text = string.Join(Environment.NewLine, _settings.ExcludedProcesses);

            // Populate Monitors
            CmbMonitors.Items.Clear();
            CmbMonitors.Items.Add("Primary Screen");
            
            // WinForms Screen helper to find other monitors
            var screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var screen = screens[i];
                string name = $"Monitor {i + 1} ({screen.Bounds.Width}x{screen.Bounds.Height}){(screen.Primary ? " [Primary]" : "")}";
                CmbMonitors.Items.Add(name);
            }

            // Set selected monitor index
            if (string.IsNullOrEmpty(_settings.SelectedMonitor) || _settings.SelectedMonitor == "Primary")
            {
                CmbMonitors.SelectedIndex = 0;
            }
            else
            {
                int index = 0;
                for (int i = 0; i < screens.Length; i++)
                {
                    if (screens[i].DeviceName == _settings.SelectedMonitor)
                    {
                        index = i + 1; // offset by Primary Screen item
                        break;
                    }
                }
                CmbMonitors.SelectedIndex = index;
            }
        }

        private void SaveSettingsFromUi()
        {
            _settings.OutputDir = TxtOutputDir.Text.Trim();
            _settings.BufferHours = (int)SldBufferHours.Value;
            _settings.EnableOcr = ChkEnableOcr.IsChecked ?? true;
            _settings.ShowBorderIndicator = ChkShowBorder.IsChecked ?? true;

            // Process exclusions list
            _settings.ExcludedProcesses = TxtExcludedApps.Text
                .Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().ToLower())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            // Set selected monitor device name
            if (CmbMonitors.SelectedIndex <= 0)
            {
                _settings.SelectedMonitor = "Primary";
            }
            else
            {
                var screens = Screen.AllScreens;
                int screenIdx = CmbMonitors.SelectedIndex - 1;
                if (screenIdx >= 0 && screenIdx < screens.Length)
                {
                    _settings.SelectedMonitor = screens[screenIdx].DeviceName;
                }
            }

            _settings.Save();
            IsSaved = true;
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            // Note: Use WinForms FolderBrowserDialog since it is built-in and works great
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select Recording Cache Directory";
                dialog.UseDescriptionForTitle = true;
                dialog.SelectedPath = TxtOutputDir.Text;
                
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    TxtOutputDir.Text = dialog.SelectedPath;
                    UpdateDiskSpaceEstimate();
                }
            }
        }

        private void BufferHours_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LblBufferText == null) return;

            int hours = (int)e.NewValue;
            if (hours >= 24)
            {
                int days = hours / 24;
                int rem = hours % 24;
                LblBufferText.Text = $"{days} Day{(days > 1 ? "s" : "")}" + (rem > 0 ? $" {rem}h" : "");
            }
            else
            {
                LblBufferText.Text = $"{hours} Hour{(hours > 1 ? "s" : "")}";
            }

            UpdateDiskSpaceEstimate();
        }

        private void UpdateDiskSpaceEstimate()
        {
            if (LblBufferSpaceEstimate == null || LblDiskSpace == null || PrgDiskSpace == null || TxtOutputDir == null) return;

            int hours = (int)SldBufferHours.Value;
            double gbEst = hours * 1.5; // ~1.5 GB/hour
            LblBufferSpaceEstimate.Text = $"Est. space requirement: ~{gbEst:F1} GB";

            string path = TxtOutputDir.Text.Trim();
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                string drive = Path.GetPathRoot(path) ?? "C:\\";
                if (!Directory.Exists(drive)) return;

                var driveInfo = new DriveInfo(drive);
                long freeBytes = driveInfo.AvailableFreeSpace;
                long totalBytes = driveInfo.TotalSize;

                double freeGb = freeBytes / (1024.0 * 1024.0 * 1024.0);
                double totalGb = totalBytes / (1024.0 * 1024.0 * 1024.0);
                double usedGb = totalGb - freeGb;

                LblDiskSpace.Text = $"{freeGb:F0} GB Free / {totalGb:F0} GB Total";

                // Progress Bar: percent of drive used
                double usedPct = (usedGb / totalGb) * 100;
                PrgDiskSpace.Value = usedPct;

                // Color of progress bar: green unless free space is low
                if (freeGb < gbEst || freeGb < 10) // less than estimated buffer size or less than 10GB
                {
                    PrgDiskSpace.Foreground = System.Windows.Media.Brushes.Tomato;
                    LblDiskWarning.Visibility = Visibility.Visible;
                }
                else
                {
                    PrgDiskSpace.Foreground = System.Windows.Media.Brushes.LightGreen;
                    LblDiskWarning.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                LblDiskSpace.Text = "Unknown space";
                PrgDiskSpace.Value = 0;
                LblDiskWarning.Visibility = Visibility.Collapsed;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string path = TxtOutputDir.Text.Trim();
            if (string.IsNullOrEmpty(path))
            {
                MessageBox.Show("Please specify a valid storage folder.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to create folder: {ex.Message}", "Storage Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SaveSettingsFromUi();
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
