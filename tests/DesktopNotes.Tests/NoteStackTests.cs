using DesktopNotes.Application;
using DesktopNotes.Domain;
using DesktopNotes.Infrastructure;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class NoteStackTests
{
    private sealed class Repository : INoteRepository
    {
        public Task<LoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new LoadResult(Array.Empty<Note>()));
        public Task SaveAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static NoteService Board(params Note[] notes)
    {
        var service = new NoteService(new Repository());
        foreach (var note in notes) service.Update(note);
        return service;
    }

    [Fact]
    public void EntireMixedHeightColumnMovesAndReturnsWithoutDrift()
    {
        var upper = new Note { X = -500, Y = -100, Height = 300 };
        var middle = new Note { X = -480, Y = 200, IsCollapsed = true, Height = 420 };
        var bottom = new Note { X = -490, Y = 244, Height = 200 };
        var service = Board(upper, middle, bottom);
        for (var i = 0; i < 5; i++)
        {
            service.Update(service.Get(upper.Id) with { IsCollapsed = true });
            Assert.Equal(-56, service.Get(middle.Id).Y);
            Assert.Equal(-12, service.Get(bottom.Id).Y);
            Assert.True(service.Get(middle.Id).IsCollapsed);
            Assert.Equal(420, service.Get(middle.Id).Height);
            service.Update(service.Get(upper.Id) with { IsCollapsed = false });
            Assert.Equal(middle.Y, service.Get(middle.Id).Y);
            Assert.Equal(bottom.Y, service.Get(bottom.Id).Y);
        }
    }

    [Fact]
    public void ExpandingMiddleOnlyMovesItsDescendants()
    {
        var upper = new Note { X = 100, Y = 100 };
        var middle = new Note { X = 100, Y = 380, IsCollapsed = true };
        var bottom = new Note { X = 100, Y = 424 };
        var service = Board(upper, middle, bottom);
        service.Update(middle with { IsCollapsed = false });
        Assert.Equal(upper, service.Get(upper.Id));
        Assert.Equal(660, service.Get(bottom.Id).Y);
    }

    [Theory]
    [InlineData(0, 45.1, false)] // Close, but outside the exact contact tolerance.
    [InlineData(320, 44, false)] // Corner contact is not a vertical column.
    [InlineData(0, 44, true)]
    [InlineData(0, 44.6, true)] // DPI rounding.
    [InlineData(0, 43.4, true)]
    public void OnlyTouchingLowerNotesMove(double x, double y, bool attached)
    {
        var upper = new Note { X = 0, Y = 0, IsCollapsed = true };
        var lower = new Note { X = x, Y = y };
        var service = Board(upper, lower);
        service.Update(upper with { IsCollapsed = false });
        Assert.Equal(y + (attached ? 236 : 0), service.Get(lower.Id).Y, 5);
    }

    [Fact]
    public void HiddenNotesBreakTheChainAndUnrelatedNotesStayPut()
    {
        var upper = new Note { X = 0, Y = 0 };
        var hidden = new Note { X = 0, Y = 280, IsHidden = true };
        var bottom = new Note { X = 0, Y = 560 };
        var side = new Note { X = 320, Y = 0 };
        var service = Board(upper, hidden, bottom, side);
        service.Update(upper with { IsCollapsed = true });
        Assert.Equal(hidden, service.Get(hidden.Id));
        Assert.Equal(bottom, service.Get(bottom.Id));
        Assert.Equal(side, service.Get(side.Id));
    }

    [Fact]
    public void BranchesAndSharedDescendantsMoveOnceWithOneChangeNotification()
    {
        var upper = new Note { X = 0, Y = 0, Width = 640 };
        var left = new Note { X = 0, Y = 280 };
        var right = new Note { X = 320, Y = 280 };
        var bottom = new Note { X = 0, Y = 560, Width = 640 };
        var service = Board(upper, left, right, bottom);
        var changed = 0; var notified = new List<Guid>();
        service.Changed += () => changed++;
        service.NoteUpdated += note =>
        {
            notified.Add(note.Id);
            Assert.Equal(324, service.Get(bottom.Id).Y);
        };
        service.Update(upper with { IsCollapsed = true });
        Assert.Equal(1, changed);
        Assert.Equal(4, notified.Distinct().Count());
        Assert.Equal(4, notified.Count);
        Assert.Equal(44, service.Get(left.Id).Y);
        Assert.Equal(44, service.Get(right.Id).Y);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PinnedFollowerOrPinnedGroupPreventsWholeTransition(bool groupPinned)
    {
        var upper = new Note { X = 0, Y = 0 };
        var lower = new Note { X = 0, Y = 280, IsPinned = !groupPinned };
        var member = new Note { X = 1000, Y = 1000, IsPinned = true };
        var service = Board(upper, lower, member);
        if (groupPinned) service.CreateGroup([lower.Id, member.Id]);
        service.Update(upper with { IsCollapsed = true });
        Assert.Equal(upper, service.Get(upper.Id));
        Assert.Equal(lower, service.Get(lower.Id));
    }

    [Fact]
    public async Task SavedColumnRestoresAndRemainsAttached()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DesktopNotesStack-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new JsonNoteRepository(directory);
            var upper = new Note { X = 0, Y = 0 };
            var lower = new Note { X = 0, Y = 280 };
            var service = Board(upper, lower);
            service.Update(upper with { IsCollapsed = true });
            await repository.SaveAsync(service.Snapshot);
            var restarted = new NoteService(repository);
            await restarted.LoadAsync();
            restarted.Update(restarted.Get(upper.Id) with { IsCollapsed = false });
            Assert.Equal(lower.Y, restarted.Get(lower.Id).Y);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
