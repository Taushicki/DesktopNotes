using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Automation;
using DesktopNotes.Application;
using DesktopNotes.Domain;
using Forms = System.Windows.Forms;

namespace DesktopNotes.Presentation;

public sealed class NoteWindow : Window
{
    private readonly NoteViewModel _vm;
    private readonly IWindowLayerController _layers;
    private readonly IMarkdownRenderer _markdown;
    private readonly Grid _body = new();
    private readonly TextBox _editor;
    private readonly FlowDocumentScrollViewer _viewer;
    private readonly Button _preview;
    private readonly Border _titleArea;
    private readonly TextBlock _titleLabel;
    private readonly TextBox _titleEditor;
    private readonly Border _frame = new() { BorderThickness = new Thickness(2), IsHitTestVisible = false };
    private readonly EdgeLinkHandles _linkHandles;
    private readonly NoteFormattingToolbar _formatting;
    private readonly Border _connectionFrame = new() { BorderThickness = new Thickness(1.2), IsHitTestVisible = false };
    private HwndSource? _source;
    internal Guid NoteId => _vm.Snapshot.Id;
    internal nint Handle => new WindowInteropHelper(this).EnsureHandle();
    internal Func<NoteWindow, bool>? BeginDrag { get; set; }
    internal Func<NoteWindow, ScreenRect, ScreenRect>? Moving { get; set; }
    internal Action<NoteWindow>? EndDrag { get; set; }
    internal Action<NoteWindow>? ControlClicked { get; set; }
    internal Action<NoteWindow>? ConnectionClicked { get; set; }
    internal Action<NoteWindow>? InteractionDeactivated { get; set; }
    internal bool IsConnectionSelected => _linkHandles.IsSelected;
    internal NoteFormattingToolbar FormattingToolbar => _formatting;
    internal bool OwnsInteractionWindow(nint handle) => handle != 0 &&
        (handle == Handle || NativeWindows.RootOwner(handle) == Handle || _formatting.OwnsWindow(handle));
    internal Action? CommitSelection { get; set; }
    internal Action<NoteWindow, ContextMenu>? PopulateBoardMenu { get; set; }
    internal Action<NoteWindow, NoteSide>? LinkDragStarted { get; set; }
    internal Action? LinkDragUpdated { get; set; }
    internal Action? LinkDragCompleted { get; set; }
    internal Func<bool>? CancelInteraction { get; set; }
    internal event Action? GeometryChanged;
    private bool _editingTitle;
    private bool _updatingLayout;
    private bool _ready;
    private bool _closing;

