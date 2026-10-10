using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopNotes.Domain;
using DesktopNotes.Presentation;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class CodeBlockEditorTests
{
    [Theory]
    [InlineData("```\ntext\n```", 4, "text\n", 0)]
    [InlineData("Before\r\n\r\n```csharp\r\nfirst\r\n\r\nsecond\r\n```\r\n\r\nAfter", 21, "Before\r\n\r\nfirst\r\n\r\nsecond\r\n\r\nAfter", 10)]
    [InlineData("~~~python\ntext\n~~~\nAfter", 10, "text\nAfter", 0)]
    [InlineData("```\n\n```", 4, "\n", 0)]
    [InlineData("```\ntext\n", 4, "text\n", 0)]
    [InlineData("```", 3, "", 0)]
    [InlineData("  ```\n  text\n  ```", 8, "  text\n", 2)]
    [InlineData("````\nfirst\n```\nlast\n````", 5, "first\n```\nlast\n", 0)]
    public Task BackspaceUnwrapsCodeWithoutLosingContentAndUndoRestoresFences(string source, int caret, string expected, int expectedCaret) => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = source, CaretOffset = caret };
        Assert.True(editor.HandleShortcut(Key.Back, ModifierKeys.None));
        Assert.Equal(expected, editor.Text); Assert.Equal(expectedCaret, editor.CaretOffset);
        editor.Undo(); Assert.Equal(source, editor.Text);
        editor.Redo(); Assert.Equal(expected, editor.Text);
    });

    [Fact]
    public Task BackspaceOnlyUnwrapsTheSelectedBlockAtItsBeginning() => OnSta(() =>
    {
        const string source = "```\nfirst\n```\n\n~~~\nsecond\nlast\n~~~";
        using var editor = new LiveMarkdownEditor { Text = source };
        editor.CaretOffset = source.IndexOf("last", StringComparison.Ordinal);
        Assert.False(editor.HandleShortcut(Key.Back, ModifierKeys.None)); Assert.Equal(source, editor.Text);
        editor.CaretOffset = source.IndexOf("second", StringComparison.Ordinal) + 1;
        Assert.False(editor.HandleShortcut(Key.Back, ModifierKeys.None));
        editor.Select(source.IndexOf("second", StringComparison.Ordinal), 2);
        Assert.False(editor.HandleShortcut(Key.Back, ModifierKeys.None));
        editor.Select(source.IndexOf("second", StringComparison.Ordinal), 0);
        Assert.True(editor.HandleShortcut(Key.Back, ModifierKeys.None));
        Assert.Equal("```\nfirst\n```\n\nsecond\nlast\n", editor.Text);
        Assert.Single(editor.Layout.CodeFrames);
    });

    [Theory]
    [InlineData("```", "```\n\n```", 4)]
    [InlineData("```csharp", "```csharp\n\n```", 10)]
    [InlineData("~~~~python", "~~~~python\n\n~~~~", 11)]
    [InlineData("  ```", "  ```\n  \n  ```", 8)]
    [InlineData("Текст\r\n\r\n```", "Текст\r\n\r\n```\r\n\r\n```", 14)]
    public Task EnterCreatesEditableCodeBlockInOneUndoStep(string source, string expected, int caret) => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = source, CaretOffset = source.Length };
        Assert.True(editor.HandleShortcut(Key.Enter, ModifierKeys.None));
        Assert.Equal(expected, editor.Text); Assert.Equal(caret, editor.CaretOffset);
        Assert.True(Assert.Single(editor.Layout.CodeFrames).HasClosingFence);
        editor.Document.Insert(editor.CaretOffset, "- **literal**\n# code");
        Assert.DoesNotContain(editor.Layout.Styles, range => range.HeadingLevel > 0);
        Assert.DoesNotContain(editor.Layout.Decorations, part => part.Kind == InlineMarkdownLayout.DecorationKind.Bullet);
        editor.Undo(); Assert.Equal(expected, editor.Text);
        editor.Undo(); Assert.Equal(source, editor.Text);
        editor.Redo(); Assert.Equal(expected, editor.Text);
    });

    [Theory]
    [InlineData("```\n\n```")]
    [InlineData("```csharp\r\nvar x = 1;\r\n```\r\n\r\nПосле")]
    [InlineData("~~~\nтекст\n~~~\n")]
    public Task FenceLinesAreHiddenWithoutChangingSourceAndInlineCodeRemainsInline(string source) => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = source };
        var frame = Assert.Single(editor.Layout.CodeFrames); Assert.True(frame.HasClosingFence);
        var hidden = editor.Layout.Decorations.Where(part => part.Kind == InlineMarkdownLayout.DecorationKind.Hidden).ToArray();
        Assert.Equal(2, hidden.Length);
        Assert.StartsWith(source.StartsWith('~') ? "~~~" : "```", source.Substring(hidden[0].Start, hidden[0].Length));
        Assert.Equal(source.StartsWith('~') ? "~~~" : "```", source.Substring(hidden[1].Start, hidden[1].Length));
        editor.Select(0, source.Length); Assert.Equal(source, editor.SelectedText);
        editor.Text = "Обычный `код`"; Assert.Empty(editor.Layout.CodeFrames);
        Assert.Contains(editor.Layout.Styles, range => range.Style == InlineMarkdownLayout.Style.Code);
    });

    [Fact]
    public Task ShiftEnterAndExistingFencesDoNotDuplicateClosingLine() => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = "```", CaretOffset = 3 };
        Assert.True(editor.HandleShortcut(Key.Enter, ModifierKeys.Shift)); Assert.Equal("```\n", editor.Text);
        editor.Text = "```\ncode\n```"; editor.CaretOffset = 3;
        Assert.False(editor.HandleShortcut(Key.Enter, ModifierKeys.None)); Assert.Equal("```\ncode\n```", editor.Text);
        editor.Text = "`` not a fence"; editor.CaretOffset = editor.Text.Length;
        Assert.True(editor.HandleShortcut(Key.Enter, ModifierKeys.None)); Assert.Equal("`` not a fence\n", editor.Text);
    });

    [Fact]
    public Task CodeFrameKeepsNativeCaretWrappingAndSelectionWithNoCharacterBackground() => OnSta(() =>
    {
        const string source = "# Пример\n\n```csharp\nvar text = \"длинная строка для проверки переноса в небольшом стикере\";\n\nConsole.WriteLine(text);\n```\n\nПосле блока\n\n`встроенный код`";
        using var editor = new LiveMarkdownEditor { Text = source, Background = Brushes.Lavender };
        editor.SetAppearance(new Note { FontSize = 20, IsBold = true, TextAlignment = NoteTextAlignment.Center });
        var host = new Window { Content = editor, Left = -10000, Top = -10000, Width = 330, Height = 390, ShowActivated = false, ShowInTaskbar = false, Background = Brushes.Lavender };
        try
        {
            host.Show(); host.UpdateLayout();
            Assert.Single(editor.TextArea.TextView.BackgroundRenderers.OfType<CodeBlockBackground>());
            var frame = Assert.Single(editor.Layout.CodeFrames);
            var codeLines = editor.TextArea.TextView.VisualLines.Where(line => line.FirstDocumentLine.Offset > frame.Start && line.FirstDocumentLine.Offset < frame.End).ToArray();
            Assert.NotEmpty(codeLines);
            var body = codeLines.First(line => line.FirstDocumentLine.Length > 3);
            Assert.True(body.TextLines.Count > 1);
            foreach (var element in body.Elements.Where(element => element.DocumentLength > 0))
            {
                Assert.Null(element.TextRunProperties.BackgroundBrush);
                Assert.Equal(FontWeights.Normal, element.TextRunProperties.Typeface.Weight);
            }
            editor.CaretOffset = source.IndexOf("Console", StringComparison.Ordinal); editor.Select(editor.CaretOffset, 7); host.UpdateLayout();
            Assert.Equal("Console", editor.SelectedText); Assert.Equal(source, editor.Text);
            if (Environment.GetEnvironmentVariable("DESKTOPNOTES_TEST_RENDER_DIR") is { } directory)
            {
                Directory.CreateDirectory(directory);
                var bitmap = new RenderTargetBitmap(310, 350, 96, 96, PixelFormats.Pbgra32); bitmap.Render(editor);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, "code-block.png")); encoder.Save(file);
            }
            editor.ScrollToEnd(); host.UpdateLayout(); editor.ScrollToHome(); host.UpdateLayout();
            editor.DisablePreview(); host.UpdateLayout(); Assert.Empty(editor.Layout.CodeFrames); Assert.Equal(source, editor.Text);
        }
        finally { host.Close(); }
    });

    [Theory]
    [InlineData("# Название\nТекст\n")]
    [InlineData("# Название\r\n\r\nТекст\r\n")]
    public Task TypingFenceAfterTextImmediatelyAcceptsCodeAndBackspace(string prefix) => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = prefix };
        editor.EnableNoteTitle(); editor.Select(editor.Text.Length, 0);
        var host = new Window { Content = editor, Width = 330, Height = 350, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            host.Show(); host.UpdateLayout();
            editor.TextArea.PerformTextInput("```"); host.UpdateLayout();
            var opening = Assert.Single(editor.Layout.CodeFrames);
            Assert.Equal(prefix.Length, opening.Start);
            Assert.False(opening.HasClosingFence);
            Assert.Null(opening.ClosingStart);
            Assert.DoesNotContain(editor.Layout.Decorations, part => part.Kind == InlineMarkdownLayout.DecorationKind.Hidden && part.Start == prefix.Length);
            editor.TextArea.PerformTextInput("python"); host.UpdateLayout();
            Assert.EndsWith("```python", editor.Text);
            Assert.DoesNotContain(editor.Layout.Decorations, part => part.Kind == InlineMarkdownLayout.DecorationKind.Hidden && part.Start == prefix.Length);
            Assert.True(editor.HandleShortcut(Key.Enter, ModifierKeys.None)); host.UpdateLayout();
            var frame = Assert.Single(editor.Layout.CodeFrames);
            Assert.True(frame.HasClosingFence);
            Assert.Equal(frame.ContentStart, editor.CaretOffset);
            Assert.True(frame.ClosingStart >= frame.ContentStart);
            Assert.True(editor.Document.GetLineByOffset(frame.End - 1).LineNumber - editor.Document.GetLineByOffset(frame.Start).LineNumber >= 2);
            var view = editor.TextArea.TextView;
            var firstRow = editor.Document.GetLineByOffset(frame.Start).LineNumber;
            var lastRow = editor.Document.GetLineByOffset(frame.End - 1).LineNumber;
            var bottom = lastRow < editor.Document.LineCount ? view.GetVisualTopByDocumentLine(lastRow + 1) : view.DocumentHeight;
            Assert.True(bottom - view.GetVisualTopByDocumentLine(firstRow) >= 40);
            editor.TextArea.PerformTextInput("code"); host.UpdateLayout();
            Assert.Equal("code", editor.Document.GetText(editor.Document.GetLineByOffset(editor.CaretOffset)));
            editor.Select(frame.ContentStart!.Value, 0);
            Assert.True(editor.HandleShortcut(Key.Back, ModifierKeys.None));
            Assert.Equal(prefix + "code" + (prefix.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n"), editor.Text);
        }
        finally { host.Close(); }
    });

    [Theory]
    [InlineData("```\n```")]
    [InlineData("```\r\n```")]
    [InlineData("# Название\nТекст\n```\n```")]
    [InlineData("# Название\nТекст\n```\n```\n")]
    [InlineData("# Название\nТекст\n```\n")]
    [InlineData("```\n")]
    [InlineData("```python")]
    [InlineData("# Название\nТекст\n```python")]
    public Task EmptyAndUnfinishedBlocksHaveOrderedBoundariesAndUnwrapSafely(string source) => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = source };
        var frame = Assert.Single(editor.Layout.CodeFrames);
        Assert.True(frame.ContentStart <= (frame.ClosingStart ?? frame.End), $"Invalid frame: {frame}");
        editor.Select(frame.ContentStart!.Value, 0);
        Assert.True(editor.HandleShortcut(Key.Back, ModifierKeys.None));
        editor.Undo(); Assert.Equal(source, editor.Text);
    });

    [Fact]
    public void CodeFrameRangesStayOrderedAcrossFenceEditingStates()
    {
        foreach (var prefix in new[] { "", "Text\n", "# Title\n\nText\n", "> ", "- ", "- item\n  " })
        foreach (var opening in new[] { "```", "~~~", "```csharp", "````", "  ```", "``` ```", "``````" })
        foreach (var suffix in new[] { "", "\n", "\n\n", "code", "\n```", "\n```\n", "\n  ```", "\n~~~", "\n`", "\n  " })
        {
            var source = prefix + opening + suffix;
            var layout = InlineMarkdownLayout.Create(source, Markdig.Markdown.Parse(source, MarkdownPipelines.Create()));
            foreach (var frame in layout.CodeFrames)
                Assert.True(frame.ContentStart is null || frame.ContentStart <= (frame.ClosingStart ?? frame.End), $"{source.Replace("\n", "\\n", StringComparison.Ordinal)}: {frame}");
        }
    }

    [Theory]
    [InlineData(14)]
    [InlineData(20)]
    public Task CodeFrameUsesCompactPaddingAroundContentRows(double fontSize) => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = "# Пример\n\n```\nПривет как дела\n```\n\nПосле блока", Background = Brushes.LemonChiffon };
        editor.EnableNoteTitle(); editor.SetAppearance(new Note { FontSize = fontSize });
        var host = new Window { Content = editor, Width = 330, Height = 280, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            host.Show(); host.UpdateLayout(); var view = editor.TextArea.TextView;
            var frame = Assert.Single(editor.Layout.CodeFrames);
            var renderer = Assert.Single(view.BackgroundRenderers.OfType<CodeBlockBackground>());
            var bounds = renderer.Bounds(view, frame)!.Value;
            var body = view.GetOrConstructVisualLine(editor.Document.GetLineByOffset(frame.ContentStart!.Value));
            var scale = fontSize / 14;
            Assert.Equal(6 * scale - .5, body.VisualTop - view.ScrollOffset.Y - bounds.Top, 4);
            Assert.Equal(10 * scale - .5, bounds.Bottom - (body.VisualTop + body.Height - view.ScrollOffset.Y), 4);
            Assert.True(bounds.Height < body.Height * 3);
            editor.Select(frame.ContentStart.Value, 0); editor.TextArea.PerformTextInput("Ещё строка\n"); host.UpdateLayout();
            Assert.Equal("Ещё строка", editor.Document.GetText(editor.Document.GetLineByOffset(frame.ContentStart.Value)));
            Assert.Equal("Привет как дела", editor.Document.GetText(editor.Document.GetLineByOffset(editor.CaretOffset)));
            if (Environment.GetEnvironmentVariable("DESKTOPNOTES_TEST_RENDER_DIR") is { } directory)
            {
                Directory.CreateDirectory(directory);
                var bitmap = new RenderTargetBitmap(330, 280, 96, 96, PixelFormats.Pbgra32); bitmap.Render(editor);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, $"code-padding-{fontSize}.png")); encoder.Save(file);
            }
        }
        finally { host.Close(); }
    });

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public Task HiddenFencesRedirectCaretIntoCodeAndArrowKeysCanLeaveBlock(string newline) => OnSta(() =>
    {
        var source = $"Before{newline}```{newline}code{newline}```{newline}After";
        using var editor = new LiveMarkdownEditor { Text = source };
        var host = new Window { Content = editor, Width = 330, Height = 280, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            host.Show(); host.UpdateLayout(); var frame = Assert.Single(editor.Layout.CodeFrames);
            editor.Select(frame.Start, 0); Assert.Equal(frame.ContentStart, editor.CaretOffset);
            editor.TextArea.PerformTextInput("X"); Assert.Contains("\nXcode", editor.Text);
            frame = Assert.Single(editor.Layout.CodeFrames);
            editor.Select(frame.ClosingStart!.Value, 0);
            Assert.Equal(editor.Document.GetLineByOffset(frame.ClosingStart.Value - 1).EndOffset, editor.CaretOffset);
            editor.TextArea.PerformTextInput("Y"); Assert.Contains("XcodeY", editor.Text);
            frame = Assert.Single(editor.Layout.CodeFrames);
            editor.Select(frame.ContentStart!.Value, 0);
            Assert.True(editor.HandleShortcut(Key.Up, ModifierKeys.None)); Assert.Equal("Before".Length, editor.CaretOffset);
            editor.Select(editor.Document.GetLineByOffset(frame.ClosingStart!.Value - 1).EndOffset, 0);
            Assert.True(editor.HandleShortcut(Key.Down, ModifierKeys.None));
            Assert.Equal(editor.Text.IndexOf("After", StringComparison.Ordinal), editor.CaretOffset);
            editor.Select(frame.Start, frame.End - frame.Start); Assert.StartsWith("```", editor.SelectedText); Assert.EndsWith("```", editor.SelectedText);
            editor.Select(frame.ContentStart.Value, 0);
            Assert.True(editor.HandleShortcut(Key.Back, ModifierKeys.None)); Assert.Contains("XcodeY", editor.Text); Assert.Empty(editor.Layout.CodeFrames);
            editor.Undo(); Assert.Single(editor.Layout.CodeFrames);
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task DownCanLeaveCodeAtEndOfDocumentAndWrappedRowsStillNavigateNormally() => OnSta(() =>
    {
        var source = "# Заголовок\n\n```\nДлинная строка кода с несколькими переносами в узком стикере\n```";
        using var editor = new LiveMarkdownEditor { Text = source };
        var host = new Window { Content = editor, Width = 220, Height = 350, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            host.Show(); host.UpdateLayout(); var frame = Assert.Single(editor.Layout.CodeFrames);
            var line = editor.TextArea.TextView.GetOrConstructVisualLine(editor.Document.GetLineByOffset(frame.ContentStart!.Value));
            Assert.True(line.TextLines.Count > 1);
            editor.Select(frame.ContentStart.Value + 3, 0);
            Assert.False(editor.HandleShortcut(Key.Down, ModifierKeys.None));
            editor.Select(editor.Document.GetLineByOffset(frame.ClosingStart!.Value - 1).EndOffset, 0);
            Assert.False(editor.HandleShortcut(Key.Up, ModifierKeys.None));
            Assert.True(editor.HandleShortcut(Key.Down, ModifierKeys.None));
            Assert.Equal(source + "\n", editor.Text); Assert.Equal(editor.Text.Length, editor.CaretOffset);
            editor.TextArea.PerformTextInput("После"); Assert.EndsWith("```\nПосле", editor.Text);
            editor.Undo(); editor.Undo(); Assert.Equal(source, editor.Text);
        }
        finally { host.Close(); }
    });

    [Theory]
    [InlineData("\n", "```")]
    [InlineData("\r\n", "~~~")]
    public Task NewFenceAboveExistingCodeDoesNotConsumeItsOpening(string newline, string fence) => OnSta(() =>
    {
        var prefix = $"# Название{newline}{newline}";
        var originalBlock = $"{fence}{newline}Исходный код{newline}{fence}";
        var source = prefix + newline + originalBlock;
        using var editor = new LiveMarkdownEditor { Text = source };
        editor.EnableNoteTitle(); editor.Select(prefix.Length, 0);
        var host = new Window { Content = editor, Width = 330, Height = 300, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            host.Show(); host.UpdateLayout();
            foreach (var c in fence) editor.TextArea.PerformTextInput(c.ToString());
            host.UpdateLayout();
            Assert.Equal(prefix + fence + newline + fence + newline + originalBlock, editor.Text);
            Assert.Equal(2, editor.Layout.CodeFrames.Count);
            var renderer = Assert.Single(editor.TextArea.TextView.BackgroundRenderers.OfType<CodeBlockBackground>());
            Assert.Null(renderer.Bounds(editor.TextArea.TextView, editor.Layout.CodeFrames[0]));
            Assert.NotNull(renderer.Bounds(editor.TextArea.TextView, editor.Layout.CodeFrames[1]));
            Assert.Equal(prefix.Length + fence.Length, editor.CaretOffset);
            Assert.True(editor.HandleShortcut(Key.Enter, ModifierKeys.None));
            editor.TextArea.PerformTextInput("Новый код"); host.UpdateLayout();
            Assert.Equal(prefix + fence + newline + "Новый код" + newline + fence + newline + originalBlock, editor.Text);
            Assert.Equal(2, editor.Layout.CodeFrames.Count);
            Assert.All(editor.Layout.CodeFrames, frame => Assert.True(frame.HasClosingFence));
            var undoStates = new List<string>();
            for (var i = 0; i < 5 && editor.Text != source; i++) { editor.Undo(); undoStates.Add(editor.Text.Replace("\n", "\\n", StringComparison.Ordinal)); }
            Assert.True(editor.Text == source, string.Join(" | ", undoStates));
            Assert.Single(editor.Layout.CodeFrames);
        }
        finally { host.Close(); }
    });

    private static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception e) { completion.SetException(e); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
