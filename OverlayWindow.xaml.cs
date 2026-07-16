using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScreenRecorder
{
    public partial class OverlayWindow : Window
    {
        private System.Windows.Point _startPoint;
        private bool _isSelecting = false;

        public System.Drawing.Rectangle SelectedRectangle { get; private set; }
        public bool IsCancelled { get; private set; } = false;

        public OverlayWindow()
        {
            InitializeComponent();

            // Set window bounds to cover all monitors (virtual screen)
            this.Left = SystemParameters.VirtualScreenLeft;
            this.Top = SystemParameters.VirtualScreenTop;
            this.Width = SystemParameters.VirtualScreenWidth;
            this.Height = SystemParameters.VirtualScreenHeight;

            this.Loaded += (s, e) =>
            {
                // Position the instruction box in the center of the primary screen
                double primaryWidth = SystemParameters.PrimaryScreenWidth;
                double primaryHeight = SystemParameters.PrimaryScreenHeight;
                
                // Adjust for virtual screen offset
                double localCenterX = (primaryWidth - InstructionBorder.ActualWidth) / 2 - SystemParameters.VirtualScreenLeft;
                double localCenterY = (primaryHeight - InstructionBorder.ActualHeight) / 2 - SystemParameters.VirtualScreenTop;

                Canvas.SetLeft(InstructionBorder, localCenterX);
                Canvas.SetTop(InstructionBorder, localCenterY);
            };

            this.KeyDown += OverlayWindow_KeyDown;
        }

        private void OverlayWindow_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                IsCancelled = true;
                this.DialogResult = false;
                this.Close();
            }
        }

        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                _startPoint = e.GetPosition(OverlayCanvas);
                _isSelecting = true;
                
                SelectionBorder.Visibility = Visibility.Visible;
                TooltipBorder.Visibility = Visibility.Visible;
                InstructionBorder.Visibility = Visibility.Collapsed;

                Canvas.SetLeft(SelectionBorder, _startPoint.X);
                Canvas.SetTop(SelectionBorder, _startPoint.Y);
                SelectionBorder.Width = 0;
                SelectionBorder.Height = 0;
            }
        }

        private void Canvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isSelecting)
            {
                System.Windows.Point currentPoint = e.GetPosition(OverlayCanvas);

                double x = Math.Min(_startPoint.X, currentPoint.X);
                double y = Math.Min(_startPoint.Y, currentPoint.Y);
                double width = Math.Abs(_startPoint.X - currentPoint.X);
                double height = Math.Abs(_startPoint.Y - currentPoint.Y);

                // Update selection border
                Canvas.SetLeft(SelectionBorder, x);
                Canvas.SetTop(SelectionBorder, y);
                SelectionBorder.Width = width;
                SelectionBorder.Height = height;

                // Update size tooltip text and location
                TxtTooltip.Text = $"{(int)width} x {(int)height}";
                
                double tooltipX = currentPoint.X + 15;
                double tooltipY = currentPoint.Y + 15;

                // Keep tooltip inside window bounds
                if (tooltipX + TooltipBorder.ActualWidth > this.Width) tooltipX = x - TooltipBorder.ActualWidth - 10;
                if (tooltipY + TooltipBorder.ActualHeight > this.Height) tooltipY = y - TooltipBorder.ActualHeight - 10;

                Canvas.SetLeft(TooltipBorder, tooltipX);
                Canvas.SetTop(TooltipBorder, tooltipY);
            }
        }

        private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isSelecting && e.ChangedButton == MouseButton.Left)
            {
                _isSelecting = false;

                double x = Canvas.GetLeft(SelectionBorder);
                double y = Canvas.GetTop(SelectionBorder);
                double w = SelectionBorder.Width;
                double h = SelectionBorder.Height;

                if (w < 10 || h < 10)
                {
                    IsCancelled = true;
                    this.DialogResult = false;
                }
                else
                {
                    // Map to absolute coordinates
                    int absoluteX = (int)(x + SystemParameters.VirtualScreenLeft);
                    int absoluteY = (int)(y + SystemParameters.VirtualScreenTop);
                    SelectedRectangle = new System.Drawing.Rectangle(absoluteX, absoluteY, (int)w, (int)h);
                    this.DialogResult = true;
                }

                this.Close();
            }
        }
    }
}
