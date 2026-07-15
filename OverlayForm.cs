using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ScreenRecorder
{
    public class OverlayForm : Form
    {
        private Bitmap _screenshot = null!;
        private Point _startPoint;
        private Point _currentPoint;
        private bool _isSelecting = false;
        private Rectangle _virtualBounds;

        public Rectangle SelectedRectangle { get; private set; }
        public bool IsCancelled { get; private set; } = false;

        public OverlayForm()
        {
            this.DoubleBuffered = true;
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.Cursor = Cursors.Cross;

            // Capture virtual screen bounds to support multiple monitors
            _virtualBounds = SystemInformation.VirtualScreen;
            this.Bounds = _virtualBounds;

            // Take a screenshot of the entire screen
            CaptureScreen();
        }

        private void CaptureScreen()
        {
            _screenshot = new Bitmap(_virtualBounds.Width, _virtualBounds.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(_screenshot))
            {
                g.CopyFromScreen(_virtualBounds.Left, _virtualBounds.Top, 0, 0, _virtualBounds.Size, CopyPixelOperation.SourceCopy);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _startPoint = e.Location;
                _currentPoint = e.Location;
                _isSelecting = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_isSelecting)
            {
                _currentPoint = e.Location;
                this.Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_isSelecting && e.Button == MouseButtons.Left)
            {
                _isSelecting = false;
                Rectangle rect = GetSelectionRectangle();
                
                // Screen coordinates need to be shifted by virtualBounds offset
                rect.Offset(_virtualBounds.Location);
                
                SelectedRectangle = rect;
                
                if (SelectedRectangle.Width < 10 || SelectedRectangle.Height < 10)
                {
                    // Selection is too small, cancel
                    IsCancelled = true;
                }
                
                this.DialogResult = DialogResult.OK;
                this.Close();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                IsCancelled = true;
                this.DialogResult = DialogResult.Cancel;
                this.Close();
            }
        }

        private Rectangle GetSelectionRectangle()
        {
            int x = Math.Min(_startPoint.X, _currentPoint.X);
            int y = Math.Min(_startPoint.Y, _currentPoint.Y);
            int width = Math.Abs(_startPoint.X - _currentPoint.X);
            int height = Math.Abs(_startPoint.Y - _currentPoint.Y);
            return new Rectangle(x, y, width, height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 1. Draw the screenshot
            g.DrawImage(_screenshot, 0, 0);

            // 2. Draw a dark semi-transparent overlay
            using (SolidBrush overlayBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            {
                g.FillRectangle(overlayBrush, 0, 0, this.Width, this.Height);
            }

            if (_isSelecting)
            {
                Rectangle selection = GetSelectionRectangle();

                if (selection.Width > 0 && selection.Height > 0)
                {
                    // 3. Draw the clear selected region (by drawing the original screenshot in that region)
                    g.DrawImage(_screenshot, selection, selection, GraphicsUnit.Pixel);

                    // 4. Draw the border around the selection (outer white border, inner blue dashed border)
                    using (Pen whitePen = new Pen(Color.White, 1.0f))
                    {
                        g.DrawRectangle(whitePen, selection);
                    }
                    
                    using (Pen bluePen = new Pen(Color.FromArgb(255, 0, 120, 215), 1.5f))
                    {
                        bluePen.DashStyle = DashStyle.Dash;
                        g.DrawRectangle(bluePen, selection);
                    }

                    // 5. Draw live dimensions tooltip
                    string dimText = $"{selection.Width} x {selection.Height}";
                    using (Font font = new Font("Segoe UI", 9, FontStyle.Bold))
                    {
                        Size textSize = TextRenderer.MeasureText(dimText, font);
                        int rectWidth = textSize.Width + 12;
                        int rectHeight = textSize.Height + 8;
                        
                        // Place tooltip bottom-right of selection, or shift if near border
                        int tipX = selection.Right + 5;
                        int tipY = selection.Bottom + 5;
                        
                        if (tipX + rectWidth > this.Width) tipX = selection.Left - rectWidth - 5;
                        if (tipY + rectHeight > this.Height) tipY = selection.Top - rectHeight - 5;

                        if (tipX < 0) tipX = 5;
                        if (tipY < 0) tipY = 5;

                        Rectangle tipRect = new Rectangle(tipX, tipY, rectWidth, rectHeight);
                        
                        // Draw rounded container for tooltip
                        using (GraphicsPath path = GetRoundedRectPath(tipRect, 5))
                        {
                            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(220, 30, 30, 46)))
                            {
                                g.FillPath(bgBrush, path);
                            }
                            using (Pen borderPen = new Pen(Color.FromArgb(100, 255, 255, 255), 1))
                            {
                                g.DrawPath(borderPen, path);
                            }
                        }

                        using (SolidBrush textBrush = new SolidBrush(Color.White))
                        {
                            g.DrawString(dimText, font, textBrush, tipX + 6, tipY + 4);
                        }
                    }
                }
            }
            else
            {
                // Draw prompt message
                string prompt = "Drag to select recording area. Press ESC to cancel.";
                using (Font font = new Font("Segoe UI", 16, FontStyle.Bold))
                {
                    Size size = TextRenderer.MeasureText(prompt, font);
                    int x = (this.Width - size.Width) / 2;
                    int y = 80;

                    Rectangle rect = new Rectangle(x - 20, y - 10, size.Width + 40, size.Height + 20);
                    using (GraphicsPath path = GetRoundedRectPath(rect, 10))
                    {
                        using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
                        {
                            g.FillPath(bgBrush, path);
                        }
                    }
                    
                    using (SolidBrush textBrush = new SolidBrush(Color.White))
                    {
                        g.DrawString(prompt, font, textBrush, x, y);
                    }
                }
            }
        }

        private GraphicsPath GetRoundedRectPath(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            Rectangle arc = new Rectangle(rect.X, rect.Y, diameter, diameter);
            
            // Top-left arc
            path.AddArc(arc, 180, 90);
            
            // Top-right arc
            arc.X = rect.Right - diameter;
            path.AddArc(arc, 270, 90);
            
            // Bottom-right arc
            arc.Y = rect.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            
            // Bottom-left arc
            arc.X = rect.Left;
            path.AddArc(arc, 90, 90);
            
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _screenshot?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
