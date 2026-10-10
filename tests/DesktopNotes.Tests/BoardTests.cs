using DesktopNotes.Application;
using DesktopNotes.Domain;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class BoardTests
{
    private sealed class Repository : INoteRepository
    {
        public WorkspaceSnapshot Stored = new();
        public Task<LoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new LoadResult(Stored));
        public Task SaveAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken = default) { Stored = snapshot; return Task.CompletedTask; }
    }

    [Fact]
    public void GroupDraftIsCommittedAndSupportsAddingAndRemovingMembers()
    {
        var service = new NoteService(new Repository());
        var a = service.Create(0, 0); var b = service.Create(350, 0); var c = service.Create(700, 0);
        var selection = new GroupSelectionSession(service);
        selection.Toggle(a.Id); selection.Toggle(b.Id);
        Assert.Empty(service.Groups);
        selection.Commit();
        var group = Assert.Single(service.Groups);
        selection.Toggle(a.Id); selection.Toggle(c.Id); selection.Commit();
        Assert.Equal(3, Assert.Single(service.Groups).NoteIds.Count);
        selection.Toggle(a.Id); selection.Toggle(b.Id); selection.Commit();
        Assert.Null(service.GroupOf(b.Id));
        Assert.Equal(group.Id, Assert.Single(service.Groups).Id);
        Assert.Equal(group.BorderColor, Assert.Single(service.Groups).BorderColor);
        Assert.Equal(2, Assert.Single(service.Groups).NoteIds.Count);
    }

    [Fact]
    public void FreeNoteCanBeSelectedBeforeExistingGroupAndCancellationDoesNotMutateGroup()
    {
        var service = new NoteService(new Repository());
        var a = service.Create(0, 0); var b = service.Create(350, 0); var c = service.Create(700, 0);
        var group = service.CreateGroup([a.Id, b.Id]);
        var selection = new GroupSelectionSession(service);
        selection.Toggle(c.Id); selection.Toggle(a.Id); selection.Commit();
        Assert.Equal(group.Id, service.GroupOf(c.Id)!.Id);
        selection.Toggle(a.Id); selection.Toggle(c.Id); selection.Cancel();
        Assert.Equal(3, Assert.Single(service.Groups).NoteIds.Count);
    }

    [Fact]
    public void MovingWithPinnedMemberIsAtomicAndDoesNotMoveAnyMember()
    {
        var service = new NoteService(new Repository());
        var a = service.Create(0, 0); var b = service.Create(350, 0);
        service.CreateGroup([a.Id, b.Id]);
        service.Update(b with { IsPinned = true });
        var positions = new Dictionary<Guid, (double, double)> { [a.Id] = (50, 80), [b.Id] = (400, 80) };
        Assert.False(service.MoveNotes(positions));
        Assert.Equal(0, service.Get(a.Id).X);
        Assert.Equal(350, service.Get(b.Id).X);
        service.Update(service.Get(b.Id) with { IsPinned = false });
        Assert.True(service.MoveNotes(positions));
        Assert.Equal(350, service.Get(b.Id).X - service.Get(a.Id).X);
    }

    [Fact]
    public async Task GroupsLinksAndPinsAreIncludedInAutosaveAndDeletionCleansReferences()
    {
        var repository = new Repository();
        var service = new NoteService(repository);
        using var save = new AutosaveCoordinator(service, repository, TimeSpan.FromHours(1));
        var a = service.Create(0, 0); var b = service.Create(350, 0);
        var group = service.CreateGroup([a.Id, b.Id]);
        service.SetGroupColor(group.Id, "#112233");
        service.Update(a with { IsPinned = true });
        var link = service.AddLink(a.Id, b.Id);
        service.SetLinkStyle(link.Id, new LinkStyle { Width = 2, Pattern = LinkLinePattern.Dotted, Route = LinkRoute.Curved });
        Assert.Throws<InvalidDataException>(() => service.SetLinkStyle(link.Id, new LinkStyle { Width = -1 }));
        Assert.Equal(link.Id, service.AddLink(a.Id, b.Id).Id);
        Assert.Throws<InvalidOperationException>(() => service.AddLink(a.Id, a.Id));
        await save.FlushAsync();
        var restarted = new NoteService(repository);
        await restarted.LoadAsync();
        Assert.True(restarted.Get(a.Id).IsPinned);
        Assert.Equal("#112233", Assert.Single(restarted.Groups).BorderColor);
        Assert.Single(restarted.Links);
        Assert.Equal(LinkLinePattern.Dotted, restarted.Links[0].Style.Pattern);
        service.Delete(a.Id);
        Assert.Empty(service.Groups);
        Assert.Empty(service.Links);
        Assert.True(service.Contains(b.Id));
        await save.FlushAsync();
        repository.Stored.Validate();
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(6, 6)]
    [InlineData(7, 0)]
    [InlineData(-5, -5)]
    public void SnappingUsesSmallThresholdAndCanBeReleased(double gap, double expected)
    {
        var offset = SnapCalculator.GetOffset([new(0, 0, 100, 100)], [new(100 + gap, 0, 100, 100)], 6);
        // Target starts to the right; positive correction closes a positive gap.
        Assert.Equal(expected, offset.X);
    }

    [Fact]
    public void SnappingAvoidsDistantNotesAndPreservesGroupOffsets()
    {
        Assert.Equal((0d, 0d), SnapCalculator.GetOffset([new(0, 0, 100, 100)], [new(105, 500, 100, 100)]));
        var moving = new NoteBounds[] { new(0, 0, 100, 100), new(120, 0, 100, 100) };
        var snap = SnapCalculator.GetOffset(moving, [new(225, 0, 100, 100)], 6);
        Assert.Equal(5, snap.X);
        Assert.Equal(120, moving[1].Offset(snap.X, snap.Y).X - moving[0].Offset(snap.X, snap.Y).X);
    }

    [Theory]
    // Horizontal neighbours align top or bottom corners, including unequal sizes.
    [InlineData(105, 5, 80, 60, 5, 5)]
    [InlineData(105, 45, 80, 60, 5, 5)]
    [InlineData(-85, -5, 80, 60, -5, -5)]
    [InlineData(-85, 35, 80, 60, -5, -5)]
    // Vertical neighbours align left or right corners.
    [InlineData(5, 105, 60, 80, 5, 5)]
    [InlineData(45, 105, 60, 80, 5, 5)]
    [InlineData(-5, -85, 60, 80, -5, -5)]
    [InlineData(35, -85, 60, 80, -5, -5)]
    // Already touching edges can align in the perpendicular direction.
    [InlineData(100, 6, 80, 60, 0, 6)]
    [InlineData(6, 100, 60, 80, 6, 0)]
    // Beyond the threshold only the adjacent edge snaps.
    [InlineData(105, 7, 80, 60, 5, 0)]
    [InlineData(7, 105, 60, 80, 0, 5)]
    public void AdjacentNotesAlignCorrespondingCorners(double x, double y, double width, double height, double expectedX, double expectedY)
    {
        Assert.Equal((expectedX, expectedY), SnapCalculator.GetOffset([new(0, 0, 100, 100)], [new(x, y, width, height)], 6));
    }

    [Theory]
    [InlineData(105, 106, 5, 6)]
    [InlineData(-105, -106, -5, -6)]
    [InlineData(106, -105, 6, -5)]
    [InlineData(-106, 105, -6, 5)]
    [InlineData(107, 105, 0, 0)]
    [InlineData(105, 107, 0, 0)]
    public void DiagonalCornersSnapOnlyWithinThreshold(double x, double y, double expectedX, double expectedY)
    {
        Assert.Equal((expectedX, expectedY), SnapCalculator.GetOffset([new(0, 0, 100, 100)], [new(x, y, 100, 100)], 6));
    }

    [Fact]
    public void CornerAlignmentUsesScaledThresholdAndMovesWholeGroup()
    {
        var moving = new NoteBounds[] { new(0, 0, 100, 100), new(120, 30, 100, 100) };
        var snap = SnapCalculator.GetOffset(moving, [new(229, 39, 100, 100)], 12);
        Assert.Equal((9d, 9d), snap);
        Assert.Equal(120, moving[1].Offset(snap.X, snap.Y).X - moving[0].Offset(snap.X, snap.Y).X);
        Assert.Equal(30, moving[1].Offset(snap.X, snap.Y).Y - moving[0].Offset(snap.X, snap.Y).Y);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 1)]
    [InlineData(3, 1, 1.5)]
    [InlineData(4, 1, 0)]
    [InlineData(-3, 1, -1.5)]
    [InlineData(2, 2, 2)]
    [InlineData(6, 2, 3)]
    [InlineData(7, 2, 0)]
    public void DragAttractionIsWeakerHasShorterRadiusAndScalesWithDpi(double gap, double scale, double expected)
    {
        var moving = new NoteBounds[] { new(0, 0, 100, 100) };
        var targets = new NoteBounds[] { new(100 + gap, 0, 100, 100) };
        Assert.Equal((expected, 0d), SnapCalculator.GetDragOffset(moving, targets, scale));
        // A preceding exact snap must not retain or increase attraction on the next move.
        SnapCalculator.GetDragOffset(moving, [new(100, 0, 100, 100)], scale);
        Assert.Equal((expected, 0d), SnapCalculator.GetDragOffset(moving, targets, scale));
    }

    [Fact]
    public void SofterCornerAttractionStillAllowsExactAlignment()
    {
        Assert.Equal((1.5d, 1d), SnapCalculator.GetDragOffset([new(0, 0, 100, 100)], [new(103, 102, 100, 100)]));
        Assert.Equal((1d, 1d), SnapCalculator.GetDragOffset([new(0, 0, 100, 100)], [new(101, 101, 100, 100)]));
        Assert.Equal((0d, 0d), SnapCalculator.GetOffset([new(0, 0, 100, 100)], [new(104, 0, 100, 100)]));
    }

    [Fact]
    public void TouchingNotesStillHaveVisibleArrowRouteOutsideTheirBounds()
    {
        var route = ArrowRouter.Route(new(0, 0, 100, 100), new(100, 0, 100, 100));
        Assert.Equal(4, route.Count);
        Assert.True(route[1].Y < 0);
        Assert.True(route[2].Y < 0);
        Assert.Equal(new DiagramPoint(50, 0), route[0]);
        Assert.Equal(new DiagramPoint(150, 0), route[^1]);
    }

    [Fact]
    public void ArrowEndpointsAreOnNoteBoundariesAndChangeWithMovement()
    {
        var first = ArrowRouter.Route(new(0, 0, 100, 100), new(300, 0, 100, 100));
        var shifted = ArrowRouter.Route(new(20, 30, 100, 100), new(320, 30, 100, 100));
        Assert.Equal(2, first.Count);
        Assert.Equal(new DiagramPoint(100, 50), first[0]);
        Assert.Equal(new DiagramPoint(300, 50), first[1]);
        Assert.Equal(first[0].X + 20, shifted[0].X);
        Assert.Equal(first[1].Y + 30, shifted[1].Y);
    }

    [Theory]
    [InlineData(NoteSide.Left, 0, 50)]
    [InlineData(NoteSide.Top, 50, 0)]
    [InlineData(NoteSide.Right, 100, 50)]
    [InlineData(NoteSide.Bottom, 50, 100)]
    public void ArrowRemainsOnChosenSideAndStartsOutward(NoteSide side, double x, double y)
    {
        var source = new NoteBounds(0, 0, 100, 100);
        var route = ArrowRouter.Route(source, new(300, 250, 100, 100), side);
        Assert.Equal(new DiagramPoint(x, y), route[0]);
        var direction = route[1];
        Assert.True(side switch
        {
            NoteSide.Left => direction.X < x && direction.Y == y,
            NoteSide.Right => direction.X > x && direction.Y == y,
            NoteSide.Top => direction.Y < y && direction.X == x,
            _ => direction.Y > y && direction.X == x
        });
        var moved = ArrowRouter.Route(source.Offset(20, 30), new(320, 280, 100, 100), side);
        Assert.Equal(new DiagramPoint(x + 20, y + 30), moved[0]);
    }

    [Fact]
    public void LinkCreationStoresSideAndRejectsInvalidSideWithoutChangingWorkspace()
    {
        var service = new NoteService(new Repository());
        var a = service.Create(0, 0); var b = service.Create(350, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => service.AddLink(a.Id, b.Id, (NoteSide)99));
        Assert.Empty(service.Links);
        var link = service.AddLink(a.Id, b.Id, NoteSide.Bottom);
        Assert.Equal(NoteSide.Bottom, link.SourceSide);
        Assert.Equal(link.Id, service.AddLink(a.Id, b.Id, NoteSide.Left).Id);
        Assert.Equal(NoteSide.Bottom, Assert.Single(service.Links).SourceSide);
        Assert.Throws<InvalidDataException>(() => (service.Snapshot with { Links = [link with { SourceSide = (NoteSide)99 }] }).Validate());
    }

    [Theory]
    [InlineData(300, 0, NoteSide.Right, NoteSide.Left)]
    [InlineData(-300, 0, NoteSide.Left, NoteSide.Right)]
    [InlineData(0, -300, NoteSide.Top, NoteSide.Bottom)]
    [InlineData(0, 300, NoteSide.Bottom, NoteSide.Top)]
    public void AutomaticConnectionChangesToNearestSidesWhenTargetMoves(double x, double y, NoteSide sourceSide, NoteSide targetSide)
    {
        var source = new NoteBounds(0, 0, 100, 100);
        var target = new NoteBounds(x, y, 100, 100);
        var route = ArrowRouter.Route(source, target);
        Assert.Equal(ArrowRouter.Anchor(source, sourceSide), route[0]);
        Assert.Equal(ArrowRouter.Anchor(target, targetSide), route[^1]);
        Assert.Equal(sourceSide, ArrowRouter.NearestSide(source, new(x + 50, y + 50)));
    }

    [Fact]
    public void SelectingAndDeletingConnectionPreservesOtherConnectionsAndNotes()
    {
        var service = new NoteService(new Repository());
        var a = service.Create(0, 0); var b = service.Create(350, 0); var c = service.Create(700, 0);
        var first = service.AddLink(a.Id, b.Id); var second = service.AddLink(b.Id, c.Id);
        var selection = new LinkSelectionSession(service);
        Assert.False(selection.DeleteSelected());
        selection.Select(first.Id); selection.Select(second.Id);
        selection.Select(Guid.NewGuid());
        Assert.Equal(second.Id, selection.SelectedId);
        Assert.True(selection.DeleteSelected());
        Assert.Null(selection.SelectedId);
        Assert.Equal(first.Id, Assert.Single(service.Links).Id);
        Assert.Equal(3, service.Notes.Count);
        selection.Select(first.Id); selection.Clear();
        Assert.False(selection.DeleteSelected());
        Assert.Single(service.Links);
    }

    [Theory]
    [InlineData(300, 180)]
    [InlineData(-300, -180)]
    [InlineData(180, -300)]
    [InlineData(-180, 300)]
    public void DiagonalConnectionsUseOnlyHorizontalAndVerticalSegments(double x, double y)
    {
        var route = ArrowRouter.Route(new(0, 0, 100, 100), new(x, y, 100, 100));
        Assert.True(route.Count >= 3);
        foreach (var (a, b) in route.Zip(route.Skip(1))) Assert.True(a.X == b.X || a.Y == b.Y);
        Assert.All(route.Zip(route.Skip(1)), segment => Assert.NotEqual(segment.First, segment.Second));
    }

    [Theory]
    [InlineData(82, 150, true)]
    [InlineData(81, 150, false)]
    [InlineData(88, 88, true)]
    [InlineData(86, 86, false)]
    [InlineData(150, 150, true)]
    [InlineData(218, 150, true)]
    public void ArrowCanAttachNearTheOutsideOfATargetWithoutAnExactHit(double x, double y, bool expected)
    {
        var id = Guid.NewGuid();
        var result = ConnectionTargeting.FindClosest([(id, new NoteBounds(100, 100, 100, 100))], new(x, y));
        Assert.Equal(expected ? id : (Guid?)null, result);
    }
}
