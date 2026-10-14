using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using DesktopNotes.Application;
using DesktopNotes.Domain;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace DesktopNotes.Presentation;

internal sealed class MarkdownTableView : Grid, IDisposable
{
    private readonly LiveMarkdownEditor _parent;
    private readonly Grid _cells = new();
    private readonly Dictionary<(int Row, int Column), LiveMarkdownEditor> _editors = [];
    private bool _syncing;
    private LiveMarkdownEditor? _active;
    private double _horizontalTarget;
    internal MarkdownTableModel Model { get; private set; }
    internal ScrollViewer Scroll { get; }
    internal LiveMarkdownEditor? ActiveCell => _parent.IsReadOnly ? null : _active;
    internal void ClearFocus() => _active = null;
    internal IEnumerable<LiveMarkdownEditor> Cells => _editors.Values;

    internal MarkdownTableView(LiveMarkdownEditor parent, MarkdownTableModel model)
    {
        _parent = parent; Model = model;
        ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = new GridLength(12) });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new GridLength(12) });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DesktopNotes;component/TableStyles.xaml", UriKind.Relative) });
        Scroll = new ScrollViewer { Content = _cells, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Style = (System.Windows.Style)Resources["TableScrollViewer"] };
        SetColumn(Scroll, 0); Children.Add(Scroll);
        Scroll.ScrollChanged += (_, _) => { _horizontalTarget = Scroll.HorizontalOffset; _parent.TableControls?.UpdatePointer(null); };
        Sync(model);
    }

    internal void Sync(MarkdownTableModel model)
    {
        _syncing = true;
        try
        {
            var rebuild = _editors.Count != model.Rows.Length * model.ColumnCount || Model.ColumnCount != model.ColumnCount || Model.Rows.Length != model.Rows.Length;
            Model = model;
            if (rebuild)
            {
                _active = null;
                foreach (var editor in _editors.Values) editor.Dispose();
                _editors.Clear(); _cells.Children.Clear(); _cells.RowDefinitions.Clear(); _cells.ColumnDefinitions.Clear();
                for (var c = 0; c < model.ColumnCount; c++) _cells.ColumnDefinitions.Add(new());
                for (var r = 0; r < model.Rows.Length; r++) _cells.RowDefinitions.Add(new() { Height = GridLength.Auto });
                for (var r = 0; r < model.Rows.Length; r++) for (var c = 0; c < model.ColumnCount; c++)
                {
                    var row = r; var column = c;
                    var editor = new LiveMarkdownEditor(false) { Padding = new Thickness(0), WordWrap = false,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                    editor.OpenWebLinkRequested += _parent.FollowWebLink;
                    editor.OpenNoteRequested += _parent.FollowNoteLink;
                    editor.TextArea.GotKeyboardFocus += (_, args) => { if (args.NewFocus == editor.TextArea) { _parent.Tables.ClearFocus(); _parent.Tables.ClearSelectionsExcept(editor); _parent.Select(_parent.CaretOffset, 0); _active = editor; } };
                    editor.TextChanged += (_, _) => SaveCell(row, column, editor);
                    editor.PreviewKeyDown += (_, e) =>
                    {
                        if (Navigate(e.Key, Keyboard.Modifiers)) e.Handled = true;
                        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Z or Key.Y)
                        { e.Handled = true; if (e.Key == Key.Z) _parent.Undo(); else _parent.Redo(); }
                    };
                    var border = new Border { Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(c == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1), Child = editor };
                    SetRow(border, r); SetColumn(border, c); _cells.Children.Add(border); _editors[(r, c)] = editor;
                }
            }
            foreach (var (position, editor) in _editors)
            {
                var value = model.Rows[position.Row][position.Column];
                if (editor.Text != value)
                {
                    var caret = Math.Min(editor.CaretOffset, value.Length);
                    editor.Text = value; editor.Select(caret, 0);
                }
            }
            UpdateAppearance();
        }
        finally { _syncing = false; }
    }

    private void SaveCell(int row, int column, LiveMarkdownEditor editor)
    {
        if (_syncing || _parent.IsReadOnly) return;
        var rows = Model.Rows.Select(values => values.ToArray()).ToArray();
        rows[row][column] = editor.Text;
        var replacement = MarkdownTableEditing.Serialize(Model with { Rows = rows });
        var original = _parent.Document.GetText(Model.Start, Model.Length);
        var prefix = 0; while (prefix < original.Length && prefix < replacement.Length && original[prefix] == replacement[prefix]) prefix++;
        var suffix = 0; while (suffix < original.Length - prefix && suffix < replacement.Length - prefix && original[^(suffix + 1)] == replacement[^(suffix + 1)]) suffix++;
        using (_parent.Document.RunUpdate()) _parent.Document.Replace(Model.Start + prefix, original.Length - prefix - suffix, replacement.Substring(prefix, replacement.Length - prefix - suffix));
    }

    internal void UpdateAppearance()
    {
        var theme = _parent.Theme;
        Resources["DesktopNotes.EditorScrollThumb"] = theme.ScrollThumb;
        foreach (var (position, editor) in _editors)
        {
            editor.SetAppearance(_parent.Appearance with { FontSize = _parent.FontSize, IsBold = position.Row == 0 || _parent.Appearance.IsBold,
                TextAlignment = Model.Alignment[position.Column].StartsWith(':') && Model.Alignment[position.Column].EndsWith(':') ? NoteTextAlignment.Center :
                    Model.Alignment[position.Column].EndsWith(':') ? NoteTextAlignment.Right : NoteTextAlignment.Left });
            // DefaultLineHeight can still refer to the previous font until the
            // next layout pass. Measure the current styled text instead.
            var minimum = Math.Max(1, editor.Document.LineCount) * editor.FontSize * editor.FontFamily.LineSpacing;
            var content = InlineMarkdownMetrics.MeasureHeight(editor, new(0, editor.Document.TextLength));
            editor.Height = Math.Ceiling(Math.Max(minimum, content)) + 2;
            ((Border)editor.Parent).BorderBrush = theme.CodeOutline.Brush;
        }
        UpdateEditingState();
        var width = 0d;
        for (var c = 0; c < Model.ColumnCount; c++)
        {
            var natural = _editors.Where(pair => pair.Key.Column == c)
                .Max(pair => InlineMarkdownMetrics.Measure(pair.Value, new(0, pair.Value.Document.TextLength)));
            var size = Math.Max(100 * _parent.FontSize / 14, natural + 20);
            _cells.ColumnDefinitions[c].Width = new GridLength(size); width += size;
        }
        Width = Math.Min(width + 12, Math.Max(70, _parent.TextArea.TextView.ActualWidth - 2));
    }

    internal void UpdateEditingState()
    {
        foreach (var editor in _editors.Values)
        {
            editor.IsReadOnly = _parent.IsReadOnly;
            editor.Focusable = !editor.IsReadOnly; editor.TextArea.Focusable = !editor.IsReadOnly;
            if (editor.IsReadOnly) editor.TextArea.Caret.Hide();
        }
    }

    internal bool Navigate(Key key, ModifierKeys modifiers)
    {
        if (_active is null || key != Key.Tab && !(key == Key.Enter && modifiers == ModifierKeys.None)) return false;
        var position = _editors.First(pair => pair.Value == _active).Key;
        var next = position.Row * Model.ColumnCount + position.Column + (key == Key.Tab ? modifiers == ModifierKeys.Shift ? -1 : 1 : Model.ColumnCount);
        if (next >= Model.Rows.Length * Model.ColumnCount)
        {
            var row = next / Model.ColumnCount; var column = next % Model.ColumnCount;
            _parent.Tables.Expand(Model.Start, TableExpansion.Row); _parent.Tables.Get(Model.Start)?.FocusCell(row, column);
        }
        else if (next >= 0) FocusCell(next / Model.ColumnCount, next % Model.ColumnCount);
        return true;
    }

    internal void FocusCell(int row, int column)
    {
        if (!_editors.TryGetValue((row, column), out var editor) || _parent.IsReadOnly) return;
        _parent.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_editors.ContainsValue(editor)) return;
            editor.TextArea.Focus(); editor.Select(0, 0); editor.BringIntoView();
        }));
    }
    internal bool ScrollHorizontal(int delta, bool nativeHorizontal)
    {
        if (Scroll.ScrollableWidth <= 0) return false;
        var distance = delta / 120d * _parent.FontSize * 3;
        _horizontalTarget = Math.Clamp(_horizontalTarget + (nativeHorizontal ? distance : -distance), 0, Scroll.ScrollableWidth);
        Scroll.ScrollToHorizontalOffset(_horizontalTarget);
        return true;
    }
    internal Rect? BodyBounds()
    {
        if (!IsVisible || Scroll.ActualHeight <= 0) return null;
        return new Rect(Scroll.TranslatePoint(new Point(), _parent.TextArea.TextView), new Size(Scroll.ActualWidth, Scroll.ActualHeight));
    }
    public void Dispose() { _active = null; foreach (var editor in _editors.Values) editor.Dispose(); _editors.Clear(); }
}

