using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopNotes.Application;
using DesktopNotes.Domain;
using DesktopNotes.Presentation;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class NoteTitleTests
{
    private sealed class Repository : INoteRepository
    {
        public WorkspaceSnapshot Stored = new();
        public Task<LoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new LoadResult(Stored));
        public Task SaveAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken = default)
        { Stored = snapshot; return Task.CompletedTask; }
    }
    private sealed class Layers : IWindowLayerController { public void Apply(nint handle, WindowLayer layer) { } }

    [Theory]
    [InlineData("## Второй\n# Первый\n# Другой", "Первый")]
    [InlineData("# **Жирный** *курсив* `код` [ссылка](https://example.com) #", "Жирный курсив код ссылка")]
    [InlineData("Название\r\n========\r\n\r\nТекст", "Название")]
    [InlineData("```\n# Код\n```\n\n> # Цитата\n\n- # Список\n\n# Настоящий", "Настоящий")]
    [InlineData("\\# Текст\n\n## H2\n\n#без пробела", "Без названия")]
    [InlineData("# \n\n# Заголовок", "Заголовок")]
    public void TitleIsPlainTextOfFirstDocumentH1(string markdown, string expected) =>
        Assert.Equal(expected, MarkdownNoteTitle.Read(markdown));

    [Fact]
    public async Task NewNoteHeadingTracksEditsUndoAndRestart()
    {
        var repository = new Repository(); var service = new NoteService(repository); var note = service.Create(10, 20);
        Assert.Equal("# Новая заметка\n\n", note.Markdown);
        using var vm = new NoteViewModel(note, service, () => { }, () => { });
        var properties = new List<string?>(); vm.PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        vm.Markdown = "# **Проект**\n\nТекст";
        Assert.Equal("Проект", vm.Title); Assert.Equal("Проект", service.Get(note.Id).Title);
        Assert.Contains(nameof(NoteViewModel.Title), properties);
        vm.ToggleCollapsed.Execute(null); vm.SetHidden(true); vm.SetBounds(20, 30, 340, 300);
        Assert.Equal("Проект", vm.Title);
        await repository.SaveAsync(service.Snapshot);
        var restored = new NoteService(repository); await restored.LoadAsync();
        Assert.Equal(vm.Snapshot, restored.Get(note.Id));
        vm.Markdown = "Текст без заголовка"; Assert.Equal("Без названия", vm.Title);
        service.Update(service.Get(note.Id) with { Markdown = "# Вернули заголовок" });
        Assert.Equal("Вернули заголовок", vm.Title);
    }

    [Fact]
    public async Task LegacyNotesKeepSourceAndFallbackTitle()
    {
        var legacy = new Note { Title = "Старое имя", Markdown = "Старый текст", IsCollapsed = true };
        var withHeading = new Note { Title = "Устаревшее имя", Markdown = "# Актуальное имя\n\nТекст" };
        var service = new NoteService(new Repository { Stored = new WorkspaceSnapshot { Notes = [legacy, withHeading] } });
        await service.LoadAsync();
        Assert.Equal(legacy, service.Get(legacy.Id));
        Assert.Equal(withHeading.Markdown, service.Get(withHeading.Id).Markdown);
        Assert.Equal("Актуальное имя", service.Get(withHeading.Id).Title);
        service.SetHidden(legacy.Id, true); Assert.Equal("Старое имя", service.Get(legacy.Id).Title);
    }

    [Fact]
    public Task OnlyCollapsedNoteShowsSeparateTitleAndEditorUndoUpdatesIt() => OnSta(() =>
    {
        var service = new NoteService(new Repository()); var note = service.Create(0, 0);
        using var vm = new NoteViewModel(note, service, () => { }, () => { });
        var window = new NoteWindow(vm, new Layers()) { Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var root = Assert.IsType<Grid>(Assert.IsType<Grid>(window.Content).Children[0]);
            var header = Assert.IsType<Grid>(root.Children[0]);
            var titleArea = Assert.IsType<Border>(header.Children[0]);
            var title = Assert.IsType<TextBlock>(titleArea.Child);
            Assert.Equal(Visibility.Collapsed, title.Visibility);
            var editor = window.MarkdownEditor;
            Assert.InRange(editor.TextArea.TextView.TranslatePoint(new Point(0, 0), window).Y, 10, 20);
            var headingPoint = editor.TextArea.TextView.TranslatePoint(new Point(20, 10), window);
            var hit = window.InputHitTest(headingPoint) as DependencyObject;
            while (hit is not null && hit != editor) hit = VisualTreeHelper.GetParent(hit);
            Assert.Same(editor, hit); // Overlay must not block editing the first heading.
            if (Environment.GetEnvironmentVariable("DESKTOPNOTES_TEST_RENDER_DIR") is { } directory)
            {
                Directory.CreateDirectory(directory);
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, "note-insets.png")); encoder.Save(file);
            }
            Assert.Equal("# Новая заметка\n\n", window.MarkdownEditor.Text);
            window.MarkdownEditor.Document.Replace(2, "Новая заметка".Length, "Работа");
            Assert.Equal("Работа", vm.Title); Assert.Equal("Работа", window.Title);
            vm.ToggleCollapsed.Execute(null);
            Assert.Equal(Visibility.Visible, title.Visibility); Assert.Equal("Работа", title.Text);
            Assert.Equal(44, window.Height);
            window.MarkdownEditor.Undo(); Assert.Equal("Новая заметка", title.Text);
            vm.ToggleCollapsed.Execute(null); Assert.Equal(Visibility.Collapsed, title.Visibility);
            Assert.Equal(280, window.Height);
            window.UpdateLayout();
            Assert.InRange(editor.TextArea.TextView.TranslatePoint(new Point(0, 0), window).Y, 10, 20);
        }
        finally { window.ClosePermanently(); }
    });

    [Fact]
    public Task ErasingTitleKeepsH1AndSingleStepUndo() => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = "# Старое имя\n\nСодержимое" };
        editor.EnableNoteTitle(); editor.Document.UndoStack.ClearAll();
        editor.Document.Remove(0, "# Старое имя".Length);
        Assert.Equal("# \n\nСодержимое", editor.Text);
        editor.Document.Insert(2, "Новое имя");
        Assert.Equal("Новое имя", MarkdownNoteTitle.Read(editor.Text));
        Assert.Contains(editor.Layout.Styles, style => style.Start == 0 && style.HeadingLevel == 1);
        editor.Undo(); Assert.Equal("# \n\nСодержимое", editor.Text);
        editor.Undo(); Assert.Equal("# Старое имя\n\nСодержимое", editor.Text);
        editor.Redo(); Assert.Equal("# \n\nСодержимое", editor.Text);
        editor.Select(2, 0);
        Assert.True(editor.HandleShortcut(System.Windows.Input.Key.Back, System.Windows.Input.ModifierKeys.None));
        Assert.StartsWith("# ", editor.Text);
        editor.SetHeading(3); Assert.StartsWith("# ", editor.Text);
        editor.Select(0, editor.Document.TextLength);
        editor.TextArea.Selection.ReplaceSelectionWithText(""); Assert.Equal("# ", editor.Text);
        editor.TextArea.PerformTextInput("Название");
        Assert.Equal("# Название", editor.Text);
        Assert.Equal(editor.Text.Length, editor.CaretOffset);
    });

    [Fact]
    public Task EnterBeforeWordKeepsFollowingLineVisibleAndTitleSpacingStable() => OnSta(() =>
    {
        var service = new NoteService(new Repository()); var note = service.Create(0, 0);
        service.Update(note with { Markdown = "# Заголовок\nСлово" });
        using var vm = new NoteViewModel(service.Get(note.Id), service, () => { }, () => { });
        var window = new NoteWindow(vm, new Layers()) { Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.Show(); window.UpdateLayout(); var editor = window.MarkdownEditor;
            Assert.True(double.IsNaN(editor.Height));
            var headingHeight = editor.TextArea.TextView.VisualLines.First().Height;
            using var plain = new LiveMarkdownEditor { Text = editor.Text };
            var comparison = new Window { Content = plain, Width = 320, Height = 280, Left = -10000, Top = -10000, ShowActivated = false };
            try
            {
                comparison.Show(); comparison.UpdateLayout();
                Assert.InRange(headingHeight - plain.TextArea.TextView.VisualLines.First().Height, 5.9, 6.1);
            }
            finally { comparison.Close(); }
            editor.Select(editor.Text.IndexOf("Слово", StringComparison.Ordinal), 0);
            Assert.True(editor.HandleShortcut(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.None));
            window.UpdateLayout();
            Assert.Equal("# Заголовок\n\nСлово", editor.Text);
            var view = editor.TextArea.TextView;
            Assert.Contains(view.VisualLines, line => line.FirstDocumentLine.LineNumber == 3 && line.TextLines.Any(text => text.Length >= 5));
            var wordLine = view.VisualLines.Single(line => line.FirstDocumentLine.LineNumber == 3);
            Assert.True(wordLine.VisualTop + wordLine.Height <= view.ActualHeight);
            editor.Select(2, 0); window.UpdateLayout();
            Assert.Equal(headingHeight, view.VisualLines.First().Height);
            Assert.Contains(view.VisualLines, line => line.FirstDocumentLine.LineNumber == 3);
            editor.Undo(); window.UpdateLayout(); Assert.Equal("# Заголовок\nСлово", editor.Text);
        }
        finally { window.ClosePermanently(); }
    });

    private static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception e) { completion.SetException(e); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
