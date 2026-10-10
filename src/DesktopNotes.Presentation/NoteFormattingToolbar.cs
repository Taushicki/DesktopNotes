using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopNotes.Domain;
using Forms = System.Windows.Forms;

namespace DesktopNotes.Presentation;

// A separate owned window keeps the toolbar outside the note without changing its saved bounds.
internal sealed class NoteFormattingToolbar : Window, IDisposable
{
    private readonly NoteWindow _host;
    private readonly NoteViewModel _vm;
    private readonly DispatcherTimer _timer;
    private readonly TextBox _size;
    internal TextBox FontSizeInput => _size;
    internal const double ToolbarScale = 0.84;
    internal const double ToolbarWidth = 272 * ToolbarScale;
    internal const double ToolbarHeight = 56 * ToolbarScale;
    private readonly Button _color, _bold, _alignment, _pin, _layer;
    private readonly List<Popup> _popups = [];
    private bool _selected, _disposed;

    internal NoteFormattingToolbar(NoteWindow host, NoteViewModel vm)
    {
        _host = host; _vm = vm;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DesktopNotes;component/ToolbarStyles.xaml", UriKind.Relative) });
        ShowInTaskbar = false; ShowActivated = false;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        Width = ToolbarWidth; Height = ToolbarHeight;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _color = Button(UiText.Get("Цвет и тема заметки"), () => OpenPalette());
        row.Children.Add(_color);
        _size = new TextBox { Width = 30, Height = 25, FontSize = 13, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, Padding = new Thickness(0) };
        AutomationProperties.SetName(_size, UiText.Get("Размер шрифта"));
        _size.LostKeyboardFocus += (_, _) => CommitSize();
        _size.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) { CommitSize(); e.Handled = true; }
            if (e.Key is System.Windows.Input.Key.Up or System.Windows.Input.Key.Down)
            { ChangeSize(e.Key == System.Windows.Input.Key.Up ? 1 : -1); e.Handled = true; }
        };
        var size = new StackPanel { Orientation = Orientation.Horizontal, Width = 46, VerticalAlignment = VerticalAlignment.Center };
        size.Children.Add(_size);
        var arrows = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var delta in new[] { 1, -1 })
        {
            var step = Button(delta > 0 ? UiText.Get("Увеличить размер шрифта") : UiText.Get("Уменьшить размер шрифта"), () => ChangeSize(delta));
            step.Width = 12; step.Height = 12; step.Margin = new Thickness(0); step.Padding = new Thickness(1);
            step.Content = CreateIcon(delta > 0 ? "M 0,4 L 4,0 L 8,4" : "M 0,0 L 4,4 L 8,0");
            ((System.Windows.Shapes.Path)step.Content).StrokeThickness = 1.8;
            ((System.Windows.Shapes.Path)step.Content).Width = 8; ((System.Windows.Shapes.Path)step.Content).Height = 4;
            arrows.Children.Add(step);
        }
        size.Children.Add(arrows); row.Children.Add(size);
        _bold = Button(UiText.Get("Жирный шрифт"), _host.ToggleBoldFormatting);
        _bold.Content = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("F0 M 3,0 L 8,0 C 14,0 14,7 9,7 C 15,7 14,14 7,14 L 0,14 Z M 5,2 L 4,6 L 7,6 C 11,6 11,2 8,2 Z M 4,8 L 3,12 L 7,12 C 11,12 12,8 8,8 Z"),
            Fill = Brushes.DarkSlateGray, Width = 12, Height = 16, Stretch = Stretch.Fill
        };
        row.Children.Add(_bold);
        _alignment = Button(UiText.Get("Выравнивание текста"), OpenAlignment);
        row.Children.Add(_alignment);
        _pin = Button(UiText.Get("Закрепить положение заметки"), () => _vm.IsPinned = !_vm.IsPinned);
        row.Children.Add(_pin);
        _layer = Button(UiText.Get("Расположение окон"), OpenLayers);
        row.Children.Add(_layer);
        var delete = Button(UiText.Get("Удалить заметку"), _host.DeleteSelectedNote);
        delete.Content = CreateIcon("M 2,4 L 18,4 M 7,4 L 7,1 L 13,1 L 13,4 M 4,4 L 5,20 L 15,20 L 16,4 M 8,8 L 8,16 M 12,8 L 12,16");
        ((System.Windows.Shapes.Path)delete.Content).Stroke = Brushes.Firebrick;
        row.Children.Add(delete);
        Content = new Border { UseLayoutRounding = true, SnapsToDevicePixels = true,
            Margin = new Thickness(5), Padding = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(252, 253, 255)), BorderBrush = new SolidColorBrush(Color.FromRgb(210, 219, 230)),
            BorderThickness = new Thickness(1), Child = row };
        RenderOptions.SetClearTypeHint((Border)Content, ClearTypeHint.Enabled);
        SourceInitialized += (_, _) => StickerWindowStyle.HideFromWindowSwitcher(new WindowInteropHelper(this).Handle);
        Deactivated += (_, _) => _host.InteractionDeactivated?.Invoke(_host);
        _vm.PropertyChanged += Changed;
        _host.LocationChanged += GeometryChanged; _host.SizeChanged += SizeChangedOnHost; _host.IsVisibleChanged += VisibilityChanged;
        _timer = new DispatcherTimer(DispatcherPriority.Background, host.Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Refresh();
        Sync();
    }

    private Button Button(string tooltip, Action action)
    {
        var button = new Button { Width = 25, Height = 26, Margin = new Thickness(0.5, 0, 0.5, 0), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Foreground = Brushes.DarkSlateGray, Focusable = false, Padding = new Thickness(2), SnapsToDevicePixels = true, UseLayoutRounding = true, FontSize = 13 };
        button.Style = (Style)Resources["ToolbarButton"];
        AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    private static System.Windows.Shapes.Path CreateIcon(string geometry) => new()
    { Data = Geometry.Parse(geometry), Stroke = Brushes.DarkSlateGray, StrokeThickness = 1.4, Width = 17, Height = 17, SnapsToDevicePixels = true, Stretch = Stretch.Uniform };

    private void CommitSize()
    {
        if (double.TryParse(_size.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value) && value is >= 8 and <= 72)
            _vm.FontSize = value;
        Sync(nameof(NoteViewModel.FontSize));
    }

    private void ChangeSize(int delta)
    {
        CommitSize();
        _vm.FontSize = Math.Clamp(_vm.FontSize + delta, 8, 72);
    }

    private void Sync(string? property = null)
    {
        if (property is null or nameof(NoteViewModel.FontSize)) _size.Text = _vm.FontSize.ToString(CultureInfo.CurrentCulture);
        if (property is null or nameof(NoteViewModel.Color)) _color.Content = new System.Windows.Shapes.Ellipse { Width = 19, Height = 19, Fill = NoteThemes.Get(_vm.Color).Background, Stroke = Brushes.SlateGray, StrokeThickness = 0.8 };
        if (property is null or nameof(NoteViewModel.IsBold)) _bold.Background = _vm.IsBold ? (Brush)Resources["ToolbarSelectedBrush"] : Brushes.Transparent;
        if (property is null or nameof(NoteViewModel.TextAlignment)) _alignment.Content = AlignmentIcon(_vm.TextAlignment);
        if (property is null or nameof(NoteViewModel.IsPinned)) _pin.Content = CreateIcon(_vm.IsPinned ? "M 5,10 L 5,6 C 5,0 15,0 15,6 L 15,10 M 3,10 L 17,10 L 17,22 L 3,22 Z M 10,14 L 10,18"
            : "M 5,10 L 5,6 C 5,0 15,0 15,6 M 3,10 L 17,10 L 17,22 L 3,22 Z M 10,14 L 10,18");
        if (property is null or nameof(NoteViewModel.Layer)) _layer.Content = CreateIcon(_vm.Layer switch
        {
            WindowLayer.Topmost => "M 2,8 L 10,3 L 18,8 L 10,13 Z M 2,12 L 10,17 L 18,12 M 10,0 L 10,5 M 7,3 L 10,0 L 13,3",
            WindowLayer.Background => "M 2,3 L 10,8 L 18,3 M 2,7 L 10,12 L 18,7 M 2,11 L 10,16 L 18,11 M 10,16 L 10,21 M 7,18 L 10,21 L 13,18",
            _ => "M 2,5 L 10,0 L 18,5 L 10,10 Z M 2,9 L 10,14 L 18,9 M 2,13 L 10,18 L 18,13"
        });
        if (property is null or nameof(NoteViewModel.IsPinned)) _pin.Background = _vm.IsPinned ? (Brush)Resources["ToolbarSelectedBrush"] : Brushes.Transparent;
        if (property is null or nameof(NoteViewModel.IsPinned)) AutomationProperties.SetName(_pin, _vm.IsPinned ? UiText.Get("Открепить положение заметки") : UiText.Get("Закрепить положение заметки"));
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
            Child = new Border { UseLayoutRounding = true, SnapsToDevicePixels = true, Background = Brushes.White, BorderBrush = Brushes.LightSlateGray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(6), Child = content } };
        TextOptions.SetTextFormattingMode(popup.Child, TextFormattingMode.Display);
        RenderOptions.SetClearTypeHint(popup.Child, ClearTypeHint.Enabled);
        _popups.Add(popup); popup.Closed += (_, _) => _popups.Remove(popup); popup.IsOpen = true;
        return popup;
    }

    private void OpenPalette()
    {
        var choices = new StackPanel();
        Popup? popup = null;
        foreach (var dark in new[] { false, true })
        {
            if (dark) choices.Children.Add(new Separator());
            var grid = new UniformGrid { Columns = 3 };
            foreach (var color in Enum.GetValues<NoteColor>().Where(color => NoteThemes.Get(color).IsDark == dark))
            {
                var theme = NoteThemes.Get(color);
                var button = Button(theme.Name, () => { _vm.Color = color; popup!.IsOpen = false; });
                button.Content = new System.Windows.Shapes.Ellipse
                {
                    Width = 20, Height = 20, Fill = theme.Background,
                    Stroke = _vm.Color == color ? Brushes.DodgerBlue : Brushes.SlateGray,
                    StrokeThickness = _vm.Color == color ? 2 : 0.8
                };
                grid.Children.Add(button);
            }
            choices.Children.Add(grid);
        }
        popup = Popup(_color, choices);
    }

    private void OpenAlignment()
    {
        var rows = new StackPanel(); var horizontal = new StackPanel { Orientation = Orientation.Horizontal };
        var vertical = new StackPanel { Orientation = Orientation.Horizontal }; Popup? popup = null;
        string[] names = [UiText.Get("По левому краю"), UiText.Get("По центру"), UiText.Get("По правому краю"), UiText.Get("По ширине")];
        foreach (var value in Enum.GetValues<NoteTextAlignment>())
        {
            var button = Button(names[(int)value], () => { _vm.TextAlignment = value; popup!.IsOpen = false; });
            button.Content = AlignmentIcon(value); button.Background = _vm.TextAlignment == value ? (Brush)Resources["ToolbarSelectedBrush"] : Brushes.Transparent;
            horizontal.Children.Add(button);
        }
        string[] verticalNames = [UiText.Get("Сверху"), UiText.Get("По середине"), UiText.Get("Снизу")];
        foreach (var value in Enum.GetValues<NoteVerticalAlignment>())
        {
            var button = Button(verticalNames[(int)value], () => { _vm.VerticalAlignment = value; popup!.IsOpen = false; });
            button.Content = CreateIcon(value switch { NoteVerticalAlignment.Top => "M 0,0 L 20,0 M 10,20 L 10,5 M 5,10 L 10,5 L 15,10",
                NoteVerticalAlignment.Bottom => "M 0,20 L 20,20 M 10,0 L 10,15 M 5,10 L 10,15 L 15,10",
                _ => "M 0,10 L 20,10 M 10,0 L 10,7 M 6,3 L 10,7 L 14,3 M 10,20 L 10,13 M 6,17 L 10,13 L 14,17" });
            button.Background = _vm.VerticalAlignment == value ? (Brush)Resources["ToolbarSelectedBrush"] : Brushes.Transparent; vertical.Children.Add(button);
        }
        rows.Children.Add(horizontal); rows.Children.Add(new Separator()); rows.Children.Add(vertical);
        popup = Popup(_alignment, rows);
    }

    private void OpenLayers()
    {
        var items = new StackPanel(); Popup? popup = null;
        string[] names = [UiText.Get("Обычный"), UiText.Get("Поверх окон"), UiText.Get("На задний план")];
        foreach (var layer in Enum.GetValues<WindowLayer>())
        {
            var button = Button(names[(int)layer], () => { _vm.Layer = layer; popup!.IsOpen = false; });
            button.Width = 126; button.Content = names[(int)layer];
            button.Background = _vm.Layer == layer ? (Brush)Resources["ToolbarSelectedBrush"] : Brushes.Transparent;
            items.Children.Add(button);
        }
        popup = Popup(_layer, items);
    }

    internal void SetSelected(bool selected) { _selected = selected; Refresh(); }
    internal bool OwnsWindow(nint handle)
    {
        if (handle == 0) return false;
        var ownHandle = new WindowInteropHelper(this).Handle;
        if (ownHandle != 0 && (handle == ownHandle || NativeWindows.RootOwner(handle) == ownHandle)) return true;
        bool IsPopup(Popup popup) => popup.IsOpen && popup.Child is { } child &&
            PresentationSource.FromVisual(child) is HwndSource source && source.Handle == handle;
        return _popups.Any(IsPopup);
    }

    internal static ScreenRect CalculateBounds(ScreenRect note, ScreenRect area, double scale)
    {
        var width = (int)Math.Round(ToolbarWidth * scale); var height = (int)Math.Round(ToolbarHeight * scale);
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
        if (args.PropertyName is nameof(NoteViewModel.Color) or nameof(NoteViewModel.FontSize) or nameof(NoteViewModel.IsBold) or nameof(NoteViewModel.TextAlignment) or nameof(NoteViewModel.IsPinned) or nameof(NoteViewModel.Layer)) Sync(args.PropertyName);
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
