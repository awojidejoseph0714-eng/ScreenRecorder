using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ScreenRecorder
{
    public partial class App : Application
    {
        // DLL Imports for Native C++ Encoder
        [DllImport("Encoder.dll", EntryPoint = "InitializeEncoder", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern bool InitializeEncoder(string filePath, int width, int height, int fps);

        [DllImport("Encoder.dll", EntryPoint = "WriteFrame", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool WriteFrame(IntPtr pixelData, int frameIndex);

        [DllImport("Encoder.dll", EntryPoint = "CloseEncoder", CallingConvention = CallingConvention.Cdecl)]
        private static extern void CloseEncoder();

        // Win32 Foreground APIs
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // Core fields
        private NotifyIcon? _notifyIcon;
        private AppSettings _settings = null!;
        private OcrIndexer _ocrIndexer = null!;
        private HotkeyManager? _hotkeyManager;
        
        // Recording State
        private bool _isRecordingActive = false;
        private bool _isExcludedAppForeground = false;
        private Thread? _captureThread;
        private CancellationTokenSource? _captureCts;
        
        private long _currentSegmentId = -1;
        private string _currentSegmentFile = "";
        private DateTime _currentSegmentStart;
        private int _currentSegmentFrameIndex = 0;
        private Rectangle _recordRect;

        // Visual Indicator Windows
        private RecordBorderWindow? _borderWindow;

        // Rolling buffer cleanups
        private System.Threading.Timer? _cleanupTimer;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. Load configuration settings
            _settings = AppSettings.Load();

            // 2. Initialize database and local OCR Indexer
            _ocrIndexer = new OcrIndexer();
            _ocrIndexer.Initialize(_settings.OutputDir);

            // 3. Initialize visual indicator and Tray Icon
            InitializeTrayIcon();

            // 4. Register global hotkeys
            InitializeHotkeys();

            // 5. Start continuous rolling recording (standard DVR behavior)
            StartRecording();

            // 6. Start 30-second rolling buffer cleanup timer
            _cleanupTimer = new System.Threading.Timer(CleanupBufferCallback, null, 10000, 30000);
        }

        private void InitializeTrayIcon()
        {
            _notifyIcon = new NotifyIcon
            {
                Text = "Screen Recorder v2 (Ctrl+Alt+V to Replay)",
                Visible = true
            };

            // Modern tray context menu
            var menu = new ContextMenuStrip();
            
            var titleItem = new ToolStripMenuItem("Screen Recorder v2") { Enabled = false };
            menu.Items.Add(titleItem);
            menu.Items.Add(new ToolStripSeparator());

            var replayItem = new ToolStripMenuItem("Open Replay & Search", null, (s, ev) => OpenReplayWindow());
            replayItem.Font = new Font(replayItem.Font, System.Drawing.FontStyle.Bold);
            menu.Items.Add(replayItem);

            var settingsItem = new ToolStripMenuItem("Settings...", null, (s, ev) => OpenSettingsWindow());
            menu.Items.Add(settingsItem);

            var folderItem = new ToolStripMenuItem("Open Recordings Folder", null, (s, ev) => OpenRecordingsFolder());
            menu.Items.Add(folderItem);

            menu.Items.Add(new ToolStripSeparator());

            var pauseItem = new ToolStripMenuItem("Pause Buffer Capture", null, (s, ev) => ToggleRecordingState());
            pauseItem.Name = "PauseToggle";
            menu.Items.Add(pauseItem);

            var exitItem = new ToolStripMenuItem("Exit", null, (s, ev) => ExitApplication());
            menu.Items.Add(exitItem);

            _notifyIcon.ContextMenuStrip = menu;
            
            // Double click opens the Replay Player
            _notifyIcon.DoubleClick += (s, ev) => OpenReplayWindow();

            // Set Initial Tray Icon (Steady Red Dot)
            UpdateTrayIcon(false);
        }

        private void UpdateTrayIcon(bool recordingPaused)
        {
            if (_notifyIcon == null) return;

            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;

                    // Slate background roundbox
                    using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(30, 30, 46)))
                    {
                        using (GraphicsPath path = new GraphicsPath())
                        {
                            path.AddArc(2, 2, 8, 8, 180, 90);
                            path.AddArc(22, 2, 8, 8, 270, 90);
                            path.AddArc(22, 22, 8, 8, 0, 90);
                            path.AddArc(2, 22, 8, 8, 90, 90);
                            path.CloseFigure();
                            g.FillPath(bgBrush, path);
                        }
                    }

                    // Recording Dot indicator
                    Color dotColor = recordingPaused ? Color.FromArgb(249, 226, 175) : Color.FromArgb(243, 139, 168); // Yellow (paused) vs Red (active)
                    using (SolidBrush dotBrush = new SolidBrush(dotColor))
                    {
                        g.FillEllipse(dotBrush, 10, 10, 12, 12);
                    }
                }

                var iconHandle = bmp.GetHicon();
                _notifyIcon.Icon = Icon.FromHandle(iconHandle);
            }
        }

        private void InitializeHotkeys()
        {
            // Hidden WPF Window to host hotkey hooks
            var hookWindow = new Window { Width = 0, Height = 0, WindowStyle = WindowStyle.None, ShowInTaskbar = false, Visibility = Visibility.Hidden };
            hookWindow.Show();

            _hotkeyManager = new HotkeyManager(hookWindow);

            // Register Toggle Recording Hotkey (Ctrl + Alt + R)
            bool regRec = _hotkeyManager.Register(101, _settings.RecordHotkeyModifiers, _settings.RecordHotkeyKey, ToggleRecordingState);
            // Register Open Replay Hotkey (Ctrl + Alt + V)
            bool regRep = _hotkeyManager.Register(102, _settings.ReplayHotkeyModifiers, _settings.ReplayHotkeyKey, OpenReplayWindow);

            if (!regRec || !regRep)
            {
                // Fallback / warning
                string message = "Global hotkey registration failed! Another application is already using Ctrl+Alt+R or Ctrl+Alt+V.\n\nPlease choose alternative hotkeys in settings.";
                _notifyIcon?.ShowBalloonTip(4000, "Hotkey Conflict", message, ToolTipIcon.Warning);
            }
        }

        private void StartRecording()
        {
            if (_isRecordingActive) return;

            // Define record bounds based on selected screen
            Screen selectedScreen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
            if (_settings.SelectedMonitor != "Primary")
            {
                var matched = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _settings.SelectedMonitor);
                if (matched != null) selectedScreen = matched;
            }

            // Ensure bounds are multiples of 2
            int w = selectedScreen.Bounds.Width;
            int h = selectedScreen.Bounds.Height;
            if (w % 2 != 0) w++;
            if (h % 2 != 0) h++;
            _recordRect = new Rectangle(selectedScreen.Bounds.Left, selectedScreen.Bounds.Top, w, h);

            _isRecordingActive = true;
            _isExcludedAppForeground = false;
            _captureCts = new CancellationTokenSource();

            // Open red border indicator overlay if enabled
            UpdateBorderIndicator();

            // Start capture thread
            _captureThread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "ScreenCapture"
            };
            _captureThread.Start(_captureCts.Token);

            var pauseToggleItem = _notifyIcon?.ContextMenuStrip?.Items["PauseToggle"] as ToolStripMenuItem;
            if (pauseToggleItem != null) pauseToggleItem.Text = "Pause Buffer Capture";

            UpdateTrayIcon(false);
        }

        private void StopRecording()
        {
            if (!_isRecordingActive) return;

            _isRecordingActive = false;
            _captureCts?.Cancel();

            if (_captureThread != null && _captureThread.IsAlive)
            {
                _captureThread.Join(1000);
            }

            // Finalize any open segment
            CloseCurrentSegment();

            // Close visual border
            CloseBorderIndicator();

            var pauseToggleItem = _notifyIcon?.ContextMenuStrip?.Items["PauseToggle"] as ToolStripMenuItem;
            if (pauseToggleItem != null) pauseToggleItem.Text = "Resume Buffer Capture";

            UpdateTrayIcon(true);
        }

        private void ToggleRecordingState()
        {
            if (_isRecordingActive)
            {
                StopRecording();
                _notifyIcon?.ShowBalloonTip(3000, "Screen Recorder Paused", "Rolling buffer recording has been paused manually.", ToolTipIcon.Info);
            }
            else
            {
                StartRecording();
                _notifyIcon?.ShowBalloonTip(3000, "Screen Recorder Active", "Rolling buffer recording has resumed.", ToolTipIcon.Info);
            }
        }

        private void UpdateBorderIndicator()
        {
            Dispatcher.Invoke(() =>
            {
                CloseBorderIndicator();

                if (_isRecordingActive && _settings.ShowBorderIndicator && !_isExcludedAppForeground)
                {
                    _borderWindow = new RecordBorderWindow(_recordRect);
                    _borderWindow.Show();
                }
            });
        }

        private void CloseBorderIndicator()
        {
            if (_borderWindow != null)
            {
                _borderWindow.Close();
                _borderWindow = null;
            }
        }

        private void StartNewSegment()
        {
            CloseCurrentSegment();

            if (!Directory.Exists(_settings.OutputDir))
            {
                Directory.CreateDirectory(_settings.OutputDir);
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"Recording_{timestamp}.mp4";
            _currentSegmentFile = Path.Combine(_settings.OutputDir, fileName);
            _currentSegmentStart = DateTime.UtcNow;
            _currentSegmentFrameIndex = 0;

            bool initSuccess = InitializeEncoder(_currentSegmentFile, _recordRect.Width, _recordRect.Height, 20); // target FPS is 20
            if (initSuccess)
            {
                // Register in SQLite
                long startTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _currentSegmentId = _ocrIndexer.RegisterSegment(_currentSegmentFile, startTimeUnixMs);
                Debug.WriteLine($"Started new recording segment: {fileName} (ID: {_currentSegmentId})");
            }
            else
            {
                _currentSegmentId = -1;
                _currentSegmentFile = "";
                Debug.WriteLine("[ERROR] Failed to start native video encoder segment.");
            }
        }

        private void CloseCurrentSegment()
        {
            if (_currentSegmentId != -1)
            {
                CloseEncoder();
                long endTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _ocrIndexer.UpdateSegmentEndTime(_currentSegmentId, endTimeUnixMs);
                Debug.WriteLine($"Closed segment (ID: {_currentSegmentId})");
                _currentSegmentId = -1;
                _currentSegmentFile = "";
            }
        }

        private uint _lastForegroundPid = 0;
        private bool _isForegroundExcluded = false;

        private bool IsForegroundWindowExcluded()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);

            if (pid != _lastForegroundPid)
            {
                _lastForegroundPid = pid;
                _isForegroundExcluded = false;
                try
                {
                    using (var proc = Process.GetProcessById((int)pid))
                    {
                        string procName = proc.ProcessName.ToLower();
                        if (_settings.ExcludedProcesses.Contains(procName))
                        {
                            _isForegroundExcluded = true;
                        }
                    }
                }
                catch
                {
                    // Process access denied or terminated
                }
            }

            return _isForegroundExcluded;
        }

        private void CaptureLoop(object? tokenObj)
        {
            var token = (CancellationToken)tokenObj!;
            int targetFps = 20;
            double frameDurationMs = 1000.0 / targetFps;
            double nextFrameTimeMs = 0;
            Stopwatch sw = Stopwatch.StartNew();

            // Preallocate GDI capture graphics objects
            using Bitmap captureBmp = new Bitmap(_recordRect.Width, _recordRect.Height, PixelFormat.Format32bppRgb);
            using Graphics g = Graphics.FromImage(captureBmp);

            while (!token.IsCancellationRequested)
            {
                double currentTimeMs = sw.Elapsed.TotalMilliseconds;
                if (currentTimeMs >= nextFrameTimeMs)
                {
                    // 1. App Exclusion Check (checks active window)
                    bool isExcluded = IsForegroundWindowExcluded();
                    if (isExcluded)
                    {
                        if (!_isExcludedAppForeground)
                        {
                            // Shift state to excluded: finalize current segment immediately
                            _isExcludedAppForeground = true;
                            CloseCurrentSegment();
                            UpdateBorderIndicator(); // Hide border indicator when paused by exclusion
                            UpdateTrayIcon(true);
                        }

                        // Sleep and wait while app is active
                        Thread.Sleep(100);
                        continue;
                    }
                    else if (_isExcludedAppForeground)
                    {
                        // Exited exclusion: start new segment and resume
                        _isExcludedAppForeground = false;
                        _isForegroundExcluded = false;
                        StartNewSegment();
                        UpdateBorderIndicator(); // Restore indicator
                        UpdateTrayIcon(false);
                    }

                    // 2. Start segment if not yet initialized
                    if (_currentSegmentId == -1)
                    {
                        StartNewSegment();
                    }

                    // 3. Segment splitting check (1 minute)
                    if (DateTime.UtcNow - _currentSegmentStart > TimeSpan.FromMinutes(1))
                    {
                        StartNewSegment();
                    }

                    // 4. Capture screenshot
                    try
                    {
                        g.CopyFromScreen(_recordRect.Left, _recordRect.Top, 0, 0, _recordRect.Size, CopyPixelOperation.SourceCopy);
                        
                        // Draw cursor
                        DrawCursor(g);

                        // 5. Lock bits to encode frame via native DLL
                        BitmapData bmpData = captureBmp.LockBits(
                            new Rectangle(0, 0, _recordRect.Width, _recordRect.Height),
                            ImageLockMode.ReadOnly,
                            PixelFormat.Format32bppRgb
                        );

                        try
                        {
                            WriteFrame(bmpData.Scan0, _currentSegmentFrameIndex);
                        }
                        finally
                        {
                            captureBmp.UnlockBits(bmpData);
                        }

                        // 6. Push to background OCR indexing thread-safe queue every 40 frames (2 seconds)
                        if (_settings.EnableOcr && _currentSegmentFrameIndex % 40 == 0)
                        {
                            double offsetSeconds = _currentSegmentFrameIndex / (double)targetFps;
                            _ocrIndexer.AddFrameToQueue(captureBmp, _currentSegmentId, offsetSeconds);
                        }

                        _currentSegmentFrameIndex++;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Frame capture error: {ex.Message}");
                    }

                    nextFrameTimeMs += frameDurationMs;

                    if (currentTimeMs > nextFrameTimeMs + frameDurationMs)
                    {
                        nextFrameTimeMs = currentTimeMs + frameDurationMs;
                    }
                }

                int sleepTime = (int)(nextFrameTimeMs - sw.Elapsed.TotalMilliseconds);
                if (sleepTime > 0)
                {
                    Thread.Sleep(sleepTime);
                }
            }
        }

        private void DrawCursor(Graphics g)
        {
            // Win32 CURSORINFO P/Invokes inside C# Graphics context
            var cursorInfo = new CURSORINFO();
            cursorInfo.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            if (GetCursorInfo(out cursorInfo) && cursorInfo.flags == 1) // CURSOR_SHOWING
            {
                int cursorX = cursorInfo.ptScreenPos.x - _recordRect.Left;
                int cursorY = cursorInfo.ptScreenPos.y - _recordRect.Top;

                var iconInfo = new ICONINFO();
                if (GetIconInfo(cursorInfo.hCursor, out iconInfo))
                {
                    int hotspotX = iconInfo.xHotspot;
                    int hotspotY = iconInfo.yHotspot;

                    if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
                    if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);

                    try
                    {
                        using (Icon icon = Icon.FromHandle(cursorInfo.hCursor))
                        {
                            g.DrawIcon(icon, cursorX - hotspotX, cursorY - hotspotY);
                        }
                    }
                    catch { }
                }
            }
        }

        private void CleanupBufferCallback(object? state)
        {
            // Run background rolling buffer cleanup (deletes expired unpinned files)
            try
            {
                long expiryTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (_settings.BufferHours * 3600 * 1000);
                var segments = _ocrIndexer.GetSegments();

                bool deletedAny = false;
                foreach (var seg in segments)
                {
                    if (!seg.IsPinned && seg.EndTime < expiryTimeUnixMs)
                    {
                        // Delete file
                        if (File.Exists(seg.FilePath))
                        {
                            try
                            {
                                File.Delete(seg.FilePath);
                                Debug.WriteLine($"Buffer cleanup: Deleted expired segment {Path.GetFileName(seg.FilePath)}");
                                deletedAny = true;
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Failed to delete file {seg.FilePath}: {ex.Message}");
                            }
                        }

                        // Remove from database
                        _ocrIndexer.DeleteSegmentFromDb(seg.FilePath);
                        deletedAny = true;
                    }
                }

                if (deletedAny)
                {
                    _ocrIndexer.CheckpointDatabase();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Buffer manager cleanup error: {ex.Message}");
            }
        }

        private void OpenSettingsWindow()
        {
            // Open settings in dispatcher thread
            Dispatcher.Invoke(() =>
            {
                // Pause recording during settings adjustments
                bool wasRecording = _isRecordingActive;
                if (wasRecording) StopRecording();

                var settingsWindow = new SettingsWindow(_settings);
                settingsWindow.ShowDialog();

                // Restart recording if it was active
                if (wasRecording || settingsWindow.IsSaved)
                {
                    // Reload output directory and refresh OCR indexer location if changed
                    _ocrIndexer.Dispose();
                    _ocrIndexer = new OcrIndexer();
                    _ocrIndexer.Initialize(_settings.OutputDir);

                    StartRecording();
                }
            });
        }

        private void OpenReplayWindow()
        {
            Dispatcher.Invoke(() =>
            {
                // Open the Replay and Search UI
                var replayWindow = new ReplayWindow(_settings, _ocrIndexer);
                replayWindow.Show();
            });
        }

        private void OpenRecordingsFolder()
        {
            if (Directory.Exists(_settings.OutputDir))
            {
                Process.Start("explorer.exe", _settings.OutputDir);
            }
            else
            {
                MessageBox.Show("Recordings directory does not exist yet.", "Folder Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ExitApplication()
        {
            StopRecording();

            _cleanupTimer?.Dispose();
            _ocrIndexer.Dispose();
            _hotkeyManager?.Dispose();

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }

            Shutdown();
        }

        // Win32 cursor capture declarations
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorInfo(out CURSORINFO pci);

        [DllImport("user32.dll")]
        private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }
}
