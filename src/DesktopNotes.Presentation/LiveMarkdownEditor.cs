using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using DesktopNotes.Application;
using DesktopNotes.Domain;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DesktopNotes.Presentation;

internal sealed class LiveMarkdownEditor : TextEditor, IDisposable
{
    private readonly MarkdownPipeline _pipeline;
    private readonly bool _inlineOnly;
    private Note _appearance = new();
    internal NoteTheme Theme { get; private set; } = NoteThemes.Get(NoteColor.Yellow);
    private List<PreviewBlock> _blocks = [];
    private string _parsedText = "";
    internal InlineMarkdownLayout Layout { get; private set; } = new();
    private bool _disposed;
    private bool _previewDisabled;
    private bool _noteTitle;
    private bool _normalizingTitle;
    private bool _redirectingFenceCaret;
    private bool _caretScrollPending;
    internal sealed record PreviewBlock(int Start, int Length, bool IsCode = false, bool IsTable = false);
    internal IReadOnlyList<PreviewBlock> PreviewBlocks => _blocks;
    internal event Action<string>? OpenNoteRequested;
    internal event Action<Uri>? OpenWebLinkRequested;
    internal event Action? ContentGeometryChanged;
    private readonly InlineGenerator _generator;
    private readonly SearchPanel _search;
    internal SmoothScrollController SmoothScroll { get; }
    internal TableEdgeControls TableControls { get; }
    internal TableViewRegistry Tables { get; }
    internal Note Appearance => _appearance;
    internal LiveMarkdownEditor EditingTarget => Tables.ActiveCell ?? this;

