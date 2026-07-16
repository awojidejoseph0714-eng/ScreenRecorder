using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ScreenRecorder
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new ScreenRecorderContext());
        }
    }

    public class ScreenRecorderContext : ApplicationContext
    {
        // P/Invoke for our custom native C++ Encoder DLL
        [DllImport("Encoder.dll", EntryPoint = "InitializeEncoder", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern bool InitializeEncoder(string filePath, int width, int height, int fps);

        [DllImport("Encoder.dll", EntryPoint = "WriteFrame", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool WriteFrame(IntPtr pixelData, int frameIndex);

        [DllImport("Encoder.dll", EntryPoint = "CloseEncoder", CallingConvention = CallingConvention.Cdecl)]
        private static extern void CloseEncoder();

        // Win32 Hotkey APIs
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // Win32 Cursor APIs
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

        private const int CURSOR_SHOWING = 0x00000001;

        // Constants
        private const int TargetFps = 20;
        private const int HotkeyIdPrimary = 1;
        private const int HotkeyIdFallback = 2;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_WIN = 0x0008;

        // UI and state
        private NotifyIcon _notifyIcon;
        private HotkeyWindow _hotkeyWindow;
        private bool _isRecording = false;
        private bool _isPaused = false;
        private Rectangle _recordRect;
        private string _lastSavedFilePath = "";

        // Recording thread & timers
        private Thread? _captureThread;
        private System.Windows.Forms.Timer _durationTimer;
        private int _elapsedSeconds = 0;
        private ControlBarForm? _controlBar;

        public ScreenRecorderContext()
        {
            // Initialize system tray icon
            _notifyIcon = new NotifyIcon();
            _notifyIcon.Text = "Screen Recorder (Win + Alt + R)";
            _notifyIcon.Visible = true;

            // Context menu
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem titleItem = new ToolStripMenuItem("Screen Recorder v1.0");
            titleItem.Enabled = false;
            menu.Items.Add(titleItem);
            menu.Items.Add(new ToolStripSeparator());
            
            ToolStripMenuItem startItem = new ToolStripMenuItem("Start Recording", null, (s, e) => ToggleRecording());
            startItem.Name = "Start";
            menu.Items.Add(startItem);

            ToolStripMenuItem stopItem = new ToolStripMenuItem("Stop Recording", null, (s, e) => StopRecording());
            stopItem.Name = "Stop";
            stopItem.Enabled = false;
            menu.Items.Add(stopItem);

            menu.Items.Add(new ToolStripMenuItem("Open Recordings Folder", null, (s, e) => OpenRecordingsFolder()));
            
            ToolStripMenuItem startupItem = new ToolStripMenuItem("Start with Windows", null, (s, e) => ToggleStartup());
            startupItem.Name = "Startup";
            startupItem.Checked = IsStartupEnabled();
            menu.Items.Add(startupItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => ExitApplication()));

            _notifyIcon.ContextMenuStrip = menu;
            
            // Double click on tray icon starts/stops recording
            _notifyIcon.DoubleClick += (s, e) => ToggleRecording();
            _notifyIcon.BalloonTipClicked += (s, e) => OpenLastRecording();

            // Set up a dynamic modern tray icon in memory
            _notifyIcon.Icon = CreateTrayIcon(Color.FromArgb(243, 139, 168)); // Blinking red color

            // Create hidden window for hotkey message handling
            _hotkeyWindow = new HotkeyWindow();
            _hotkeyWindow.HotkeyTriggered += ToggleRecording;

            // Register Hotkeys
            // Primary: Win + Alt + R (fsModifiers = MOD_WIN | MOD_ALT = 0x0009, key = R (82))
            bool regPrimary = RegisterHotKey(_hotkeyWindow.Handle, HotkeyIdPrimary, MOD_WIN | MOD_ALT, 82);
            // Fallback: Ctrl + Alt + R (fsModifiers = MOD_CONTROL | MOD_ALT = 0x0003, key = R (82))
            bool regFallback = RegisterHotKey(_hotkeyWindow.Handle, HotkeyIdFallback, MOD_CONTROL | MOD_ALT, 82);

            string message = "Screen Recorder is active in system tray.\n";
            if (regPrimary)
            {
                message += "Press Win + Alt + R to start recording.";
            }
            else if (regFallback)
            {
                message += "Hotkey Win+Alt+R is busy. Press Ctrl + Alt + R to record.";
            }
            else
            {
                message += "Both hotkeys are busy. Use the tray icon to start.";
            }

            _notifyIcon.ShowBalloonTip(4000, "Screen Recorder Active", message, ToolTipIcon.Info);

            // Duration timer (ticks every second to update UI)
            _durationTimer = new System.Windows.Forms.Timer();
            _durationTimer.Interval = 1000;
            _durationTimer.Tick += DurationTimer_Tick;
        }

        private Icon CreateTrayIcon(Color dotColor)
        {
            // Draw a stylish tray icon: a dark rounded box containing a colored recording dot
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    
                    // Background rounded rectangle
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

                    // Recording inner dot
                    using (SolidBrush dotBrush = new SolidBrush(dotColor))
                    {
                        g.FillEllipse(dotBrush, 10, 10, 12, 12);
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        private void ToggleRecording()
        {
            if (!_isRecording)
            {
                StartRecordingFlow();
            }
            else
            {
                StopRecording();
            }
        }

        private void StartRecordingFlow()
        {
            // 1. Show Snipping overlay
            using (OverlayForm overlay = new OverlayForm())
            {
                DialogResult res = overlay.ShowDialog();
                if (res != DialogResult.OK || overlay.IsCancelled)
                {
                    return; // cancelled or invalid region
                }

                _recordRect = overlay.SelectedRectangle;
            }

            // 2. Adjust rectangle coordinates to ensure Width and Height are multiples of 2.
            // Many hardware encoders fail if dimensions are odd numbers.
            int width = _recordRect.Width;
            int height = _recordRect.Height;
            if (width % 2 != 0) width++;
            if (height % 2 != 0) height++;
            _recordRect = new Rectangle(_recordRect.Left, _recordRect.Top, width, height);

            // 3. Define output path (Videos/Recordings folder)
            string videosPath = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrEmpty(videosPath))
            {
                videosPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
            }
            string recordingsDir = Path.Combine(videosPath, "Recordings");
            if (!Directory.Exists(recordingsDir))
            {
                Directory.CreateDirectory(recordingsDir);
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"Recording_{timestamp}.mp4";
            _lastSavedFilePath = Path.Combine(recordingsDir, fileName);

            // 4. Initialize native Media Foundation encoder DLL
            bool initSuccess = InitializeEncoder(_lastSavedFilePath, _recordRect.Width, _recordRect.Height, TargetFps);
            if (!initSuccess)
            {
                MessageBox.Show("Failed to initialize video encoder. Please check screen resolution settings.", 
                                "Encoder Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 5. Update state and tray menu
            _isRecording = true;
            _isPaused = false;
            _elapsedSeconds = 0;

            var menu = _notifyIcon.ContextMenuStrip;
            if (menu != null)
            {
                var startItem = menu.Items["Start"];
                if (startItem != null) startItem.Text = "Stop Recording";
                var stopItem = menu.Items["Stop"];
                if (stopItem != null) stopItem.Enabled = true;
            }

            _notifyIcon.Icon = CreateTrayIcon(Color.FromArgb(166, 227, 161)); // Green tray icon to indicate recording

            // 6. Show floating control bar
            _controlBar = new ControlBarForm();
            _controlBar.OnPauseToggle += (s, e) => TogglePause();
            _controlBar.OnStopRequested += (s, e) => StopRecording();
            _controlBar.Show();

            // 7. Start capture loop thread
            _captureThread = new Thread(CaptureLoop);
            _captureThread.IsBackground = true;
            _captureThread.Start();

            // Start timer
            _durationTimer.Start();
        }

        private void TogglePause()
        {
            if (!_isRecording) return;
            _isPaused = !_isPaused;
            _controlBar?.SetPaused(_isPaused);
            
            if (_isPaused)
            {
                _notifyIcon.Icon = CreateTrayIcon(Color.FromArgb(249, 226, 175)); // Yellow tray icon when paused
            }
            else
            {
                _notifyIcon.Icon = CreateTrayIcon(Color.FromArgb(166, 227, 161)); // Green tray icon when recording
            }
        }

        private void StopRecording()
        {
            if (!_isRecording) return;

            // 1. Stop timers and thread
            _durationTimer.Stop();
            _isRecording = false;

            if (_captureThread != null && _captureThread.IsAlive)
            {
                _captureThread.Join(1000); // Wait for thread to finish writing
            }

            // 2. Finalize video writer
            CloseEncoder();

            // 3. Clean up floating toolbar
            _controlBar?.Close();
            _controlBar = null;

            // 4. Reset tray icon and menu
            _notifyIcon.Icon = CreateTrayIcon(Color.FromArgb(243, 139, 168)); // back to red
            var menu = _notifyIcon.ContextMenuStrip;
            if (menu != null)
            {
                var startItem = menu.Items["Start"];
                if (startItem != null) startItem.Text = "Start Recording";
                var stopItem = menu.Items["Stop"];
                if (stopItem != null) stopItem.Enabled = false;
            }

            // 5. Show save notification
            _notifyIcon.ShowBalloonTip(4000, "Recording Saved", 
                $"Saved to:\n{Path.GetFileName(_lastSavedFilePath)}\nClick here to play the video.", 
                ToolTipIcon.Info);
        }

        private void DurationTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isPaused)
            {
                _elapsedSeconds++;
                _controlBar?.UpdateTimer(_elapsedSeconds);
            }
        }

        private void CaptureLoop()
        {
            int frameIndex = 0;
            Stopwatch sw = Stopwatch.StartNew();
            double frameDurationMs = 1000.0 / TargetFps;
            double nextFrameTimeMs = 0;
            
            int width = _recordRect.Width;
            int height = _recordRect.Height;

            // Preallocate capture bitmap
            using (Bitmap captureBmp = new Bitmap(width, height, PixelFormat.Format32bppRgb))
            {
                using (Graphics g = Graphics.FromImage(captureBmp))
                {
                    while (_isRecording)
                    {
                        if (_isPaused)
                        {
                            Thread.Sleep(50);
                            continue;
                        }

                        double currentTimeMs = sw.Elapsed.TotalMilliseconds;
                        if (currentTimeMs >= nextFrameTimeMs)
                        {
                            // Capture the screen region
                            g.CopyFromScreen(_recordRect.Left, _recordRect.Top, 0, 0, _recordRect.Size, CopyPixelOperation.SourceCopy);
                            
                            // Render mouse cursor on the frame
                            DrawCursor(g);

                            // Lock bits to pass memory pointer to DLL
                            BitmapData bmpData = captureBmp.LockBits(
                                new Rectangle(0, 0, width, height),
                                ImageLockMode.ReadOnly,
                                PixelFormat.Format32bppRgb);

                            try
                            {
                                WriteFrame(bmpData.Scan0, frameIndex);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Error writing frame: {ex.Message}");
                            }
                            finally
                            {
                                captureBmp.UnlockBits(bmpData);
                            }

                            frameIndex++;
                            nextFrameTimeMs += frameDurationMs;

                            // Skip target time ahead if we fall behind too much
                            if (currentTimeMs > nextFrameTimeMs + frameDurationMs)
                            {
                                nextFrameTimeMs = currentTimeMs + frameDurationMs;
                            }
                        }

                        // Sleep to maintain steady frame rate
                        int sleepTime = (int)(nextFrameTimeMs - sw.Elapsed.TotalMilliseconds);
                        if (sleepTime > 0)
                        {
                            Thread.Sleep(sleepTime);
                        }
                    }
                }
            }
        }

        private void DrawCursor(Graphics g)
        {
            CURSORINFO pci;
            pci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            if (GetCursorInfo(out pci) && pci.flags == CURSOR_SHOWING)
            {
                int cursorX = pci.ptScreenPos.x - _recordRect.Left;
                int cursorY = pci.ptScreenPos.y - _recordRect.Top;

                ICONINFO iconInfo;
                if (GetIconInfo(pci.hCursor, out iconInfo))
                {
                    int hotspotX = iconInfo.xHotspot;
                    int hotspotY = iconInfo.yHotspot;

                    // Delete created resources
                    if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
                    if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);

                    try
                    {
                        using (Icon icon = Icon.FromHandle(pci.hCursor))
                        {
                            g.DrawIcon(icon, cursorX - hotspotX, cursorY - hotspotY);
                        }
                    }
                    catch
                    {
                        // Ignore cursor drawing issues
                    }
                }
            }
        }

        private void OpenLastRecording()
        {
            if (File.Exists(_lastSavedFilePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(_lastSavedFilePath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to play video: {ex.Message}", "Playback Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void OpenRecordingsFolder()
        {
            string videosPath = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrEmpty(videosPath))
            {
                videosPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
            }
            string recordingsDir = Path.Combine(videosPath, "Recordings");
            if (Directory.Exists(recordingsDir))
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", recordingsDir) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open directory: {ex.Message}", "Explorer Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private bool IsStartupEnabled()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (key == null) return false;
                    object? val = key.GetValue("ScreenRecorder");
                    if (val == null) return false;
                    return val.ToString() == $"\"{Application.ExecutablePath}\"";
                }
            }
            catch
            {
                return false;
            }
        }

        private void ToggleStartup()
        {
            try
            {
                bool currentlyEnabled = IsStartupEnabled();
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    if (currentlyEnabled)
                    {
                        key.DeleteValue("ScreenRecorder", false);
                    }
                    else
                    {
                        key.SetValue("ScreenRecorder", $"\"{Application.ExecutablePath}\"");
                    }
                }

                var menu = _notifyIcon.ContextMenuStrip;
                if (menu != null)
                {
                    var startupItem = menu.Items["Startup"] as ToolStripMenuItem;
                    if (startupItem != null)
                    {
                        startupItem.Checked = !currentlyEnabled;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to toggle startup registry key: {ex.Message}", "Startup Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ExitApplication()
        {
            StopRecording();
            
            // Clean up resources
            UnregisterHotKey(_hotkeyWindow.Handle, HotkeyIdPrimary);
            UnregisterHotKey(_hotkeyWindow.Handle, HotkeyIdFallback);
            _hotkeyWindow.Dispose();

            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();

            Application.Exit();
        }
    }

    public class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        public event Action? HotkeyTriggered;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                HotkeyTriggered?.Invoke();
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            DestroyHandle();
        }
    }
}