internal sealed class TableViewRegistry : IDisposable, IReadOnlySectionProvider
{
    private readonly LiveMarkdownEditor _editor;
    private readonly bool _enabled;
    private readonly Dictionary<int, MarkdownTableView> _views = [];
    private readonly List<CollapsedLineSection> _collapsed = [];
    private HwndSource? _source;
    internal IEnumerable<MarkdownTableView> Views => _views.Values;
    internal LiveMarkdownEditor? ActiveCell => _views.Values.Select(view => view.ActiveCell).FirstOrDefault(cell => cell is not null);
    internal TableViewRegistry(LiveMarkdownEditor editor, bool enabled)
    {
        _editor = editor; _enabled = enabled;
        if (enabled)
        {
            editor.TextArea.GotKeyboardFocus += OnMainFocus; editor.TextArea.ReadOnlySectionProvider = this;
            editor.PreviewMouseWheel += OnWheel; editor.Loaded += OnLoaded; editor.Unloaded += OnUnloaded;
        }
    }
    internal void ClearFocus() { foreach (var view in _views.Values) view.ClearFocus(); }
    internal void ClearSelectionsExcept(LiveMarkdownEditor? active)
    {
        foreach (var cell in _views.Values.SelectMany(view => view.Cells))
            if (cell != active && cell.SelectionLength > 0) cell.Select(cell.CaretOffset, 0);
    }
    private void OnMainFocus(object sender, KeyboardFocusChangedEventArgs args)
    {
        if (args.NewFocus == _editor.TextArea) { ClearFocus(); ClearSelectionsExcept(null); }
        UpdateCaret();
    }
    internal void UpdateCaret()
    {
        if (!_enabled) return;
        var nestedFocus = _editor.TextArea.IsKeyboardFocusWithin && !_editor.TextArea.IsKeyboardFocused;
        _editor.TextArea.Caret.CaretBrush = nestedFocus ? Brushes.Transparent : _editor.Theme.Foreground;
        if (nestedFocus) _editor.TextArea.Caret.Hide();
    }
    internal bool HandleWheel(Point point, int delta, ModifierKeys modifiers, bool nativeHorizontal = false)
    {
        if (!nativeHorizontal && modifiers != ModifierKeys.Shift) return false;
        var view = _views.Values.FirstOrDefault(view => view.BodyBounds() is { } bounds && bounds.Contains(point));
        return view?.ScrollHorizontal(delta, nativeHorizontal) ?? false;
    }
    private void OnWheel(object sender, MouseWheelEventArgs args)
    {
        if (HandleWheel(args.GetPosition(_editor.TextArea.TextView), args.Delta, Keyboard.Modifiers)) args.Handled = true;
    }
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        var source = PresentationSource.FromVisual(_editor) as HwndSource;
        if (_source == source) return;
        _source?.RemoveHook(OnWindowMessage); _source = source; _source?.AddHook(OnWindowMessage);
    }
    private void OnUnloaded(object sender, RoutedEventArgs args) { _source?.RemoveHook(OnWindowMessage); _source = null; }
    private nint OnWindowMessage(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int MouseHorizontalWheel = 0x020E;
        if (handled || message != MouseHorizontalWheel || !_editor.IsVisible) return 0;
        var delta = (short)((long)wParam >> 16);
        var point = new Point((short)((long)lParam & 0xffff), (short)((long)lParam >> 16));
        handled = HandleWheel(_editor.TextArea.TextView.PointFromScreen(point), delta, ModifierKeys.None, nativeHorizontal: true);
        return 0;
    }
    public bool CanInsert(int offset) => !_editor.IsReadOnly && !_views.Values.Any(view => offset > view.Model.Start && offset < view.Model.Start + view.Model.Length);
    public IEnumerable<ISegment> GetDeletableSegments(ISegment segment)
    {
        if (_editor.IsReadOnly) yield break;
        var cursor = segment.Offset;
        foreach (var view in _views.Values.OrderBy(view => view.Model.Start))
        {
            var start = view.Model.Start; var end = start + view.Model.Length;
            if (end <= cursor || start >= segment.EndOffset || segment.Offset <= start && segment.EndOffset >= end) continue;
            if (cursor < start) yield return new TextSegment { StartOffset = cursor, Length = start - cursor };
            cursor = Math.Min(segment.EndOffset, end);
        }
        if (cursor < segment.EndOffset) yield return new TextSegment { StartOffset = cursor, Length = segment.EndOffset - cursor };
    }
    internal bool AllowsEdit(int start, int length) => !_views.Values.Any(view =>
        length == 0 ? start > view.Model.Start && start < view.Model.Start + view.Model.Length :
        start < view.Model.Start + view.Model.Length && start + length > view.Model.Start &&
        !(start <= view.Model.Start && start + length >= view.Model.Start + view.Model.Length));
    internal void Reset()
    {
        foreach (var section in _collapsed) section.Uncollapse(); _collapsed.Clear();
        foreach (var view in _views.Values) view.Dispose(); _views.Clear();
    }
    internal bool EnterAtCaret(bool backwards)
    {
        var view = _views.Values.FirstOrDefault(view => _editor.CaretOffset >= view.Model.Start && _editor.CaretOffset <= view.Model.Start + view.Model.Length);
        if (view is null) return false;
        view.FocusCell(backwards ? view.Model.Rows.Length - 1 : 0, backwards ? view.Model.ColumnCount - 1 : 0); return true;
    }
    internal bool Leave()
    {
        var view = _views.Values.FirstOrDefault(view => view.ActiveCell is not null);
        if (view is null) return false;
        var end = view.Model.Start + view.Model.Length;
        var line = _editor.Document.GetLineByOffset(end);
        if (line.NextLine is { } next)
        {
            if (InlineMarkdownLayout.TableDisplayEnd(_editor.Text, end) > end && next.NextLine is { } following) next = following;
            _editor.Select(next.Offset, 0);
        }
        else _editor.ApplyEdit(new(end, 0, view.Model.NewLine, view.Model.NewLine.Length, 0));
        ClearFocus(); _editor.TextArea.Focus(); return true;
    }
    internal bool Navigate(Key key, ModifierKeys modifiers) => _views.Values.Any(view => view.ActiveCell is not null && view.Navigate(key, modifiers));
    internal void Sync()
    {
        if (!_enabled) return;
        foreach (var section in _collapsed) section.Uncollapse(); _collapsed.Clear();
        var models = MarkdownTableEditing.ReadTables(_editor.Text);
        foreach (var key in _views.Keys.Except(models.Select(model => model.Start)).ToArray()) { _views[key].Dispose(); _views.Remove(key); }
        foreach (var model in models)
        {
            if (_views.TryGetValue(model.Start, out var view)) view.Sync(model);
            else _views.Add(model.Start, new MarkdownTableView(_editor, model));
            var first = _editor.Document.GetLineByOffset(model.Start);
            var end = model.Start + model.Length;
            var displayEnd = InlineMarkdownLayout.TableDisplayEnd(_editor.Text, end);
            var last = _editor.Document.GetLineByOffset(displayEnd > end ? displayEnd : end - 1);
            if (first.NextLine is { } next && last.LineNumber > first.LineNumber)
                _collapsed.Add(_editor.TextArea.TextView.CollapseLines(next, last));
        }
    }
    internal MarkdownTableView? Get(int start) => _views.GetValueOrDefault(start);
    internal bool Contains(int offset) => _views.Values.Any(view => offset >= view.Model.Start && offset < view.Model.Start + view.Model.Length);
    internal void UpdateAppearance() { foreach (var view in _views.Values) view.UpdateAppearance(); }
    internal void UpdateEditingState() { foreach (var view in _views.Values) view.UpdateEditingState(); }
    internal void Expand(int start, TableExpansion expansion)
    {
        if (_editor.IsReadOnly || Get(start) is not { } view) return;
        var row = expansion == TableExpansion.Row ? view.Model.Rows.Length : 0;
        var column = expansion == TableExpansion.ColumnRight ? view.Model.ColumnCount : 0;
        var edit = MarkdownTableEditing.Expand(_editor.Text, start, expansion);
        if (edit is null) return;
        _editor.ApplyEdit(edit); Get(start)?.FocusCell(row, column);
    }
    internal bool ProtectDelete(Key key)
    {
        if (key is not (Key.Back or Key.Delete)) return false;
        foreach (var view in _views.Values)
        {
            var start = view.Model.Start; var end = start + view.Model.Length;
            var selectedEnd = _editor.SelectionStart + _editor.SelectionLength;
            if (_editor.SelectionLength > 0 && _editor.SelectionStart < end && selectedEnd > start)
            {
                if (_editor.SelectionStart <= start && selectedEnd >= end) continue;
                _editor.Select(Math.Min(start, _editor.SelectionStart), Math.Max(end, selectedEnd) - Math.Min(start, _editor.SelectionStart)); return true;
            }
            if (_editor.SelectionLength == 0 && (key == Key.Delete && _editor.CaretOffset == start || key == Key.Back && _editor.CaretOffset == end || _editor.CaretOffset > start && _editor.CaretOffset < end))
            { _editor.Select(start, end - start); return true; }
        }
        return false;
    }
    internal bool ProtectTyping(string text)
    {
        foreach (var view in _views.Values)
        {
            var start = view.Model.Start; var end = start + view.Model.Length;
            var previous = _editor.Document.GetLineByOffset(start).PreviousLine;
            if (previous is not null && _editor.SelectionStart >= previous.Offset &&
                _editor.SelectionStart + _editor.SelectionLength <= previous.EndOffset &&
                string.IsNullOrWhiteSpace(_editor.Document.GetText(previous)))
            {
                var offset = _editor.SelectionStart - previous.Offset;
                var original = _editor.Document.GetText(previous);
                var replacement = original.Remove(offset, _editor.SelectionLength).Insert(offset, text) + view.Model.NewLine;
                _editor.ApplyEdit(new(previous.Offset, previous.Length, replacement, offset + text.Length, 0)); return true;
            }
            if (_editor.SelectionLength != 0) continue;
            if (_editor.CaretOffset == start) { _editor.ApplyEdit(new(start, 0, text + view.Model.NewLine + view.Model.NewLine, text.Length, 0)); return true; }
            if (_editor.CaretOffset == end) { _editor.ApplyEdit(new(end, 0, view.Model.NewLine + view.Model.NewLine + text, view.Model.NewLine.Length * 2 + text.Length, 0)); return true; }
            // Markdig requires a paragraph boundary after the table. Keep that
            // boundary when typing on the first empty source line below it.
            var next = _editor.Document.GetLineByOffset(end).NextLine;
            if (next is not null && _editor.CaretOffset == next.Offset)
            {
                _editor.ApplyEdit(new(next.Offset, 0, view.Model.NewLine + text, view.Model.NewLine.Length + text.Length, 0)); return true;
            }
        }
        return false;
    }
    public void Dispose()
    {
        if (_enabled)
        {
            _editor.TextArea.GotKeyboardFocus -= OnMainFocus; _editor.PreviewMouseWheel -= OnWheel;
            _editor.Loaded -= OnLoaded; _editor.Unloaded -= OnUnloaded;
            _source?.RemoveHook(OnWindowMessage); _source = null;
        }
        Reset();
    }
}
