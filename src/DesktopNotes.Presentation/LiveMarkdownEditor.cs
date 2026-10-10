using System.Globalization;
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

namespace DesktopNotes.Presentation;

internal sealed class LiveMarkdownEditor : TextEditor, IDisposable
{
    private readonly IMarkdownRenderer _renderer;
    private readonly MarkdownPipeline _pipeline = MarkdownPipelines.Create();
    private Note _appearance = new();
    private List<PreviewBlock> _blocks = [];
    private string _parsedText = "";
    private readonly List<CollapsedLineSection> _collapsed = [];
    private bool _disposed;
    internal sealed record PreviewBlock(int Start, int Length, bool IsCode = false, bool IsTable = false);
    internal IReadOnlyList<PreviewBlock> PreviewBlocks => _blocks;
    internal event Action<string>? OpenNoteRequested;
    internal event Action? ContentGeometryChanged;
    private readonly PreviewGenerator _generator;
    private readonly SearchPanel _search;

    internal LiveMarkdownEditor(IMarkdownRenderer renderer)
    {
        _renderer = renderer;
        Background = Brushes.Transparent; BorderThickness = new Thickness(0); Padding = new Thickness(8);
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        WordWrap = true; ShowLineNumbers = false;
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden; HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Options.CutCopyWholeLine = true; Options.ConvertTabsToSpaces = true; Options.IndentationSize = 2;
        Options.EnableHyperlinks = false; Options.EnableEmailHyperlinks = false;
        _generator = new PreviewGenerator(this); TextArea.TextView.ElementGenerators.Add(_generator);
        TextArea.TextView.LineTransformers.Add(new ActiveLineStyling());
        TextChanged += OnTextChanged;
        TextArea.Caret.PositionChanged += OnCaretChanged;
        TextArea.SelectionChanged += OnCaretChanged;
        GotKeyboardFocus += OnFocusChanged; LostKeyboardFocus += OnFocusChanged;
        SizeChanged += (_, _) => TextArea.TextView.Redraw();
        TextArea.TextView.VisualLinesChanged += (_, _) => ContentGeometryChanged?.Invoke();
        PreviewKeyDown += OnKey;
        PreviewTextInput += OnTextInput;
        _search = SearchPanel.Install(this); _search.Localization = new RussianSearch();
        System.Windows.Automation.AutomationProperties.SetName(this, "Текст заметки — Markdown Live Preview");
    }

    private void OnTextChanged(object? sender, EventArgs args)
    {
        RefreshPreview();
    }
    private void RefreshPreview()
    {
        // Caret/selection events can precede TextChanged during Undo or a replacement.
        // Never reuse source ranges from the previous document in that interval.
        if (_parsedText != Text)
        {
            _parsedText = Text;
            var parsed = Markdig.Markdown.Parse(_parsedText, _pipeline);
            _blocks = parsed.Where(block => block.Span.Start >= 0 && block.Span.Length > 0 && block.Span.End < _parsedText.Length)
                .Select(block => new PreviewBlock(block.Span.Start, block.Span.Length, block is Markdig.Syntax.CodeBlock, block is Markdig.Extensions.Tables.Table)).ToList();
        }
        foreach (var section in _collapsed) section.Uncollapse();
        _collapsed.Clear();
        foreach (var block in _blocks.Where(block => !IsSourceVisible(block)))
        {
            var first = Document.GetLineByOffset(block.Start); var last = Document.GetLineByOffset(block.Start + block.Length - 1);
            if (first.LineNumber < last.LineNumber) _collapsed.Add(TextArea.TextView.CollapseLines(first.NextLine, last));
        }
        TextArea.TextView.Redraw();
    }
    private void OnCaretChanged(object? sender, EventArgs args) => RefreshPreview();
    private void OnFocusChanged(object sender, KeyboardFocusChangedEventArgs args) => RefreshPreview();
    internal bool IsSourceVisible(PreviewBlock block)
    {
        if (!IsKeyboardFocusWithin) return false;
        var end = block.Start + block.Length;
        return SelectionLength > 0 ? SelectionStart <= end && SelectionStart + SelectionLength >= block.Start : CaretOffset >= block.Start && CaretOffset <= end;
    }

