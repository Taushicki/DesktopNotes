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
    public Task EnteredLineBreaksRemainVisibleInTextListsAndQuotes() => OnStaThread(() =>
    {
        var renderer = new MarkdownRenderer();
        foreach (var source in new[] { "первая\nвторая", "первая\r\nвторая", "**первая**\n*вторая*", "первая  \nвторая" })
        {
            var paragraph = Assert.IsType<Paragraph>(renderer.Render(source).Blocks.FirstBlock);
            Assert.Single(paragraph.Inlines.OfType<LineBreak>());
            Assert.Contains("\r\n", new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Trim());
        }
        var list = Assert.IsType<System.Windows.Documents.List>(renderer.Render("- первая\nпродолжение\n- вторая").Blocks.FirstBlock);
        Assert.Equal(2, list.ListItems.Count);
        var item = Assert.IsType<Paragraph>(list.ListItems.FirstListItem.Blocks.FirstBlock);
        Assert.Single(item.Inlines.OfType<LineBreak>());
        Assert.Contains("первая\r\nпродолжение", new TextRange(item.ContentStart, item.ContentEnd).Text);
        var quote = Assert.IsType<Section>(renderer.Render("> первая\n> продолжение").Blocks.FirstBlock);
        Assert.Single(Assert.IsType<Paragraph>(quote.Blocks.FirstBlock).Inlines.OfType<LineBreak>());
    });

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
