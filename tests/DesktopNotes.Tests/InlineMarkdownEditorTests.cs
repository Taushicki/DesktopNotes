using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopNotes.Presentation;
using DesktopNotes.Domain;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class InlineMarkdownEditorTests
{
    [Theory]
    [InlineData("- ", InlineMarkdownLayout.DecorationKind.Bullet)]
    [InlineData("+ item", InlineMarkdownLayout.DecorationKind.Bullet)]
    [InlineData("2. item", InlineMarkdownLayout.DecorationKind.Number)]
    [InlineData("- [ ] task", InlineMarkdownLayout.DecorationKind.Task)]
    [InlineData("# Heading", InlineMarkdownLayout.DecorationKind.Hidden)]
    public Task MarkersRenderImmediatelyWhileTyping(string source, object kind) => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor();
        var host = Host(editor);
        try
        {
            host.Show(); Keyboard.Focus(editor.TextArea);
            foreach (var character in source) { editor.Document.Insert(editor.Document.TextLength, character.ToString()); host.UpdateLayout(); }
            Assert.Equal(source, editor.Text);
            Assert.Contains(editor.TextArea.TextView.VisualLines.SelectMany(line => line.Elements).OfType<MarkdownDecorationElement>(),
                element => element.Decoration.Kind == (InlineMarkdownLayout.DecorationKind)kind);
        }
        finally { host.Close(); }
    });

    [Theory]
    [InlineData("Привет\n- первая строка\nпродолжение\n- ещё одна\n1. номер\n2. второй", 14)]
    [InlineData("# Заголовок\n\n**жирный** и *курсив* и `код`\n\n> цитата\n> вторая строка\n\n- [ ] задача\n  - вложенный пункт", 20)]
    [InlineData("- длинный пункт списка который переносится на несколько визуальных строк в маленьком окне заметки\nпродолжение\n\n[[Target|Ссылка]] и [сайт](https://example.com)", 16)]
    [InlineData("| A | B |\n| --- | --- |\n| one | two |\n\n```\n- не список\n**не жирный**\n```", 14)]
    public Task CaretAndFocusNeverChangeTextGeometry(string source, double size) => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor() { Text = source, FontSize = size };
        var other = new Button { Content = "outside" };
        var panel = new DockPanel(); DockPanel.SetDock(other, Dock.Bottom); panel.Children.Add(other); panel.Children.Add(editor);
        var host = Host(panel);
        try
        {
            host.Show(); host.UpdateLayout();
            var baseline = Geometry(editor);
            Keyboard.Focus(editor.TextArea);
            for (var offset = 0; offset <= source.Length; offset++)
            {
                editor.Select(offset, 0); host.UpdateLayout();
                Assert.Equal(baseline, Geometry(editor));
            }
            editor.Select(0, source.Length); host.UpdateLayout(); Assert.Equal(baseline, Geometry(editor));
            Keyboard.Focus(other); host.UpdateLayout(); Assert.Equal(baseline, Geometry(editor));
            Assert.Equal(source, editor.Text);
            Assert.DoesNotContain(editor.TextArea.TextView.VisualLines.SelectMany(line => line.Elements), element => element is InlineObjectElement inline && inline.Element is not MarkdownTableView);
        }
        finally { host.Close(); }
    });

    [Fact]
    public void CodeAndEscapedMarkersStayLiteral()
    {
        var source = "```\n- item\n**bold**\n```\n\n\\- literal\n\n`**code**`\n\n**real**";
        var layout = InlineMarkdownLayout.Create(source, Markdig.Markdown.Parse(source, MarkdownPipelines.Create()));
        Assert.DoesNotContain(layout.Decorations, part => part.Kind == InlineMarkdownLayout.DecorationKind.Bullet);
        Assert.DoesNotContain(layout.Styles, style => style.Style.HasFlag(InlineMarkdownLayout.Style.Bold) && style.Start < source.IndexOf("**real**", StringComparison.Ordinal));
    }

    [Fact]
    public Task ListIndentAndMarkerRemovalAreUndoable() => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor() { Text = "- first" };
        editor.Select(editor.Text.Length, 0);
        Assert.True(editor.HandleShortcut(Key.Tab, ModifierKeys.None)); Assert.Equal("  - first", editor.Text);
        editor.Select(editor.Text.Length, 0); editor.HandleShortcut(Key.Tab, ModifierKeys.Shift); Assert.Equal("- first", editor.Text);
        editor.Select(editor.Text.Length, 0); Assert.True(editor.HandleShortcut(Key.Home, ModifierKeys.None)); Assert.Equal(2, editor.CaretOffset);
        Assert.True(editor.HandleShortcut(Key.Back, ModifierKeys.None)); Assert.Equal("first", editor.Text);
        editor.Undo(); Assert.Equal("- first", editor.Text);
        editor.Text = "- "; editor.Select(2, 0); Assert.True(editor.HandleShortcut(Key.Back, ModifierKeys.None)); Assert.Empty(editor.Text);
    });

    [Fact]
    public Task WrappedListsAndTableColumnsHaveStableAlignment() => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor() { Text = "- длинный пункт который должен перенестись на следующую строку с отступом\n\n| Short | B |\n| --- | --- |\n| long value | **bold** |" };
        var host = Host(editor);
        try
        {
            host.Show(); host.UpdateLayout();
            var list = editor.TextArea.TextView.VisualLines.First(); Assert.True(list.TextLines.Count > 1);
            Assert.Equal(16, list.TextLines[1].GetDistanceFromCharacterHit(new System.Windows.Media.TextFormatting.CharacterHit(list.TextLines[0].Length, 0)), 1);
            var table = Assert.Single(editor.Tables.Views);
            var grid = Assert.IsType<Grid>(table.Scroll.Content);
            Assert.Equal(2, grid.ColumnDefinitions.Count);
            var borders = grid.Children.Cast<Border>().ToArray();
            Assert.Equal(4, borders.Length);
            foreach (var column in Enumerable.Range(0, 2))
            {
                var cells = borders.Where(cell => Grid.GetColumn(cell) == column).ToArray();
                Assert.Equal(cells[0].ActualWidth, cells[1].ActualWidth, 1);
            }
        }
        finally { host.Close(); }
    });

    [Theory]
    [InlineData(NoteTextAlignment.Center)]
    [InlineData(NoteTextAlignment.Right)]
    public Task NoteAlignmentDoesNotDisappearWhileEditing(NoteTextAlignment alignment) => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor() { Text = "**Короткий текст**" };
        editor.SetAppearance(new Note { FontSize = 16, TextAlignment = alignment });
        var host = Host(editor);
        try
        {
            host.Show(); host.UpdateLayout();
            var initial = Geometry(editor);
            var line = editor.TextArea.TextView.VisualLines.First();
            Assert.True(line.TextLines[0].GetDistanceFromCharacterHit(new System.Windows.Media.TextFormatting.CharacterHit(1, 0)) > 40);
            Keyboard.Focus(editor.TextArea); editor.Select(4, 0); host.UpdateLayout(); Assert.Equal(initial, Geometry(editor));
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task JustifiedTextKeepsItsSpacingWhenFocusChanges() => OnSta(() =>
    {
        const string source = "Это длинный абзац который занимает несколько строк и должен сохранять одинаковое выравнивание при редактировании и после потери фокуса.";
        using var editor = new LiveMarkdownEditor() { Text = source };
        editor.SetAppearance(new Note { TextAlignment = NoteTextAlignment.Justify });
        var host = Host(editor);
        try
        {
            host.Show(); host.UpdateLayout();
            var initial = Geometry(editor); var line = editor.TextArea.TextView.VisualLines.First();
            Assert.True(line.TextLines.Count > 1);
            Assert.True(line.TextLines.First().Width > editor.TextArea.TextView.ActualWidth - 4);
            Keyboard.Focus(editor.TextArea); editor.Select(10, 0); host.UpdateLayout(); Assert.Equal(initial, Geometry(editor));
            Assert.Equal(source, editor.Text);
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task TaskMarkerIsClickableWithoutReplacingTheParagraph() => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor() { Text = "- [ ] задача" };
        var host = Host(editor);
        try
        {
            host.Show(); host.UpdateLayout();
            var point = editor.TextArea.TextView.TranslatePoint(new Point(8, 8), editor);
            Assert.True(editor.TryToggleTask(point)); Assert.Equal("- [x] задача", editor.Text);
            editor.Undo(); Assert.Equal("- [ ] задача", editor.Text);
            var textPoint = editor.TextArea.TextView.TranslatePoint(new Point(40, 8), editor);
            Assert.False(editor.TryToggleTask(textPoint)); Assert.Equal("- [ ] задача", editor.Text);
        }
        finally { host.Close(); }
    });

    [Theory]
    [InlineData(ModifierKeys.None)]
    [InlineData(ModifierKeys.Shift)]
    public Task EnterScrollsNewCaretIntoViewBeforeTypingAndScrollbarIsSlim(ModifierKeys modifiers) => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = string.Join("\n", Enumerable.Range(1, 30).Select(n => $"Строка {n}")) };
        var host = Host(editor); host.Height = 180;
        try
        {
            host.Show(); host.UpdateLayout(); editor.Select(editor.Text.Length, 0); editor.ScrollToEnd(); host.UpdateLayout();
            var oldOffset = editor.VerticalOffset;
            Assert.True(editor.HandleShortcut(Key.Enter, modifiers)); host.UpdateLayout();
            editor.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background); host.UpdateLayout();
            Assert.True(editor.VerticalOffset > oldOffset);
            var view = editor.TextArea.TextView;
            var line = view.GetOrConstructVisualLine(editor.Document.GetLineByOffset(editor.CaretOffset));
            Assert.True(line.VisualTop + line.Height - view.ScrollOffset.Y <= view.ActualHeight + 1);
            Assert.Equal(ScrollBarVisibility.Auto, editor.VerticalScrollBarVisibility);
            static IEnumerable<DependencyObject> Descendants(DependencyObject root)
            {
                for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                {
                    var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i); yield return child;
                    foreach (var nested in Descendants(child)) yield return nested;
                }
            }
            var bar = Assert.Single(Descendants(editor).OfType<System.Windows.Controls.Primitives.ScrollBar>(), b => b.Orientation == Orientation.Vertical);
            Assert.Equal(6, bar.Width); Assert.InRange(bar.ActualWidth, 5.5, 6.5); Assert.Equal(Visibility.Visible, bar.Visibility);
            var lavenderTheme = NoteThemes.Get(DesktopNotes.Domain.NoteColor.Lavender);
            editor.SetTheme(lavenderTheme);
            var thumbBrush = Assert.IsType<System.Windows.Media.SolidColorBrush>(editor.Resources["DesktopNotes.EditorScrollThumb"]);
            Assert.Equal((byte)(lavenderTheme.Background.Color.R * .7), thumbBrush.Color.R);
            var beforePageUp = editor.VerticalOffset;
            System.Windows.Controls.Primitives.ScrollBar.PageUpCommand.Execute(null, bar); host.UpdateLayout();
            Assert.True(editor.VerticalOffset < beforePageUp);
            if (Environment.GetEnvironmentVariable("DESKTOPNOTES_TEST_RENDER_DIR") is { } directory)
            {
                host.Background = lavenderTheme.Background;
                Directory.CreateDirectory(directory);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(host);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, "note-scrollbar.png")); encoder.Save(file);
            }
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task WheelScrollAdvancesGraduallyAndEditingCancelsPendingMotion() => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = string.Join("\n", Enumerable.Range(1, 50).Select(n => $"Строка {n}")) };
        var host = Host(editor); host.Height = 180;
        try
        {
            host.Show(); host.UpdateLayout();
            void Wheel(int delta) => editor.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Wheel(-120);
            Assert.True(editor.SmoothScroll.IsAnimating); Assert.Equal(0, editor.VerticalOffset);
            editor.SmoothScroll.Advance(TimeSpan.FromMilliseconds(60)); host.UpdateLayout();
            var intermediate = editor.VerticalOffset; Assert.True(intermediate > 0);
            editor.SmoothScroll.Advance(TimeSpan.FromMilliseconds(180)); host.UpdateLayout();
            Assert.True(editor.VerticalOffset > intermediate); Assert.False(editor.SmoothScroll.IsAnimating);
            Wheel(-120); Assert.True(editor.SmoothScroll.IsAnimating);
            editor.Select(editor.Text.Length, 0); Assert.True(editor.HandleShortcut(Key.Enter, ModifierKeys.None));
            Assert.False(editor.SmoothScroll.IsAnimating); host.UpdateLayout();
            editor.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background); host.UpdateLayout();
            var offset = editor.VerticalOffset;
            editor.SmoothScroll.Advance(TimeSpan.FromMilliseconds(180)); host.UpdateLayout();
            Assert.Equal(offset, editor.VerticalOffset);
            editor.ScrollToHome(); host.UpdateLayout();
            Wheel(120); Assert.False(editor.SmoothScroll.IsAnimating); Assert.Equal(0, editor.VerticalOffset);
            Wheel(-120); Assert.True(editor.SmoothScroll.IsAnimating);
            host.Close(); editor.Dispose(); Assert.False(editor.SmoothScroll.IsAnimating);
        }
        finally { host.Close(); }
    });

    [Theory]
    [InlineData("- item", "\n")]
    [InlineData("- [ ] task", "\r\n")]
    [InlineData("2. item", "\n")]
    [InlineData("  - nested", "\n")]
    public Task ListContinuationCaretIsIndentedBeforeTyping(string item, string newline) => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = item + (newline == "\r\n" ? "\r\n" + item : "") };
        var host = Host(editor);
        try
        {
            host.Show(); editor.Select(editor.Text.Length, 0);
            editor.HandleShortcut(Key.Enter, ModifierKeys.Shift); host.UpdateLayout();
            double CaretX() => editor.TextArea.TextView.GetVisualPosition(editor.TextArea.Caret.Position, VisualYPosition.LineTop).X;
            var before = CaretX(); Assert.True(before >= 16, $"Caret X: {before}");
            editor.Document.Insert(editor.CaretOffset, "x"); host.UpdateLayout();
            // Compare the start of the typed continuation with the empty-line caret.
            editor.Select(editor.Text.Length - 1, 0); host.UpdateLayout();
            Assert.Equal(before, CaretX(), 1);
        }
        finally { host.Close(); }
    });

    [Theory]
    [InlineData("- item", "\n")]
    [InlineData("- [ ] task", "\r\n")]
    [InlineData("2. item", "\n")]
    public Task DoubleEnterLeavesListWithoutIndentingNewText(string item, string newline) => OnSta(() =>
    {
        var original = item + (newline == "\r\n" ? "\r\n" + item : "");
        using var editor = new LiveMarkdownEditor { Text = original };
        var host = Host(editor);
        try
        {
            host.Show(); editor.Select(editor.Text.Length, 0);
            editor.HandleShortcut(Key.Enter, ModifierKeys.None);
            editor.HandleShortcut(Key.Enter, ModifierKeys.None); host.UpdateLayout();
            Assert.Equal(original + newline, editor.Text);
            editor.Document.Insert(editor.CaretOffset, "plain text"); host.UpdateLayout();
            var offset = editor.Text.LastIndexOf("plain text", StringComparison.Ordinal);
            Assert.DoesNotContain(editor.Layout.Decorations, part => part.Start == offset && part.Kind == InlineMarkdownLayout.DecorationKind.Indent);
            editor.Undo(); editor.Undo(); Assert.EndsWith(newline + (item.StartsWith("2.") ? "3. " : item.StartsWith("- [") ? "- [ ] " : "- "), editor.Text);
        }
        finally { host.Close(); }
    });

    [Theory]
    [InlineData("- ")]
    [InlineData("1. ")]
    [InlineData("- [ ] ")]
    public Task CreatingListAbovePlainTextDoesNotIndentIt(string marker) => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = "first\nordinary text" };
        var host = Host(editor);
        try
        {
            host.Show(); editor.Document.Insert(0, marker); host.UpdateLayout();
            var offset = editor.Text.IndexOf("ordinary text", StringComparison.Ordinal);
            Assert.DoesNotContain(editor.Layout.Decorations, part => part.Start == offset && part.Kind == InlineMarkdownLayout.DecorationKind.Indent);
            editor.Select(offset, 0); host.UpdateLayout();
            Assert.Equal(0, editor.TextArea.TextView.GetVisualPosition(editor.TextArea.Caret.Position, VisualYPosition.LineTop).X, 1);
            editor.HandleShortcut(Key.Enter, ModifierKeys.Shift);
            Assert.Equal(marker + "first\n\nordinary text", editor.Text);
            editor.Undo(); Assert.Equal(marker + "first\nordinary text", editor.Text);
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task ExplicitContinuationSurvivesReloadAndAnotherShiftEnter() => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = "- item" };
        editor.Select(editor.Text.Length, 0); editor.HandleShortcut(Key.Enter, ModifierKeys.Shift);
        editor.Document.Insert(editor.CaretOffset, "continuation");
        editor.Select(editor.Text.Length, 0); editor.HandleShortcut(Key.Enter, ModifierKeys.Shift);
        editor.Document.Insert(editor.CaretOffset, "more");
        Assert.Equal("- item\n  continuation\n  more", editor.Text);
        using var reloaded = new LiveMarkdownEditor { Text = editor.Text };
        foreach (var offset in new[] { 7, 22 })
            Assert.Contains(reloaded.Layout.Decorations, part => part.Start == offset && part.Kind == InlineMarkdownLayout.DecorationKind.Indent);
    });

    [Theory]
    [InlineData("- item", "- ", "\n")]
    [InlineData("3. item", "4. ", "\r\n")]
    [InlineData("3) item", "4) ", "\n")]
    [InlineData("- [x] task", "- [ ] ", "\n")]
    [InlineData("- outer\n  + nested", "  + ", "\n")]
    public Task EnterAfterShiftEnterCreatesNextItem(string source, string nextMarker, string newline) => OnSta(() =>
    {
        if (newline == "\r\n") source = "intro\r\n\r\n" + source;
        using var editor = new LiveMarkdownEditor { Text = source };
        var host = Host(editor);
        try
        {
            host.Show(); editor.Select(editor.Text.Length, 0);
            editor.HandleShortcut(Key.Enter, ModifierKeys.Shift); host.UpdateLayout();
            var emptyContinuation = editor.Text;
            editor.HandleShortcut(Key.Enter, ModifierKeys.None); host.UpdateLayout();
            Assert.Equal(emptyContinuation + newline + nextMarker, editor.Text);
            editor.Undo(); Assert.Equal(emptyContinuation, editor.Text);
            editor.Document.Insert(editor.CaretOffset, "continued");
            editor.Select(editor.Text.Length, 0);
            var filledContinuation = editor.Text;
            editor.HandleShortcut(Key.Enter, ModifierKeys.None); host.UpdateLayout();
            Assert.Equal(filledContinuation + newline + nextMarker, editor.Text);
            Assert.Equal(editor.Text.Length, editor.CaretOffset);
            editor.HandleShortcut(Key.Enter, ModifierKeys.None);
            Assert.Equal(filledContinuation + newline, editor.Text);
            editor.Document.Insert(editor.CaretOffset, "plain"); host.UpdateLayout();
            var plain = editor.Text.LastIndexOf("plain", StringComparison.Ordinal);
            Assert.DoesNotContain(editor.Layout.Decorations, part => part.Start == plain && part.Kind == InlineMarkdownLayout.DecorationKind.Indent);
        }
        finally { host.Close(); }
    });


    [Fact]
    public Task CombinedStylesRenderTogetherIncludingCodeAndLinkDecorations() => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = "[==~~***`word`***~~==](https://example.com)" };
        var host = Host(editor);
        try
        {
            host.Show(); host.UpdateLayout();
            var offset = editor.Text.IndexOf("word", StringComparison.Ordinal);
            var line = editor.TextArea.TextView.VisualLines.Single();
            var element = line.Elements.First(item => item.DocumentLength > 0 && line.StartOffset + item.RelativeTextOffset <= offset && line.StartOffset + item.RelativeTextOffset + item.DocumentLength > offset);
            var properties = element.TextRunProperties;
            Assert.Equal(FontWeights.Bold, properties.Typeface.Weight);
            Assert.Equal(FontStyles.Italic, properties.Typeface.Style);
            Assert.Equal("Consolas", properties.Typeface.FontFamily.Source);
            Assert.Null(properties.BackgroundBrush); // Fills render below the selection layer.
            Assert.Same(editor.Theme.Link, properties.ForegroundBrush);
            Assert.Contains(properties.TextDecorations, decoration => decoration.Location == TextDecorationLocation.Strikethrough);
            Assert.Contains(properties.TextDecorations, decoration => decoration.Location == TextDecorationLocation.Underline);
            editor.Select(offset, 4); editor.ToggleBoldSelection(); host.UpdateLayout();
            Assert.DoesNotContain("***", editor.Text);
            editor.Undo(); host.UpdateLayout();
            Assert.Equal("[==~~***`word`***~~==](https://example.com)", editor.Text);
        }
        finally { host.Close(); }
    });

    private static Window Host(object content) => new() { Content = content, Left = -10000, Top = -10000, Width = 320, Height = 500, ShowActivated = false, ShowInTaskbar = false };
    private static string Geometry(TextEditor editor) => string.Join(";", editor.TextArea.TextView.VisualLines.Select(line =>
        $"{line.StartOffset}:{line.VisualTop:R}:{line.Height:R}:" + string.Join(",", line.TextLines.Select(text => $"{text.WidthIncludingTrailingWhitespace:R}/{text.Height:R}"))));
    private static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
