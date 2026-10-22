using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using RetroCap.Interop;
using RetroCap.Services;
using RetroCap.Ui;

namespace RetroCap.Recording
{
    /// <summary>
    /// While recording: a red frame just outside the region (click-through) and a small SAP bar with
    /// the elapsed time and a Stop button. Both sit outside the recorded pixels and are also excluded
    /// from screen capture where Windows supports it (Windows 10 2004+).
    /// </summary>
    public sealed class RecordingOverlay
    {
        private readonly Window _frame;
        private readonly Window _bar;
        private readonly TextBlock _time;
        private readonly DispatcherTimer _timer;
        private readonly Func<TimeSpan> _elapsed;

        public event Action? StopRequested;

        public RecordingOverlay(Int32Rect region, RecordFormat format, Func<TimeSpan> elapsed)
        {
            _elapsed = elapsed;
            var res = Application.Current;
            var red = (Brush)res.FindResource("Sap.StatusError");

            // ---- frame ------------------------------------------------------
            var border = new Border { BorderBrush = red };
            _frame = MakeWindow(border, clickThrough: true);
            // Rough placement in DIPs; corrected to exact pixels once the window handle exists.
            double scale = res.MainWindow is { } main ? VisualTreeHelper.GetDpi(main).DpiScaleX : 1.0;
            _frame.Left = region.X / scale;
            _frame.Top = region.Y / scale;
            _frame.Width = region.Width / scale;
            _frame.Height = region.Height / scale;
            _frame.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(_frame).Handle;
                var dpi = VisualTreeHelper.GetDpi(_frame);
                int px = Math.Max(2, (int)Math.Round(2 * dpi.DpiScaleX));
                border.BorderThickness = new Thickness(px / dpi.DpiScaleX, px / dpi.DpiScaleY, px / dpi.DpiScaleX, px / dpi.DpiScaleY);
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, region.X - px, region.Y - px,
                    region.Width + 2 * px, region.Height + 2 * px, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            };

            // ---- control bar ------------------------------------------------
            _time = new TextBlock
            {
                Text = "00:00", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 8, 0),
                FontFamily = (FontFamily)res.FindResource("Sap.FontMono"), FontSize = 11,
            };
            var stopIcon = new Rectangle { Width = 9, Height = 9, Fill = Brushes.Black, VerticalAlignment = VerticalAlignment.Center };
            var stopRow = new StackPanel { Orientation = Orientation.Horizontal };
            stopRow.Children.Add(stopIcon);
            stopRow.Children.Add(new TextBlock { Text = "Stop", Margin = new Thickness(4, 0, 0, 0), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            var stop = new Button
            {
                Style = (Style)res.FindResource("Sap.Button.Primary"), Content = stopRow, Padding = new Thickness(6, 1, 6, 1),
                MinWidth = 0, Focusable = false, ToolTip = "Stop Recording  (Ctrl+Alt+Z)",
            };
            stop.Click += (_, _) => StopRequested?.Invoke();

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(5, 3, 3, 3) };
            row.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = red, Stroke = Brushes.Black, StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = "REC " + (format == RecordFormat.Gif ? "GIF" : "MP4"), Margin = new Thickness(5, 0, 0, 0), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(_time);
            row.Children.Add(stop);
            var bevel = new Bevel { Kind = BevelKind.OutStrong, Background = (Brush)res.FindResource("Sap.Surface"), Child = row };
            _bar = MakeWindow(bevel, clickThrough: false);
            _bar.FontFamily = (FontFamily)res.FindResource("Sap.Font");
            _bar.FontSize = 11;
            _bar.SizeToContent = SizeToContent.WidthAndHeight;
            _bar.Loaded += (_, _) => PlaceBar(region);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => UpdateTime();
        }

        public void Show()
        {
            _frame.Show();
            _bar.Left = -32000; // placed in physical pixels once it has a size
            _bar.Show();
            _timer.Start();
        }

        public void Close()
        {
            _timer.Stop();
            _frame.Close();
            _bar.Close();
        }

        private void UpdateTime()
        {
            var t = _elapsed();
            _time.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        }

        /// <summary>Below the region if there is room, else above, else inside its bottom-right corner.</summary>
        private void PlaceBar(Int32Rect region)
        {
            var hwnd = new WindowInteropHelper(_bar).Handle;
            var dpi = VisualTreeHelper.GetDpi(_bar);
            int w = (int)Math.Ceiling(_bar.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(_bar.ActualHeight * dpi.DpiScaleY);
            int gap = (int)Math.Round(6 * dpi.DpiScaleY);
            var vs = ScreenCapture.VirtualScreen;

            int x = Math.Clamp(region.X + region.Width - w, vs.X, vs.X + vs.Width - w);
            int y = region.Y + region.Height + gap;
            if (y + h > vs.Y + vs.Height)
            {
                y = region.Y - h - gap;
                if (y < vs.Y) y = region.Y + region.Height - h - gap;
            }
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        private static Window MakeWindow(UIElement content, bool clickThrough)
        {
            var win = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                UseLayoutRounding = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Content = content,
                Title = "RetroCap Recording",
            };
            TextOptions.SetTextFormattingMode(win, TextFormattingMode.Display);
            win.SourceInitialized += (_, _) =>
            {
                var src = (HwndSource)PresentationSource.FromVisual(win)!;
                // Keep our pixel-exact placement when the window touches a monitor with another DPI.
                src.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
                {
                    if (msg == Native.WM_DPICHANGED) handled = true;
                    return IntPtr.Zero;
                });
                int ex = Native.GetWindowLong(src.Handle, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                if (clickThrough) ex |= Native.WS_EX_TRANSPARENT;
                Native.SetWindowLong(src.Handle, Native.GWL_EXSTYLE, ex);
                Native.SetWindowDisplayAffinity(src.Handle, Native.WDA_EXCLUDEFROMCAPTURE); // fails quietly before Win10 2004
            };
            return win;
        }
    }
}
