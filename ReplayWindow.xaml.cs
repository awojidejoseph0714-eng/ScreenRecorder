using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;

namespace ScreenRecorder
{
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

        public ReplayWindow(AppSettings settings, OcrIndexer ocrIndexer)
        {
            InitializeComponent();
            _settings = settings;
            _ocrIndexer = ocrIndexer;

            InitializePlayer();
            LoadTimelineData();

            // Select Search Tab by default
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

            // Timer to update scrubber
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
                LblCurrentPlayTime.Text = "No recordings found.";
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

            // Set start and end labels
            var startLocal = DateTimeOffset.FromUnixTimeMilliseconds(_oldestTimeMs).DateTime.ToLocalTime();
            var endLocal = DateTimeOffset.FromUnixTimeMilliseconds(_newestTimeMs).DateTime.ToLocalTime();
            LblTimelineStart.Text = startLocal.ToString("t");
            LblTimelineEnd.Text = endLocal.ToString("t");

            // Load Pinned list
            RefreshPinnedList();

            // Start playing the latest segment by default
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
                // File might have been deleted, search next or prompt
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

            UpdatePinButtonState();
        }

        private void Player_MediaEnded(object? sender, EventArgs e)
        {
            if (_currentSegment == null) return;

            // Find next segment in chronological order
            int index = _segments.FindIndex(s => s.Id == _currentSegment.Id);
            if (index != -1 && index + 1 < _segments.Count)
            {
                var nextSeg = _segments[index + 1];
                // Gap handling: if next segment starts after current segment ends
                long gapMs = nextSeg.StartTime - _currentSegment.EndTime;
                if (gapMs > 2000)
                {
                    // There was a pause gap. Let's just switch and start playing next segment from 0
                    LoadSegment(nextSeg, 0);
                }
                else
                {
                    // Seamless next segment
                    LoadSegment(nextSeg, 0);
                }
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
            // Adjust video dimensions to avoid stretching
            double videoW = _player.NaturalVideoWidth;
            double videoH = _player.NaturalVideoHeight;
            if (videoW > 0 && videoH > 0)
            {
                _videoDrawing.Rect = new Rect(0, 0, videoW, videoH);
            }
        }

        private void Player_MediaFailed(object? sender, ExceptionEventArgs e)
        {
            Debug.WriteLine($"MediaPlayer error: {e.ErrorException.Message}");
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_isDraggingSlider || _currentSegment == null || _segments.Count == 0) return;

            double elapsedSec = (_currentSegment.StartTime - _oldestTimeMs) / 1000.0 + _player.Position.TotalSeconds;
            SldTimeline.Value = elapsedSec;

            // Update Current Play Time label
            var currentClock = DateTimeOffset.FromUnixTimeMilliseconds(_currentSegment.StartTime)
                                .DateTime.ToLocalTime().Add(_player.Position);
            LblCurrentPlayTime.Text = currentClock.ToString("F");
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
            if (_segments.Count == 0) return;

            long targetTimeMs = _oldestTimeMs + (long)(totalSeconds * 1000);
            
            // Find which segment covers this timestamp
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
                // We are in a gap (exclusion period). Look for the NEXT available segment
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
            if (_currentSegment == null) return;

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
            if (_currentSegment == null) return;

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
            _player.Close();
            _timer.Stop();
            Close();
        }

        // Tab Switching
        private void SearchTab_Click(object sender, RoutedEventArgs e)
        {
            SelectTab(true);
        }

        private void PinnedTab_Click(object sender, RoutedEventArgs e)
        {
            SelectTab(false);
        }

