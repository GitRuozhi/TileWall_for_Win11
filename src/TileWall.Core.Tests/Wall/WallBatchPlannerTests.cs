using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class WallBatchPlannerTests
{
    private const double OneLaneWidth = 436;
    private const double TwoLaneWidth = 864;
    private const double ThreeLaneWidth = 1292;
    private const double OneGroupHeight = 112;

    private static TileMember Tile(string id, int column = 0, string? title = null) => new()
    {
        Id = id,
        LocalBounds = new GridRect(column, 0, 1, 1),
        Visual = new MemberVisual { Title = title },
        Entry = new ManagedEntryReference(id, $"Objects/{id}/{id}.lnk", ManagedEntryKind.Lnk),
    };

    private static StartGroup Group(string id, params TileMember[] members) => new()
    {
        Id = id,
        Members = members,
    };

    private static WallDocument Document(params WallLane[] lanes) => new()
    {
        Columns = lanes.Length,
        Rows = 2,
        Lanes = lanes,
        Generation = 7,
    };

    private static WallLane Lane(params StartGroup[] groups) => new() { Groups = groups };

    [Fact]
    public void ExistingHoleIsSkippedAndBatchAppendsWithoutMovingNeighbors()
    {
        var before = Document(Lane(Group("target", Tile("left", 0), Tile("right", 2))));
        var incoming = Tile("new", 7, "Same display name");
        var result = WallBatchPlanner.TryPlan(before, [incoming], "target", null, "batch",
            OneLaneWidth, OneGroupHeight);

        Assert.True(result.Success, result.Detail);
        var members = Assert.Single(result.Proposal!.Document.Lanes[0].Groups).Members;
        Assert.Equal(new GridRect(0, 0, 1, 1), members.Single(m => m.Id == "left").LocalBounds);
        Assert.Equal(new GridRect(2, 0, 1, 1), members.Single(m => m.Id == "right").LocalBounds);
        var placed = Assert.IsType<TileMember>(members.Single(m => m.Id == "new"));
        Assert.Equal(new GridRect(3, 0, 1, 1), placed.LocalBounds);
        Assert.Equal(incoming.Entry, placed.Entry);
        Assert.Equal(incoming.Visual, placed.Visual);
        Assert.Equal(new GridRect(7, 0, 1, 1), incoming.LocalBounds);
    }

    [Fact]
    public void FullTargetCreatesUnnamedGroupInNextLane()
    {
        var full = Enumerable.Range(0, 8).Select(i => Tile($"old-{i}", i)).ToArray();
        var before = Document(Lane(Group("target", full)));
        var result = WallBatchPlanner.TryPlan(before, [Tile("one"), Tile("two")],
            "target", 0, "import", TwoLaneWidth, OneGroupHeight);

        Assert.True(result.Success, result.Detail);
        var proposed = result.Proposal!.Document;
        Assert.Equal(2, proposed.Columns);
        Assert.Equal(8, Assert.Single(proposed.Lanes[0].Groups).Members.Count);
        var spill = Assert.Single(proposed.Lanes[1].Groups);
        Assert.Equal("import-0001", spill.Id);
        Assert.Null(spill.Title);
        Assert.Equal(["one", "two"], spill.Members.Select(m => m.Id).OrderBy(id => id));
        Assert.Equal(8, before.Lanes[0].Groups[0].Members.Count);
    }

    [Fact]
    public void FullCurrentRowExpandsHeightBeforeCreatingAnotherGroup()
    {
        var full = Enumerable.Range(0, 8).Select(i => Tile($"old-{i}", i)).ToArray();
        var before = Document(Lane(Group("target", full)));
        var result = WallBatchPlanner.TryPlan(before, [Tile("added")], "target", 0,
            "batch", OneLaneWidth, 176);

        Assert.True(result.Success, result.Detail);
        var proposed = result.Proposal!.Document;
        Assert.Equal(3, proposed.Rows);
        var group = Assert.Single(proposed.Lanes[0].Groups);
        Assert.Equal(new GridRect(0, 1, 1, 1), group.Members.Single(m => m.Id == "added").LocalBounds);
        Assert.Equal(7, before.Generation);
    }

    [Fact]
    public void WrapsToEarlierLaneAfterPreferredLaneIsFull()
    {
        var full = Enumerable.Range(0, 8).Select(i => Tile($"old-{i}", i)).ToArray();
        var before = Document(Lane(), Lane(), Lane(Group("target", full)));
        var result = WallBatchPlanner.TryPlan(before, [Tile("added")], "target", 2,
            "batch", ThreeLaneWidth, OneGroupHeight);

        Assert.True(result.Success, result.Detail);
        Assert.Empty(result.Proposal!.Document.Lanes[1].Groups);
        Assert.Equal("added", Assert.Single(Assert.Single(result.Proposal.Document.Lanes[0].Groups).Members).Id);
    }

    [Fact]
    public void GroupTitleConsumesCapacity()
    {
        var empty = WallDocument.Empty(1, 1);
        var before = WallJson.Serialize(empty);
        Assert.Equal(OneGroupHeight, new WallMetrics().WallHeight(2));
        var beyondMonitor = WallBatchPlanner.TryPlan(empty, [Tile("one")], null, 0,
            "batch", OneLaneWidth, OneGroupHeight - 1);
        Assert.False(beyondMonitor.Success);
        Assert.Equal(WallRejection.NoSpace, beyondMonitor.Rejection);
        Assert.Null(beyondMonitor.Proposal);
        Assert.Equal(before, WallJson.Serialize(empty));
        var exact = WallBatchPlanner.TryPlan(empty, [Tile("one")], null, 0,
            "batch", OneLaneWidth, OneGroupHeight);
        Assert.True(exact.Success, exact.Detail);
        Assert.Equal(2, exact.Proposal!.Document.Rows);
    }

    [Fact]
    public void LastTileFailureRejectsWholeBatchAndLeavesSourceUnchanged()
    {
        var existing = Enumerable.Range(0, 7).Select(i => Tile($"old-{i}", i)).ToArray();
        var before = Document(Lane(Group("target", existing)));
        var serialized = WallJson.Serialize(before);
        var result = WallBatchPlanner.TryPlan(before, [Tile("first"), Tile("old-0")],
            "target", 0, "batch", OneLaneWidth, OneGroupHeight);

        Assert.False(result.Success);
        Assert.Null(result.Proposal);
        Assert.Equal(serialized, WallJson.Serialize(before));
        Assert.Equal(7, before.Generation);
    }

    [Fact]
    public void SameTitleDistinctIdsSurviveAndGenerationMovesOnlyOnce()
    {
        var before = WallDocument.Empty(1, 2) with { Generation = 11 };
        var one = Tile("id-one", title: "Same");
        var two = Tile("id-two", title: "Same");
        var result = WallBatchPlanner.TryPlan(before, [one, two], null, 0,
            "batch", OneLaneWidth, OneGroupHeight);

        Assert.True(result.Success, result.Detail);
        var proposed = result.Proposal!.Document;
        Assert.Equal(12, proposed.Generation);
        Assert.Equal(11, before.Generation);
        var group = Assert.Single(proposed.Lanes[0].Groups);
        Assert.Equal("batch-0001", group.Id);
        Assert.Equal(["id-one", "id-two"], group.Members.Select(m => m.Id).OrderBy(id => id));
        Assert.All(group.Members, member => Assert.Equal("Same", member.Visual.Title));
        Assert.Equal(one.Entry, group.Members.Single(member => member.Id == one.Id).Entry);
        Assert.Equal(two.Entry, group.Members.Single(member => member.Id == two.Id).Entry);
    }

    [Fact]
    public void BatchKeepsInputMemberSizesWhenSearchingCurrentGroupAndNewGroup()
    {
        var before = WallDocument.Empty(1, 1);
        var first = Tile("medium-one") with { LocalBounds = new GridRect(7, 8, 2, 2) };
        var second = Tile("medium-two") with { LocalBounds = new GridRect(6, 9, 2, 2) };

        var result = WallBatchPlanner.TryPlan(before, [first, second], null, 0,
            "batch", OneLaneWidth, new WallMetrics().WallHeight(3));

        Assert.True(result.Success, result.Detail);
        var group = Assert.Single(result.Proposal!.Document.Lanes[0].Groups);
        var firstPlacement = group.Members.Single(member => member.Id == first.Id).LocalBounds;
        var secondPlacement = group.Members.Single(member => member.Id == second.Id).LocalBounds;
        Assert.Equal(new GridSize(2, 2), firstPlacement.Size);
        Assert.Equal(new GridSize(2, 2), secondPlacement.Size);
        Assert.False(firstPlacement.Intersects(secondPlacement));
        Assert.Equal(new GridSize(2, 2), first.LocalBounds.Size);
        Assert.Equal(3, result.Proposal.Document.Rows);
    }

    [Fact]
    public void NewLaneRespectsBudgetAndGroupIdsAvoidMemberIds()
    {
        var full = Enumerable.Range(0, 8).Select(i => Tile($"old-{i}", i)).ToArray();
        var before = Document(Lane(Group("target", full)));
        var serialized = WallJson.Serialize(before);
        var beyondMonitor = WallBatchPlanner.TryPlan(before, [Tile("added")], "target", 0,
            "batch", OneLaneWidth, OneGroupHeight);
        Assert.False(beyondMonitor.Success);
        Assert.Equal(WallRejection.NoSpace, beyondMonitor.Rejection);
        Assert.Null(beyondMonitor.Proposal);
        Assert.Equal(serialized, WallJson.Serialize(before));

        var incoming = Tile("batch-0001");
        var allowed = WallBatchPlanner.TryPlan(before, [incoming], "target", 0,
            "batch", TwoLaneWidth, OneGroupHeight);
        Assert.True(allowed.Success, allowed.Detail);
        Assert.Equal("batch-0002", Assert.Single(allowed.Proposal!.Document.Lanes[1].Groups).Id);
        Assert.Equal("batch-0001", Assert.Single(allowed.Proposal.Document.Lanes[1].Groups[0].Members).Id);
    }

    [Fact]
    public void MissingTargetAndBadLaneAreRejected()
    {
        var before = WallDocument.Empty(1, 2);
        Assert.Equal(WallRejection.ObjectNotFound, WallBatchPlanner.TryPlan(before,
            [Tile("one")], "missing", 0, "batch", OneLaneWidth, OneGroupHeight).Rejection);
        Assert.Equal(WallRejection.InvalidAction, WallBatchPlanner.TryPlan(before,
            [Tile("one")], null, -1, "batch", OneLaneWidth, OneGroupHeight).Rejection);
    }

    [Fact]
    public void SavedHeightBeyondViewportUsesFinalCapacityCompression()
    {
        var metrics = new WallMetrics();
        var existing = Tile("existing") with { LocalBounds = new GridRect(0, 6, 2, 2) };
        var before = Document(Lane(Group("target", existing))) with { Rows = 10 };
        var serialized = WallJson.Serialize(before);
        var incoming = Tile("incoming") with { LocalBounds = new GridRect(0, 0, 2, 2) };

        var result = WallBatchPlanner.TryPlan(before, [incoming], "target", 0, "batch",
            metrics.WallWidth(1), metrics.WallHeight(6), metrics);

        Assert.True(result.Success, result.Detail);
        Assert.Equal(6, result.Proposal!.Document.Rows);
        Assert.Equal(before.Generation + 1, result.Proposal.Document.Generation);
        var members = Assert.Single(result.Proposal.Document.Lanes[0].Groups).Members;
        Assert.Equal(new GridRect(0, 3, 2, 2), members.Single(member => member.Id == existing.Id).LocalBounds);
        Assert.Equal(new GridRect(2, 3, 2, 2), members.Single(member => member.Id == incoming.Id).LocalBounds);
        Assert.Equal(serialized, WallJson.Serialize(before));
    }

    [Fact]
    public void FragmentedEmptyRowsCanCompressAnAppendBeyondViewport()
    {
        var metrics = new WallMetrics();
        var first = Tile("first") with { LocalBounds = new GridRect(0, 0, 4, 2) };
        var firstRight = Tile("first-right") with { LocalBounds = new GridRect(4, 0, 4, 2) };
        var second = Tile("second") with { LocalBounds = new GridRect(0, 4, 4, 2) };
        var secondRight = Tile("second-right") with { LocalBounds = new GridRect(4, 4, 4, 2) };
        var before = Document(Lane(Group("target", first, firstRight, second, secondRight))) with { Rows = 8 };
        var incoming = Tile("incoming") with { LocalBounds = new GridRect(0, 0, 4, 3) };

        var result = WallBatchPlanner.TryPlan(before, [incoming], "target", 0, "batch",
            metrics.WallWidth(1), metrics.WallHeight(8), metrics);

        Assert.True(result.Success, result.Detail);
        Assert.Equal(8, result.Proposal!.Document.Rows);
        var members = Assert.Single(result.Proposal.Document.Lanes[0].Groups).Members;
        Assert.Equal(new GridRect(0, 2, 4, 2), members.Single(member => member.Id == second.Id).LocalBounds);
        Assert.Equal(new GridRect(0, 4, 4, 3), members.Single(member => member.Id == incoming.Id).LocalBounds);
        Assert.Equal(new GridRect(0, 4, 4, 2), second.LocalBounds);
    }

    [Fact]
    public void LastTileStillRejectsWholeBatchAfterCompression()
    {
        var metrics = new WallMetrics();
        var existing = Tile("existing") with { LocalBounds = new GridRect(0, 6, 4, 2) };
        var existingRight = Tile("existing-right") with { LocalBounds = new GridRect(4, 6, 4, 2) };
        var before = Document(Lane(Group("target", existing, existingRight))) with { Rows = 10 };
        var serialized = WallJson.Serialize(before);
        var first = Tile("first") with { LocalBounds = new GridRect(0, 0, 4, 2) };
        var second = Tile("second") with { LocalBounds = new GridRect(0, 0, 4, 2) };
        var last = Tile("last") with { LocalBounds = new GridRect(0, 0, 4, 2) };
        var width = metrics.WallWidth(1);
        var height = metrics.WallHeight(5);
        Assert.True(WallBatchPlanner.TryPlan(before, [first, second], "target", 0, "batch", width, height, metrics).Success);

        var result = WallBatchPlanner.TryPlan(before, [first, second, last], "target", 0, "batch", width, height, metrics);

        Assert.False(result.Success);
        Assert.Equal(WallRejection.NoSpace, result.Rejection);
        Assert.Null(result.Proposal);
        Assert.Equal(serialized, WallJson.Serialize(before));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompressionReservesExpandedTrailingLane(bool fullScreen)
    {
        var metrics = new WallMetrics();
        var existing = Tile("existing") with { LocalBounds = new GridRect(0, 6, 4, 2) };
        var before = Document(Lane(Group("target", existing)), Lane()) with
        {
            Rows = 10,
            Settings = new WallSettings { FullScreen = fullScreen, ExpandColumns = true },
        };
        var incoming = Tile("incoming") with { LocalBounds = new GridRect(0, 0, 4, 2) };

        var fits = WallBatchPlanner.TryPlan(before, [incoming], "target", 0, "batch",
            metrics.WallWidth(2), metrics.WallHeight(5), metrics);
        Assert.True(fits.Success, fits.Detail);
        Assert.Equal(2, fits.Proposal!.Document.Columns);
        Assert.Equal(5, fits.Proposal.Document.Rows);
        Assert.Empty(fits.Proposal.Document.Lanes[^1].Groups);

        var cannotFitTail = WallBatchPlanner.TryPlan(before, [incoming], "target", 0, "batch",
            metrics.WallWidth(1), metrics.WallHeight(5), metrics);
        Assert.False(cannotFitTail.Success);
        Assert.Null(cannotFitTail.Proposal);
        Assert.Empty(before.Lanes[^1].Groups);
    }
}
