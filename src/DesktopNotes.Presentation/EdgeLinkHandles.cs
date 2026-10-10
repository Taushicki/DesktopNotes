using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopNotes.Application;
using DesktopNotes.Domain;

namespace DesktopNotes.Presentation;

// Owned windows keep all four ports outside the note's HWND without clipping.
// Selection, rather than a keyboard modifier, controls their lifetime.
internal sealed class EdgeLinkHandles : Grid, IDisposable
{
    private sealed record Port(Window Window, Button Button, PortVisual Visual);
    private readonly Window _host;
    private readonly DispatcherTimer _timer;
    private readonly Action<NoteSide> _begin;
    private readonly Action _update, _complete, _cancel;
    private readonly Action? _deactivated;
    private readonly Dictionary<NoteSide, Port> _ports = [];
    private readonly Dictionary<NoteSide, Port> _attachments = [];
    private Port? _dragPort;
    private bool _targeted, _disposed;
    internal bool IsSelected { get; private set; }
    internal bool IsDragging => _dragPort is not null;
    internal int VisiblePortCount => _ports.Values.Count(port => port.Window.IsVisible);

    public EdgeLinkHandles(Window host, Action<NoteSide> begin, Action update, Action complete, Action cancel, Action? deactivated = null)
    {
        _host = host; _begin = begin; _update = update; _complete = complete; _cancel = cancel;
        _deactivated = deactivated;
        _timer = new DispatcherTimer(DispatcherPriority.Render, host.Dispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Refresh();
        host.IsVisibleChanged += OnVisibilityChanged;
        host.LocationChanged += OnLocationChanged;
        host.SizeChanged += OnSizeChanged;
    }

    internal void SetState(bool selected, bool targeted)
    {
        IsSelected = selected; _targeted = targeted;
        Refresh();
    }

    internal static NoteBounds MarkerBounds(NoteBounds note, NoteSide side, double scale = 1)
    {
        var anchor = ArrowRouter.Anchor(note, side);
        var size = 28 * scale;
        var gap = 10 * scale;
        return side switch
        {
            NoteSide.Left => new(note.X - gap - size, anchor.Y - size / 2, size, size),
            NoteSide.Top => new(anchor.X - size / 2, note.Y - gap - size, size, size),
            NoteSide.Right => new(note.Right + gap, anchor.Y - size / 2, size, size),
            _ => new(anchor.X - size / 2, note.Bottom + gap, size, size)
        };
    }

    internal static NoteBounds AttachmentBounds(NoteBounds note, NoteSide side, double scale = 1)
    {
        var anchor = ArrowRouter.Anchor(note, side);
        var size = 16 * scale;
        return new(anchor.X - size / 2, anchor.Y - size / 2, size, size);
    }

    private Port CreatePort(NoteSide side, bool attachment)
    {
        var visual = new PortVisual { Side = side, IsAttachment = attachment, IsHitTestVisible = false,
            Width = attachment ? 16 : 28, Height = attachment ? 16 : 28 };
        var button = new Button
        {
            Content = visual, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Focusable = false, Cursor = Cursors.Cross
        };
        AutomationProperties.SetName(button, $"Создать связь: {side switch { NoteSide.Left => "слева", NoteSide.Top => "сверху", NoteSide.Right => "справа", _ => "снизу" }}");
        var window = new Window
        {
            Owner = _host, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
            Background = attachment ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)),
            Width = attachment ? 16 : 28, Height = attachment ? 16 : 28, Content = button
        };
        var port = new Port(window, button, visual);
        window.SourceInitialized += (_, _) => NativeWindows.MakeOverlay(new WindowInteropHelper(window).Handle, !attachment);
        window.Deactivated += (_, _) => _deactivated?.Invoke();
        if (attachment) return port;
        button.MouseEnter += (_, _) => { if (_dragPort is null) { visual.IsHovered = true; visual.InvalidateVisual(); } };
        button.MouseLeave += (_, _) => { visual.IsHovered = false; visual.InvalidateVisual(); };
        button.MouseMove += (_, _) => { if (_dragPort == port) _update(); };
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!IsSelected || (Keyboard.Modifiers & ModifierKeys.Control) != 0) return;
            e.Handled = true;
            // Activating another HWND after capture can release it before the drag starts.
            _host.Activate();
            if (!button.CaptureMouse()) return;
            _dragPort = port;
            visual.IsHovered = false; visual.InvalidateVisual();
            _begin(side);
        };
        button.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_dragPort != port) return;
            e.Handled = true; _dragPort = null;
            try { _complete(); }
            finally { button.ReleaseMouseCapture(); Refresh(); }
        };
        button.LostMouseCapture += (_, _) => { if (_dragPort == port) CancelDrag(); };
        window.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { CancelDrag(); _host.Activate(); e.Handled = true; }
        };
        return port;
    }

    private void Refresh()
    {
        if (_disposed) return;
        if ((!IsSelected && !_targeted) || !_host.IsVisible)
        {
            HidePorts(); _timer.Stop(); return;
        }
        var hwnd = new WindowInteropHelper(_host).Handle;
        if (!NativeWindows.IsShown(hwnd)) { HidePorts(); return; }
        var note = NativeWindows.Rect(hwnd).Bounds;
        var scale = NativeWindows.Scale(hwnd);
        var visible = _targeted ? _attachments : _ports;
        var hidden = _targeted ? _ports : _attachments;
        foreach (var port in hidden.Values) port.Window.Hide();
        foreach (var side in Enum.GetValues<NoteSide>())
        {
            if (!visible.TryGetValue(side, out var port)) { port = CreatePort(side, _targeted); visible.Add(side, port); }
            port.Window.Topmost = _host.Topmost;
            var bounds = _targeted ? AttachmentBounds(note, side, scale) : MarkerBounds(note, side, scale);
            // Position before first Show to avoid flashes at the default window location.
            var handle = new WindowInteropHelper(port.Window).EnsureHandle();
            NativeWindows.SetBounds(handle, new ScreenRect
            {
                Left = (int)Math.Round(bounds.X), Top = (int)Math.Round(bounds.Y),
                Right = (int)Math.Round(bounds.Right), Bottom = (int)Math.Round(bounds.Bottom)
            });
            if (!port.Window.IsVisible) port.Window.Show();
        }
        _timer.Start();
    }

    private void HidePorts()
    {
        foreach (var port in _ports.Values.Concat(_attachments.Values)) if (port.Window.IsVisible) port.Window.Hide();
    }
    private void OnLocationChanged(object? sender, EventArgs e) => Refresh();
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Refresh();
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_host.IsVisible) CancelDrag();
        Refresh();
    }

    public void CancelDrag()
    {
        var port = _dragPort; _dragPort = null;
        if (port is not null) { _cancel(); port.Button.ReleaseMouseCapture(); }
        Refresh();
    }
    public void Dispose()
    {
        _disposed = true; CancelDrag(); _timer.Stop();
        _host.IsVisibleChanged -= OnVisibilityChanged; _host.LocationChanged -= OnLocationChanged; _host.SizeChanged -= OnSizeChanged;
        foreach (var port in _ports.Values.Concat(_attachments.Values)) port.Window.Close();
        _ports.Clear(); _attachments.Clear();
    }

    internal sealed class PortVisual : FrameworkElement
    {
        public NoteSide Side;
        public bool IsAttachment, IsHovered;
        protected override void OnRender(DrawingContext dc)
        {
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            var blue = new SolidColorBrush(Color.FromRgb(74, 164, 255));
            if (IsAttachment) { dc.DrawEllipse(Brushes.White, new Pen(blue, 1.8), center, 6, 6); return; }
            if (!IsHovered) { dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(145, 202, 255)), new Pen(Brushes.White, 1.8), center, 4.5, 4.5); return; }
            dc.PushTransform(new TranslateTransform(center.X, center.Y));
            dc.PushTransform(new RotateTransform(Side switch { NoteSide.Left => 180, NoteSide.Top => 270, NoteSide.Bottom => 90, _ => 0 }));
            var arrow = Geometry.Parse("M -7,-2 L 0,-2 L 0,-6 L 8,0 L 0,6 L 0,2 L -7,2 Z");
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(145, 202, 255)), new Pen(Brushes.White, 1.8) { LineJoin = PenLineJoin.Round }, arrow);
            dc.Pop(); dc.Pop();
        }
    }
}
