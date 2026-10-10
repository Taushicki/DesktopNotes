using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using DesktopNotes.Domain;
using Forms = System.Windows.Forms;

namespace DesktopNotes.Presentation;

// A separate owned window keeps the toolbar outside the note without changing its saved bounds.
internal sealed class NoteFormattingToolbar : Window, IDisposable
{
    internal static readonly string[] Palette = ["#FFF1A8", "#FFD9BC", "#CFF0D6", "#D3E9FC", "#E3D9FA", "#FAD8E4"];
    private readonly NoteWindow _host;
    private readonly NoteViewModel _vm;
    private readonly DispatcherTimer _timer;
    private readonly ComboBox _size;
    private readonly Button _color, _bold, _alignment, _pin;
    private readonly List<Popup> _popups = [];
    private bool _selected, _disposed, _updating;

    internal NoteFormattingToolbar(NoteWindow host, NoteViewModel vm)
    {
        _host = host; _vm = vm;
        ShowInTaskbar = false; ShowActivated = false;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        Width = 272; Height = 56;
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _color = Button("Цвет заметки", () => OpenPalette());
        row.Children.Add(_color);
        _size = new ComboBox { Width = 66, Height = 30, IsEditable = true, FontSize = 15, VerticalContentAlignment = VerticalAlignment.Center,
            ItemsSource = new double[] { 8, 10, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 60, 72 } };
        AutomationProperties.SetName(_size, "Размер шрифта");
        _size.SelectionChanged += (_, _) => { if (!_updating && _size.SelectedItem is double value) _vm.FontSize = value; };
        _size.LostKeyboardFocus += (_, _) => CommitSize();
        _size.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { CommitSize(); e.Handled = true; } };
        row.Children.Add(_size);
        _bold = Button("Жирный шрифт", _host.ToggleBoldFormatting);
        _bold.Content = new TextBlock { Text = "B", FontWeight = FontWeights.Bold, FontStyle = FontStyles.Italic, FontSize = 19 };
        row.Children.Add(_bold);
        _alignment = Button("Выравнивание текста", OpenAlignment);
        row.Children.Add(_alignment);
        _pin = Button("Закрепить положение заметки", () => _vm.IsPinned = !_vm.IsPinned);
        row.Children.Add(_pin);
        Content = new Border { Margin = new Thickness(5), Padding = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromRgb(252, 253, 255)), BorderBrush = new SolidColorBrush(Color.FromRgb(210, 219, 230)),
            BorderThickness = new Thickness(1), Child = row, Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.16 } };
        SourceInitialized += (_, _) => StickerWindowStyle.HideFromWindowSwitcher(new WindowInteropHelper(this).Handle);
        Deactivated += (_, _) => _host.InteractionDeactivated?.Invoke(_host);
        _vm.PropertyChanged += Changed;
        _host.LocationChanged += GeometryChanged; _host.SizeChanged += SizeChangedOnHost; _host.IsVisibleChanged += VisibilityChanged;
        _timer = new DispatcherTimer(DispatcherPriority.Background, host.Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Refresh();
        Sync();
    }

    private static Button Button(string tooltip, Action action)
    {
        var button = new Button { Width = 43, Height = 32, Margin = new Thickness(1, 0, 1, 0), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Foreground = Brushes.DarkSlateGray, Focusable = false, Padding = new Thickness(4) };
        AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    private static System.Windows.Shapes.Path CreateIcon(string geometry) => new()
    { Data = Geometry.Parse(geometry), Stroke = Brushes.DarkSlateGray, StrokeThickness = 1.6, Width = 19, Height = 19, Stretch = Stretch.Uniform };

    private void CommitSize()
    {
        if (double.TryParse(_size.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value) && value is >= 8 and <= 72)
            _vm.FontSize = value;
        Sync();
    }

    private void Sync(string? property = null)
    {
        _updating = true;
        if (property is null or nameof(NoteViewModel.FontSize)) _size.Text = _vm.FontSize.ToString(CultureInfo.CurrentCulture);
        if (property is null or nameof(NoteViewModel.Color)) _color.Content = new System.Windows.Shapes.Ellipse { Width = 23, Height = 23, Fill = (Brush)new BrushConverter().ConvertFromString(Palette[(int)_vm.Color])!, Stroke = Brushes.SlateGray, StrokeThickness = 0.8 };
        if (property is null or nameof(NoteViewModel.IsBold)) _bold.Background = _vm.IsBold ? Brushes.LightBlue : Brushes.Transparent;
        if (property is null or nameof(NoteViewModel.TextAlignment)) _alignment.Content = AlignmentIcon(_vm.TextAlignment);
        if (property is null or nameof(NoteViewModel.IsPinned)) _pin.Content = CreateIcon(_vm.IsPinned ? "M 5,10 L 5,6 C 5,0 15,0 15,6 L 15,10 M 3,10 L 17,10 L 17,22 L 3,22 Z M 10,14 L 10,18"
            : "M 5,10 L 5,6 C 5,0 15,0 15,6 M 3,10 L 17,10 L 17,22 L 3,22 Z M 10,14 L 10,18");
        if (property is null or nameof(NoteViewModel.IsPinned)) _pin.Background = _vm.IsPinned ? Brushes.LightBlue : Brushes.Transparent;
        if (property is null or nameof(NoteViewModel.IsPinned)) AutomationProperties.SetName(_pin, _vm.IsPinned ? "Открепить положение заметки" : "Закрепить положение заметки");
        _updating = false;
    }

    private static System.Windows.Shapes.Path AlignmentIcon(NoteTextAlignment alignment) => CreateIcon(alignment switch
    {
        NoteTextAlignment.Center => "M 0,0 L 20,0 M 4,6 L 16,6 M 0,12 L 20,12 M 4,18 L 16,18",
        NoteTextAlignment.Right => "M 0,0 L 20,0 M 8,6 L 20,6 M 0,12 L 20,12 M 8,18 L 20,18",
        NoteTextAlignment.Justify => "M 0,0 L 20,0 M 0,6 L 20,6 M 0,12 L 20,12 M 0,18 L 20,18",
        _ => "M 0,0 L 20,0 M 0,6 L 12,6 M 0,12 L 20,12 M 0,18 L 12,18"
    });

    private Popup Popup(Button target, UIElement content)
    {
        var popup = new Popup { PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = new Border { Background = Brushes.White, BorderBrush = Brushes.LightSlateGray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(8), Child = content } };
        _popups.Add(popup); popup.Closed += (_, _) => _popups.Remove(popup); popup.IsOpen = true;
        return popup;
    }

    private void OpenPalette()
    {
        var grid = new UniformGrid { Columns = 3 };
        Popup? popup = null;
        string[] names = ["Жёлтый", "Персиковый", "Мятный", "Голубой", "Лавандовый", "Розовый"];
        foreach (var color in Enum.GetValues<NoteColor>())
        {
            var button = Button(names[(int)color], () => { _vm.Color = color; popup!.IsOpen = false; });
            button.Content = new System.Windows.Shapes.Ellipse { Width = 25, Height = 25, Fill = (Brush)new BrushConverter().ConvertFromString(Palette[(int)color])!,
                Stroke = _vm.Color == color ? Brushes.DodgerBlue : Brushes.SlateGray, StrokeThickness = _vm.Color == color ? 2 : 0.8 };
            grid.Children.Add(button);
        }
        popup = Popup(_color, grid);
    }

    private void OpenAlignment()
    {
        var rows = new StackPanel(); var horizontal = new StackPanel { Orientation = Orientation.Horizontal };
        var vertical = new StackPanel { Orientation = Orientation.Horizontal }; Popup? popup = null;
        string[] names = ["По левому краю", "По центру", "По правому краю", "По ширине"];
        foreach (var value in Enum.GetValues<NoteTextAlignment>())
        {
            var button = Button(names[(int)value], () => { _vm.TextAlignment = value; popup!.IsOpen = false; });
            button.Content = AlignmentIcon(value); button.Background = _vm.TextAlignment == value ? Brushes.LightBlue : Brushes.Transparent;
            horizontal.Children.Add(button);
        }
        string[] verticalNames = ["Сверху", "По середине", "Снизу"];
        foreach (var value in Enum.GetValues<NoteVerticalAlignment>())
        {
            var button = Button(verticalNames[(int)value], () => { _vm.VerticalAlignment = value; popup!.IsOpen = false; });
            button.Content = CreateIcon(value switch { NoteVerticalAlignment.Top => "M 0,0 L 20,0 M 10,20 L 10,5 M 5,10 L 10,5 L 15,10",
                NoteVerticalAlignment.Bottom => "M 0,20 L 20,20 M 10,0 L 10,15 M 5,10 L 10,15 L 15,10",
                _ => "M 0,10 L 20,10 M 10,0 L 10,7 M 6,3 L 10,7 L 14,3 M 10,20 L 10,13 M 6,17 L 10,13 L 14,17" });
            button.Background = _vm.VerticalAlignment == value ? Brushes.LightBlue : Brushes.Transparent; vertical.Children.Add(button);
        }
        rows.Children.Add(horizontal); rows.Children.Add(new Separator()); rows.Children.Add(vertical);
        popup = Popup(_alignment, rows);
    }

    internal void SetSelected(bool selected) { _selected = selected; Refresh(); }
    internal bool OwnsWindow(nint handle)
    {
        if (handle == 0) return false;
        var ownHandle = new WindowInteropHelper(this).Handle;
        if (ownHandle != 0 && (handle == ownHandle || NativeWindows.RootOwner(handle) == ownHandle)) return true;
        bool IsPopup(Popup popup) => popup.IsOpen && popup.Child is { } child &&
            PresentationSource.FromVisual(child) is HwndSource source && source.Handle == handle;
        return _popups.Any(IsPopup) || (_size.Template?.FindName("PART_Popup", _size) is Popup sizePopup && IsPopup(sizePopup));
    }

    internal static ScreenRect CalculateBounds(ScreenRect note, ScreenRect area, double scale)
    {
        var width = (int)Math.Round(272 * scale); var height = (int)Math.Round(56 * scale);
        var centered = (int)Math.Round(note.Left + (note.Width - width) / 2.0);
        var x = Math.Clamp(centered, area.Left, Math.Max(area.Left, area.Right - width));
        var y = note.Top - height - (int)Math.Round(5 * scale);
        if (y < area.Top) y = Math.Min(note.Bottom + (int)Math.Round(5 * scale), area.Bottom - height);
        return new ScreenRect { Left = x, Top = Math.Max(area.Top, y), Right = x + width, Bottom = Math.Max(area.Top, y) + height };
    }
    private void Refresh()
    {
        if (_disposed) return;
        if (!_selected || _vm.IsCollapsed || !_host.IsVisible || !NativeWindows.IsShown(_host.Handle))
        {
            foreach (var popup in _popups.ToArray()) popup.IsOpen = false;
            Hide(); _timer.Stop(); return;
        }
        var bounds = NativeWindows.Rect(_host.Handle); var scale = NativeWindows.Scale(_host.Handle);
        if (Owner is null) Owner = _host;
        var area = Forms.Screen.FromRectangle(System.Drawing.Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)).WorkingArea;
        Topmost = _host.Topmost;
        NativeWindows.SetBounds(new WindowInteropHelper(this).EnsureHandle(), CalculateBounds(bounds,
            new ScreenRect { Left = area.Left, Top = area.Top, Right = area.Right, Bottom = area.Bottom }, scale));
        if (!IsVisible) Show();
        _timer.Start();
    }
    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(NoteViewModel.Color) or nameof(NoteViewModel.FontSize) or nameof(NoteViewModel.IsBold) or nameof(NoteViewModel.TextAlignment) or nameof(NoteViewModel.IsPinned)) Sync(args.PropertyName);
        if (args.PropertyName is nameof(NoteViewModel.IsCollapsed) or nameof(NoteViewModel.Layer)) Refresh();
    }
    private void GeometryChanged(object? sender, EventArgs args) => Refresh();
    private void SizeChangedOnHost(object sender, SizeChangedEventArgs args) => Refresh();
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => Refresh();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop(); foreach (var popup in _popups.ToArray()) popup.IsOpen = false;
        _vm.PropertyChanged -= Changed; _host.LocationChanged -= GeometryChanged; _host.SizeChanged -= SizeChangedOnHost; _host.IsVisibleChanged -= VisibilityChanged;
        Close();
    }
}
