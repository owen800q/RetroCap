using System;
using System.Collections.Generic;
using IOException = System.IO.IOException;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using RetroCap.Interop;
using RetroCap.Recording;
using RetroCap.Services;
using RetroCap.Ui;

namespace RetroCap.Capture
{
    /// <summary>
    /// Full-virtual-desktop overlay shown on Ctrl+Alt+A. Displays a frozen snapshot of the screen,
    /// lets the user pick a region (drag, or click a highlighted window), annotate it, and then
    /// copy / save / pin the result. In record mode (Ctrl+Alt+Z) it only picks the region and a
    /// video format, then hands them to the recorder.
    ///
    /// All overlay content lives in <see cref="_root"/>, whose units are physical pixels of the
    /// snapshot; chrome (toolbar, magnifier, labels) is scaled back up by the monitor DPI.
    /// </summary>
    public sealed class CaptureWindow : Window
    {
        private enum Phase { Selecting, Editing }
        private enum Drag { None, NewSelection, Move, Resize, Annotate }

        private const double MinSelection = 4;
        private static readonly Brush AccentBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF0, 0xAB, 0x00)));
        private static readonly double[][] SizePresets =
        {
            new double[] { 2, 4, 7 },     // shapes, arrow, pen
            new double[] { 12, 20, 30 },  // highlighter
            new double[] { 14, 20, 30 },  // text (font px)
            new double[] { 10, 18, 30 },  // mosaic
        };

        private readonly ScreenSnapshot _shot;
        private readonly Settings _settings;
        private readonly Canvas _root = new();
        private readonly AnnotationLayer _layer = new();
        private readonly Path _mask = new() { Fill = Frozen(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0))), IsHitTestVisible = false };
        private readonly Rectangle _border = new() { Stroke = AccentBrush, StrokeThickness = 2, IsHitTestVisible = false };
        private readonly Rectangle[] _handles = new Rectangle[8];
        private readonly Bevel _sizeLabel;
        private readonly TextBlock _sizeText;
        private readonly Magnifier _magnifier;
        private readonly CaptureToolbar _toolbar = new();
        private readonly RecordToolbar? _recordBar;
        private TextBox? _editor;

        private Phase _phase = Phase.Selecting;
        private Drag _drag = Drag.None;
        private Rect _sel = Rect.Empty;
        private Rect _hover = Rect.Empty;
        private Point _down;
        private Rect _selAtDown;
        private int _handleAtDown = -1;

        private Tool _tool = Tool.None;
        private readonly Dictionary<Tool, int> _sizeIndex = new();
        private readonly Dictionary<Tool, Color> _colors = new();
        private double _dpiX = 1, _dpiY = 1;

        /// <summary>Raised with the final image and the selection rect (virtual-screen pixels) when the user pins it.</summary>
        public event Action<BitmapSource, Rect>? PinRequested;
        /// <summary>Raised after a copy/save with a short status message.</summary>
        public event Action<string>? Completed;
        /// <summary>Record mode: raised with the region (virtual-screen pixels) and format; the window then closes.</summary>
        public event Action<Int32Rect, RecordFormat>? RecordRequested;

        public CaptureWindow(ScreenSnapshot shot, Settings settings, bool record = false)
        {
            _shot = shot;
            _settings = settings;
            if (record) _recordBar = new RecordToolbar(settings.RecordFormat);

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.Black;
            Cursor = Cursors.Cross;
            WindowStartupLocation = WindowStartupLocation.Manual;
            UseLayoutRounding = false;
            Title = record ? "RetroCap Record" : "RetroCap Capture";
            FontFamily = (FontFamily)Application.Current.FindResource("Sap.Font");

            var b = shot.Bounds;
            Left = b.X; Top = b.Y; Width = b.Width; Height = b.Height; // fixed up in OnSourceInitialized

            foreach (var t in Enum.GetValues<Tool>())
            {
                _sizeIndex[t] = 1;
                _colors[t] = t == Tool.Highlighter ? Color.FromRgb(0xFF, 0xE6, 0x00) : Color.FromRgb(0xC8, 0x28, 0x1E);
            }

            // ---- layers -------------------------------------------------------
            _root.Width = b.Width;
            _root.Height = b.Height;
            _root.ClipToBounds = true;
            var bg = new Image { Source = shot.Image, Width = b.Width, Height = b.Height, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(bg, BitmapScalingMode.NearestNeighbor);
            _root.Children.Add(bg);

            _layer.Width = b.Width;
            _layer.Height = b.Height;
            _root.Children.Add(_layer);
            _root.Children.Add(_mask);
            _root.Children.Add(_border);

            for (int i = 0; i < 8; i++)
            {
                _handles[i] = new Rectangle
                {
                    Width = 7, Height = 7, Fill = AccentBrush, Stroke = Brushes.Black, StrokeThickness = 1,
                    Visibility = Visibility.Collapsed, IsHitTestVisible = false,
                };
                _root.Children.Add(_handles[i]);
            }

            _sizeText = new TextBlock { FontFamily = new FontFamily("Lucida Console, Courier New"), FontSize = 10, Foreground = Brushes.Black };
            _sizeLabel = new Bevel
            {
                Kind = BevelKind.OutStrong, Background = (Brush)Application.Current.FindResource("Sap.Surface2"),
                Padding = new Thickness(5, 2, 5, 2), Child = _sizeText, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            };
            _root.Children.Add(_sizeLabel);

            _magnifier = new Magnifier(shot) { IsHitTestVisible = false };
            _root.Children.Add(_magnifier);

            Bar.Visibility = Visibility.Collapsed;
            _root.Children.Add(Bar);
            WireToolbar();

            Content = _root;
            UpdateMask();
        }

        // =====================================================================
        // Window placement: cover the whole virtual desktop 1:1 in pixels.
        // =====================================================================

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var src = (HwndSource)PresentationSource.FromVisual(this)!;
            src.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                // Never let WPF resize the overlay when it straddles monitors with different DPI.
                if (msg == Native.WM_DPICHANGED) handled = true;
                return IntPtr.Zero;
            });
            var b = _shot.Bounds;
            Native.SetWindowPos(src.Handle, Native.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, Native.SWP_SHOWWINDOW);
            ApplyDpi();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) => ApplyDpi();

        private void ApplyDpi()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            _dpiX = dpi.DpiScaleX;
            _dpiY = dpi.DpiScaleY;
            _root.LayoutTransform = new ScaleTransform(1 / _dpiX, 1 / _dpiY);
            var chrome = new ScaleTransform(_dpiX, _dpiY);
            Bar.LayoutTransform = chrome;
            _sizeLabel.LayoutTransform = chrome;
            _magnifier.LayoutTransform = chrome;
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            Activate();
            Focus();
            Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
            var p = Mouse.GetPosition(_root);
            UpdateHover(p);
            _magnifier.Track(p, _root.Width, _root.Height);
        }

        // =====================================================================
        // Mouse
        // =====================================================================

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (IsOverToolbar(e)) return;
            var p = Clamp(e.GetPosition(_root));
            _down = p;

            if (e.ClickCount == 2 && _phase == Phase.Editing && _sel.Contains(p) && _tool is Tool.None)
            {
                if (IsRecordMode) FinishRecord();
                else FinishCopy();
                return;
            }

            if (_phase == Phase.Selecting)
            {
                _sel = Rect.Empty;
                _drag = Drag.NewSelection;
                CaptureMouse();
                return;
            }

            if (_editor != null)
            {
                CommitText();
                // Like QQ: clicking elsewhere with the text tool commits and starts a new text box.
                if (_tool != Tool.Text || !_sel.Contains(p) || HitHandle(p) >= 0) return;
            }

            int h = HitHandle(p);
            if (h >= 0)
            {
                StartDrag(Drag.Resize, h);
                return;
            }

            if (!_sel.Contains(p)) return;

            if (_tool == Tool.None)
            {
                StartDrag(Drag.Move, -1);
            }
            else if (_tool == Tool.Text)
            {
                BeginText(p);
            }
            else
            {
                _layer.Current = CreateAnnotation(p);
                _drag = Drag.Annotate;
                CaptureMouse();
            }
        }

        private void StartDrag(Drag kind, int handle)
        {
            _drag = kind;
            _handleAtDown = handle;
            _selAtDown = _sel;
            Bar.Visibility = Visibility.Collapsed;
            CaptureMouse();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = Clamp(e.GetPosition(_root));

            switch (_drag)
            {
                case Drag.None:
                    if (_phase == Phase.Selecting) UpdateHover(p);
                    else UpdateCursor(p);
                    break;

                case Drag.NewSelection:
                    if ((p - _down).Length >= 3 || !_sel.IsEmpty)
                    {
                        _sel = new Rect(_down, p);
                        UpdateMask();
                    }
                    break;

                case Drag.Move:
                    var off = p - _down;
                    double x = Math.Clamp(_selAtDown.X + off.X, 0, _root.Width - _sel.Width);
                    double y = Math.Clamp(_selAtDown.Y + off.Y, 0, _root.Height - _sel.Height);
                    _sel = new Rect(Math.Round(x), Math.Round(y), _sel.Width, _sel.Height);
                    UpdateMask();
                    break;

                case Drag.Resize:
                    _sel = ResizeRect(_selAtDown, _handleAtDown, p);
                    UpdateMask();
                    break;

                case Drag.Annotate:
                    UpdateAnnotation(p, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                    break;
            }

            bool showMagnifier = _phase == Phase.Selecting || _drag == Drag.Resize;
            _magnifier.Visibility = showMagnifier ? Visibility.Visible : Visibility.Collapsed;
            if (showMagnifier) _magnifier.Track(p, _root.Width, _root.Height);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            var drag = _drag;
            _drag = Drag.None;

            switch (drag)
            {
                case Drag.NewSelection:
                    if (_sel.IsEmpty || _sel.Width < MinSelection || _sel.Height < MinSelection)
                        _sel = _hover; // plain click: take the highlighted window
                    _sel = Snap(_sel);
                    EnterEditing();
                    break;

                case Drag.Move:
                case Drag.Resize:
                    if (_sel.Width < MinSelection || _sel.Height < MinSelection) _sel = _selAtDown;
                    _sel = Snap(_sel);
                    EnterEditing();
                    break;

                case Drag.Annotate:
                    if (_layer.Current != null && IsMeaningful(_layer.Current)) _layer.Items.Add(_layer.Current);
                    _layer.Current = null;
                    _layer.InvalidateVisual();
                    SyncToolbar();
                    break;
            }
            // Releasing capture raises a synthetic MouseMove, so only do it once state is final.
            ReleaseMouseCapture();
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);
            if (_editor != null) { CommitText(); return; }
            // QQ convention: right-click backs out of the selection, a second right-click exits.
            if (_phase == Phase.Editing) ResetSelection();
            else Close();
        }

        // =====================================================================
        // Keyboard
        // =====================================================================

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (_editor != null)
            {
                if (e.Key == Key.Escape) { CommitText(); e.Handled = true; }
                return; // typing goes to the editor
            }

            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (_recordBar != null)
            {
                switch (e.Key)
                {
                    case Key.Escape: Close(); break;
                    case Key.Enter: if (_phase == Phase.Editing) FinishRecord(); break;
                    case Key.V: _recordBar.SetFormat(RecordFormat.Mp4); break;
                    case Key.G: _recordBar.SetFormat(RecordFormat.Gif); break;
                    case Key.Left: Nudge(-1, 0); break;
                    case Key.Right: Nudge(1, 0); break;
                    case Key.Up: Nudge(0, -1); break;
                    case Key.Down: Nudge(0, 1); break;
                    default: return;
                }
                e.Handled = true;
                return;
            }
            switch (e.Key)
            {
                case Key.Escape: Close(); break;
                case Key.Enter: if (_phase == Phase.Editing) FinishCopy(); break;
                case Key.C when ctrl: if (_phase == Phase.Editing) FinishCopy(); break;
                case Key.S when ctrl: if (_phase == Phase.Editing) FinishSave(); break;
                case Key.Z when ctrl: Undo(); break;
                case Key.F3: if (_phase == Phase.Editing) FinishPin(); break;
                case Key.R when !ctrl: SelectTool(Tool.Rectangle); break;
                case Key.E when !ctrl: SelectTool(Tool.Ellipse); break;
                case Key.A when !ctrl: SelectTool(Tool.Arrow); break;
                case Key.P when !ctrl: SelectTool(Tool.Pen); break;
                case Key.H when !ctrl: SelectTool(Tool.Highlighter); break;
                case Key.T when !ctrl: SelectTool(Tool.Text); break;
                case Key.M when !ctrl: SelectTool(Tool.Mosaic); break;
                case Key.Left: Nudge(-1, 0); break;
                case Key.Right: Nudge(1, 0); break;
                case Key.Up: Nudge(0, -1); break;
                case Key.Down: Nudge(0, 1); break;
                default: return;
            }
            e.Handled = true;
        }

        private void Nudge(int dx, int dy)
        {
            if (_phase != Phase.Editing) return;
            var r = _sel;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                r = new Rect(r.X, r.Y, Math.Max(MinSelection, r.Width + dx), Math.Max(MinSelection, r.Height + dy));
            else
                r.Offset(dx, dy);
            r.Intersect(new Rect(0, 0, _root.Width, _root.Height));
            if (r.IsEmpty) return;
            _sel = r;
            UpdateMask();
            PlaceToolbar();
        }

        // =====================================================================
        // Selection state
        // =====================================================================

        private void UpdateHover(Point p)
        {
            _hover = _shot.Windows.FirstOrDefault(w => w.Contains(p));
            if (_drag == Drag.None && _phase == Phase.Selecting) UpdateMask();
        }

        private void EnterEditing()
        {
            if (_sel.IsEmpty) return;
            _phase = Phase.Editing;
            _magnifier.Visibility = Visibility.Collapsed;
            UpdateMask();
            SyncToolbar();
            Bar.Visibility = Visibility.Visible;
            PlaceToolbar();
        }

        private void ResetSelection()
        {
            _phase = Phase.Selecting;
            _sel = Rect.Empty;
            _tool = Tool.None;
            _layer.Items.Clear();
            _layer.InvalidateVisual();
            Bar.Visibility = Visibility.Collapsed;
            var p = Mouse.GetPosition(_root);
            UpdateHover(p);
            _magnifier.Visibility = Visibility.Visible;
            _magnifier.Track(p, _root.Width, _root.Height);
            Cursor = Cursors.Cross;
        }

        /// <summary>Redraws the dimmed mask, the selection frame, handles and the size label.</summary>
        private void UpdateMask()
        {
            var full = new Rect(0, 0, _root.Width, _root.Height);
            // While selecting, preview the window under the cursor until the user starts dragging.
            var shown = _phase == Phase.Selecting && _sel.IsEmpty ? _hover : _sel;

            var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
            g.Children.Add(new RectangleGeometry(full));
            if (!shown.IsEmpty) g.Children.Add(new RectangleGeometry(shown));
            g.Freeze();
            _mask.Data = g;

            _layer.Clip = _sel.IsEmpty ? Geometry.Empty : new RectangleGeometry(_sel);

            if (shown.IsEmpty)
            {
                _border.Visibility = Visibility.Collapsed;
                _sizeLabel.Visibility = Visibility.Collapsed;
                foreach (var h in _handles) h.Visibility = Visibility.Collapsed;
                return;
            }

            // Frame sits just outside the region so it never covers captured pixels.
            _border.Visibility = Visibility.Visible;
            Canvas.SetLeft(_border, shown.X - 2);
            Canvas.SetTop(_border, shown.Y - 2);
            _border.Width = shown.Width + 4;
            _border.Height = shown.Height + 4;

            bool handles = !_sel.IsEmpty;
            var pts = HandlePoints(shown);
            for (int i = 0; i < 8; i++)
            {
                _handles[i].Visibility = handles ? Visibility.Visible : Visibility.Collapsed;
                Canvas.SetLeft(_handles[i], pts[i].X - 3.5);
                Canvas.SetTop(_handles[i], pts[i].Y - 3.5);
            }

            _sizeText.Text = $"{(int)Math.Round(shown.Width)} x {(int)Math.Round(shown.Height)}";
            _sizeLabel.Visibility = Visibility.Visible;
            _sizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double ly = shown.Y - _sizeLabel.DesiredSize.Height - 6;
            if (ly < 0) ly = shown.Y + 6;
            Canvas.SetLeft(_sizeLabel, Math.Max(0, shown.X));
            Canvas.SetTop(_sizeLabel, ly);
        }

        private static Point[] HandlePoints(Rect r)
        {
            double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            return new[]
            {
                r.TopLeft, new Point(cx, r.Top), r.TopRight, new Point(r.Right, cy),
                r.BottomRight, new Point(cx, r.Bottom), r.BottomLeft, new Point(r.Left, cy),
            };
        }

        private int HitHandle(Point p)
        {
            if (_sel.IsEmpty) return -1;
            var pts = HandlePoints(_sel);
            for (int i = 0; i < 8; i++)
                if (Math.Abs(p.X - pts[i].X) <= 6 && Math.Abs(p.Y - pts[i].Y) <= 6) return i;
            return -1;
        }

        private static Rect ResizeRect(Rect r, int handle, Point p)
        {
            double l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
            switch (handle)
            {
                case 0: l = p.X; t = p.Y; break;
                case 1: t = p.Y; break;
                case 2: rt = p.X; t = p.Y; break;
                case 3: rt = p.X; break;
                case 4: rt = p.X; b = p.Y; break;
                case 5: b = p.Y; break;
                case 6: l = p.X; b = p.Y; break;
                case 7: l = p.X; break;
            }
            return new Rect(new Point(l, t), new Point(rt, b));
        }

        private void UpdateCursor(Point p)
        {
            int h = HitHandle(p);
            Cursor = h switch
            {
                0 or 4 => Cursors.SizeNWSE,
                2 or 6 => Cursors.SizeNESW,
                1 or 5 => Cursors.SizeNS,
                3 or 7 => Cursors.SizeWE,
                _ when _sel.Contains(p) => _tool switch
                {
                    Tool.None => Cursors.SizeAll,
                    Tool.Text => Cursors.IBeam,
                    _ => Cursors.Cross,
                },
                _ => Cursors.Arrow,
            };
        }

        /// <summary>Puts the toolbar under the selection (right-aligned), else above it, else inside.</summary>
        private void PlaceToolbar()
        {
            var bar = Bar;
            bar.UpdateLayout();
            bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = bar.DesiredSize.Width, h = bar.DesiredSize.Height; // includes DPI LayoutTransform
            const double gap = 6;
            double x = Math.Clamp(_sel.Right - w, 0, Math.Max(0, _root.Width - w));
            double y = _sel.Bottom + gap + 2;
            if (y + h > _root.Height)
            {
                y = _sel.Top - h - gap - 2;
                if (y < 0) y = Math.Max(0, _sel.Bottom - h - gap);
            }
            Canvas.SetLeft(bar, Math.Round(x));
            Canvas.SetTop(bar, Math.Round(y));
        }

        private bool IsOverToolbar(MouseEventArgs e)
        {
            var bar = Bar;
            if (bar.Visibility != Visibility.Visible) return false;
            var hit = e.OriginalSource as DependencyObject;
            while (hit != null)
            {
                if (hit == bar) return true;
                hit = hit is Visual || hit is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
            }
            return false;
        }

        private Point Clamp(Point p) => new(Math.Clamp(Math.Round(p.X), 0, _root.Width), Math.Clamp(Math.Round(p.Y), 0, _root.Height));

        private static Rect Snap(Rect r) => r.IsEmpty ? r : new Rect(Math.Round(r.X), Math.Round(r.Y), Math.Round(r.Width), Math.Round(r.Height));

        // =====================================================================
        // Tools & annotations
        // =====================================================================

        /// <summary>The toolbar for the current mode.</summary>
        private FrameworkElement Bar => _recordBar ?? (FrameworkElement)_toolbar;

        private bool IsRecordMode => _recordBar != null;

        private void WireToolbar()
        {
            if (_recordBar != null)
            {
                _recordBar.CancelRequested += Close;
                _recordBar.RecordRequested += FinishRecord;
                return;
            }
            _toolbar.ToolSelected += SelectTool;
            _toolbar.SizeSelected += i => { _sizeIndex[_tool] = i; SyncToolbar(); };
            _toolbar.ColorSelected += c =>
            {
                _colors[_tool] = c;
                if (_editor != null) _editor.Foreground = new SolidColorBrush(c);
                SyncToolbar();
            };
            _toolbar.UndoRequested += Undo;
            _toolbar.PinRequested += FinishPin;
            _toolbar.SaveRequested += FinishSave;
            _toolbar.CancelRequested += Close;
            _toolbar.CopyRequested += FinishCopy;
        }

        private void SelectTool(Tool tool)
        {
            if (_phase != Phase.Editing || IsRecordMode) return;
            if (_editor != null) CommitText();
            _tool = _tool == tool ? Tool.None : tool;
            SyncToolbar();
            PlaceToolbar();
            UpdateCursor(Mouse.GetPosition(_root));
        }

        private void SyncToolbar() => _toolbar.Show(_tool, _sizeIndex[_tool], _colors[_tool], _layer.Items.Count > 0);

        private double CurrentSize => SizePresets[_tool switch
        {
            Tool.Highlighter => 1,
            Tool.Text => 2,
            Tool.Mosaic => 3,
            _ => 0,
        }][_sizeIndex[_tool]];

        private Annotation CreateAnnotation(Point p)
        {
            var color = _colors[_tool];
            double size = CurrentSize;
            Annotation a = _tool switch
            {
                Tool.Rectangle => new RectangleAnnotation { Color = color, Size = size, Start = p, End = p },
                Tool.Ellipse => new EllipseAnnotation { Color = color, Size = size, Start = p, End = p },
                Tool.Arrow => new ArrowAnnotation { Color = color, Size = size, Start = p, End = p },
                Tool.Highlighter => new HighlighterAnnotation { Color = color, Size = size },
                Tool.Mosaic => new MosaicAnnotation { Size = size, Pixelated = _shot.GetPixelated() },
                _ => new StrokeAnnotation { Color = color, Size = size },
            };
            if (a is StrokeAnnotation s) s.Points.Add(p);
            return a;
        }

        private void UpdateAnnotation(Point p, bool constrain)
        {
            switch (_layer.Current)
            {
                case DragAnnotation d:
                    if (constrain) p = Constrain(d, p);
                    d.End = p;
                    break;
                case HighlighterAnnotation h when constrain:
                    // Shift: straight horizontal/vertical marker line from the first point.
                    var first = h.Points[0];
                    h.Points.Clear();
                    h.Points.Add(first);
                    h.Points.Add(Math.Abs(p.X - first.X) >= Math.Abs(p.Y - first.Y) ? new Point(p.X, first.Y) : new Point(first.X, p.Y));
                    break;
                case StrokeAnnotation s:
                    if (s.Points.Count == 0 || (s.Points[^1] - p).Length >= 1) s.Points.Add(p);
                    break;
            }
            _layer.InvalidateVisual();
        }

        /// <summary>Shift-drag: squares/circles, and 45-degree arrows.</summary>
        private static Point Constrain(DragAnnotation d, Point p)
        {
            var v = p - d.Start;
            if (d is ArrowAnnotation)
            {
                double ang = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
                return d.Start + new Vector(Math.Cos(ang), Math.Sin(ang)) * v.Length;
            }
            double m = Math.Max(Math.Abs(v.X), Math.Abs(v.Y));
            return d.Start + new Vector(Math.Sign(v.X) * m, Math.Sign(v.Y) * m);
        }

        private static bool IsMeaningful(Annotation a) => a switch
        {
            DragAnnotation d => (d.End - d.Start).Length >= 3,
            _ => true,
        };

        private void Undo()
        {
            if (_editor != null) { RemoveEditor(); return; }
            if (_layer.Items.Count == 0) return;
            _layer.Items.RemoveAt(_layer.Items.Count - 1);
            _layer.InvalidateVisual();
            SyncToolbar();
        }

        // ---- text ---------------------------------------------------------

        private void BeginText(Point p)
        {
            var color = _colors[Tool.Text];
            _editor = new TextBox
            {
                FontFamily = TextAnnotation.Font,
                FontSize = CurrentSize,
                Foreground = new SolidColorBrush(color),
                CaretBrush = new SolidColorBrush(color),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                AcceptsReturn = true,
                MinWidth = 60,
                Template = EditorTemplate(),
                Tag = p,
            };
            TextOptions.SetTextFormattingMode(_editor, TextFormattingMode.Ideal);
            Canvas.SetLeft(_editor, p.X - EditorInset.X);
            Canvas.SetTop(_editor, p.Y - EditorInset.Y);
            _root.Children.Insert(_root.Children.IndexOf(_mask), _editor);
            _editor.Loaded += (_, _) => _editor?.Focus();
        }

        /// <summary>Offset between the editor's top-left and where its text actually starts.</summary>
        private static readonly Vector EditorInset = new(3, 1);

        private static ControlTemplate EditorTemplate()
        {
            // A dashed amber frame around a bare text host, so typing looks like the final render.
            var t = new ControlTemplate(typeof(TextBox));
            var grid = new FrameworkElementFactory(typeof(Grid));
            var frame = new FrameworkElementFactory(typeof(Rectangle));
            frame.SetValue(Shape.StrokeProperty, AccentBrush);
            frame.SetValue(Shape.StrokeThicknessProperty, 1.0);
            frame.SetValue(Shape.StrokeDashArrayProperty, new DoubleCollection { 3, 2 });
            grid.AppendChild(frame);
            var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
            host.SetValue(MarginProperty, new Thickness(1));
            host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            grid.AppendChild(host);
            t.VisualTree = grid;
            return t;
        }

        private void CommitText()
        {
            if (_editor == null) return;
            var text = _editor.Text.TrimEnd();
            if (text.Length > 0)
            {
                _layer.Items.Add(new TextAnnotation
                {
                    Text = text, Color = ((SolidColorBrush)_editor.Foreground).Color,
                    Size = _editor.FontSize, Origin = (Point)_editor.Tag,
                });
                _layer.InvalidateVisual();
            }
            RemoveEditor();
            SyncToolbar();
        }

        private void RemoveEditor()
        {
            if (_editor == null) return;
            _root.Children.Remove(_editor);
            _editor = null;
            Focus();
        }

        // =====================================================================
        // Output
        // =====================================================================

        /// <summary>Flattens snapshot + annotations inside the selection into a bitmap.</summary>
        private BitmapSource Render()
        {
            if (_editor != null) CommitText();
            var r = _sel;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new TranslateTransform(-r.X, -r.Y));
                dc.DrawImage(_shot.Image, new Rect(0, 0, _shot.Bounds.Width, _shot.Bounds.Height));
                foreach (var a in _layer.Items) a.Render(dc);
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap((int)r.Width, (int)r.Height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        private void FinishCopy()
        {
            var img = Render();
            Hide();
            bool ok = ImageOutput.CopyToClipboard(img);
            string msg = ok ? $"Image copied to clipboard. {img.PixelWidth} x {img.PixelHeight}" : "Error: clipboard is busy.";
            if (ok && _settings.AutoSave) msg += AutoSave(img);
            Completed?.Invoke(msg);
            Close();
        }

        private void FinishSave()
        {
            var img = Render();
            Hide();
            try
            {
                var path = ImageOutput.SaveWithDialog(img, _settings.SaveFolder, null);
                if (path != null) Completed?.Invoke($"Document saved. {System.IO.Path.GetFileName(path)}");
                Close();
            }
            catch (Exception ex)
            {
                Completed?.Invoke("Error: " + ex.Message);
                Close();
            }
        }

        private void FinishPin()
        {
            var img = Render();
            var at = new Rect(_sel.X + _shot.Bounds.X, _sel.Y + _shot.Bounds.Y, _sel.Width, _sel.Height);
            PinRequested?.Invoke(img, at);
            Close();
        }

        private void FinishRecord()
        {
            if (_recordBar == null || _sel.IsEmpty) return;
            var r = Snap(_sel);
            var region = new Int32Rect((int)r.X + _shot.Bounds.X, (int)r.Y + _shot.Bounds.Y, (int)r.Width, (int)r.Height);
            var format = _recordBar.Format;
            Hide();
            RecordRequested?.Invoke(region, format);
            Close();
        }

        private string AutoSave(BitmapSource img)
        {
            try
            {
                var path = System.IO.Path.Combine(_settings.SaveFolder, ImageOutput.DefaultFileName());
                ImageOutput.SavePng(img, path);
                return " Saved to " + System.IO.Path.GetFileName(path);
            }
            catch (IOException) { return " Auto-save failed."; }
            catch (UnauthorizedAccessException) { return " Auto-save failed."; }
        }

        private static Brush Frozen(Brush b) { b.Freeze(); return b; }

        /// <summary>Renders committed annotations plus the one being drawn.</summary>
        private sealed class AnnotationLayer : FrameworkElement
        {
            public List<Annotation> Items { get; } = new();
            public Annotation? Current { get; set; }

            public AnnotationLayer() => IsHitTestVisible = false;

            protected override void OnRender(DrawingContext dc)
            {
                foreach (var a in Items) a.Render(dc);
                Current?.Render(dc);
            }
        }
    }
}
