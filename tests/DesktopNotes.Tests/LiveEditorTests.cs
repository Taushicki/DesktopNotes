using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using DesktopNotes.Application;
using DesktopNotes.Domain;
using DesktopNotes.Presentation;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class LiveEditorTests
{
    [Fact]
    public Task BuiltInNavigationAndSelectionWorkWithLivePreview() => OnSta(() =>
    {
        const string source = "one two\nthree four\n\n# heading";
        using var editor = new LiveMarkdownEditor(new MarkdownRenderer()) { Text = source };
        var host = new Window { Content = editor, Left = -10000, Top = -10000, Width = 320, Height = 280, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            host.Show(); Keyboard.Focus(editor.TextArea); host.UpdateLayout();
            EditingCommands.MoveToDocumentEnd.Execute(null, editor.TextArea); Assert.Equal(source.Length, editor.CaretOffset);
            EditingCommands.SelectToDocumentStart.Execute(null, editor.TextArea); Assert.Equal(source, editor.SelectedText);
            editor.Select(0, 0); EditingCommands.MoveToLineEnd.Execute(null, editor.TextArea); Assert.Equal(7, editor.CaretOffset);
            EditingCommands.SelectToLineStart.Execute(null, editor.TextArea); Assert.Equal("one two", editor.SelectedText);
            editor.Select(0, 0); EditingCommands.SelectDownByLine.Execute(null, editor.TextArea); Assert.Equal("one two\n", editor.SelectedText);
            Assert.Equal(source, editor.Text);
            Assert.True(editor.HandleEscape()); Assert.Equal(0, editor.SelectionLength);
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task ObsidianTaskAndLinkShortcutsAreDistinctAndUndoable() => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor(new MarkdownRenderer()) { Text = "  - first\n  - [ ] second\n\n[[Target|caption]]\n\n[web](https://example.com)" };
        editor.Select(0, editor.Text.IndexOf("\n\n", StringComparison.Ordinal));
        Assert.True(editor.HandleShortcut(Key.L, ModifierKeys.Control));
        Assert.StartsWith("  - [ ] first\n  - [x] second", editor.Text);
        editor.Undo(); Assert.StartsWith("  - first\n  - [ ] second", editor.Text);
        string? note = null; Uri? url = null;
        editor.OpenNoteRequested += value => note = value;
        editor.OpenWebLinkRequested += value => url = value;
        editor.Select(editor.Text.IndexOf("caption", StringComparison.Ordinal), 0);
        var before = editor.Text;
        Assert.True(editor.HandleShortcut(Key.Enter, ModifierKeys.Control));
        Assert.Equal("Target", note); Assert.Equal(before, editor.Text);
        editor.Select(editor.Text.IndexOf("web", StringComparison.Ordinal), 0);
        editor.HandleShortcut(Key.Enter, ModifierKeys.Control); Assert.Equal("https://example.com/", url?.AbsoluteUri);
        editor.Text = "[file](file:///C:/private.txt)"; editor.Select(2, 0); url = null;
        Assert.False(editor.OpenLinkAtCaret()); Assert.Null(url);
        editor.Text = "`[[Target]]`"; editor.Select(5, 0); note = null;
        Assert.False(editor.OpenLinkAtCaret()); Assert.Null(note);
    });

    [Fact]
    public Task WordShortcutsOperateOnSourceAcrossRenderedBlocks() => OnSta(() =>
    {
        const string source = "first\n\nsecond third";
        using var editor = new LiveMarkdownEditor(new MarkdownRenderer()) { Text = source };
        editor.Select(0, 0);
        Assert.True(editor.HandleShortcut(Key.Right, ModifierKeys.Control)); Assert.Equal(5, editor.CaretOffset);
        editor.HandleShortcut(Key.Right, ModifierKeys.Control | ModifierKeys.Shift); Assert.Equal("\n\nsecond", editor.SelectedText);
        editor.HandleShortcut(Key.Left, ModifierKeys.Control | ModifierKeys.Shift); Assert.Equal("\n\n", editor.SelectedText);
        editor.Select(5, 0); editor.HandleShortcut(Key.Delete, ModifierKeys.Control);
        Assert.Equal("first third", editor.Text); editor.Undo(); Assert.Equal(source, editor.Text);
        editor.Select(editor.Text.Length, 0); editor.HandleShortcut(Key.Back, ModifierKeys.Control);
        Assert.Equal("first\n\nsecond ", editor.Text);
    });

    [Fact]
    public Task LineShortcutsRespectExclusiveSelectionAndCRLF() => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor(new MarkdownRenderer()) { Text = "one\r\ntwo\r\nthree" };
        editor.Select(5, 0); editor.HandleShortcut(Key.OemCloseBrackets, ModifierKeys.Control);
        Assert.Equal("one\r\n  two\r\nthree", editor.Text);
        editor.HandleShortcut(Key.OemOpenBrackets, ModifierKeys.Control); Assert.Equal("one\r\ntwo\r\nthree", editor.Text);
        editor.Select(0, 5); editor.HandleShortcut(Key.K, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.Equal("two\r\nthree", editor.Text); editor.Undo();
        editor.Select(editor.Text.Length, 0); editor.HandleShortcut(Key.K, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.Equal("one\r\ntwo", editor.Text); editor.Undo(); Assert.EndsWith("three", editor.Text);
    });

    [Theory]
    [InlineData("first\nsecond\n")]
    [InlineData("first\r\nsecond\r\n\r\nthird")]
    [InlineData("- one\n- two\n\nnext")]
    [InlineData("- one\r\n- two\r\n\r\nnext")]
    [InlineData("> one\n> two\n\nnext")]
    [InlineData("```\none\ntwo\n```\n\nnext")]
    [InlineData("# Heading\n\nplain\ntext\n\nmore")]
    public Task SavedMultilineMarkdownCanRenderAtStartup(string source) => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor(new MarkdownRenderer()) { Text = source };
        var host = new Window { Content = editor, Left = -10000, Top = -10000, Width = 320, Height = 280, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            host.Show(); host.UpdateLayout();
            Assert.Equal(source, editor.Text);
            System.Windows.Input.Keyboard.Focus(editor.TextArea);
            editor.CaretOffset = source.Length;
            host.UpdateLayout();
            editor.Document.Insert(editor.CaretOffset, "\nnew line\n");
            host.UpdateLayout();
            host.Focus(); System.Windows.Input.Keyboard.ClearFocus(); host.UpdateLayout();
            editor.Undo(); host.UpdateLayout(); Assert.Equal(source, editor.Text);
            editor.DisablePreview(); host.UpdateLayout();
            Assert.Empty(editor.PreviewBlocks); Assert.Equal(source, editor.Text);
            editor.Document.Insert(0, "source "); host.UpdateLayout();
            Assert.StartsWith("source ", editor.Text);
        }
        finally { host.Close(); }
    });

    [Theory]
    [InlineData("- first", "- first\n- ")]
    [InlineData("3. third", "3. third\n4. ")]
    [InlineData("- [x] done", "- [x] done\n- [ ] ")]
    [InlineData("> quote", "> quote\n> ")]
    [InlineData("- ", "")]
    [InlineData("- [ ] ", "")]
    [InlineData("text", "text\n")]
    public void EnterContinuesMarkdownStructureAndEndsEmptyItems(string text, string expected)
    {
        var edit = MarkdownEditingCommands.ContinueLine(text, text.Length);
        Assert.Equal(expected, text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement));
    }

    [Fact]
    public void BracketPairingPreservesSelectionAndSkipsExistingClosers()
    {
        var pair = MarkdownEditingCommands.PairCharacter("word", 0, 4, '[')!;
        Assert.Equal("[word]", pair.Replacement); Assert.Equal(1, pair.SelectionStart); Assert.Equal(4, pair.SelectionLength);
        var closing = MarkdownEditingCommands.PairCharacter("[]", 1, 0, ']')!;
        Assert.Equal("", closing.Replacement); Assert.Equal(1, closing.SelectionStart);
        Assert.Null(MarkdownEditingCommands.PairCharacter("can't", 3, 0, '\''));
    }

    [Fact]
    public void InlineFormattingAndTaskCommandsAreReversible()
    {
        var italic = MarkdownEditingCommands.ToggleInline("word", 0, 4, "*");
        Assert.Equal("*word*", italic.Replacement);
        Assert.Equal("word", MarkdownEditingCommands.ToggleInline("*word*", 1, 4, "*").Replacement);
        var placeholder = MarkdownEditingCommands.ToggleInline("", 0, 0, "`");
        Assert.Equal("``", placeholder.Replacement); Assert.Equal(1, placeholder.SelectionStart);
        var task = MarkdownEditingCommands.ToggleTask("- [ ] work", 5);
        Assert.Equal("x", task.Replacement); Assert.Equal(3, task.Start);
        Assert.Equal("# title", MarkdownEditingCommands.PrefixLines("## title", 0, 0, "# ").Replacement);
    }

    [Fact]
    public Task RendererSupportsTablesTasksAndInternalLinks() => OnSta(() =>
    {
        int? clicked = null; string? opened = null;
        const string source = "- [ ] task\n\n| A | B |\n| --- | --- |\n| one | two |\n\n[[Some note|Caption]]";
        var doc = new MarkdownRenderer().Render(source, new MarkdownRenderActions((offset, _) => clicked = offset, value => opened = value));
        var list = Assert.IsType<System.Windows.Documents.List>(doc.Blocks.FirstBlock);
        var paragraph = Assert.IsType<Paragraph>(list.ListItems.FirstListItem.Blocks.FirstBlock);
        var checkbox = Assert.IsType<CheckBox>(Assert.Single(paragraph.Inlines.OfType<InlineUIContainer>()).Child);
        checkbox.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Assert.Equal(source.IndexOf("[ ]", StringComparison.Ordinal), clicked);
        var table = Assert.Single(doc.Blocks.OfType<System.Windows.Documents.Table>());
        Assert.Equal(2, table.RowGroups[0].Rows.Count); Assert.Equal(2, table.RowGroups[0].Rows[0].Cells.Count);
        var links = Assert.IsType<Paragraph>(doc.Blocks.LastBlock).Inlines.OfType<Hyperlink>().ToArray();
        var link = Assert.Single(links);
        Assert.Equal("Caption", new TextRange(link.ContentStart, link.ContentEnd).Text);
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); Assert.Equal("Some note", opened);
    });

    [Fact]
    public Task LivePreviewDoesNotRewriteMarkdownAndTableNavigationIsUndoable() => OnSta(() =>
    {
        const string source = "# Title\n\n**bold** and *italic*\n\n| A | B |\n| --- | --- |\n| one | two |";
        using var editor = new LiveMarkdownEditor(new MarkdownRenderer()) { Text = source, Width = 440, Height = 360 };
        var surface = new Grid { Background = Brushes.White }; surface.Children.Add(editor);
        var host = new Window { Content = surface, Left = -10000, Top = -10000, Width = 460, Height = 400, ShowActivated = false, ShowInTaskbar = false };
        try
        {
        host.Show();
        var frame = new System.Windows.Threading.DispatcherFrame();
        editor.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        editor.SetAppearance(new Note { FontSize = 16, TextAlignment = NoteTextAlignment.Center });
        editor.ApplyTemplate();
        editor.Measure(new Size(440, 360)); editor.Arrange(new Rect(0, 0, 440, 360)); editor.UpdateLayout();
        Assert.Equal(source, editor.Text);
        Assert.Equal(3, editor.PreviewBlocks.Count);
        editor.CaretOffset = source.IndexOf("one", StringComparison.Ordinal);
        Assert.True(editor.MoveTableCell(false)); Assert.Equal("two", editor.SelectedText);
        Assert.True(editor.MoveTableCell(false)); Assert.EndsWith("\n|  |  |", editor.Text);
        editor.Undo(); Assert.Equal(source, editor.Text);
        editor.Select(0, 0); editor.InsertTable(); Assert.Contains("Заголовок 1", editor.Text);
        editor.Undo(); Assert.Equal(source, editor.Text);
        editor.UpdateLayout();
        surface.Measure(new Size(440, 360)); surface.Arrange(new Rect(0, 0, 440, 360));
        var bitmap = new RenderTargetBitmap(440, 360, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        var pixels = new byte[440 * 360 * 4]; bitmap.CopyPixels(pixels, 440 * 4, 0);
        Assert.True(Enumerable.Range(0, 440 * 360).Count(index => pixels[index * 4 + 3] > 0 && pixels[index * 4] < 200) > 100);
        if (Environment.GetEnvironmentVariable("DESKTOPNOTES_TEST_RENDER_DIR") is { } directory)
        {
            Directory.CreateDirectory(directory); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, "live-markdown-editor.png")); encoder.Save(file);
        }
        System.Windows.Input.Keyboard.Focus(editor.TextArea);
        editor.CaretOffset = source.IndexOf("bold", StringComparison.Ordinal) + 2;
        Assert.True(editor.IsKeyboardFocusWithin);
        Assert.True(editor.IsSourceVisible(editor.PreviewBlocks[1]));
        Assert.False(editor.IsSourceVisible(editor.PreviewBlocks[0]));
        editor.UpdateLayout();
        Assert.Equal(source, editor.Text);
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task EscapedWikiLinksRemainLiteralAndTaskTemplateIsInteractive() => OnSta(() =>
    {
        var doc = new MarkdownRenderer().Render("\\[\\[Title]]");
        Assert.Empty(Assert.IsType<Paragraph>(doc.Blocks.FirstBlock).Inlines.OfType<Hyperlink>());
        var checkbox = new CheckBox { Content = "task" };
        checkbox.ApplyTemplate();
        var content = new TextBlock { Text = "task" }; checkbox.Content = content;
        Assert.True(LiveMarkdownEditor.IsInteractivePreviewElement(checkbox));
        // Text in a checkbox must not be intercepted as a request to edit Markdown.
        Assert.True(LiveMarkdownEditor.IsInteractivePreviewElement(content));
        using var editor = new LiveMarkdownEditor(new MarkdownRenderer()) { Text = "before code after" };
        editor.Select(7, 4); editor.InsertCodeBlock();
        Assert.Equal("code", editor.SelectedText);
        Assert.Contains("```\ncode\n```", editor.Text);
        editor.Undo(); Assert.Equal("before code after", editor.Text);
    });

    [Fact]
    public Task InlineEditsPreserveUndoAndStableInternalLinkIds() => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor(new MarkdownRenderer()) { Text = "before word after" };
        editor.Select(7, 4); editor.ToggleBoldSelection(); Assert.Equal("before **word** after", editor.Text);
        editor.Undo(); Assert.Equal("before word after", editor.Text); editor.Redo(); Assert.Equal("before **word** after", editor.Text);
        editor.Select(0, 0); var id = Guid.NewGuid(); editor.InsertNoteLink(id, "Title");
        Assert.StartsWith($"[[{id}|Title]]", editor.Text);
        editor.Undo(); Assert.Equal("before **word** after", editor.Text);
    });

    private static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