    internal void SetAppearance(Note note)
    {
        _appearance = note; FontSize = note.FontSize; FontWeight = note.IsBold ? FontWeights.Bold : FontWeights.Normal;
        TextArea.TextView.Redraw();
    }
    internal void ApplyEdit(MarkdownTextEdit edit)
    {
        using (Document.RunUpdate()) Document.Replace(edit.Start, edit.Length, edit.Replacement);
        Select(edit.Start + edit.SelectionStart, edit.SelectionLength);
    }
    internal void ToggleBoldSelection() => ApplyEdit(MarkdownEditingCommands.ToggleInline(Text, SelectionStart, SelectionLength, "**"));

    internal bool HandleEscape()
    {
        if (!_search.IsClosed) { _search.Close(); return true; }
        if (SelectionLength > 0) { Select(CaretOffset, 0); return true; }
        return false;
    }

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length != 1 || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;
        var edit = MarkdownEditingCommands.PairCharacter(Text, SelectionStart, SelectionLength, e.Text[0]);
        if (edit is not null) { ApplyEdit(edit); e.Handled = true; }
    }

    internal void InsertNoteLink(Guid id, string title)
    {
        var label = title.Replace("|", " ").Replace("]", "");
        ApplyEdit(new(SelectionStart, SelectionLength, $"[[{id}|{label}]]", 0, label.Length + 41));
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Back && modifiers == ModifierKeys.None && SelectionLength == 0 && CaretOffset > 0 && CaretOffset < Text.Length)
        {
            if (Text.Substring(CaretOffset - 1, 2) is "()" or "[]" or "{}" or "\"\"" or "''")
            { ApplyEdit(new(CaretOffset - 1, 2, "", 0, 0)); e.Handled = true; return; }
        }
        if (modifiers == ModifierKeys.Control)
        {
            var marker = e.Key switch { Key.B => "**", Key.I => "*", Key.Oem3 => "`", _ => null };
            if (marker is not null) { ApplyEdit(MarkdownEditingCommands.ToggleInline(Text, SelectionStart, SelectionLength, marker)); e.Handled = true; return; }
            if (e.Key == Key.K)
            {
                var label = SelectionLength == 0 ? "ссылка" : SelectedText;
                var replacement = $"[{label}](https://)";
                ApplyEdit(new(SelectionStart, SelectionLength, replacement, label.Length + 3, 8)); e.Handled = true; return;
            }
            if (e.Key == Key.Enter) { ApplyEdit(MarkdownEditingCommands.ToggleTask(Text, CaretOffset)); e.Handled = true; return; }
            if (e.Key >= Key.D1 && e.Key <= Key.D6)
            { ApplyEdit(MarkdownEditingCommands.PrefixLines(Text, SelectionStart, SelectionLength, new string('#', e.Key - Key.D1 + 1) + " ")); e.Handled = true; return; }
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (e.Key == Key.V) { ApplicationCommands.Paste.Execute(null, TextArea); e.Handled = true; return; }
            if (e.Key is Key.D7 or Key.D8 or Key.OemPeriod)
            { ApplyEdit(MarkdownEditingCommands.PrefixLines(Text, SelectionStart, SelectionLength, e.Key == Key.D7 ? "1. " : e.Key == Key.D8 ? "- " : "> ")); e.Handled = true; return; }
            if (e.Key == Key.T) { InsertTable(); e.Handled = true; return; }
            if (e.Key == Key.K)
            {
                var line = Document.GetLineByOffset(CaretOffset); Document.Remove(line.Offset, line.TotalLength); e.Handled = true; return;
            }
            if (e.Key == Key.Z) { Document.UndoStack.Redo(); e.Handled = true; return; }
        }
        if (e.Key == Key.Enter && modifiers == ModifierKeys.None && SelectionLength == 0 &&
            !_blocks.Any(block => block.IsCode && CaretOffset >= block.Start && CaretOffset <= block.Start + block.Length))
        { ApplyEdit(MarkdownEditingCommands.ContinueLine(Text, CaretOffset)); e.Handled = true; }
        if (e.Key == Key.Tab && modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            if (!MoveTableCell(modifiers == ModifierKeys.Shift)) ApplyEdit(MarkdownEditingCommands.Indent(Text, SelectionStart, SelectionLength, modifiers == ModifierKeys.Shift));
            e.Handled = true;
        }
    }

    internal void InsertTable()
    {
        const string table = "| Заголовок 1 | Заголовок 2 |\n| --- | --- |\n|  |  |\n";
        var prefix = SelectionStart > 0 && Text[SelectionStart - 1] != '\n' ? "\n\n" : "";
        ApplyEdit(new(SelectionStart, SelectionLength, prefix + table, prefix.Length + 2, 11));
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
        var formatting = new MenuItem { Header = "Оформление текста" };
        void Add(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => { action(); TextArea.Focus(); }; formatting.Items.Add(item); }
        Add("Жирный — Ctrl+B", ToggleBoldSelection);
        Add("Курсив — Ctrl+I", () => ApplyEdit(MarkdownEditingCommands.ToggleInline(Text, SelectionStart, SelectionLength, "*")));
        Add("Код — Ctrl+`", () => ApplyEdit(MarkdownEditingCommands.ToggleInline(Text, SelectionStart, SelectionLength, "`")));
        Add("Блок кода", InsertCodeBlock);
        Add("Зачёркнутый", () => ApplyEdit(MarkdownEditingCommands.ToggleInline(Text, SelectionStart, SelectionLength, "~~")));
        Add("Выделить маркером", () => ApplyEdit(MarkdownEditingCommands.ToggleInline(Text, SelectionStart, SelectionLength, "==")));
        Add("Задача — Ctrl+Enter", () => ApplyEdit(MarkdownEditingCommands.ToggleTask(Text, CaretOffset)));
        Add("Таблица — Ctrl+Shift+T", InsertTable);
        menu.Items.Add(formatting);
    }

    private FrameworkElement CreatePreview(PreviewBlock block)
    {
        var source = Text.Substring(block.Start, block.Length);
        var document = _renderer.Render(source, new MarkdownRenderActions((offset, value) =>
        {
            var marker = source.IndexOf('[', Math.Clamp(offset, 0, source.Length - 1));
            if (marker >= 0 && marker + 2 < source.Length && source[marker + 2] == ']')
            { using (Document.RunUpdate()) Document.Replace(block.Start + marker + 1, 1, value ? "x" : " "); }
        }, destination => OpenNoteRequested?.Invoke(destination)));
        NoteWindow.ApplyDocumentFormatting(document, _appearance); document.PagePadding = new Thickness(0);
        var view = new RichTextBox { Document = document, IsReadOnly = true, IsDocumentEnabled = true, Focusable = false,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Cursor = Cursors.IBeam,
            Width = Math.Max(40, TextArea.TextView.ActualWidth - 4), VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, ContextMenu = ContextMenu };
        view.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (IsInteractivePreviewElement(e.OriginalSource as DependencyObject)) return;
            e.Handled = true;
            var pointer = view.GetPositionFromPoint(e.GetPosition(view), true);
            var plain = pointer is null ? "" : new TextRange(document.ContentStart, pointer).Text;
            var position = 0;
            foreach (var character in plain)
            {
                if (char.IsWhiteSpace(character)) continue;
                var found = source.IndexOf(character, position); if (found < 0) break; position = found + 1;
            }
            Focus(); CaretOffset = Math.Clamp(block.Start + position, block.Start, block.Start + block.Length);
            TextArea.TextView.Redraw();
        };
        view.Measure(new Size(view.Width, double.PositiveInfinity)); view.Height = Math.Max(FontSize * 1.4, view.DesiredSize.Height);
        return view;
    }

    internal static bool IsInteractivePreviewElement(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is CheckBox || current is Hyperlink && (Keyboard.Modifiers & ModifierKeys.Control) != 0) return true;
            current = current is FrameworkContentElement content ? content.Parent :
                current is Visual ? VisualTreeHelper.GetParent(current) ?? (current as FrameworkElement)?.TemplatedParent ?? LogicalTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }
    private sealed class PreviewGenerator(LiveMarkdownEditor editor) : VisualLineElementGenerator
    {
        public override int GetFirstInterestedOffset(int startOffset) => editor._blocks.FirstOrDefault(block => block.Start >= startOffset && !editor.IsSourceVisible(block))?.Start ?? -1;
        public override VisualLineElement? ConstructElement(int offset)
        {
            var block = editor._blocks.FirstOrDefault(block => block.Start == offset && !editor.IsSourceVisible(block));
            return block is null ? null : new InlineObjectElement(block.Length, editor.CreatePreview(block));
        }
    }

    private sealed class ActiveLineStyling : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(ICSharpCode.AvalonEdit.Document.DocumentLine line)
        {
            var text = CurrentContext.Document.GetText(line); var heading = Regex.Match(text, @"^#{1,6} ");
            if (heading.Success) ChangeLinePart(line.Offset, line.EndOffset, element =>
            { element.TextRunProperties.SetFontRenderingEmSize(CurrentContext.GlobalTextRunProperties.FontRenderingEmSize * (1.9 - heading.Length * 0.12)); });
            foreach (Match match in Regex.Matches(text, @"(\*\*.+?\*\*|(?<!\*)\*[^*]+\*(?!\*)|`[^`]+`|~~.+?~~|==.+?==)"))
                ChangeLinePart(line.Offset + match.Index, line.Offset + match.Index + match.Length, element =>
                {
                    var face = element.TextRunProperties.Typeface;
                    element.TextRunProperties.SetTypeface(new Typeface(match.Value.StartsWith('`') ? new FontFamily("Consolas") : face.FontFamily,
                        match.Value.StartsWith('*') && !match.Value.StartsWith("**", StringComparison.Ordinal) ? FontStyles.Italic : face.Style,
                        match.Value.StartsWith("**", StringComparison.Ordinal) ? FontWeights.Bold : face.Weight, face.Stretch));
                    if (match.Value.StartsWith("~~", StringComparison.Ordinal)) element.TextRunProperties.SetTextDecorations(TextDecorations.Strikethrough);
                    if (match.Value.StartsWith("==", StringComparison.Ordinal)) element.TextRunProperties.SetBackgroundBrush(Brushes.LightGoldenrodYellow);
                });
        }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var section in _collapsed) section.Uncollapse(); _collapsed.Clear();
        TextChanged -= OnTextChanged; TextArea.Caret.PositionChanged -= OnCaretChanged; TextArea.SelectionChanged -= OnCaretChanged;
        GotKeyboardFocus -= OnFocusChanged; LostKeyboardFocus -= OnFocusChanged; PreviewKeyDown -= OnKey;
        PreviewTextInput -= OnTextInput; _search.Uninstall();
    }

    private sealed class RussianSearch : ICSharpCode.AvalonEdit.Search.Localization
    {
        public override string MatchCaseText => "Учитывать регистр";
        public override string MatchWholeWordsText => "Слова целиком";
        public override string UseRegexText => "Регулярное выражение";
        public override string FindNextText => "Следующее совпадение (F3)";
        public override string FindPreviousText => "Предыдущее совпадение (Shift+F3)";
        public override string ErrorText => "Ошибка: ";
        public override string NoMatchesFoundText => "Совпадений нет";
    }
}