    internal LiveMarkdownEditor(bool tables = true)
    {
        _pipeline = MarkdownPipelines.Create(tables); _inlineOnly = !tables;
        Tables = new TableViewRegistry(this, tables);
        Background = Brushes.Transparent; BorderThickness = new Thickness(0); Padding = new Thickness(6);
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        WordWrap = true; ShowLineNumbers = false;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto; HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DesktopNotes;component/EditorScrollBar.xaml", UriKind.Relative) });
        SetTheme(Theme);
        SmoothScroll = new SmoothScrollController(this);
        TableControls = new TableEdgeControls(this, tables);
        Options.CutCopyWholeLine = true; Options.ConvertTabsToSpaces = true; Options.IndentationSize = 2;
        Options.InheritWordWrapIndentation = true;
        Options.ShowSpaces = false; Options.ShowTabs = false; Options.ShowEndOfLine = false; Options.ShowBoxForControlCharacters = false;
        TextArea.SelectionBorder = null; TextArea.SelectionCornerRadius = 0;
        TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(100, 90, 160, 240));
        TextArea.SelectionForeground = null;
        Options.EnableHyperlinks = false; Options.EnableEmailHyperlinks = false;
        TextArea.TextView.ElementGenerators.Add(new AlignmentGenerator(this));
        _generator = new InlineGenerator(this); TextArea.TextView.ElementGenerators.Add(_generator);
        TextArea.TextView.LineTransformers.Add(new InlineStyling(this));
        TextArea.TextView.BackgroundRenderers.Add(new CodeBlockBackground(this));
        TextArea.TextView.BackgroundRenderers.Add(new InlineBackground(this));
        TextChanged += OnTextChanged;
        Loaded += OnEditorLoaded;
        SizeChanged += (_, _) => { Tables.UpdateAppearance(); TextArea.TextView.Redraw(); };
        TextArea.TextView.VisualLinesChanged += (_, _) => ContentGeometryChanged?.Invoke();
        PreviewKeyDown += OnKey;
        PreviewTextInput += OnTextInput;
        DataObject.AddPastingHandler(this, OnPaste);
        TextArea.PreviewMouseLeftButtonDown += OnEditorMouseDown;
        TextArea.Caret.PositionChanged += OnFenceCaretChanged;
        TextArea.SelectionChanged += OnFenceCaretChanged;
        TextArea.TextEntering += OnTableTextEntering;
        TextArea.TextEntering += OnFenceTextEntering;
        _search = SearchPanel.Install(this); _search.Localization = new LocalizedSearch(); _search.MarkerBrush = Theme.Highlight;
        System.Windows.Automation.AutomationProperties.SetName(this, UiText.Get("Текст заметки — Markdown Live Preview"));
        System.ComponentModel.PropertyChangedEventManager.AddHandler(UiText.Current, OnLanguageChanged, string.Empty);
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnLanguageChanged(sender, e));
            return;
        }
        if (_disposed) return;
        _search.Localization = new LocalizedSearch();
        System.Windows.Automation.AutomationProperties.SetName(this, UiText.Get("Текст заметки — Markdown Live Preview"));
    }

    private void OnTextChanged(object? sender, EventArgs args)
    {
        NormalizeTitle();
        RefreshPreview();
    }
    private void OnEditorLoaded(object sender, RoutedEventArgs e)
    {
        void StyleScrollbars(DependencyObject root)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is System.Windows.Controls.Primitives.ScrollBar { Orientation: Orientation.Vertical } bar)
                {
                    // Override the system width assigned locally by the viewer template.
                    bar.Style = (System.Windows.Style)Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)];
                    bar.Width = 6; bar.MinWidth = 0; bar.Margin = new Thickness(0);
                    System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(bar, true);
                }
                else StyleScrollbars(child);
            }
        }
        StyleScrollbars(this);
    }
    private void OnTableTextEntering(object sender, TextCompositionEventArgs args)
    {
        if (!IsReadOnly && Tables.ProtectTyping(args.Text)) args.Handled = true;
    }
    private void OnFenceTextEntering(object sender, TextCompositionEventArgs e)
    {
        if (IsReadOnly || SelectionLength != 0 || e.Text.Contains('\n')) return;
        var line = Document.GetLineByOffset(CaretOffset);
        if (CaretOffset != line.EndOffset || Layout.CodeFrames.Any(frame => CaretOffset >= frame.Start && CaretOffset < frame.End)) return;
        if (!Layout.CodeFrames.Any(frame => frame.HasClosingFence && frame.Start > CaretOffset)) return;
        var original = Document.GetText(line);
        if (Regex.IsMatch(original, @"^ {0,3}(?:`{3,}|~{3,})")) return;
        var match = Regex.Match(original + e.Text, @"^(?<indent> {0,3})(?<fence>`{3,}|~{3,})(?<info>[^\r\n]*)$");
        if (!match.Success || match.Groups["fence"].Value[0] == '`' && match.Groups["info"].Value.Contains('`')) return;
        var newline = Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        // Insert both fences in the same edit: the next block's opening cannot
        // become this one's closing, and Undo removes the automatic fence too.
        ApplyEdit(new(CaretOffset, 0, e.Text + newline + match.Groups["indent"].Value + match.Groups["fence"].Value, e.Text.Length, 0));
        e.Handled = true;
    }
    internal void EnableNoteTitle()
    {
        _noteTitle = true;
        NormalizeTitle();
        RefreshPreview();
    }

    private void NormalizeTitle()
    {
        if (!_noteTitle || _normalizingTitle || Text.StartsWith("# ", StringComparison.Ordinal)) return;
        var firstLine = Document.GetText(Document.GetLineByNumber(1));
        var prefix = Regex.Match(firstLine, @"^ {0,3}#{1,6}(?=[ \t]|$)[ \t]*");
        var length = prefix.Success ? prefix.Length : 0;
        var start = SelectionStart; var end = start + SelectionLength;
        int Map(int offset) => offset < length ? 2 : offset + 2 - length;
        _normalizingTitle = true;
        try
        {
            // TextChanged runs inside the document's update/undo group, so restoring
            // the structural title marker remains part of the original edit.
            Document.Replace(0, length, "# ");
            Select(Map(start), Math.Max(0, Map(end) - Map(start)));
        }
        finally { _normalizingTitle = false; }
    }
    private void RefreshPreview()
    {
        if (_disposed) return;
        if (_previewDisabled) { Tables.Reset(); Layout = new(); _blocks.Clear(); TextArea.TextView.Redraw(); return; }
        // Parse only when the document changes. Focus and caret movements must never
        // rebuild the text into a different layout.
        if (_parsedText != Text)
        {
            _parsedText = Text;
            var parsed = Markdig.Markdown.Parse(_inlineOnly ? "x " + _parsedText.Replace('\r', ' ').Replace('\n', ' ') : _parsedText, _pipeline);
            if (_inlineOnly)
            {
                void Shift(ContainerInline container)
                {
                    foreach (var inline in container)
                    {
                        inline.Span = new Markdig.Syntax.SourceSpan(inline.Span.Start - 2, inline.Span.End - 2);
                        if (inline is ContainerInline child) Shift(child);
                    }
                }
                foreach (var block in parsed.OfType<LeafBlock>()) if (block.Inline is { } inline) Shift(inline);
            }
            Layout = InlineMarkdownLayout.Create(_parsedText, parsed, _inlineOnly);
            _blocks = parsed.Where(block => !_inlineOnly).Where(block => block.Span.Start >= 0 && block.Span.Length > 0 && block.Span.End < _parsedText.Length)
                .Select(block =>
                {
                    // Keep navigation ranges inside actual content, excluding trailing delimiters.
                    var end = block.Span.End + 1;
                    while (end > block.Span.Start && _parsedText[end - 1] is '\r' or '\n') end--;
                    return new PreviewBlock(block.Span.Start, end - block.Span.Start, block is Markdig.Syntax.CodeBlock, block is Markdig.Extensions.Tables.Table);
                }).Where(block => block.Length > 0).ToList();
        }
        Tables.Sync();
        TextArea.TextView.Redraw();
    }
    internal void DisablePreview()
    {
        _previewDisabled = true;
        RefreshPreview();
    }

    private void OnEditorMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || !_inlineOnly && IsInteractivePreviewElement(e.OriginalSource as DependencyObject)) return;
        if (HandleInteractiveClick(e)) e.Handled = true;
    }

    private void OnFenceCaretChanged(object? sender, EventArgs e)
    {
        if (_disposed || _redirectingFenceCaret || Document.IsInUpdate || _parsedText != Text || SelectionLength != 0) return;
        var line = Document.GetLineByOffset(CaretOffset);
        foreach (var frame in Layout.CodeFrames)
        {
            if (frame.ContentStart is not { } start) continue;
            var openingLine = Document.GetLineByOffset(frame.Start);
            var closingLine = frame.ClosingStart is { } closing ? Document.GetLineByOffset(closing) : null;
            if (line != openingLine && line != closingLine) continue;
            if (line == openingLine && !Layout.Decorations.Any(part => part.Start == frame.Start && part.Kind == InlineMarkdownLayout.DecorationKind.Hidden)) continue;
            if (frame.ClosingStart == start)
            {
                if (line == openingLine) continue;
                _redirectingFenceCaret = true;
                try { Select(openingLine.EndOffset, 0); }
                finally { _redirectingFenceCaret = false; }
                return;
            }
            var target = line == openingLine ? start : Document.GetLineByOffset(frame.ClosingStart!.Value - 1).EndOffset;
            _redirectingFenceCaret = true;
            try { Select(target, 0); }
            finally { _redirectingFenceCaret = false; }
            return;
        }
    }

    private bool TryLeaveCodeBlock(Key key)
    {
        foreach (var frame in Layout.CodeFrames)
        {
            if (frame.ContentStart is not { } start || frame.ClosingStart is not { } closing || start >= closing) continue;
            var first = Document.GetLineByOffset(start);
            var last = Document.GetLineByOffset(closing - 1);
            var current = Document.GetLineByOffset(CaretOffset);
            if (current != first && current != last) continue;
            var visual = TextArea.TextView.GetOrConstructVisualLine(current);
            var row = visual.GetTextLine(TextArea.Caret.VisualColumn);
            var before = current == first && (key == Key.Up && row == visual.TextLines[0] || key == Key.Left && CaretOffset == start);
            var after = current == last && (key == Key.Down && row == visual.TextLines[^1] || key == Key.Right && CaretOffset == last.EndOffset);
            var opening = Document.GetLineByOffset(frame.Start);
            var ending = Document.GetLineByOffset(closing);
            if (before && opening.PreviousLine is { } previous) { Select(previous.EndOffset, 0); return true; }
            if (after && ending.NextLine is { } next) { Select(next.Offset, 0); return true; }
            if (after && ending.NextLine is null)
            {
                var newline = Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                ApplyEdit(new(ending.EndOffset, 0, newline, newline.Length, 0));
                return true;
            }
        }
        return false;
    }
    internal bool HandleInteractiveClick(MouseButtonEventArgs e)
    {
        if (TryToggleTask(e.GetPosition(this))) return true;
        var position = GetPositionFromPoint(e.GetPosition(this));
        if (position is null) return false;
        CaretOffset = Document.GetOffset(position.Value.Location);
        return OpenLinkAtCaret();
    }
    internal bool TryToggleTask(Point point)
    {
        var position = GetPositionFromPoint(point);
        if (position is null) return false;
        var line = TextArea.TextView.GetOrConstructVisualLine(Document.GetLineByNumber(position.Value.Line));
        var marker = line.Elements.OfType<MarkdownDecorationElement>().FirstOrDefault(element => element.Decoration.Kind == InlineMarkdownLayout.DecorationKind.Task);
        if (marker is null) return false;
        var x = TranslatePoint(point, TextArea.TextView).X + TextArea.TextView.ScrollOffset.X;
        var textLine = line.GetTextLine(marker.VisualColumn);
        var left = textLine.GetDistanceFromCharacterHit(new System.Windows.Media.TextFormatting.CharacterHit(marker.VisualColumn, 0));
        var right = textLine.GetDistanceFromCharacterHit(new System.Windows.Media.TextFormatting.CharacterHit(marker.VisualColumn + 1, 0));
        if (x < left || x >= right) return false;
        ToggleTask(marker.Decoration); return true;
    }
    private void ToggleTask(InlineMarkdownLayout.Decoration part)
    {
        if (IsReadOnly) return;
        var marker = Text.IndexOf('[', part.Start, part.Length);
        if (marker < 0) return;
        using (Document.RunUpdate()) Document.Replace(marker + 1, 1, Text[marker + 1] == ' ' ? "x" : " ");
        TextArea.Focus();
    }

    internal void FollowWebLink(Uri uri) => OpenWebLinkRequested?.Invoke(uri);
    internal void FollowNoteLink(string title) => OpenNoteRequested?.Invoke(title);

    internal void SetAppearance(Note note)
    {
        _appearance = note; FontSize = note.FontSize; FontWeight = note.IsBold ? FontWeights.Bold : FontWeights.Normal;
        SetTheme(NoteThemes.Get(note.Color));
        TextArea.TextView.Redraw();
    }
    internal void SetTheme(NoteTheme theme)
    {
        Theme = theme;
        Foreground = theme.Foreground;
        TextArea.Caret.CaretBrush = theme.Foreground;
        if (_search is not null) _search.MarkerBrush = theme.Highlight;
        Resources["DesktopNotes.EditorScrollThumb"] = theme.ScrollThumb;
        Tables.UpdateAppearance(); Tables.UpdateCaret();
        TextArea.TextView.Redraw();
    }

    internal void ApplyEdit(MarkdownTextEdit edit)
    {
        if (!Tables.AllowsEdit(edit.Start, edit.Length)) return;
        using (Document.RunUpdate()) Document.Replace(edit.Start, edit.Length, edit.Replacement);
        Select(edit.Start + edit.SelectionStart, edit.SelectionLength);
        TextArea.Caret.BringCaretToView();
        if (!_caretScrollPending)
        {
            _caretScrollPending = true;
            // Recheck after the new line has been measured, before the next input.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                _caretScrollPending = false;
                if (!_disposed) TextArea.Caret.BringCaretToView();
            }));
        }
    }
    internal void ToggleBoldSelection()
    {
        var target = EditingTarget;
        target.ApplyEdit(MarkdownEditingCommands.ToggleInline(target.Text, target.SelectionStart, target.SelectionLength, "**"));
    }
    internal void SetHeading(int level) => ApplyEdit(MarkdownEditingCommands.SetHeading(Text, SelectionStart, SelectionLength, level));

    internal bool HandleEscape()
    {
        if (Tables.ActiveCell is { } cell) return cell.HandleEscape() || Tables.Leave();
        if (!_search.IsClosed) { _search.Close(); return true; }
        if (SelectionLength > 0) { Select(CaretOffset, 0); return true; }
        return false;
    }

    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (Tables.ActiveCell is not null || IsReadOnly) return;
        if (e.DataObject.GetData(DataFormats.UnicodeText) is string text && Tables.ProtectTyping(text)) e.CancelCommand();
    }

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (Tables.ActiveCell is not null) return;
        if (!IsReadOnly && Tables.ProtectTyping(e.Text)) { e.Handled = true; return; }
        if (IsReadOnly || e.Text.Length != 1 || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;
        var edit = MarkdownEditingCommands.PairCharacter(Text, SelectionStart, SelectionLength, e.Text[0]);
        if (edit is not null) { ApplyEdit(edit); e.Handled = true; }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (HandleShortcut(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers)) e.Handled = true;
    }
    internal bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (IsReadOnly) return false;
        if (Tables.ActiveCell is { } cell)
        {
            if (key == Key.Escape && modifiers == ModifierKeys.None) return Tables.Leave();
            if (key == Key.Z && modifiers is ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Shift) || key == Key.Y && modifiers == ModifierKeys.Control) { if (key == Key.Z && modifiers == ModifierKeys.Control) Undo(); else Redo(); return true; }
            if (Tables.Navigate(key, modifiers)) return true;
            return cell.HandleShortcut(key, modifiers);
        }
        if (Tables.ProtectDelete(key)) return true;
        if (modifiers == ModifierKeys.None && SelectionLength == 0 && key is Key.Up or Key.Down or Key.Left or Key.Right && TryLeaveCodeBlock(key)) return true;
        if (key == Key.Back && modifiers == ModifierKeys.None && SelectionLength == 0 && TryUnwrapCodeBlock()) return true;
        if (key == Key.Enter && modifiers == ModifierKeys.Shift)
        {
            // A list continuation is explicit in the source; ordinary following
            // text stays unindented, even without an intervening blank line.
            var newline = Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var replacement = newline + Layout.ListContinuationPrefix(Text, SelectionStart);
            ApplyEdit(new(SelectionStart, SelectionLength, replacement, replacement.Length, 0));
            return true;
        }
        if ((modifiers is ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Shift)) && key is Key.Left or Key.Right)
        {
            var anchor = SelectionLength > 0 && CaretOffset == SelectionStart ? SelectionStart + SelectionLength : SelectionStart;
            var target = WordBoundary(key == Key.Right);
            if ((modifiers & ModifierKeys.Shift) != 0) { Select(Math.Min(anchor, target), Math.Abs(target - anchor)); CaretOffset = target; }
            else { Select(target, 0); }
            TextArea.Caret.BringCaretToView(); return true;
        }
        if (modifiers == ModifierKeys.Control && key is Key.Back or Key.Delete)
        {
            if (SelectionLength > 0) ApplyEdit(new(SelectionStart, SelectionLength, "", 0, 0));
            else
            {
                var target = WordBoundary(key == Key.Delete);
                ApplyEdit(new(Math.Min(CaretOffset, target), Math.Abs(target - CaretOffset), "", 0, 0));
            }
            return true;
        }
        if (key == Key.Back && modifiers == ModifierKeys.None && SelectionLength == 0 && CaretOffset > 0 && CaretOffset < Text.Length)
        {
            if (Text.Substring(CaretOffset - 1, 2) is "()" or "[]" or "{}" or "\"\"" or "''")
            { ApplyEdit(new(CaretOffset - 1, 2, "", 0, 0)); return true; }
        }
        if (key is Key.Back or Key.Home && modifiers == ModifierKeys.None && SelectionLength == 0)
        {
            var line = Document.GetLineByOffset(CaretOffset);
            var marker = Layout.Decorations.FirstOrDefault(part => part.Start == line.Offset && part.Length > 0 &&
                (part.Kind is InlineMarkdownLayout.DecorationKind.Bullet or InlineMarkdownLayout.DecorationKind.Number or InlineMarkdownLayout.DecorationKind.Task or InlineMarkdownLayout.DecorationKind.Quote ||
                part.Kind == InlineMarkdownLayout.DecorationKind.Hidden && Layout.Styles.Any(range => range.HeadingLevel > 0 && range.Start == line.Offset)));
            if (marker is not null)
            {
                if (key == Key.Home && CaretOffset != marker.Start + marker.Length) { Select(marker.Start + marker.Length, 0); return true; }
                if (key == Key.Back && CaretOffset == marker.Start + marker.Length)
                {
                    if (!_noteTitle || line.LineNumber != 1) ApplyEdit(new(marker.Start, marker.Length, "", 0, 0));
                    return true;
                }
            }
        }
        if (modifiers == ModifierKeys.Control)
        {
            var marker = key switch { Key.B => "**", Key.I => "*", Key.U => "++", Key.Oem3 => "`", _ => null };
            if (marker is not null) { ApplyEdit(MarkdownEditingCommands.ToggleInline(Text, SelectionStart, SelectionLength, marker)); return true; }
            if (key == Key.K)
            {
                var label = SelectionLength == 0 ? UiText.Get("ссылка") : SelectedText;
                var replacement = $"[{label}](https://)";
                ApplyEdit(new(SelectionStart, SelectionLength, replacement, label.Length + 3, 8)); return true;
            }
            if (key == Key.L) { ApplyEdit(MarkdownEditingCommands.ToggleTasks(Text, SelectionStart, SelectionLength)); return true; }
            if (key == Key.Enter) { OpenLinkAtCaret(); return true; }
            if (key is Key.OemOpenBrackets or Key.OemCloseBrackets)
            { ApplyEdit(MarkdownEditingCommands.IndentLines(Text, SelectionStart, SelectionLength, key == Key.OemOpenBrackets)); return true; }
            if (key >= Key.D1 && key <= Key.D6)
            { SetHeading(key - Key.D1 + 1); return true; }
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (key == Key.V) { ApplicationCommands.Paste.Execute(null, TextArea); return true; }
            if (key is Key.D7 or Key.D8 or Key.OemPeriod)
            { ApplyEdit(MarkdownEditingCommands.PrefixLines(Text, SelectionStart, SelectionLength, key == Key.D7 ? "1. " : key == Key.D8 ? "- " : "> ")); return true; }
            if (key == Key.T) { InsertTable(); return true; }
            if (key == Key.K)
            {
                ApplyEdit(MarkdownEditingCommands.DeleteLines(Text, SelectionStart, SelectionLength)); return true;
            }
            if (key == Key.Z) { Document.UndoStack.Redo(); return true; }
        }
        if (key == Key.Enter && modifiers == ModifierKeys.None && SelectionLength == 0 && !_inlineOnly && TryCreateFencedCodeBlock()) return true;
        if (key == Key.Enter && modifiers == ModifierKeys.None && SelectionLength == 0 &&
            !_blocks.Any(block => block.IsCode && CaretOffset >= block.Start && CaretOffset <= block.Start + block.Length))
        { ApplyEdit(MarkdownEditingCommands.ContinueLine(Text, CaretOffset, Layout.ParentListItemPrefix(Text, CaretOffset))); return true; }
        if (key == Key.Tab && modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            if (!Tables.EnterAtCaret(modifiers == ModifierKeys.Shift) && !MoveTableCell(modifiers == ModifierKeys.Shift))
            {
                var line = Document.GetLineByOffset(CaretOffset);
                var list = Layout.Decorations.Any(part => part.Start == line.Offset && part.Kind is InlineMarkdownLayout.DecorationKind.Bullet or InlineMarkdownLayout.DecorationKind.Number or InlineMarkdownLayout.DecorationKind.Task);
                ApplyEdit(list ? MarkdownEditingCommands.IndentLines(Text, SelectionStart, SelectionLength, modifiers == ModifierKeys.Shift) :
                    MarkdownEditingCommands.Indent(Text, SelectionStart, SelectionLength, modifiers == ModifierKeys.Shift));
            }
            return true;
        }
        return false;
    }

    private bool TryUnwrapCodeBlock()
    {
        foreach (var frame in Layout.CodeFrames)
        {
            if (frame.ContentStart is not { } contentStart) continue;
            var opening = Document.GetLineByOffset(frame.Start);
            var atFence = CaretOffset == frame.Start || CaretOffset == opening.EndOffset;
            var atContent = CaretOffset >= contentStart && Document.GetLineByOffset(CaretOffset).Offset == contentStart &&
                string.IsNullOrWhiteSpace(Text[contentStart..CaretOffset]);
            if (!atFence && !atContent) continue;
            var bodyEnd = frame.ClosingStart ?? frame.End;
            var end = frame.ClosingStart is { } closing ? Document.GetLineByOffset(closing).Offset + Document.GetLineByOffset(closing).TotalLength : frame.End;
            var body = Text[contentStart..bodyEnd];
            // One replacement removes both fence rows; all content and line endings
            // inside the block are retained, and Undo restores the original block.
            ApplyEdit(new(frame.Start, end - frame.Start, body, atContent ? Math.Min(body.Length, CaretOffset - contentStart) : 0, 0));
            return true;
        }
        return false;
    }

    private bool TryCreateFencedCodeBlock()
    {
        var line = Document.GetLineByOffset(CaretOffset);
        var frame = Layout.CodeFrames.FirstOrDefault(frame => frame.Start == line.Offset);
        if (CaretOffset != line.EndOffset || frame is null || frame.HasClosingFence && frame.ContentStart != frame.ClosingStart) return false;
        var match = Regex.Match(Document.GetText(line), @"^(?<indent> {0,3})(?<fence>`{3,}|~{3,})(?<info>[^\r\n]*)$");
        if (!match.Success) return false;
        var newline = Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var indent = match.Groups["indent"].Value;
        if (frame.HasClosingFence)
        {
            ApplyEdit(new(CaretOffset, 0, newline + indent, newline.Length + indent.Length, 0));
            return true;
        }
        ApplyEdit(new(CaretOffset, 0, newline + indent + newline + indent + match.Groups["fence"].Value, newline.Length + indent.Length, 0));
        return true;
    }

    private int WordBoundary(bool forward)
    {
        // Navigate the source document, not a preview's single visual object.
        // This prevents Ctrl+Delete from removing a whole neighbouring rendered block.
        var offset = CaretOffset;
        if (forward) while (offset < Text.Length && char.IsWhiteSpace(Text[offset])) offset++;
        else while (offset > 0 && char.IsWhiteSpace(Text[offset - 1])) offset--;
        var next = ICSharpCode.AvalonEdit.Document.TextUtilities.GetNextCaretPosition(Document, offset,
            forward ? LogicalDirection.Forward : LogicalDirection.Backward, ICSharpCode.AvalonEdit.Document.CaretPositioningMode.WordBorder);
        return next < 0 ? forward ? Text.Length : 0 : next;
    }

    internal void InsertTable()
    {
        var table = UiText.Get("| Заголовок 1 | Заголовок 2 |\n| --- | --- |\n|  |  |\n");
        var prefix = SelectionStart > 0 && Text[SelectionStart - 1] != '\n' ? "\n\n" : "";
        ApplyEdit(new(SelectionStart, SelectionLength, prefix + table, prefix.Length, 0));
        Tables.Get(SelectionStart)?.FocusCell(0, 0);
    }

    internal bool OpenLinkAtCaret()
    {
        var parsed = Markdig.Markdown.Parse(Text, _pipeline);
        var link = parsed.Descendants().FirstOrDefault(item => item.Span.Start <= CaretOffset && item.Span.End >= CaretOffset &&
            item is WikiLinkInline or LinkInline or AutolinkInline);
        if (link is WikiLinkInline wiki) { OpenNoteRequested?.Invoke(wiki.Destination); return true; }
        var url = link switch { LinkInline { IsImage: false } web => web.Url, AutolinkInline { IsEmail: false } auto => auto.Url, _ => null };
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        OpenWebLinkRequested?.Invoke(uri); return true;
    }

    internal void InsertCodeBlock()
    {
        var selected = SelectedText;
        var fence = new string('`', Math.Max(3, Regex.Matches(selected, "`+").Cast<Match>().Select(match => match.Length + 1).DefaultIfEmpty(3).Max()));
        var prefix = SelectionStart > 0 && Text[SelectionStart - 1] != '\n' ? "\n\n" : "";
        ApplyEdit(new(SelectionStart, SelectionLength, prefix + fence + "\n" + selected + "\n" + fence + "\n", prefix.Length + fence.Length + 1, selected.Length));
    }

    internal bool MoveTableCell(bool backwards)
    {
        var line = Document.GetLineByOffset(CaretOffset); var text = Document.GetText(line);
        var table = _blocks.FirstOrDefault(block => block.IsTable && line.Offset >= block.Start && line.Offset < block.Start + block.Length);
        if (table is null) return false;
        if (!text.TrimStart().StartsWith('|') || text.Count(c => c == '|') < 2) return false;
        var cells = Regex.Matches(Text, @"(?m)^.*\|.*$").Cast<Match>()
            .Where(match => !Regex.IsMatch(match.Value, @"^\s*\|?[\s:|\-]+$"))
            .Where(match => match.Index >= table.Start && match.Index < table.Start + table.Length)
            .SelectMany(match => Regex.Matches(match.Value, @"(?<=\|)[^|]*(?=\|)").Cast<Match>().Select(cell => (Start: match.Index + cell.Index, Length: cell.Length))).ToList();
        var cellIndex = cells.FindIndex(cell => CaretOffset >= cell.Start && CaretOffset <= cell.Start + cell.Length);
        if (cellIndex < 0) return false;
        var next = backwards ? cellIndex - 1 : cellIndex + 1;
        if (next < 0) next = 0;
        if (next >= cells.Count)
        {
            var count = text.Count(c => c == '|') - 1;
            var row = "\n|" + string.Concat(Enumerable.Repeat("  |", count));
            ApplyEdit(new(line.EndOffset, 0, row, 3, 0)); return true;
        }
        var selected = cells[next]; var raw = Text.Substring(selected.Start, selected.Length);
        var leading = raw.Length - raw.TrimStart().Length;
        Select(selected.Start + leading, raw.Trim().Length); return true;
    }

    internal void AppendFormattingMenu(ContextMenu menu)
    {
        if (EditingTarget != this) { EditingTarget.AppendFormattingMenu(menu); return; }
        var formatting = new MenuItem { Header = UiText.Get("Оформление текста") };
        void Add(string label, Action action)
        {
            var parts = label.Split(" — ", 2, StringSplitOptions.None);
            var item = new MenuItem { Header = parts[0], InputGestureText = parts.Length == 2 ? parts[1] : "" };
            item.Click += (_, _) => { action(); TextArea.Focus(); };
            formatting.Items.Add(item);
        }
        Add(UiText.Get("Жирный — Ctrl+B"), ToggleBoldSelection);
        Add(UiText.Get("Курсив — Ctrl+I"), () => ApplyEdit(MarkdownEditingCommands.ToggleInline(Text, SelectionStart, SelectionLength, "*")));
        Add(UiText.Get("Подчёркнутый — Ctrl+U"), () => ApplyEdit(MarkdownEditingCommands.ToggleInline(Text, SelectionStart, SelectionLength, "++")));
        Add(UiText.Get("Код — Ctrl+`"), () => ApplyEdit(MarkdownEditingCommands.ToggleInline(Text, SelectionStart, SelectionLength, "`")));
        Add(UiText.Get("Блок кода"), InsertCodeBlock);
        Add(UiText.Get("Зачёркнутый"), () => ApplyEdit(MarkdownEditingCommands.ToggleInline(Text, SelectionStart, SelectionLength, "~~")));
        Add(UiText.Get("Выделить маркером"), () => ApplyEdit(MarkdownEditingCommands.ToggleInline(Text, SelectionStart, SelectionLength, "==")));
        var headings = new MenuItem { Header = UiText.Get("Заголовок") };
        var normal = new MenuItem { Header = UiText.Get("Обычный текст") };
        normal.Click += (_, _) => { SetHeading(0); TextArea.Focus(); }; headings.Items.Add(normal);
        for (var level = 1; level <= 6; level++)
        {
            var selectedLevel = level;
            var item = new MenuItem { Header = $"H{level}", InputGestureText = $"Ctrl+{level}" };
            item.Click += (_, _) => { SetHeading(selectedLevel); TextArea.Focus(); }; headings.Items.Add(item);
        }
        formatting.Items.Add(headings);
        Add(UiText.Get("Задача — Ctrl+L"), () => ApplyEdit(MarkdownEditingCommands.ToggleTasks(Text, SelectionStart, SelectionLength)));
        Add(UiText.Get("Таблица — Ctrl+Shift+T"), InsertTable);
        menu.Items.Add(formatting);
    }

    internal static bool IsInteractivePreviewElement(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is MarkdownTableView || current is CheckBox || current is Hyperlink && (Keyboard.Modifiers & ModifierKeys.Control) != 0) return true;
            current = current is FrameworkContentElement content ? content.Parent :
                current is Visual ? VisualTreeHelper.GetParent(current) ?? (current as FrameworkElement)?.TemplatedParent ?? LogicalTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }
    private sealed class InlineGenerator(LiveMarkdownEditor editor) : VisualLineElementGenerator
    {
        public override int GetFirstInterestedOffset(int startOffset) => editor.Layout.Decorations.FirstOrDefault(part => part.Start >= startOffset)?.Start ?? -1;
        public override VisualLineElement? ConstructElement(int offset)
        {
            var part = editor.Layout.Decorations.FirstOrDefault(part => part.Start == offset && part.Kind == InlineMarkdownLayout.DecorationKind.Table && editor.Tables.Get(offset) is not null)
                ?? editor.Layout.Decorations.FirstOrDefault(part => part.Start == offset && part.Kind != InlineMarkdownLayout.DecorationKind.Table);
            if (part is null) return null;
            if (part.Kind == InlineMarkdownLayout.DecorationKind.Table && editor.Tables.Get(part.Start) is { } table)
                return new InlineObjectElement(part.Length, table);
            return new MarkdownDecorationElement(part, () =>
            {
                if (part.Kind != InlineMarkdownLayout.DecorationKind.Task) return;
                editor.ToggleTask(part);
            }, cell => InlineMarkdownMetrics.Measure(editor, cell), column => InlineMarkdownMetrics.MeasureColumn(editor, column),
                editor._noteTitle && part.Start == 0 && part.Kind == InlineMarkdownLayout.DecorationKind.Hidden ? 6 : 0);
        }
    }

    private sealed class AlignmentGenerator(LiveMarkdownEditor editor) : VisualLineElementGenerator
    {
        private readonly List<InlineMarkdownLayout.Decoration> _spaces = [];
        public override void StartGeneration(ITextRunConstructionContext context)
        {
            base.StartGeneration(context); _spaces.Clear();
            if (editor._appearance.TextAlignment != NoteTextAlignment.Justify) return;
            var line = context.VisualLine.FirstDocumentLine;
            if (editor.Layout.Decorations.Any(part => part.Start >= line.Offset && part.Start <= line.EndOffset && part.Kind is InlineMarkdownLayout.DecorationKind.TableEdge or InlineMarkdownLayout.DecorationKind.TableRule)) return;
            if (editor._blocks.Any(block => block.IsCode && line.Offset >= block.Start && line.Offset < block.Start + block.Length)) return;
            var prefix = editor.Layout.Decorations.FirstOrDefault(part => part.Start == line.Offset && part.Kind is InlineMarkdownLayout.DecorationKind.Bullet or InlineMarkdownLayout.DecorationKind.Number or InlineMarkdownLayout.DecorationKind.Task or InlineMarkdownLayout.DecorationKind.Quote or InlineMarkdownLayout.DecorationKind.Indent);
            var contentStart = line.Offset + (prefix?.Length ?? 0);
            var indent = prefix is null ? 0 : (prefix.Indent + (prefix.Kind == InlineMarkdownLayout.DecorationKind.Indent ? 0 : prefix.Kind == InlineMarkdownLayout.DecorationKind.Quote ? 10 : 16)) * editor.FontSize / 14;
            var available = editor.TextArea.TextView.ActualWidth - indent - 2;
            if (available <= 20) return;
            var words = Regex.Matches(editor.Text[contentStart..line.EndOffset], @"\S+").Select(match => new InlineMarkdownLayout.Cell(contentStart + match.Index, match.Length)).ToArray();
            var gaps = new List<InlineMarkdownLayout.Cell>(); var widths = new List<double>(); double row = 0;
            InlineMarkdownLayout.Cell? previous = null;
            foreach (var word in words)
            {
                var wordWidth = InlineMarkdownMetrics.Measure(editor, word);
                var gap = previous is null ? null : new InlineMarkdownLayout.Cell(previous.Start + previous.Length, word.Start - previous.Start - previous.Length);
                var gapWidth = gap is null ? 0 : InlineMarkdownMetrics.Measure(editor, gap);
                if (row + gapWidth + wordWidth > available && previous is not null)
                {
                    if (gaps.Count > 0)
                        for (var index = 0; index < gaps.Count; index++)
                            _spaces.Add(new(gaps[index].Start, gaps[index].Length, InlineMarkdownLayout.DecorationKind.Spacing, Indent: (widths[index] + Math.Max(0, available - row) / gaps.Count) * 14 / editor.FontSize));
                    gaps.Clear(); widths.Clear(); row = wordWidth;
                }
                else
                {
                    row += gapWidth + wordWidth;
                    if (gap is not null && gap.Length > 0) { gaps.Add(gap); widths.Add(gapWidth); }
                }
                previous = word;
            }
        }
        public override int GetFirstInterestedOffset(int startOffset)
        {
            var line = CurrentContext.VisualLine.FirstDocumentLine;
            if (editor.Layout.CodeFrames.Any(frame => line.Offset >= frame.Start && line.Offset < frame.End))
                return startOffset <= line.Offset ? line.Offset : -1;
            if (editor._appearance.TextAlignment == NoteTextAlignment.Justify) return _spaces.FirstOrDefault(part => part.Start >= startOffset)?.Start ?? -1;
            var start = CurrentContext.VisualLine.FirstDocumentLine.Offset;
            return editor._appearance.TextAlignment is NoteTextAlignment.Center or NoteTextAlignment.Right && startOffset <= start ? start : -1;
        }
        public override VisualLineElement? ConstructElement(int offset)
        {
            var codeLine = CurrentContext.VisualLine.FirstDocumentLine;
            if (editor.Layout.CodeFrames.Any(frame => codeLine.Offset >= frame.Start && codeLine.Offset < frame.End))
                return new MarkdownDecorationElement(new(offset, 0, InlineMarkdownLayout.DecorationKind.Indent, Indent: 10), () => { });
            if (editor._appearance.TextAlignment == NoteTextAlignment.Justify)
            {
                var spacing = _spaces.FirstOrDefault(part => part.Start == offset);
                return spacing is null ? null : new MarkdownDecorationElement(spacing, () => { });
            }
            var line = CurrentContext.VisualLine.FirstDocumentLine;
            var width = InlineMarkdownMetrics.Measure(editor, new(line.Offset, line.Length));
            foreach (var part in editor.Layout.Decorations.Where(part => part.Start >= line.Offset && part.Start < line.EndOffset && part.Kind != InlineMarkdownLayout.DecorationKind.Hidden))
            {
                width -= InlineMarkdownMetrics.Measure(editor, new(part.Start, part.Length));
                width += part.Kind switch
                {
                    InlineMarkdownLayout.DecorationKind.Indent => part.Indent * editor.FontSize / 14,
                    InlineMarkdownLayout.DecorationKind.Bullet or InlineMarkdownLayout.DecorationKind.Task or InlineMarkdownLayout.DecorationKind.Number => (part.Indent + 16) * editor.FontSize / 14,
                    InlineMarkdownLayout.DecorationKind.Quote => 10 * editor.FontSize / 14,
                    _ => 0
                };
            }
            // Tables keep a shared column origin, rather than aligning individual rows independently.
            if (editor.Layout.Decorations.Any(part => part.Start >= line.Offset && part.Start <= line.EndOffset && part.Kind is InlineMarkdownLayout.DecorationKind.TableEdge or InlineMarkdownLayout.DecorationKind.TableRule)) return null;
            var padding = Math.Max(0, editor.TextArea.TextView.ActualWidth - width - 2);
            if (editor._appearance.TextAlignment == NoteTextAlignment.Center) padding /= 2;
            return padding <= 0 ? null : new MarkdownDecorationElement(new(offset, 0, InlineMarkdownLayout.DecorationKind.Indent, Indent: padding * 14 / editor.FontSize), () => { });
        }
    }

    private sealed class InlineStyling(LiveMarkdownEditor editor) : DocumentColorizingTransformer
    {
        private static TextDecorationCollection MergeDecorations(TextDecorationCollection? existing, TextDecorationCollection added)
        {
            if (existing is null || existing.Count == 0) return added;
            var combined = new TextDecorationCollection(existing);
            foreach (var decoration in added)
                if (!combined.Any(item => item.Location == decoration.Location)) combined.Add(decoration);
            combined.Freeze(); return combined;
        }

        protected override void ColorizeLine(ICSharpCode.AvalonEdit.Document.DocumentLine line)
        {
            if (editor.Tables.Contains(line.Offset)) return;
            foreach (var range in editor.Layout.Styles)
            {
                var start = Math.Max(line.Offset, range.Start); var end = Math.Min(line.EndOffset, range.Start + range.Length);
                if (start >= end) continue;
                ChangeLinePart(start, end, element =>
                {
                    var style = range.Style; var face = element.TextRunProperties.Typeface;
                    element.TextRunProperties.SetTypeface(new Typeface(style.HasFlag(InlineMarkdownLayout.Style.Code) ? new FontFamily("Consolas") : face.FontFamily,
                        style.HasFlag(InlineMarkdownLayout.Style.Italic) ? FontStyles.Italic : face.Style,
                        range.HeadingLevel > 0 ? range.HeadingLevel == 1 ? FontWeights.Bold : FontWeights.SemiBold :
                        style.HasFlag(InlineMarkdownLayout.Style.Bold) ? FontWeights.Bold : face.Weight, face.Stretch));
                    if (range.Scale != 1) element.TextRunProperties.SetFontRenderingEmSize(editor.FontSize * range.Scale);
                    if (style.HasFlag(InlineMarkdownLayout.Style.Strike)) element.TextRunProperties.SetTextDecorations(MergeDecorations(element.TextRunProperties.TextDecorations, TextDecorations.Strikethrough));
                    if (style.HasFlag(InlineMarkdownLayout.Style.Underline)) element.TextRunProperties.SetTextDecorations(MergeDecorations(element.TextRunProperties.TextDecorations, TextDecorations.Underline));
                    if (style.HasFlag(InlineMarkdownLayout.Style.BlockCode))
                        element.TextRunProperties.SetTypeface(new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal));
                    if (style.HasFlag(InlineMarkdownLayout.Style.Link))
                    { element.TextRunProperties.SetForegroundBrush(editor.Theme.Link); element.TextRunProperties.SetTextDecorations(MergeDecorations(element.TextRunProperties.TextDecorations, TextDecorations.Underline)); }
                });
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        SmoothScroll.Dispose(); TableControls.Dispose(); Tables.Dispose();
        System.ComponentModel.PropertyChangedEventManager.RemoveHandler(UiText.Current, OnLanguageChanged, string.Empty);
        Loaded -= OnEditorLoaded;
        TextChanged -= OnTextChanged; PreviewKeyDown -= OnKey;
        TextArea.PreviewMouseLeftButtonDown -= OnEditorMouseDown;
        TextArea.Caret.PositionChanged -= OnFenceCaretChanged;
        TextArea.SelectionChanged -= OnFenceCaretChanged;
        TextArea.TextEntering -= OnTableTextEntering;
        TextArea.TextEntering -= OnFenceTextEntering;
        DataObject.RemovePastingHandler(this, OnPaste);
        PreviewTextInput -= OnTextInput; _search.Uninstall();
    }

    private sealed class LocalizedSearch : ICSharpCode.AvalonEdit.Search.Localization
    {
        public override string MatchCaseText => UiText.Get("Учитывать регистр");
        public override string MatchWholeWordsText => UiText.Get("Слова целиком");
        public override string UseRegexText => UiText.Get("Регулярное выражение");
        public override string FindNextText => UiText.Get("Следующее совпадение (F3)");
        public override string FindPreviousText => UiText.Get("Предыдущее совпадение (Shift+F3)");
        public override string ErrorText => UiText.Get("Ошибка: ");
        public override string NoMatchesFoundText => UiText.Get("Совпадений нет");
    }
}
