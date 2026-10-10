using DesktopNotes.Application;
using DesktopNotes.Domain;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class ApplicationTests
{
    private sealed class MemoryRepository : INoteRepository
    {
        public IReadOnlyList<Note> Stored = [];
        public bool Fail;
        public int Writes;
        public Task<LoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new LoadResult(Stored));
        public Task SaveAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("disk full");
            Stored = snapshot.Notes.ToArray(); Writes++; return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task HideRestoreAndDeleteSurviveRestart()
    {
        var repository = new MemoryRepository();
        var service = new NoteService(repository);
        using var save = new AutosaveCoordinator(service, repository, TimeSpan.FromHours(1));
        var note = service.Create(10, 20);
        service.SetHidden(note.Id, true);
        await save.FlushAsync();
        var restarted = new NoteService(repository);
        await restarted.LoadAsync();
        Assert.True(Assert.Single(restarted.Notes).IsHidden);
        service.SetHidden(note.Id, false);
        Assert.False(service.Get(note.Id).IsHidden);
        service.Delete(note.Id);
        await save.FlushAsync();
        await restarted.LoadAsync();
        Assert.Empty(restarted.Notes);
    }

    [Fact]
    public async Task FailedSaveRetainsChangesAndCanBeRetried()
    {
        var repository = new MemoryRepository { Fail = true };
        var service = new NoteService(repository);
        using var save = new AutosaveCoordinator(service, repository, TimeSpan.FromHours(1));
        Exception? error = null;
        save.SaveFailed += ex => error = ex;
        var note = service.Create(1, 2);
        service.Update(note with { Markdown = "unsaved" });
        Assert.False(await save.FlushAsync());
        Assert.IsType<IOException>(error);
        Assert.Equal("unsaved", service.Get(note.Id).Markdown);
        repository.Fail = false;
        Assert.True(await save.FlushAsync());
        Assert.Equal("unsaved", Assert.Single(repository.Stored).Markdown);
    }

    [Fact]
    public async Task RapidChangesAreDebouncedIntoOneSave()
    {
        var repository = new MemoryRepository();
        var service = new NoteService(repository);
        using var save = new AutosaveCoordinator(service, repository, TimeSpan.FromMilliseconds(50));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        save.Saved += () => completion.TrySetResult();
        var note = service.Create(1, 2);
        service.Update(note with { Markdown = "first" });
        service.Update(note with { Markdown = "last" });
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, repository.Writes);
        Assert.Equal("last", Assert.Single(repository.Stored).Markdown);
    }
}
