using System.Windows.Documents;
using DesktopNotes.Application;
using DesktopNotes.Domain;
using DesktopNotes.Presentation;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class PresentationTests
{
    private sealed class EmptyRepository : INoteRepository
    {
        public Task<LoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new LoadResult([]));
        public Task SaveAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public void HiddenAndCollapsedStateIsRetainedWhenEditingAndMoving()
    {
        var service = new NoteService(new EmptyRepository());
        var note = service.Create(1, 2);
        var vm = new NoteViewModel(note, service, () => { }, () => { });
        vm.ToggleCollapsed.Execute(null);
        vm.SetHidden(true);
        vm.SetBounds(30, 40, 320, vm.Snapshot.Height);
        vm.Markdown = "# Изменённый текст";
        var stored = service.Get(note.Id);
        Assert.True(stored.IsCollapsed);
        Assert.True(stored.IsHidden);
        Assert.Equal(280, stored.Height);
        vm.SetHidden(false);
        vm.ToggleCollapsed.Execute(null);
        Assert.False(service.Get(note.Id).IsHidden);
        Assert.False(service.Get(note.Id).IsCollapsed);
    }

    [Fact]
    public Task MarkdownRendersStructureAndOnlyWebLinks() => OnStaThread(() =>
    {
        var document = new MarkdownRenderer().Render("# Заголовок\n\n**Жирный** и *курсив* и `код`\n\n- один\n- два\n\n> цитата\n\n```\nvar x = 1;\n```\n\n[веб](https://example.com) [файл](file:///C:/secret) ![изображение](https://example.com/image.png)");
        Assert.Contains(document.Blocks, b => b is System.Windows.Documents.List);
        Assert.Contains(document.Blocks, b => b is Section);
        var first = Assert.IsType<Paragraph>(document.Blocks.FirstBlock);
        Assert.Equal("Заголовок", new TextRange(first.ContentStart, first.ContentEnd).Text.Trim());
        var last = Assert.IsType<Paragraph>(document.Blocks.LastBlock);
        var links = last.Inlines.OfType<Hyperlink>().ToArray();
        Assert.Single(links);
        Assert.Equal("https://example.com/", links[0].NavigateUri.AbsoluteUri);
        Assert.Contains("[изображение]", new TextRange(last.ContentStart, last.ContentEnd).Text);
    });

    private static Task OnStaThread(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
