using TileWall.Core.Application;
using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class WallResizeCompactionTests
{
    private static TileMember Tile(string id, int row, int height = 1, int column = 0) => new()
    {
        Id = id, LocalBounds = new GridRect(column, row, 1, height),
    };

    private static StartGroup Group(string id, params WallMember[] members) => new()
    {
        Id = id, Members = members,
    };

    private static WallDocument Document(int rows, params StartGroup[][] lanes) => new()
    {
        Columns = lanes.Length, Rows = rows,
        Lanes = lanes.Select(groups => new WallLane { Groups = groups }).ToArray(),
    };

    private static WallLayoutResult Resize(WallDocument document, int columns, int rows) =>
        WallLayoutEngine.TryApply(document,
            new ResizeWallAction(document.Generation, columns, rows), 3000, 3000);

    [Fact]
    public void ShrinkRemovesLeadingAndMiddleEmptyLanesWithoutMovingMembersInsideGroups()
    {
        var first = Group("first", Tile("a", 2));
        var second = Group("second", Tile("b", 3));
        var current = Document(8, [], [first], [], [second]);
        var before = WallJson.Serialize(current);

        var result = Resize(current, 2, 8);

        Assert.True(result.Success, result.Detail);
        var next = result.Proposal!.Document;
        Assert.Equal(2, next.Columns);
        Assert.Equal(new[] { "first", "second" }, next.Lanes.SelectMany(l => l.Groups).Select(g => g.Id));
        Assert.Equal(first.Members[0].LocalBounds, next.Lanes[0].Groups[0].Members[0].LocalBounds);
        Assert.Equal(second.Members[0].LocalBounds, next.Lanes[1].Groups[0].Members[0].LocalBounds);
        Assert.Equal(current.Generation + 1, next.Generation);
        Assert.Equal(before, WallJson.Serialize(current));
    }

    [Theory]
    [InlineData(4, 1, 3)]
    [InlineData(3, 1, 2)]
    public void HorizontalShrinkDeletesOnlyNeededEmptyLanesStartingAtTheRight(
        int columns, int firstLane, int secondLane)
    {
        var current = Document(8, [], [Group("first", Tile("a", 0))], [],
            [Group("second", Tile("b", 0))], []);

        var result = Resize(current, columns, 8);

        Assert.True(result.Success, result.Detail);
        var lanes = result.Proposal!.Document.Lanes;
        Assert.Equal(columns, lanes.Count);
        Assert.Empty(lanes[0].Groups);
        Assert.Equal("first", Assert.Single(lanes[firstLane].Groups).Id);
        Assert.Equal("second", Assert.Single(lanes[secondLane].Groups).Id);
    }

    [Fact]
    public void RemovingMiddleEmptyLaneKeepsSurvivingImportMarkersAndNewLanesAreManual()
    {
        var current = Document(8, [], [Group("first", Tile("a", 0))], [],
            [Group("second", Tile("b", 0))]);
        current = current with
        {
            Lanes =
            [
                current.Lanes[0],
                current.Lanes[1] with { ImportBatchId = "batch-first" },
                current.Lanes[2] with { ImportBatchId = "batch-empty" },
                current.Lanes[3] with { ImportBatchId = "batch-second" },
            ],
        };
        var shrink = Resize(current, 3, 8);
        Assert.True(shrink.Success, shrink.Detail);
        var compacted = shrink.Proposal!.Document;
        Assert.Null(compacted.Lanes[0].ImportBatchId);
        Assert.Empty(compacted.Lanes[0].Groups);
        Assert.Equal("batch-first", compacted.Lanes[1].ImportBatchId);
        Assert.Equal("first", Assert.Single(compacted.Lanes[1].Groups).Id);
        Assert.Equal("batch-second", compacted.Lanes[2].ImportBatchId);
        Assert.Equal("second", Assert.Single(compacted.Lanes[2].Groups).Id);

        var expand = Resize(compacted, 4, 8);
        Assert.True(expand.Success, expand.Detail);
        var expanded = expand.Proposal!.Document;
        Assert.Null(expanded.Lanes[3].ImportBatchId);
        Assert.Empty(expanded.Lanes[3].Groups);
        Assert.Equal("batch-first", expanded.Lanes[1].ImportBatchId);
        Assert.Equal("batch-second", expanded.Lanes[2].ImportBatchId);
    }

    [Theory]
    [InlineData(6, 2, 4)]
    [InlineData(5, 2, 3)]
    [InlineData(4, 1, 2)]
    public void VerticalShrinkUsesWallTailWhitespaceThenOnlyTheNecessaryLastEmptyRows(
        int rows, int middleRow, int lastRow)
    {
        var current = Document(8, [Group("group", Tile("first", 0), Tile("middle", 2), Tile("last", 4))]);

        var result = Resize(current, 1, rows);

        Assert.True(result.Success, result.Detail);
        var members = result.Proposal!.Document.Lanes[0].Groups[0].Members;
        Assert.Same(current.Lanes[0].Groups[0].Members[0], members[0]);
        Assert.Equal(middleRow, members[1].LocalBounds.Row);
        Assert.Equal(lastRow, members[2].LocalBounds.Row);
    }

    [Fact]
    public void VerticalShrinkPrefersEmptyRowsInLaterGroupsAndLeavesOtherLanesAlone()
    {
        var first = Group("first", Tile("a", 1));
        var last = Group("last", Tile("b", 1));
        var shortGroup = Group("short", Tile("c", 1));
        var current = Document(10, [first, last], [shortGroup]);

        var result = Resize(current, 2, 5);

        Assert.True(result.Success, result.Detail);
        var lanes = result.Proposal!.Document.Lanes;
        Assert.Same(first, lanes[0].Groups[0]);
        Assert.Equal(0, lanes[0].Groups[1].Members[0].LocalBounds.Row);
        Assert.Same(shortGroup, Assert.Single(lanes[1].Groups));
    }

    [Fact]
    public void VerticalShrinkRemovesLeadingAndInternalWholeEmptyRows()
    {
        var first = Tile("first", 2, 2, 3);
        var second = Tile("second", 5, column: 6);
        var current = Document(8, [Group("group", first, second)]);
        var before = WallJson.Serialize(current);

        var result = Resize(current, 1, 4);

        Assert.True(result.Success, result.Detail);
        var members = result.Proposal!.Document.Lanes[0].Groups[0].Members;
        Assert.Equal(new[] { "first", "second" }, members.Select(m => m.Id));
        Assert.Equal(new GridRect(3, 0, 1, 2), members[0].LocalBounds);
        Assert.Equal(new GridRect(6, 2, 1, 1), members[1].LocalBounds);
        Assert.Empty(WallLayoutEngine.ValidateGeometry(result.Proposal.Document));
        Assert.Equal(before, WallJson.Serialize(current));
    }

    [Fact]
    public void VerticalShrinkCompactsEachGroupWithoutMergingOrReorderingGroups()
    {
        var current = Document(10,
            [Group("first", Tile("a", 1)), Group("second", Tile("b", 1))]);

        var result = Resize(current, 1, 4);

        Assert.True(result.Success, result.Detail);
        var groups = result.Proposal!.Document.Lanes[0].Groups;
        Assert.Equal(new[] { "first", "second" }, groups.Select(g => g.Id));
        Assert.All(groups, group => Assert.Equal(0, Assert.Single(group.Members).LocalBounds.Row));
    }

    [Fact]
    public void VerticalShrinkRejectsTwoGroupsWhenTheirMinimumHeightExceedsThreeRows()
    {
        var current = Document(10,
            [Group("first", Tile("a", 1)), Group("second", Tile("b", 1))]);
        var before = WallJson.Serialize(current);

        var result = Resize(current, 1, 3);

        Assert.Equal(WallRejection.NoSpace, result.Rejection);
        Assert.Null(result.Proposal);
        Assert.Equal(before, WallJson.Serialize(current));
    }

    [Fact]
    public void OccupiedRowsOfMultiRowPuzzleRemainWholeAndPuzzleContentsDoNotChange()
    {
        var puzzle = new PuzzleMember
        {
            Id = "puzzle", LocalBounds = new GridRect(2, 2, 2, 2),
            Partitions = [new GridRect(0, 0, 1, 2), new GridRect(1, 0, 1, 2)],
        };
        var current = Document(9, [Group("group", Tile("a", 1), puzzle, Tile("b", 5))]);

        var result = Resize(current, 1, 5);

        Assert.True(result.Success, result.Detail);
        var members = result.Proposal!.Document.Lanes[0].Groups[0].Members;
        var moved = Assert.IsType<PuzzleMember>(members[1]);
        Assert.Equal(new GridRect(2, 1, 2, 2), moved.LocalBounds);
        Assert.Equal(puzzle.Partitions, moved.Partitions);
        Assert.Equal(puzzle.Images, moved.Images);
        Assert.Equal(3, members[2].LocalBounds.Row);
    }

    [Fact]
    public void VerticalShrinkDoesNotDeleteEmptyLanes()
    {
        var current = Document(8, [], [Group("group", Tile("a", 2))], []);

        var result = Resize(current, 3, 2);

        Assert.True(result.Success, result.Detail);
        Assert.Equal(3, result.Proposal!.Document.Columns);
        Assert.Empty(result.Proposal.Document.Lanes[0].Groups);
        Assert.Equal("group", Assert.Single(result.Proposal.Document.Lanes[1].Groups).Id);
        Assert.Empty(result.Proposal.Document.Lanes[2].Groups);
    }

    [Fact]
    public void ExpansionDoesNotCompactLanesOrRows()
    {
        var current = Document(8, [], [Group("group", Tile("a", 3))]);

        var result = Resize(current, 3, 10);

        Assert.True(result.Success, result.Detail);
        Assert.Empty(result.Proposal!.Document.Lanes[0].Groups);
        Assert.Equal(3, result.Proposal.Document.Lanes[1].Groups[0].Members[0].LocalBounds.Row);
        Assert.Empty(result.Proposal.Document.Lanes[2].Groups);
    }

    [Fact]
    public void NonemptyLanesMergeAsWholeGroupsWhenEmptyLaneRemovalIsInsufficient()
    {
        var current = Document(8, [Group("first", Tile("a", 0))], [], [Group("second", Tile("b", 0))]);
        var before = WallJson.Serialize(current);

        var result = Resize(current, 1, 8);

        Assert.True(result.Success, result.Detail);
        var groups = Assert.Single(result.Proposal!.Document.Lanes).Groups;
        Assert.Equal(new[] { "first", "second" }, groups.Select(group => group.Id));
        Assert.Same(current.Lanes[0].Groups[0], groups[0]);
        Assert.Same(current.Lanes[2].Groups[0], groups[1]);
        Assert.Equal(before, WallJson.Serialize(current));
    }

    [Fact]
    public void WholeGroupReflowTakesTheLongestContinuousPrefixAndPreservesMemberGeometry()
    {
        var a = Group("a", Tile("a-tile", 0));
        var b = Group("b", Tile("b-tile", 0));
        var c = Group("c", Tile("c-tile", 0));
        var d = Group("d", Tile("d-tile", 0));
        var e = Group("e", Tile("e-tile", 0));
        var current = Document(8, [a, b], [c], [d, e]);

        var result = Resize(current, 2, 6);

        Assert.True(result.Success, result.Detail);
        var lanes = result.Proposal!.Document.Lanes;
        Assert.Equal(new[] { "a", "b", "c" }, lanes[0].Groups.Select(group => group.Id));
        Assert.Equal(new[] { "d", "e" }, lanes[1].Groups.Select(group => group.Id));
        Assert.Equal(new[] { a, b, c, d, e }, lanes.SelectMany(lane => lane.Groups));
    }

    [Fact]
    public void ReflowProbesMinimumHeightsButOnlyDeletesNeededRowsInFinalLanes()
    {
        var a = Group("a", Tile("a-tile", 1));
        var b = Group("b", Tile("b-tile", 1));
        var c = Group("c", Tile("c-tile", 0));
        var current = Document(8, [a], [b], [c]);

        var result = Resize(current, 2, 5);

        Assert.True(result.Success, result.Detail);
        var lanes = result.Proposal!.Document.Lanes;
        Assert.Equal(new[] { "a", "b" }, lanes[0].Groups.Select(group => group.Id));
        Assert.Same(a, lanes[0].Groups[0]);
        Assert.Equal(0, lanes[0].Groups[1].Members[0].LocalBounds.Row);
        Assert.Same(c, Assert.Single(lanes[1].Groups));
    }

    [Fact]
    public void ReflowNeverSkipsATallGroupToFillEarlierSpaceAndFailureIsAtomic()
    {
        var current = Document(12,
            [Group("a", Tile("a-tile", 0, 2))],
            [Group("b", Tile("b-tile", 0, 4))],
            [Group("c", Tile("c-tile", 0))]);
        var before = WallJson.Serialize(current);

        var result = Resize(current, 2, 5);

        Assert.False(result.Success);
        Assert.Equal(WallRejection.NoSpace, result.Rejection);
        Assert.Null(result.Proposal);
        Assert.Equal(before, WallJson.Serialize(current));
    }

    [Fact]
    public void ShrinkRejectsWhenOccupiedRowsAndTitlesCannotFit()
    {
        var current = Document(8, [Group("group", Tile("a", 2, 2))]);
        var before = WallJson.Serialize(current);

        var result = Resize(current, 1, 2);

        Assert.Equal(WallRejection.NoSpace, result.Rejection);
        Assert.Null(result.Proposal);
        Assert.Equal(before, WallJson.Serialize(current));
    }

    [Fact]
    public void CompactedResizeSaveFailureKeepsSnapshotAndRetryCommitsCompaction()
    {
        var files = new InMemoryFileStore();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wall-resize-" + Guid.NewGuid().ToString("N")));
        var session = new WallSession(root, files, new FakeLinkFileService(files));
        Assert.Equal(WallOpenStatus.Loaded, session.Open(3, 8).Status);
        var add = session.Propose(new AddMemberAction(session.Current.Generation,
            Tile("tile", 2), null, 2, 0, "group"), 3000, 3000);
        Assert.True(add.Success, add.Detail);
        Assert.True(session.Commit(add.Proposal!).Committed);
        var before = WallJson.Serialize(session.Current);
        var beforeState = files.ReadAllBytes(Path.Combine(root, "state.json"));
        var resize = session.Propose(new ResizeWallAction(session.Current.Generation, 1, 2), 3000, 3000);
        Assert.True(resize.Success, resize.Detail);

        files.InjectedFault = FaultPoint.BeforeMove;
        Assert.False(session.Commit(resize.Proposal!).Committed);
        Assert.Equal(before, WallJson.Serialize(session.Current));
        Assert.Equal(beforeState, files.ReadAllBytes(Path.Combine(root, "state.json")));

        files.InjectedFault = FaultPoint.None;
        Assert.True(session.Commit(resize.Proposal!).Committed);
        Assert.Equal(1, session.Current.Columns);
        Assert.Equal(0, session.Current.Lanes[0].Groups[0].Members[0].LocalBounds.Row);
        Assert.Equal(session.Current.Generation,
            UserDataSnapshotJson.ReadManifest(files.ReadAllBytes(Path.Combine(root, "state.json"))).Generation);
    }
}
