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
    private readonly Grid _body = new();
    private readonly Grid _root = new() { Margin = new Thickness(7.5) };
    private readonly RowDefinition _captionRow = new() { Height = new GridLength(0) };
    private readonly LiveMarkdownEditor _editor;
    private readonly Border _titleArea;
    private readonly TextBlock _titleLabel;
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
    internal LiveMarkdownEditor MarkdownEditor => _editor;
    internal bool OwnsInteractionWindow(nint handle) => handle != 0 &&
        (handle == Handle || NativeWindows.RootOwner(handle) == Handle || _formatting.OwnsWindow(handle));
    internal Action? CommitSelection { get; set; }
    internal Action<NoteWindow, ContextMenu>? PopulateBoardMenu { get; set; }
    internal Action<NoteWindow, NoteSide>? LinkDragStarted { get; set; }
    internal Action? LinkDragUpdated { get; set; }
    internal Action? LinkDragCompleted { get; set; }
    internal Func<bool>? CancelInteraction { get; set; }
    internal event Action? GeometryChanged;
    private bool _updatingLayout;
    private bool _ready;
    private bool _closing;

    public NoteWindow(NoteViewModel vm, IWindowLayerController layers)
    {
        _vm = vm; _layers = layers;
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

        var root = _root;
        root.RowDefinitions.Add(_captionRow);
        root.RowDefinitions.Add(new RowDefinition());
        var header = new Grid { Height = 34, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetRowSpan(header, 2);
        Panel.SetZIndex(header, 1);
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _titleArea = new Border
        {
            Background = Brushes.Transparent, Padding = new Thickness(8, 0, 4, 0), Focusable = true,
            Height = 6, VerticalAlignment = VerticalAlignment.Top,
            Cursor = System.Windows.Input.Cursors.SizeAll
        };
        _titleLabel = new TextBlock { Name = "CollapsedTitle", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = vm.IsCollapsed ? Visibility.Visible : Visibility.Collapsed };
        _titleLabel.SetBinding(TextBlock.TextProperty, new Binding(nameof(vm.Title)));
        _titleArea.Child = _titleLabel;
        _titleArea.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _titleArea.Focus();
            if (_vm.IsPinned || !(BeginDrag?.Invoke(this) ?? true)) return;
            try { DragMove(); }
            finally { EndDrag?.Invoke(this); }
        };
        header.Children.Add(_titleArea);
        var collapse = MakeButton("−", "Свернуть / развернуть"); collapse.Command = vm.ToggleCollapsed;
        collapse.VerticalAlignment = VerticalAlignment.Top;
        collapse.Height = 28;
        collapse.Width = 28;
        collapse.Padding = new Thickness(0);
        Grid.SetColumn(collapse, 1); header.Children.Add(collapse);
        root.Children.Add(header);

        _editor = new LiveMarkdownEditor() { Text = vm.Markdown, ContextMenu = ContextMenu };
        _editor.TextChanged += (_, _) => { if (_editor.Text != _vm.Markdown) _vm.Markdown = _editor.Text; };
        _editor.OpenNoteRequested += destination => OpenNoteLink?.Invoke(destination);
        _editor.OpenWebLinkRequested += uri =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException)
            { System.Windows.MessageBox.Show(this, "Не удалось открыть ссылку: " + error.Message, "DesktopNotes"); }
        };
        _editor.ContentGeometryChanged += UpdateEditorHeight;
        _body.Children.Add(_editor);
        // Keep editable text clear of the collapse control without a blank caption row.
        _body.Margin = new Thickness(0, 0, 28, 0);
        Grid.SetRow(_body, 1); root.Children.Add(_body);
        var shell = new Grid();
        shell.Children.Add(root);
        shell.Children.Add(_frame);
        shell.Children.Add(_connectionFrame);
        _linkHandles = new EdgeLinkHandles(this,
            side => LinkDragStarted?.Invoke(this, side),
            () => LinkDragUpdated?.Invoke(), () => LinkDragCompleted?.Invoke(), () => CancelInteraction?.Invoke(),
            () => InteractionDeactivated?.Invoke(this));
        shell.Children.Add(_linkHandles);
        Content = shell;
        _formatting = new NoteFormattingToolbar(this, vm);
        ApplyTextFormatting();
        _body.SizeChanged += (_, _) => UpdateEditorHeight();
        PreviewMouseDown += (_, _) =>
        {
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0) CommitSelection?.Invoke();
        };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (LiveMarkdownEditor.IsInteractivePreviewElement(e.OriginalSource as DependencyObject)) return;
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0 && _editor.IsMouseOver && _editor.HandleInteractiveClick(e))
            { e.Handled = true; return; }
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
            if (HandleWindowShortcut(key, System.Windows.Input.Keyboard.Modifiers)) { e.Handled = true; return; }
            if (key == System.Windows.Input.Key.Escape && _editor.HandleEscape()) { e.Handled = true; return; }
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
        Closed += (_, _) => { _editor.Dispose(); _formatting.Dispose(); _linkHandles.Dispose(); vm.PropertyChanged -= OnViewModelChanged; vm.Dispose(); _source?.RemoveHook(WindowMessages); };
    }

    private static Button MakeButton(string text, string accessibleName)
    {
        var button = new Button { Content = text, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(8, 3, 8, 3), MinWidth = 28, Focusable = false };
        AutomationProperties.SetName(button, accessibleName);
        return button;
    }

    internal void ToggleBoldFormatting()
    {
        if (_editor.SelectionLength == 0) { _vm.IsBold = !_vm.IsBold; return; }
        _editor.ToggleBoldSelection();
    }

    internal Action<string>? OpenNoteLink { get; set; }
    internal Action? NewNoteRequested { get; set; }
    internal Action? SaveRequested { get; set; }
    internal bool HandleWindowShortcut(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers)
    {
        if (modifiers != System.Windows.Input.ModifierKeys.Control) return false;
        if (key == System.Windows.Input.Key.N && NewNoteRequested is not null) { NewNoteRequested(); return true; }
        if (key == System.Windows.Input.Key.S && SaveRequested is not null) { SaveBounds(); SaveRequested(); return true; }
        if (key == System.Windows.Input.Key.W) { HideNote(); return true; }
        return false;
    }
    internal void InsertNoteLink(Guid id, string title) => _editor.InsertNoteLink(id, title);

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
        _editor?.AppendFormattingMenu(menu);
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
                ApplyTextFormatting(); break;
            case nameof(NoteViewModel.Title): Title = _vm.Title; break;
            case nameof(NoteViewModel.Markdown):
                if (_editor.Text != _vm.Markdown)
                {
                    var caret = _editor.CaretOffset;
                    _editor.Text = _vm.Markdown;
                    _editor.CaretOffset = Math.Min(caret, _editor.Text.Length);
                }
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
            _titleLabel.Visibility = _vm.IsCollapsed ? Visibility.Visible : Visibility.Collapsed;
            _captionRow.Height = new GridLength(_vm.IsCollapsed ? 34 : 0);
            _titleArea.Height = _vm.IsCollapsed ? 34 : 6;
            _root.Margin = new Thickness(_vm.IsCollapsed ? 5 : 7.5);
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
        _editor.SetAppearance(_vm.Snapshot);
        _editor.VerticalAlignment = VerticalAlignmentFor(_vm.VerticalAlignment);
        UpdateEditorHeight();
    }

    internal static TextAlignment TextAlignmentFor(NoteTextAlignment alignment) => alignment switch
    { NoteTextAlignment.Center => TextAlignment.Center, NoteTextAlignment.Right => TextAlignment.Right,
        NoteTextAlignment.Justify => TextAlignment.Justify, _ => TextAlignment.Left };
    private static VerticalAlignment VerticalAlignmentFor(NoteVerticalAlignment alignment) => alignment switch
    { NoteVerticalAlignment.Center => VerticalAlignment.Center, NoteVerticalAlignment.Bottom => VerticalAlignment.Bottom, _ => VerticalAlignment.Top };

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
                if (block is System.Windows.Documents.Table table) foreach (var group in table.RowGroups) foreach (var row in group.Rows) foreach (var cell in row.Cells) Format(cell.Blocks);
            }
        }
        Format(document.Blocks);
    }

    private void UpdateEditorHeight()
    {
        if (_body.ActualHeight <= 0 || _editor.ExtentHeight <= 0) return;
        var height = Math.Max(1, Math.Min(_body.ActualHeight, _editor.ExtentHeight + _editor.Padding.Top + _editor.Padding.Bottom));
        if (double.IsNaN(_editor.Height) || Math.Abs(_editor.Height - height) > 0.5) _editor.Height = height;
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
