using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopNotes.Presentation;
using ICSharpCode.AvalonEdit.Rendering;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class HeadingEditorTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public Task HeadingsFormatAsYouTypeWithoutMovingOnSelection(int level) => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor(); var host = Host(editor);
        try
        {
            host.Show(); Keyboard.Focus(editor.TextArea);
            editor.Document.Insert(0, new string('#', level)); editor.Document.Insert(level, " "); host.UpdateLayout();
            var emptyHeading = Assert.Single(editor.Layout.Styles, range => range.HeadingLevel == level);
            Assert.Equal(InlineMarkdownLayout.HeadingScale(level), emptyHeading.Scale);
            editor.Document.Insert(level + 1, "Заголовок *курсив* и **жирный**"); host.UpdateLayout();
            var line = editor.TextArea.TextView.VisualLines.First(); var height = line.Height;
            Assert.Contains(line.Elements, element => Math.Abs(element.TextRunProperties.FontRenderingEmSize - editor.FontSize * emptyHeading.Scale) < .01);
            editor.Select(0, editor.Text.Length); host.UpdateLayout(); Assert.Equal(height, editor.TextArea.TextView.VisualLines.First().Height);
            host.Focus(); Keyboard.ClearFocus(); host.UpdateLayout(); Assert.Equal(height, editor.TextArea.TextView.VisualLines.First().Height);
            Assert.Equal(new string('#', level) + " Заголовок *курсив* и **жирный**", editor.Text);
        }
        finally { host.Close(); }
    });

    [Theory]
    [InlineData("##\tЗаголовок ###", 2)]
    [InlineData("  ### Заголовок", 3)]
    [InlineData("> #### Заголовок", 4)]
    [InlineData("- ##### Заголовок", 5)]
    [InlineData("Заголовок\n=====", 1)]
    [InlineData("Заголовок\r\n-----", 2)]
    public void ParsedHeadingVariantsKeepTheirLevel(string source, int level)
    {
        var layout = InlineMarkdownLayout.Create(source, Markdig.Markdown.Parse(source, MarkdownPipelines.Create()));
        Assert.Contains(layout.Styles, style => style.HeadingLevel == level);
        Assert.DoesNotContain(layout.Decorations, part => part.Kind == InlineMarkdownLayout.DecorationKind.Rule);
        if (source.EndsWith("###", StringComparison.Ordinal))
            Assert.Contains(layout.Decorations, part => part.Kind == InlineMarkdownLayout.DecorationKind.Hidden && part.Start > 2);
    }

    [Theory]
    [InlineData("\\# обычный текст")]
    [InlineData("####### обычный текст")]
    [InlineData("#без пробела")]
    [InlineData("```\n# это код\n```")]
    [InlineData("    # это код")]
    public void LiteralHashesAndCodeRemainOrdinaryText(string source)
    {
        var layout = InlineMarkdownLayout.Create(source, Markdig.Markdown.Parse(source, MarkdownPipelines.Create()));
        Assert.DoesNotContain(layout.Styles, range => range.HeadingLevel > 0);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public Task StartingListDoesNotPromotePreviousLineToHeading(string newline) => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = $"# Заголовок{newline}{newline}Обычный текст" };
        editor.EnableNoteTitle(); var host = Host(editor);
        try
        {
            host.Show(); host.UpdateLayout();
            var textOffset = editor.Text.IndexOf("Обычный текст", StringComparison.Ordinal);
            var height = editor.TextArea.TextView.GetOrConstructVisualLine(editor.Document.GetLineByOffset(textOffset)).Height;
            editor.Select(editor.Text.Length, 0);
            Assert.True(editor.HandleShortcut(Key.Enter, ModifierKeys.None));
            editor.TextArea.PerformTextInput("-"); host.UpdateLayout();
            var dashOffset = editor.Text.Length - 1;
            Assert.DoesNotContain(editor.Layout.Styles, style => style.HeadingLevel > 0 && style.Start >= textOffset);
            Assert.DoesNotContain(editor.Layout.Decorations, part => part.Kind == InlineMarkdownLayout.DecorationKind.Hidden && part.Start <= dashOffset && part.Start + part.Length > dashOffset);
            Assert.Equal(height, editor.TextArea.TextView.GetOrConstructVisualLine(editor.Document.GetLineByOffset(textOffset)).Height);
            editor.TextArea.PerformTextInput(" "); host.UpdateLayout();
            Assert.Contains(editor.Layout.Decorations, part => part.Kind == InlineMarkdownLayout.DecorationKind.Bullet && part.Start == dashOffset);
            Assert.Equal(height, editor.TextArea.TextView.GetOrConstructVisualLine(editor.Document.GetLineByOffset(textOffset)).Height);
            editor.TextArea.PerformTextInput("Пункт"); host.UpdateLayout();
            Assert.EndsWith("- Пункт", editor.Text);
            Assert.DoesNotContain(editor.Layout.Styles, style => style.HeadingLevel > 0 && style.Start >= textOffset);
        }
        finally { host.Close(); }
    });

    [Theory]
    [InlineData("Текст\n-", false)]
    [InlineData("Текст\r\n  -  ", false)]
    [InlineData("Текст\n- ", false)]
    [InlineData("Текст\n--", true)]
    [InlineData("Текст\n---", true)]
    [InlineData("Текст\n===", true)]
    public void SingleDashStaysLiteralButLongerSetextUnderlinesStillWork(string source, bool heading)
    {
        var layout = InlineMarkdownLayout.Create(source, Markdig.Markdown.Parse(source, MarkdownPipelines.Create()));
        Assert.Equal(heading, layout.Styles.Any(style => style.HeadingLevel > 0));
    }

    [Fact]
    public Task HeadingCommandsPreserveBlankLinesAndUndo() => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor() { Text = "Первый\r\n\r\nВторой\r\nТретий" };
        editor.Select(0, editor.Text.IndexOf("Третий", StringComparison.Ordinal)); editor.SetHeading(2);
        Assert.Equal("## Первый\r\n\r\n## Второй\r\nТретий", editor.Text);
        editor.Undo(); Assert.Equal("Первый\r\n\r\nВторой\r\nТретий", editor.Text);
        editor.Text = "### Название ###"; editor.Select(5, 0); editor.SetHeading(1); Assert.Equal("# Название", editor.Text);
        editor.SetHeading(1); Assert.Equal("Название", editor.Text);
        editor.SetHeading(6); editor.SetHeading(0); Assert.Equal("Название", editor.Text);
        editor.Text = "## Название"; editor.Select(editor.Text.Length, 0); Assert.True(editor.HandleShortcut(Key.Home, ModifierKeys.None)); Assert.Equal(3, editor.CaretOffset);
        Assert.True(editor.HandleShortcut(Key.Back, ModifierKeys.None)); Assert.Equal("Название", editor.Text);
        editor.Undo(); Assert.Equal("## Название", editor.Text);
    });

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public Task EmptyLinesHaveCleanSelectionWithoutServiceMarkers(string newline) => OnSta(() =>
    {
        var source = $"# Заголовок{newline}{newline}Текст{newline}{newline}{newline}Ещё текст";
        using var editor = new LiveMarkdownEditor() { Text = source, Background = Brushes.White };
        var host = Host(editor);
        try
        {
            host.Show(); Keyboard.Focus(editor.TextArea); editor.Select(0, source.Length); host.UpdateLayout();
            Assert.False(editor.Options.ShowEndOfLine); Assert.False(editor.Options.ShowSpaces); Assert.False(editor.Options.ShowTabs); Assert.False(editor.Options.ShowBoxForControlCharacters);
            Assert.Null(editor.TextArea.SelectionBorder); Assert.Equal(0, editor.TextArea.SelectionCornerRadius);
            foreach (var line in editor.TextArea.TextView.VisualLines.Where(line => line.FirstDocumentLine.Length == 0))
            {
                Assert.Empty(line.Elements); Assert.Equal(0, line.VisualLengthWithEndOfLineMarker);
                Assert.DoesNotContain(line.TextLines.SelectMany(text => text.GetTextRunSpans()), span => span.Value is System.Windows.Media.TextFormatting.TextEmbeddedObject);
            }
            if (Environment.GetEnvironmentVariable("DESKTOPNOTES_TEST_RENDER_DIR") is { } directory)
            {
                Directory.CreateDirectory(directory);
                var bitmap = new RenderTargetBitmap(320, 360, 96, 96, PixelFormats.Pbgra32); bitmap.Render(editor);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, newline.Length == 1 ? "heading-selection-lf.png" : "heading-selection-crlf.png")); encoder.Save(file);

            }
            Assert.Equal(source, editor.SelectedText); editor.Document.Replace(editor.SelectionStart, editor.SelectionLength, ""); editor.Undo(); Assert.Equal(source, editor.Text);
        }
        finally { host.Close(); }
    });

    private static Window Host(object content) => new() { Content = content, Left = -10000, Top = -10000, Width = 340, Height = 400, ShowActivated = false, ShowInTaskbar = false, Background = Brushes.White };
    private static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
