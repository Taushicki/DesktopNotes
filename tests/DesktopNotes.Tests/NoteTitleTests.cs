using System.Windows;
using System.Windows.Controls;
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
            Assert.Equal("# Новая заметка\n\n", window.MarkdownEditor.Text);
            window.MarkdownEditor.Document.Replace(2, "Новая заметка".Length, "Работа");
            Assert.Equal("Работа", vm.Title); Assert.Equal("Работа", window.Title);
            vm.ToggleCollapsed.Execute(null);
            Assert.Equal(Visibility.Visible, title.Visibility); Assert.Equal("Работа", title.Text);
            Assert.Equal(44, window.Height);
            window.MarkdownEditor.Undo(); Assert.Equal("Новая заметка", title.Text);
            vm.ToggleCollapsed.Execute(null); Assert.Equal(Visibility.Collapsed, title.Visibility);
            Assert.Equal(280, window.Height);
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
