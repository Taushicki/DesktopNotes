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

    private static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception e) { completion.SetException(e); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
