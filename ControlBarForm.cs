using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ScreenRecorder
{
    public class ControlBarForm : Form
    {
        private Point _dragStart;
        private int _seconds = 0;
        private bool _isPaused = false;
        private bool _blinkOn = true;
        private System.Windows.Forms.Timer _blinkTimer;
        
        private Rectangle _pauseRect = new Rectangle(160, 10, 32, 32);
        private Rectangle _stopRect = new Rectangle(205, 10, 32, 32);
        private int _hoveredButton = -1; // -1 = none, 0 = pause, 1 = stop

        public event EventHandler? OnPauseToggle;
        public event EventHandler? OnStopRequested;

        public ControlBarForm()
        {
            this.DoubleBuffered = true;
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.Size = new Size(250, 52);
            this.BackColor = Color.FromArgb(30, 30, 46); // Modern slate dark color
            
            // Set starting position: bottom right of screen, slightly offset
            Rectangle screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            this.StartPosition = FormStartPosition.Manual;
            this.Location = new Point(screen.Width - this.Width - 40, screen.Height - this.Height - 80);

            // Blinking timer for the recording dot
            _blinkTimer = new System.Windows.Forms.Timer();
            _blinkTimer.Interval = 500;
            _blinkTimer.Tick += (s, e) => {
                _blinkOn = !_blinkOn;
                this.Invalidate(new Rectangle(15, 15, 20, 20)); // Only invalidate the dot region to prevent flickering
            };
            _blinkTimer.Start();

            // Set rounded corners region
            this.Load += (s, e) => {
                using (GraphicsPath path = GetRoundedRectPath(new Rectangle(0, 0, this.Width, this.Height), 12))
                {
                    this.Region = new Region(path);
                }
            };
        }

        public void UpdateTimer(int elapsedSeconds)
        {
            _seconds = elapsedSeconds;
            this.Invalidate(new Rectangle(40, 10, 110, 30)); // Update timer text region
        }

        public void SetPaused(bool isPaused)
        {
            _isPaused = isPaused;
            _hoveredButton = -1;
            this.Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // Check if clicking buttons
                if (_pauseRect.Contains(e.Location))
                {
                    OnPauseToggle?.Invoke(this, EventArgs.Empty);
                }
                else if (_stopRect.Contains(e.Location))
                {
                    OnStopRequested?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    _dragStart = e.Location;
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // Dragging the window
                this.Left += e.X - _dragStart.X;
                this.Top += e.Y - _dragStart.Y;
            }
            else
            {
                // Hover effects
                int prevHover = _hoveredButton;
                if (_pauseRect.Contains(e.Location))
                {
                    _hoveredButton = 0;
                }
                else if (_stopRect.Contains(e.Location))
                {
                    _hoveredButton = 1;
                }
                else
                {
                    _hoveredButton = -1;
                }

                if (prevHover != _hoveredButton)
                {
                    this.Invalidate();
                }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hoveredButton != -1)
            {
                _hoveredButton = -1;
                this.Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 1. Draw thin outer highlight border
            using (Pen borderPen = new Pen(Color.FromArgb(80, 255, 255, 255), 1))
            {
                using (GraphicsPath path = GetRoundedRectPath(new Rectangle(0, 0, this.Width - 1, this.Height - 1), 12))
                {
                    g.DrawPath(borderPen, path);
                }
            }

            // 2. Draw blinking recording indicator (Red circle)
            Color dotColor = Color.FromArgb(243, 139, 168); // Soft bright pinkish-red
            if (_isPaused)
            {
                dotColor = Color.FromArgb(249, 226, 175); // Yellow when paused
            }
            else if (!_blinkOn)
            {
                dotColor = Color.FromArgb(80, 243, 139, 168); // Dimmed red during blink off
            }

            using (SolidBrush dotBrush = new SolidBrush(dotColor))
            {
                g.FillEllipse(dotBrush, 18, 20, 12, 12);
            }

            // 3. Draw Timer text
            int minutes = _seconds / 60;
            int seconds = _seconds % 60;
            string timeStr = $"{minutes:D2}:{seconds:D2}";
            if (_isPaused) timeStr += " (PAUSED)";
            
            using (Font font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                g.DrawString(timeStr, font, textBrush, 36, 15);
            }

            // 4. Draw Pause/Resume button
            DrawButton(g, _pauseRect, _hoveredButton == 0, _isPaused ? "play" : "pause");

            // 5. Draw Stop button
            DrawButton(g, _stopRect, _hoveredButton == 1, "stop");
        }

        private void DrawButton(Graphics g, Rectangle rect, bool isHovered, string type)
        {
            // Draw button background
            Color bg = isHovered ? Color.FromArgb(60, 60, 80) : Color.FromArgb(45, 45, 65);
            using (SolidBrush brush = new SolidBrush(bg))
            {
                g.FillEllipse(brush, rect);
            }

            // Draw button outline
            using (Pen outlinePen = new Pen(Color.FromArgb(100, 255, 255, 255), 1))
            {
                g.DrawEllipse(outlinePen, rect);
            }

            // Draw icon inside
            using (SolidBrush iconBrush = new SolidBrush(Color.White))
            {
                if (type == "pause")
                {
                    // Draw pause bars
                    g.FillRectangle(iconBrush, rect.X + 11, rect.Y + 10, 3, 12);
                    g.FillRectangle(iconBrush, rect.X + 18, rect.Y + 10, 3, 12);
                }
                else if (type == "play")
                {
                    // Draw play triangle
                    Point[] points = {
                        new Point(rect.X + 12, rect.Y + 10),
                        new Point(rect.X + 12, rect.Y + 22),
                        new Point(rect.X + 22, rect.Y + 16)
                    };
                    g.FillPolygon(iconBrush, points);
                }
                else if (type == "stop")
                {
                    // Draw stop square (Red square)
                    using (SolidBrush stopBrush = new SolidBrush(Color.FromArgb(243, 139, 168)))
                    {
                        g.FillRectangle(stopBrush, rect.X + 10, rect.Y + 10, 12, 12);
                    }
                }
            }
        }

        private GraphicsPath GetRoundedRectPath(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            Rectangle arc = new Rectangle(rect.X, rect.Y, diameter, diameter);
            
            // Top-left
            path.AddArc(arc, 180, 90);
            
            // Top-right
            arc.X = rect.Right - diameter;
            path.AddArc(arc, 270, 90);
            
            // Bottom-right
            arc.Y = rect.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            
            // Bottom-left
            arc.X = rect.Left;
            path.AddArc(arc, 90, 90);
            
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _blinkTimer?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