        private void SelectTab(bool showSearch)
        {
            if (showSearch)
            {
                BtnSearchTab.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x45, 0x47, 0x5A));
                BtnSearchTab.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCD, 0xD6, 0xF4));
                BtnPinnedTab.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x31, 0x32, 0x44));
                BtnPinnedTab.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA6, 0xAD, 0xC8));

                GridSearchTab.Visibility = Visibility.Visible;
                GridPinnedTab.Visibility = Visibility.Collapsed;
            }
            else
            {
                BtnPinnedTab.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x45, 0x47, 0x5A));
                BtnPinnedTab.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCD, 0xD6, 0xF4));
                BtnSearchTab.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x31, 0x32, 0x44));
                BtnSearchTab.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA6, 0xAD, 0xC8));

                GridSearchTab.Visibility = Visibility.Collapsed;
                GridPinnedTab.Visibility = Visibility.Visible;
            }
        }

        // OCR Search Handler
        private void SearchQuery_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
            string query = TxtSearchQuery.Text.Trim();
            if (string.IsNullOrEmpty(query))
            {
                LstSearchResults.ItemsSource = null;
                return;
            }

            // Run search query in SQLite database
            var results = _ocrIndexer.Search(query);
            LstSearchResults.ItemsSource = results;
        }

        private void LstSearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = LstSearchResults.SelectedItem as SearchResult;
            if (selected == null) return;

            // Find segment
            var seg = _segments.FirstOrDefault(s => s.FilePath == selected.FilePath);
            if (seg != null)
            {
                // Update scrubber and load
                double totalTimelineSec = (seg.StartTime - _oldestTimeMs) / 1000.0 + selected.OffsetSeconds;
                SldTimeline.Value = totalTimelineSec;
                LoadSegment(seg, selected.OffsetSeconds);
            }
        }

        // Pinning Moment Controls
        private void RefreshPinnedList()
        {
            var pinned = _segments.Where(s => s.IsPinned).ToList();
            LstPinnedMoments.ItemsSource = pinned;
        }

        private void UpdatePinButtonState()
        {
            if (_currentSegment == null) return;
            BtnPinMoment.Content = _currentSegment.IsPinned ? "📌 Unpin Segment" : "📌 Pin Segment";
            BtnPinMoment.Background = _currentSegment.IsPinned ? 
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x45, 0x47, 0x5A)) : 
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0x8B, 0xA8));
        }

        private void PinMoment_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSegment == null) return;

            bool isCurrentlyPinned = _currentSegment.IsPinned;
            string oldPath = _currentSegment.FilePath;
            string dir = Path.GetDirectoryName(oldPath) ?? _settings.OutputDir;
            string oldName = Path.GetFileName(oldPath);

            string newName;
            if (isCurrentlyPinned)
            {
                // Unpin: remove pinned_ prefix
                newName = oldName.StartsWith("pinned_") ? oldName.Substring(7) : oldName;
            }
            else
            {
                // Pin: add pinned_ prefix
                newName = oldName.StartsWith("pinned_") ? oldName : "pinned_" + oldName;
            }

            string newPath = Path.Combine(dir, newName);

            try
            {
                if (File.Exists(oldPath))
                {
                    File.Move(oldPath, newPath);
                }

                _currentSegment.FilePath = newPath;
                _currentSegment.IsPinned = !isCurrentlyPinned;

                // Update SQLite database state
                _ocrIndexer.PinSegment(oldPath, !isCurrentlyPinned);
                // Also update the filepath in SQLite
                UpdateFilePathInDb(oldPath, newPath);

                // Reload timeline to sync files
                LoadTimelineData();

                // Seek player back to current position
                double curPosSec = _player.Position.TotalSeconds;
                LoadSegment(_currentSegment, curPosSec);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to rename file: {ex.Message}", "Pinning Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateFilePathInDb(string oldPath, string newPath)
        {
            try
            {
                string dbPath = Path.Combine(_settings.OutputDir, "ocr_index.db");
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                using var cmd = new Microsoft.Data.Sqlite.SqliteCommand("UPDATE segments SET filepath = $newPath WHERE filepath = $oldPath", conn);
                cmd.Parameters.AddWithValue("$newPath", newPath);
                cmd.Parameters.AddWithValue("$oldPath", oldPath);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to update filename in database: {ex.Message}");
            }
        }

        private void LstPinnedMoments_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = LstPinnedMoments.SelectedItem as SegmentInfo;
            if (selected == null) return;

            double totalTimelineSec = (selected.StartTime - _oldestTimeMs) / 1000.0;
            SldTimeline.Value = totalTimelineSec;
            LoadSegment(selected, 0);
        }

        private void ExportPinned_Click(object sender, RoutedEventArgs e)
        {
            // Find segment bound to the clicked button's context
            var button = sender as System.Windows.Controls.Button;
            var segment = button?.DataContext as SegmentInfo;
            if (segment == null || !File.Exists(segment.FilePath)) return;

            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = Path.GetFileName(segment.FilePath).Replace("pinned_", ""),
                Filter = "MP4 Video (*.mp4)|*.mp4",
                Title = "Export Pinned Segment"
            };

            if (saveDialog.ShowDialog() == true)
            {
                try
                {
                    File.Copy(segment.FilePath, saveDialog.FileName, true);
                    MessageBox.Show("Clip exported successfully!", "Export Clip", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to export clip: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}
