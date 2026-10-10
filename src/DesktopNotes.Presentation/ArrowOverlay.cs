using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopNotes.Application;
using DesktopNotes.Domain;

namespace DesktopNotes.Presentation;

internal sealed class ArrowOverlay : Window
{
    private readonly ArrowVisual _visual = new();
    private readonly WindowInteropHelper _interop;
    private nint _source, _target;
    private readonly bool _interactive;
    private Window? _startAdornment, _endAdornment;
    private nint _startOwner, _endOwner;
    private readonly ScreenRect? _previewBounds;
    public event Action? SelectRequested;
    public event Action? DeleteRequested;
    public event Action? DeselectRequested;

    public ArrowOverlay(bool interactive = false, ScreenRect? previewBounds = null)
    {
        _interactive = interactive;
        _previewBounds = previewBounds;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        IsHitTestVisible = interactive;
        _visual.Interactive = interactive;
        _visual.Focusable = interactive;
        // A free preview end is an arrowhead, not a selection handle.
        Topmost = !interactive;
        Content = _visual;
        _interop = new WindowInteropHelper(this);
        SourceInitialized += (_, _) => NativeWindows.MakeOverlay(_interop.Handle, interactive);
        IsVisibleChanged += (_, _) => { if (!IsVisible) HideAdornments(); };
        Closed += (_, _) => { _startAdornment?.Close(); _endAdornment?.Close(); };
        if (!interactive) return;
        MouseMove += (_, e) =>
        {
            var hovered = _visual.ContainsPoint(e.GetPosition(_visual));
            if (_visual.IsHovered == hovered) return;
            _visual.IsHovered = hovered;
            Cursor = _visual.IsHovered ? Cursors.Hand : Cursors.Arrow;
            _visual.InvalidateVisual();
        };
        MouseLeave += (_, _) => { _visual.IsHovered = false; _visual.InvalidateVisual(); };
        MouseLeftButtonDown += (_, e) =>
        {
            if (!_visual.ContainsPoint(e.GetPosition(_visual))) return;
            e.Handled = true;
            SelectRequested?.Invoke();
            Activate(); Keyboard.Focus(_visual);
            NativeWindows.PutBehind(_interop.Handle, _source, _target);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (!_visual.IsSelected) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Delete) { e.Handled = true; DeleteRequested?.Invoke(); }
            if (key == Key.Escape) { e.Handled = true; DeselectRequested?.Invoke(); }
        };
        Deactivated += (_, _) => DeselectRequested?.Invoke();
    }

    public void SetSelected(bool selected)
    {
        if (_visual.IsSelected == selected) return;
        _visual.IsSelected = selected;
        UpdateAdornments();
        _visual.InvalidateVisual();
    }

    public void Draw(IReadOnlyList<DiagramPoint> points, LinkStyle style, nint source, nint target)
    {
        const int padding = 40;
        var rect = new ScreenRect
        {
            Left = (int)Math.Floor(points.Min(p => p.X)) - padding,
            Top = (int)Math.Floor(points.Min(p => p.Y)) - padding,
            Right = (int)Math.Ceiling(points.Max(p => p.X)) + padding,
            Bottom = (int)Math.Ceiling(points.Max(p => p.Y)) + padding
        };
        if (!_interactive)
        {
            // Keep the layered HWND stationary while drawing the moving arrow.
            // Resizing its backing surface every mouse sample causes visible stutter.
            var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
            rect = _previewBounds ?? new ScreenRect
            {
                Left = desktop.Left - 128, Top = desktop.Top - 128,
                Right = desktop.Right + 128, Bottom = desktop.Bottom + 128
            };
        }
        if (IsVisible && _source == source && _target == target && _visual.LinkStyle == style && _visual.Points.SequenceEqual(points)) return;
        if (_interactive)
        {
            // Reuse the layered surface across nearby frames instead of resizing
            // and changing its coordinate origin for every moving endpoint.
            var currentBounds = _interop.Handle == 0 ? default : NativeWindows.Rect(_interop.Handle);
            if (currentBounds.Left <= rect.Left && currentBounds.Top <= rect.Top &&
                currentBounds.Right >= rect.Right && currentBounds.Bottom >= rect.Bottom)
                rect = currentBounds;
            else
                rect = new ScreenRect { Left = rect.Left - 128, Top = rect.Top - 128, Right = rect.Right + 128, Bottom = rect.Bottom + 128 };
        }
        _source = source; _target = target;
        _interop.EnsureHandle();
        var wasVisible = IsVisible;
        var current = NativeWindows.Rect(_interop.Handle);
        if (current.Left != rect.Left || current.Top != rect.Top || current.Right != rect.Right || current.Bottom != rect.Bottom)
            NativeWindows.SetBounds(_interop.Handle, rect);
        _visual.Points = points;
        _visual.LinkStyle = style;
        _visual.PhysicalOrigin = NativeWindows.Rect(_interop.Handle);
        _visual.Scale = NativeWindows.Scale(_interop.Handle);
        if (!wasVisible) Show();
        if (_interactive) NativeWindows.PutBehind(_interop.Handle, source, target);
        UpdateAdornments();
        _visual.InvalidateVisual();
    }

    private void HideAdornments()
    {
        if (_startAdornment is { IsVisible: true }) _startAdornment.Hide();
        if (_endAdornment is { IsVisible: true }) _endAdornment.Hide();
    }

    private void UpdateAdornments()
    {
        if (!IsVisible || !_interactive || !_visual.IsSelected || _visual.Points.Count < 2) { HideAdornments(); return; }
        void Place(ref Window? adornment, ref nint previousOwner, nint ownerHandle, DiagramPoint point)
        {
            if (!NativeWindows.IsShown(ownerHandle)) return;
            if (adornment is not null && previousOwner != ownerHandle) { adornment.Close(); adornment = null; }
            if (adornment is null)
            {
                var owner = HwndSource.FromHwnd(ownerHandle)?.RootVisual as Window;
                if (owner is null) return;
                var window = new Window
                {
                    Owner = owner, ShowActivated = false, ShowInTaskbar = false, AllowsTransparency = true,
                    Background = Brushes.Transparent, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                    Width = 16, Height = 16, Content = new EdgeLinkHandles.PortVisual { IsAttachment = true, Width = 16, Height = 16 }
                };
                window.SourceInitialized += (_, _) => NativeWindows.MakeOverlay(new WindowInteropHelper(window).Handle);
                adornment = window;
                previousOwner = ownerHandle;
            }
            var scale = NativeWindows.Scale(ownerHandle);
            adornment.Topmost = (HwndSource.FromHwnd(ownerHandle)?.RootVisual as Window)?.Topmost ?? false;
            var handle = new WindowInteropHelper(adornment).EnsureHandle();
            NativeWindows.SetBounds(handle, new ScreenRect
            {
                Left = (int)Math.Round(point.X - 8 * scale), Top = (int)Math.Round(point.Y - 8 * scale),
                Right = (int)Math.Round(point.X + 8 * scale), Bottom = (int)Math.Round(point.Y + 8 * scale)
            });
            if (!adornment.IsVisible) adornment.Show();
        }
        Place(ref _startAdornment, ref _startOwner, _source, _visual.Points[0]);
        Place(ref _endAdornment, ref _endOwner, _target, _visual.Points[^1]);
    }

    internal sealed class ArrowVisual : FrameworkElement
    {
        public IReadOnlyList<DiagramPoint> Points = [];
        public LinkStyle LinkStyle = new();
        public ScreenRect PhysicalOrigin;
        public double Scale = 1;
        public bool Interactive;
        public bool IsHovered;
        public bool IsSelected;
        // Update the old default appearance without overriding explicit custom widths.
        private bool LegacyStandard => LinkStyle.Pattern == LinkLinePattern.Solid && LinkStyle.Route == LinkRoute.Straight &&
            ((LinkStyle.Width == 2 && LinkStyle.Color == "#808080") || (LinkStyle.Width == 1.5 && LinkStyle.Color == "#64748B") || (LinkStyle.Width == 2.2 && LinkStyle.Color == "#475569"));
        private double StrokeWidth => LegacyStandard ? 3 : LinkStyle.Width;
        private Point Local(DiagramPoint point) => new((point.X - PhysicalOrigin.Left) / Scale, (point.Y - PhysicalOrigin.Top) / Scale);

        private DiagramPoint[] _shapePoints = [];
        private ScreenRect _shapeOrigin;
        private double _shapeScale, _shapeWidth;
        private LinkRoute _shapeRoute;
        private (Geometry Line, Geometry Head)? _shapes;

        private (Geometry Line, Geometry Head) Shapes()
        {
            if (_shapes is { } cached && _shapeOrigin.Equals(PhysicalOrigin) && _shapeScale == Scale &&
                _shapeWidth == StrokeWidth && _shapeRoute == LinkStyle.Route && _shapePoints.SequenceEqual(Points))
                return cached;
            var geometry = new StreamGeometry();
            var head = new StreamGeometry();
            if (Points.Count < 2) return (geometry, head);
            var start = Local(Points[0]);
            var end = Local(Points[^1]);
            Point previous;
            using (var context = geometry.Open())
            {
                context.BeginFigure(start, false, false);
                if (LinkStyle.Route == LinkRoute.Curved && Points.Count == 2)
                {
                    var dx = end.X - start.X;
                    var control1 = new Point(start.X + dx / 3, start.Y - 24);
                    var control2 = new Point(end.X - dx / 3, end.Y - 24);
                    context.BezierTo(control1, control2, end, true, false);
                    previous = control2;
                }
                else
                {
                    for (var i = 1; i < Points.Count - 1; i++)
                    {
                        var before = Local(Points[i - 1]); var corner = Local(Points[i]); var after = Local(Points[i + 1]);
                        var incoming = corner - before; var outgoing = after - corner;
                        var radius = Math.Min(24, Math.Min(incoming.Length, outgoing.Length) / 2);
                        if (radius < 0.01) { context.LineTo(corner, true, false); continue; }
                        incoming.Normalize(); outgoing.Normalize();
                        var entry = corner - incoming * radius; var exit = corner + outgoing * radius;
                        context.LineTo(entry, true, false);
                        context.QuadraticBezierTo(corner, exit, true, false);
                    }
                    context.LineTo(end, true, false);
                    previous = Local(Points[^2]);
                }
            }
            var direction = end - previous;
            if (direction.Length >= 0.01)
            {
                direction.Normalize();
                var normal = new Vector(-direction.Y, direction.X);
                var length = 12 + Math.Max(0, StrokeWidth - 2);
                var halfWidth = 6 + Math.Max(0, StrokeWidth - 2) / 2;
                using var context = head.Open();
                context.BeginFigure(end, true, true);
                context.LineTo(end - direction * length + normal * halfWidth, true, false);
                context.LineTo(end - direction * length - normal * halfWidth, true, false);
            }
            geometry.Freeze(); head.Freeze();
            _shapePoints = Points.ToArray();
            _shapeOrigin = PhysicalOrigin; _shapeScale = Scale; _shapeWidth = StrokeWidth; _shapeRoute = LinkStyle.Route;
            _shapes = (geometry, head);
            return (geometry, head);
        }

        internal bool ContainsPoint(Point point)
        {
            if (Points.Count < 2) return false;
            var (line, head) = Shapes();
            return line.StrokeContains(new Pen(Brushes.Black, Math.Max(12, StrokeWidth + 8)), point) || head.FillContains(point);
        }

        protected override HitTestResult? HitTestCore(PointHitTestParameters parameters) =>
            ContainsPoint(parameters.HitPoint) ? new PointHitTestResult(this, parameters.HitPoint) : null;

        protected override void OnRender(DrawingContext dc)
        {
            if (Points.Count < 2) return;
            var (geometry, head) = Shapes();
            // Alpha 1 provides a forgiving input corridor only along the actual
            // route. All other pixels remain transparent to windows underneath.
            if (Interactive)
            {
                var hitBrush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
                dc.DrawGeometry(null, new Pen(hitBrush, Math.Max(12, StrokeWidth + 8)), geometry);
                dc.DrawGeometry(hitBrush, null, head);
            }
            var color = LegacyStandard ? "#606060" : LinkStyle.Color;
            var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
            var width = StrokeWidth + (IsHovered ? 0.35 : 0);
            var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.DashStyle = LinkStyle.Pattern switch { LinkLinePattern.Dashed => DashStyles.Dash, LinkLinePattern.Dotted => DashStyles.Dot, _ => DashStyles.Solid };
            if (IsHovered) dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(30, 74, 164, 255)), width + 4), geometry);
            dc.DrawGeometry(null, pen, geometry);
            dc.DrawGeometry(brush, null, head);
            // Interactive overlays use owned endpoint windows above the notes.
            // Drawing the same rings here leaves a second, lagging copy underneath.
            if (IsSelected && !Interactive)
            {
                var blue = new Pen(new SolidColorBrush(Color.FromRgb(74, 164, 255)), 1.8);
                dc.DrawEllipse(Brushes.White, blue, Local(Points[0]), 6, 6);
                dc.DrawEllipse(Brushes.White, blue, Local(Points[^1]), 6, 6);
            }
        }
    }
}
