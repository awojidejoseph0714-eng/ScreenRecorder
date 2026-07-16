using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using Application = System.Windows.Application;

namespace ScreenRecorder
{
    public class SavedClipItem
    {
        public string Name { get; set; } = "";
        public string FilePath { get; set; } = "";
        public DateTime CreationTime { get; set; }
    }

    public partial class ReplayWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly OcrIndexer _ocrIndexer;

        private MediaPlayer _player = null!;
        private VideoDrawing _videoDrawing = null!;
        private DispatcherTimer _timer = null!;

        private List<SegmentInfo> _segments = new List<SegmentInfo>();
        private SegmentInfo? _currentSegment;
        private long _oldestTimeMs = 0;
        private long _newestTimeMs = 0;
        private bool _isDraggingSlider = false;
        private bool _isPaused = false;

        // Clip Range selection markers
        private double _markStartVal = -1;
        private double _markEndVal = -1;

        public ReplayWindow(AppSettings settings, OcrIndexer ocrIndexer)
        {
            InitializeComponent();
            _settings = settings;
            _ocrIndexer = ocrIndexer;

            InitializePlayer();
            LoadTimelineData();
            RefreshSavedClipsList();

            // Default to OCR Search tab
            SelectTab(true);
        }

        private void InitializePlayer()
        {
            _player = new MediaPlayer();
            _player.MediaEnded += Player_MediaEnded;
            _player.MediaOpened += Player_MediaOpened;
            _player.MediaFailed += Player_MediaFailed;

            _videoDrawing = new VideoDrawing
            {
                Rect = new Rect(0, 0, 1920, 1080),
                Player = _player
            };

            var drawingGroup = new DrawingGroup();
            drawingGroup.Children.Add(_videoDrawing);
            VideoPlayerDrawingImage.Drawing = drawingGroup;

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        private void LoadTimelineData()
        {
            _segments = _ocrIndexer.GetSegments();
            if (_segments.Count == 0)
            {
                LblCurrentPlayTime.Text = "No rolling recordings found yet.";
                SldTimeline.IsEnabled = false;
                return;
            }

            _oldestTimeMs = _segments.First().StartTime;
            _newestTimeMs = _segments.Last().EndTime;

            double totalSeconds = (_newestTimeMs - _oldestTimeMs) / 1000.0;
            if (totalSeconds <= 0) totalSeconds = 1;

            SldTimeline.Minimum = 0;
            SldTimeline.Maximum = totalSeconds;
            SldTimeline.Value = 0;
            SldTimeline.IsEnabled = true;

            var startLocal = DateTimeOffset.FromUnixTimeMilliseconds(_oldestTimeMs).DateTime.ToLocalTime();
            var endLocal = DateTimeOffset.FromUnixTimeMilliseconds(_newestTimeMs).DateTime.ToLocalTime();
            LblTimelineStart.Text = startLocal.ToString("t");
            LblTimelineEnd.Text = endLocal.ToString("t");

            // Reset markers
            _markStartVal = -1;
            _markEndVal = -1;
            SldTimeline.SelectionStart = 0;
            SldTimeline.SelectionEnd = 0;
            UpdateClipRangeLabel();

            PlayLatestSegment();
        }

        private void PlayLatestSegment()
        {
            if (_segments.Count == 0) return;
            var latest = _segments.Last();
            LoadSegment(latest, 0);
        }

        private void LoadSegment(SegmentInfo segment, double offsetSeconds)
        {
            if (!File.Exists(segment.FilePath))
            {
                Debug.WriteLine($"Recording file not found: {segment.FilePath}");
                return;
            }

            _currentSegment = segment;
            _player.Open(new Uri(segment.FilePath));
            _player.Position = TimeSpan.FromSeconds(offsetSeconds);
            
            if (!_isPaused)
            {
                _player.Play();
                BdrPausedOverlay.Visibility = Visibility.Collapsed;
                BtnPlayPause.Content = "⏸ Pause";
            }
            else
            {
                _player.Pause();
                BdrPausedOverlay.Visibility = Visibility.Visible;
                BtnPlayPause.Content = "▶ Play";
            }
        }

        private void Player_MediaEnded(object? sender, EventArgs e)
        {
            if (_currentSegment == null) return;

            int index = _segments.FindIndex(s => s.Id == _currentSegment.Id);
            if (index != -1 && index + 1 < _segments.Count)
            {
                var nextSeg = _segments[index + 1];
                LoadSegment(nextSeg, 0);
            }
            else
            {
                _player.Pause();
                _isPaused = true;
                BdrPausedOverlay.Visibility = Visibility.Visible;
                BtnPlayPause.Content = "▶ Play";
            }
        }

        private void Player_MediaOpened(object? sender, EventArgs e)
        {
            double videoW = _player.NaturalVideoWidth;
            double videoH = _player.NaturalVideoHeight;
            if (videoW > 0 && videoH > 0)
            {
                _videoDrawing.Rect = new Rect(0, 0, videoW, videoH);
            }

            if (_currentSegment == null)
            {
                // Playback of standalone saved clip: update scrubber boundaries
                double totalSec = _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan.TotalSeconds : 100;
                SldTimeline.Minimum = 0;
                SldTimeline.Maximum = totalSec;
                SldTimeline.Value = 0;
                SldTimeline.IsEnabled = true;
                LblTimelineStart.Text = "00:00";
                LblTimelineEnd.Text = _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan.ToString(@"mm\:ss") : "--:--";
            }
        }

        private void Player_MediaFailed(object? sender, ExceptionEventArgs e)
        {
            Debug.WriteLine($"MediaPlayer error: {e.ErrorException.Message}");
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            UpdateRecordButtonState();

            if (_isDraggingSlider) return;

            if (_currentSegment != null)
            {
                double elapsedSec = (_currentSegment.StartTime - _oldestTimeMs) / 1000.0 + _player.Position.TotalSeconds;
                SldTimeline.Value = elapsedSec;

                var currentClock = DateTimeOffset.FromUnixTimeMilliseconds(_currentSegment.StartTime)
                                    .DateTime.ToLocalTime().Add(_player.Position);
                LblCurrentPlayTime.Text = currentClock.ToString("F");
            }
            else
            {
                // Playback of standalone clip
                SldTimeline.Value = _player.Position.TotalSeconds;
                string elapsedStr = _player.Position.ToString(@"mm\:ss");
                string totalStr = _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan.ToString(@"mm\:ss") : "--:--";
                LblCurrentPlayTime.Text = $"Clip: {elapsedStr} / {totalStr}";
            }
        }

        private void SldTimeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isDraggingSlider) return;
            SeekToTimelineSeconds(e.NewValue);
        }

        private void SldTimeline_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSlider = true;
        }

        private void SldTimeline_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSlider = false;
            SeekToTimelineSeconds(SldTimeline.Value);
        }

        private void SeekToTimelineSeconds(double totalSeconds)
        {
            if (_currentSegment == null)
            {
                // Standalone clip seek
                _player.Position = TimeSpan.FromSeconds(totalSeconds);
                return;
            }

            if (_segments.Count == 0) return;

            long targetTimeMs = _oldestTimeMs + (long)(totalSeconds * 1000);
            var segment = _segments.FirstOrDefault(s => targetTimeMs >= s.StartTime && targetTimeMs <= s.EndTime);
            
            if (segment != null)
            {
                double offset = (targetTimeMs - segment.StartTime) / 1000.0;
                if (_currentSegment?.Id != segment.Id)
                {
                    LoadSegment(segment, offset);
                }
                else
                {
                    _player.Position = TimeSpan.FromSeconds(offset);
                }
            }
            else
            {
                // Look for next segment if we hit a gap
                var nextSeg = _segments.FirstOrDefault(s => s.StartTime >= targetTimeMs);
                if (nextSeg != null)
                {
                    double timelineOffsetSec = (nextSeg.StartTime - _oldestTimeMs) / 1000.0;
                    SldTimeline.Value = timelineOffsetSec;
                    LoadSegment(nextSeg, 0);
                }
            }
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_isPaused)
            {
                _player.Play();
                _isPaused = false;
                BdrPausedOverlay.Visibility = Visibility.Collapsed;
                BtnPlayPause.Content = "⏸ Pause";
            }
            else
            {
                _player.Pause();
                _isPaused = true;
                BdrPausedOverlay.Visibility = Visibility.Visible;
                BtnPlayPause.Content = "▶ Play";
            }
        }

        private void Rewind30s_Click(object sender, RoutedEventArgs e)
        {
            ShiftPlaybackPosition(-30);
        }

        private void Forward30s_Click(object sender, RoutedEventArgs e)
        {
            ShiftPlaybackPosition(30);
        }

        private void Rewind5m_Click(object sender, RoutedEventArgs e)
        {
            ShiftPlaybackPosition(-300);
        }

        private void Forward5m_Click(object sender, RoutedEventArgs e)
        {
            ShiftPlaybackPosition(300);
        }

        private void ShiftPlaybackPosition(double seconds)
        {
            if (_currentSegment == null)
            {
                double target = _player.Position.TotalSeconds + seconds;
                if (target < 0) target = 0;
                double max = _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan.TotalSeconds : 100;
                if (target > max) target = max;
                _player.Position = TimeSpan.FromSeconds(target);
                return;
            }

            double currentSec = (_currentSegment.StartTime - _oldestTimeMs) / 1000.0 + _player.Position.TotalSeconds;
            double targetSec = currentSec + seconds;

            if (targetSec < 0) targetSec = 0;
            double maxSec = (_newestTimeMs - _oldestTimeMs) / 1000.0;
            if (targetSec > maxSec) targetSec = maxSec;

            SldTimeline.Value = targetSec;
            SeekToTimelineSeconds(targetSec);
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        // Tab Handling
        private void SearchTab_Click(object sender, RoutedEventArgs e)
        {
            SelectTab(true);
        }

        private void PinnedTab_Click(object sender, RoutedEventArgs e)
        {
            SelectTab(false);
            RefreshSavedClipsList();
        }

        private void SelectTab(bool showSearch)
        {
            if (showSearch)
            {
                BtnSearchTab.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44));
                BtnSearchTab.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));
                BtnPinnedTab.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33));
                BtnPinnedTab.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));

                GridSearchTab.Visibility = Visibility.Visible;
                GridPinnedTab.Visibility = Visibility.Collapsed;
            }
            else
            {
                BtnPinnedTab.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44));
                BtnPinnedTab.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));
                BtnSearchTab.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33));
                BtnSearchTab.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));

                GridSearchTab.Visibility = Visibility.Collapsed;
                GridPinnedTab.Visibility = Visibility.Visible;
            }
        }

        // FTS OCR Searching
        private void SearchQuery_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            string query = TxtSearchQuery.Text.Trim();
            if (string.IsNullOrEmpty(query))
            {
                LstSearchResults.ItemsSource = null;
                return;
            }

            var results = _ocrIndexer.Search(query);
            LstSearchResults.ItemsSource = results;
        }

        private void LstSearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = LstSearchResults.SelectedItem as SearchResult;
            if (selected == null) return;

            var seg = _segments.FirstOrDefault(s => s.FilePath == selected.FilePath);
            if (seg != null)
            {
                double totalTimelineSec = (seg.StartTime - _oldestTimeMs) / 1000.0 + selected.OffsetSeconds;
                SldTimeline.Value = totalTimelineSec;
                LoadSegment(seg, selected.OffsetSeconds);
            }
        }

        // Saved Clips Operations
        public void RefreshSavedClipsList()
        {
            try
            {
                if (!Directory.Exists(_settings.SavedDir))
                {
                    Directory.CreateDirectory(_settings.SavedDir);
                }

                var files = Directory.GetFiles(_settings.SavedDir, "*.mp4")
                    .Select(f => new SavedClipItem
                    {
                        Name = Path.GetFileName(f),
                        FilePath = f,
                        CreationTime = File.GetCreationTime(f)
                    })
                    .OrderByDescending(f => f.CreationTime)
                    .ToList();

                LstPinnedMoments.ItemsSource = files;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load saved clips list: {ex.Message}");
            }
        }

        private void PlaySavedClip_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var item = btn?.DataContext as SavedClipItem;
            if (item != null && File.Exists(item.FilePath))
            {
                _currentSegment = null; // playing standalone clip
                _player.Open(new Uri(item.FilePath));
                _player.Play();
                _isPaused = false;
                BdrPausedOverlay.Visibility = Visibility.Collapsed;
                BtnPlayPause.Content = "⏸ Pause";
            }
        }

        private void LstPinnedMoments_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = LstPinnedMoments.SelectedItem as SavedClipItem;
            if (selected != null && File.Exists(selected.FilePath))
            {
                _currentSegment = null;
                _player.Open(new Uri(selected.FilePath));
                _player.Play();
                _isPaused = false;
                BdrPausedOverlay.Visibility = Visibility.Collapsed;
                BtnPlayPause.Content = "⏸ Pause";
            }
        }

        private void OpenSavedFolder_Click(object sender, RoutedEventArgs e)
        {
            if (Directory.Exists(_settings.SavedDir))
            {
                Process.Start("explorer.exe", _settings.SavedDir);
            }
        }

        // Range Clipping Logic (Keyframes)
        private void MarkStart_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSegment == null) return; // Only allow clipping from rolling DVR buffer
            _markStartVal = SldTimeline.Value;
            SldTimeline.SelectionStart = _markStartVal;
            UpdateClipRangeLabel();
        }

        private void MarkEnd_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSegment == null) return;
            _markEndVal = SldTimeline.Value;
            SldTimeline.SelectionEnd = _markEndVal;
            UpdateClipRangeLabel();
        }

        private void UpdateClipRangeLabel()
        {
            if (_markStartVal < 0 && _markEndVal < 0)
            {
                LblClipRange.Text = "Select range by marking keyframes";
                return;
            }

            string startStr = _markStartVal >= 0 ? FormatTimelineTime(_markStartVal) : "--:--:--";
            string endStr = _markEndVal >= 0 ? FormatTimelineTime(_markEndVal) : "--:--:--";

            if (_markStartVal >= 0 && _markEndVal >= 0)
            {
                double duration = _markEndVal - _markStartVal;
                if (duration < 0)
                {
                    LblClipRange.Text = $"⚠️ End before Start! [{startStr} - {endStr}]";
                }
                else
                {
                    LblClipRange.Text = $"Selected: [{startStr} - {endStr}] ({duration:F0}s)";
                }
            }
            else
            {
                LblClipRange.Text = $"Selected: [{startStr} - {endStr}]";
            }
        }

        private string FormatTimelineTime(double offsetSec)
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(_oldestTimeMs).DateTime.ToLocalTime().AddSeconds(offsetSec);
            return dt.ToString("HH:mm:ss");
        }

        private void SaveClip_Click(object sender, RoutedEventArgs e)
        {
            if (_markStartVal < 0 || _markEndVal <= _markStartVal)
            {
                MessageBox.Show("Please mark a valid Start and End keyframe on the timeline first.", "Invalid Range", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            long startMs = _oldestTimeMs + (long)(_markStartVal * 1000.0);
            long endMs = _oldestTimeMs + (long)(_markEndVal * 1000.0);

            var intersectingSegments = _segments.Where(s => s.EndTime >= startMs && s.StartTime <= endMs).ToList();
            if (intersectingSegments.Count == 0)
            {
                MessageBox.Show("No temporary buffer files found covering this selected range.", "Export Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var saveDialog = new SaveFileDialog
            {
                Title = "Save Clipped Video",
                InitialDirectory = _settings.SavedDir,
                FileName = $"Clip_{DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExt = ".mp4",
                Filter = "MP4 Video (*.mp4)|*.mp4"
            };

            if (saveDialog.ShowDialog() == true)
            {
                string targetPath = saveDialog.FileName;
                string[] filePaths = intersectingSegments.Select(s => s.FilePath).ToArray();
                double[] startOffsets = intersectingSegments.Select(s => (s.StartTime - _oldestTimeMs) / 1000.0).ToArray();

                BtnSaveClip.IsEnabled = false;
                BtnSaveClip.Content = "Exporting...";

                Task.Run(() =>
                {
                    bool success = App.ClipVideo(
                        filePaths,
                        startOffsets,
                        filePaths.Length,
                        _markStartVal,
                        _markEndVal,
                        1920, 1080, 20,
                        targetPath
                    );

                    Dispatcher.Invoke(() =>
                    {
                        BtnSaveClip.IsEnabled = true;
                        BtnSaveClip.Content = "💾 Save Clip";
                        
                        if (success)
                        {
                            MessageBox.Show($"Clip saved successfully!\nDestination: {targetPath}", "Export Success", MessageBoxButton.OK, MessageBoxImage.Information);
                            RefreshSavedClipsList();
                        }
                        else
                        {
                            MessageBox.Show("Failed to merge and clip temporary files. Check if some segment files were cleaned up.", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    });
                });
            }
        }

        // Capture Record Button controls
        private void RecordToggle_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).ToggleRecordingState();
            UpdateRecordButtonState();
        }

        private void UpdateRecordButtonState()
        {
            if (BtnRecordToggle == null) return;
            bool active = ((App)Application.Current).IsRecordingActive;
            if (active)
            {
                BtnRecordToggle.Content = "🔴 Recording";
                BtnRecordToggle.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x55, 0x55));
            }
            else
            {
                BtnRecordToggle.Content = "⚫ Stopped";
                BtnRecordToggle.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));
            }
        }

        // Settings / Exit Menu Handlers
        private void MenuSettings_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).OpenSettingsWindow();
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).ExitApplication();
        }

        public void ReloadSettings()
        {
            LoadTimelineData();
            RefreshSavedClipsList();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!((App)Application.Current).IsExiting)
            {
                e.Cancel = true;
                this.Hide();
                ((App)Application.Current).ShowMinimizeTrayBalloon();
            }
            else
            {
                _player.Close();
                _timer.Stop();
                base.OnClosing(e);
            }
        }
    }
}