    public NoteWindow(NoteViewModel vm, IWindowLayerController layers, IMarkdownRenderer markdown)
    {
        _vm = vm; _layers = layers; _markdown = markdown;
        DataContext = vm;
        Title = vm.Title;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        MinWidth = 220; MinHeight = 140;
        Width = vm.Snapshot.Width; Height = vm.Snapshot.Height;
        Left = vm.Snapshot.X; Top = vm.Snapshot.Y;
        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 44, 52));
        FontFamily = new FontFamily("Segoe UI");
        ContextMenu = new ContextMenu();
        PopulateMenu(ContextMenu);
        ContextMenu.Opened += (_, _) => PopulateMenu(ContextMenu);
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(5), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0) });

        var root = new Grid { Margin = new Thickness(5) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        root.RowDefinitions.Add(new RowDefinition());
        var header = new Grid { Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _titleArea = new Border
        {
            Background = Brushes.Transparent, Padding = new Thickness(8, 0, 4, 0), Focusable = true,
            Cursor = System.Windows.Input.Cursors.SizeAll,
            ToolTip = "Перетащите за заголовок. Двойной клик или F2 — изменить название."
        };
        var titleContent = new Grid();
        _titleLabel = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _titleLabel.SetBinding(TextBlock.TextProperty, new Binding(nameof(vm.Title)));
        _titleEditor = new TextBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold,
            VerticalContentAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
            Cursor = System.Windows.Input.Cursors.IBeam, ToolTip = "Enter — сохранить название; Escape — отменить"
        };
        _titleEditor.SetBinding(TextBox.TextProperty, new Binding(nameof(vm.Title)) { UpdateSourceTrigger = UpdateSourceTrigger.Explicit });
        _titleEditor.ContextMenu = ContextMenu;
        titleContent.Children.Add(_titleLabel);
        titleContent.Children.Add(_titleEditor);
        _titleArea.Child = titleContent;
        _titleArea.MouseLeftButtonDown += (_, e) =>
        {
            if (_editingTitle) return;
            e.Handled = true;
            if (e.ClickCount == 2) BeginTitleEdit();
            else
            {
                _titleArea.Focus();
                if (_vm.IsPinned || !(BeginDrag?.Invoke(this) ?? true)) return;
                try { DragMove(); }
                finally { EndDrag?.Invoke(this); }
            }
        };
        _titleArea.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.F2) { BeginTitleEdit(); e.Handled = true; }
        };
        _titleEditor.KeyDown += (_, e) =>
        {
            if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Escape)
            {
                FinishTitleEdit(e.Key == System.Windows.Input.Key.Enter);
                _titleArea.Focus();
                e.Handled = true;
            }
        };
        _titleEditor.LostKeyboardFocus += (_, _) => FinishTitleEdit(true);
        header.Children.Add(_titleArea);
        var collapse = MakeButton("−", "Свернуть / развернуть"); collapse.Command = vm.ToggleCollapsed;
        collapse.VerticalAlignment = VerticalAlignment.Top;
        collapse.Height = 28;
        collapse.Width = 28;
        collapse.Padding = new Thickness(0);
        Grid.SetColumn(collapse, 1); header.Children.Add(collapse);
        root.Children.Add(header);

        _body.ColumnDefinitions.Add(new ColumnDefinition());
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        _preview = MakeButton("", "Показать оформленный Markdown");
        _preview.Content = CreateModeIcon(vm.IsPreview);
        _preview.HorizontalAlignment = HorizontalAlignment.Center;
        _preview.VerticalAlignment = VerticalAlignment.Top;
        _preview.MinWidth = 14;
        _preview.Width = 14;
        _preview.Height = 14;
        _preview.Padding = new Thickness(1.5);
        AutomationProperties.SetName(_preview, "Показать оформленный Markdown");
        _preview.Command = vm.TogglePreview;
        Grid.SetColumn(_preview, 1);
        _body.Children.Add(_preview);
        _editor = new TextBox
        {
            AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(8), FontSize = 14,
            IsUndoEnabled = true, ToolTip = "Текст в формате Markdown"
        };
        _editor.SetBinding(TextBox.TextProperty, new Binding(nameof(vm.Markdown)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        _editor.ContextMenu = ContextMenu;
        _body.Children.Add(_editor);
        _viewer = new FlowDocumentScrollViewer { Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsToolBarVisible = false, Visibility = Visibility.Collapsed, ContextMenu = ContextMenu };
        _body.Children.Add(_viewer);
        Grid.SetRow(_body, 1); root.Children.Add(_body);
        var shell = new Grid();
        shell.Children.Add(root);
        shell.Children.Add(_frame);
        shell.Children.Add(_connectionFrame);
        _linkHandles = new EdgeLinkHandles(this,
            side => { FinishTitleEdit(true); LinkDragStarted?.Invoke(this, side); },
            () => LinkDragUpdated?.Invoke(), () => LinkDragCompleted?.Invoke(), () => CancelInteraction?.Invoke(),
            () => InteractionDeactivated?.Invoke(this));
        shell.Children.Add(_linkHandles);
        Content = shell;
        _formatting = new NoteFormattingToolbar(this, vm);
        ApplyTextFormatting();
        _body.SizeChanged += (_, _) => UpdatePreviewHeight();
        _viewer.Loaded += (_, _) =>
        {
            if (FindScrollViewer(_viewer) is { } scroll) scroll.ScrollChanged += (_, _) => UpdatePreviewHeight();
            UpdatePreviewHeight();
        };
        PreviewMouseDown += (_, _) =>
        {
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0) CommitSelection?.Invoke();
        };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0)
            {
                ConnectionClicked?.Invoke(this);
                return;
            }
            ControlClicked?.Invoke(this);
            e.Handled = true;
        };
        PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            if (key == System.Windows.Input.Key.Escape && (CancelInteraction?.Invoke() ?? false))
            {
                _linkHandles.CancelDrag();
                e.Handled = true;
            }
        };
        vm.PropertyChanged += OnViewModelChanged;
        SourceInitialized += (_, _) =>
        {
            StickerWindowStyle.HideFromWindowSwitcher(new WindowInteropHelper(this).Handle);
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(WindowMessages);
            ApplyState();
        };
        Loaded += (_, _) => { _ready = true; EnsureVisibleOnScreen(); SaveBounds(); };
        LocationChanged += (_, _) => { SaveBounds(); GeometryChanged?.Invoke(); };
        SizeChanged += (_, _) => { SaveBounds(); GeometryChanged?.Invoke(); };
        IsVisibleChanged += (_, _) => GeometryChanged?.Invoke();
        Activated += (_, _) => GeometryChanged?.Invoke();
        Deactivated += (_, _) => { if (_vm.Layer == WindowLayer.Background && !_closing) ApplyLayer(); GeometryChanged?.Invoke(); InteractionDeactivated?.Invoke(this); };
        Closing += (_, e) => { if (!_closing) { e.Cancel = true; vm.Hide.Execute(null); } };
        Closed += (_, _) => { _formatting.Dispose(); _linkHandles.Dispose(); vm.PropertyChanged -= OnViewModelChanged; vm.Dispose(); _source?.RemoveHook(WindowMessages); };
    }

    private static Button MakeButton(string text, string tooltip) => new()
    { Content = text, ToolTip = tooltip, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(8, 3, 8, 3), MinWidth = 28, Focusable = false };

    private System.Windows.Shapes.Path CreateModeIcon(bool isPreview) => new()
    {
        Data = Geometry.Parse(isPreview
            ? "M 12,5 C 9,3 5,2 1,3 L 1,19 C 5,18 9,19 12,21 C 15,19 19,18 23,19 L 23,3 C 19,2 15,3 12,5 L 12,21"
            : "M 3,17 L 3,21 L 7,21 L 21,7 L 17,3 Z M 15,5 L 19,9 M 3,17 L 7,21"),
        Stroke = Foreground, StrokeThickness = 0.8, StrokeLineJoin = PenLineJoin.Round,
        Width = 10, Height = 10, Stretch = Stretch.Uniform, IsHitTestVisible = false
    };

    private void BeginTitleEdit()
    {
        if (_editingTitle) return;
        _editingTitle = true;
        _titleEditor.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        _titleLabel.Visibility = Visibility.Collapsed;
        _titleEditor.Visibility = Visibility.Visible;
        _titleEditor.Focus();
        _titleEditor.SelectAll();
    }

    private void FinishTitleEdit(bool commit)
    {
        if (!_editingTitle) return;
        _editingTitle = false;
        var binding = _titleEditor.GetBindingExpression(TextBox.TextProperty);
        if (commit) binding?.UpdateSource(); else binding?.UpdateTarget();
        _titleEditor.Visibility = Visibility.Collapsed;
        _titleLabel.Visibility = Visibility.Visible;
    }

    private void PopulateMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        var layers = new MenuItem { Header = "Расположение" };
        string[] layerNames = ["Обычный", "Поверх окон", "На задний план"];
        foreach (var layer in Enum.GetValues<WindowLayer>())
        {
            var item = new MenuItem { Header = layerNames[(int)layer], IsCheckable = true, IsChecked = _vm.Layer == layer };
            item.Click += (_, _) => _vm.Layer = layer; layers.Items.Add(item);
        }
        menu.Items.Add(layers);
        PopulateBoardMenu?.Invoke(this, menu);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Скрыть", Command = _vm.Hide });
        menu.Items.Add(new MenuItem { Header = "Удалить…", Command = _vm.Delete });
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(NoteViewModel.Snapshot): ApplySnapshotBounds(); break;
            case nameof(NoteViewModel.IsPinned): _titleArea.Cursor = _vm.IsPinned ? System.Windows.Input.Cursors.No : System.Windows.Input.Cursors.SizeAll; break;
            case nameof(NoteViewModel.FontSize): case nameof(NoteViewModel.IsBold):
            case nameof(NoteViewModel.TextAlignment): case nameof(NoteViewModel.VerticalAlignment):
                ApplyTextFormatting(); if (_vm.IsPreview) RenderPreview(); break;
            case nameof(NoteViewModel.Title): Title = _vm.Title; break;
            case nameof(NoteViewModel.Markdown): if (_vm.IsPreview) RenderPreview(); break;
            case nameof(NoteViewModel.IsPreview):
                _preview.Content = CreateModeIcon(_vm.IsPreview);
                _editor.Visibility = _vm.IsPreview ? Visibility.Collapsed : Visibility.Visible;
                _viewer.Visibility = _vm.IsPreview ? Visibility.Visible : Visibility.Collapsed;
                var previewAction = _vm.IsPreview ? "Редактировать Markdown" : "Показать оформленный Markdown";
                _preview.ToolTip = previewAction;
                AutomationProperties.SetName(_preview, previewAction);
                if (_vm.IsPreview) RenderPreview(); else _editor.Focus();
                break;
            case nameof(NoteViewModel.Color): case nameof(NoteViewModel.IsCollapsed): ApplyState(); break;
            case nameof(NoteViewModel.Layer): ApplyLayer(); break;
        }
    }

    private void ApplyState()
    {
        _updatingLayout = true;
        try
        {
            Background = (Brush)new BrushConverter().ConvertFromString(NoteFormattingToolbar.Palette[(int)_vm.Color])!;
            _body.Visibility = _vm.IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            MinHeight = _vm.IsCollapsed ? 44 : 140;
            Height = _vm.IsCollapsed ? 44 : _vm.Snapshot.Height;
            ResizeMode = _vm.IsCollapsed ? ResizeMode.NoResize : ResizeMode.CanResize;
            ApplyLayer();
        }
        finally { _updatingLayout = false; }
    }

    private void ApplyLayer()
    {
        Topmost = _vm.Layer == WindowLayer.Topmost;
        try { _layers.Apply(new WindowInteropHelper(this).Handle, _vm.Layer); }
        catch (Win32Exception ex) { System.Windows.MessageBox.Show(this, "Не удалось изменить порядок окон: " + ex.Message, "DesktopNotes"); }
    }

    private void ApplyTextFormatting()
    {
        _editor.FontSize = _vm.FontSize;
        _editor.FontWeight = _vm.IsBold ? FontWeights.Bold : FontWeights.Normal;
        _editor.TextAlignment = TextAlignmentFor(_vm.TextAlignment);
        _editor.VerticalContentAlignment = VerticalAlignmentFor(_vm.VerticalAlignment);
        _viewer.VerticalAlignment = VerticalAlignmentFor(_vm.VerticalAlignment);
    }

    internal static TextAlignment TextAlignmentFor(NoteTextAlignment alignment) => alignment switch
    { NoteTextAlignment.Center => TextAlignment.Center, NoteTextAlignment.Right => TextAlignment.Right,
        NoteTextAlignment.Justify => TextAlignment.Justify, _ => TextAlignment.Left };
    private static VerticalAlignment VerticalAlignmentFor(NoteVerticalAlignment alignment) => alignment switch
    { NoteVerticalAlignment.Center => VerticalAlignment.Center, NoteVerticalAlignment.Bottom => VerticalAlignment.Bottom, _ => VerticalAlignment.Top };

    private void RenderPreview()
    {
        _viewer.Document = _markdown.Render(_vm.Markdown);
        ApplyDocumentFormatting(_viewer.Document, _vm.Snapshot);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, UpdatePreviewHeight);
    }

    internal static void ApplyDocumentFormatting(System.Windows.Documents.FlowDocument document, Note note)
    {
        document.FontSize = note.FontSize;
        document.FontWeight = note.IsBold ? FontWeights.Bold : FontWeights.Normal;
        document.TextAlignment = TextAlignmentFor(note.TextAlignment);
        void Format(System.Windows.Documents.BlockCollection blocks)
        {
            foreach (var block in blocks)
            {
                if (block.ReadLocalValue(System.Windows.Documents.TextElement.FontSizeProperty) is double size) block.FontSize = size * note.FontSize / 14;
                if (note.IsBold) block.FontWeight = FontWeights.Bold;
                if (block is System.Windows.Documents.Section section) Format(section.Blocks);
                if (block is System.Windows.Documents.List list) foreach (var item in list.ListItems) Format(item.Blocks);
            }
        }
        Format(document.Blocks);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }

    private void UpdatePreviewHeight()
    {
        if (!_vm.IsPreview || _body.ActualHeight <= 0 || FindScrollViewer(_viewer) is not { } scroll || scroll.ExtentHeight <= 0) return;
        var height = Math.Max(1, Math.Min(_body.ActualHeight, scroll.ExtentHeight));
        if (double.IsNaN(_viewer.Height) || Math.Abs(_viewer.Height - height) > 0.5) _viewer.Height = height;
    }

    private void ApplySnapshotBounds()
    {
        if (_updatingLayout || _closing) return;
        _updatingLayout = true;
        try
        {
            var note = _vm.Snapshot;
            if (Left != note.X) Left = note.X;
            if (Top != note.Y) Top = note.Y;
            if (Width != note.Width) Width = note.Width;
            if (Height != (note.IsCollapsed ? 44 : note.Height)) Height = note.IsCollapsed ? 44 : note.Height;
        }
        finally { _updatingLayout = false; }
    }

    private nint WindowMessages(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0232) EndDrag?.Invoke(this);
        if (message == 0x0216 && Moving is not null)
        {
            var rectangle = Marshal.PtrToStructure<ScreenRect>(lParam);
            rectangle = Moving(this, rectangle);
            Marshal.StructureToPtr(rectangle, lParam, false);
            handled = true;
            return 1;
        }
        return 0;
    }

    internal void MovePhysical(int x, int y)
    {
        NativeWindows.Move(Handle, x, y);
        // Hidden group members may have an HWND but have not received Loaded yet.
        _vm.SetBounds(Left, Top, Width, _vm.IsCollapsed ? _vm.Snapshot.Height : Height);
    }

    internal void SetGroupAppearance(string? color, bool selected, bool canMove)
    {
        _frame.BorderBrush = color is null ? Brushes.Transparent : (Brush)new BrushConverter().ConvertFromString(color)!;
        _frame.BorderThickness = new Thickness(selected ? 3 : 2);
        _titleArea.Cursor = canMove ? System.Windows.Input.Cursors.SizeAll : System.Windows.Input.Cursors.No;
    }

    internal void SetConnectionState(bool selected, bool targeted)
    {
        _connectionFrame.BorderBrush = selected || targeted ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 164, 255)) : Brushes.Transparent;
        _linkHandles.SetState(selected, targeted);
        _formatting.SetSelected(selected);
    }

    public void SaveBounds()
    {
        FinishTitleEdit(true);
        if (!_ready || _updatingLayout || _closing || WindowState != WindowState.Normal) return;
        _vm.SetBounds(Left, Top, Width, _vm.IsCollapsed ? _vm.Snapshot.Height : Height);
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    public void EnsureVisibleOnScreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(handle, out var rect)) return;
        var bounds = System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        var screen = Forms.Screen.FromRectangle(bounds);
        var area = screen.WorkingArea;
        var width = Math.Min(bounds.Width, area.Width);
        var height = Math.Min(bounds.Height, area.Height);
        var x = Math.Clamp(bounds.X, area.Left, area.Right - width);
        var y = Math.Clamp(bounds.Y, area.Top, area.Bottom - height);
        if (bounds.X != x || bounds.Y != y || width != bounds.Width || height != bounds.Height)
            SetWindowPos(handle, 0, x, y, width, height, 0x0004 | 0x0010);
    }

    public void Reveal()
    {
        _vm.SetHidden(false);
        Show(); EnsureVisibleOnScreen();
        Activate();
    }

    public void HideNote()
    {
        SaveBounds();
        _vm.SetHidden(true);
        Hide();
    }

    public void ClosePermanently() { _closing = true; Close(); }
}
