using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class WallBatchPlannerTests
{
    private const double OneLaneWidth = 436;
    private const double TwoLaneWidth = 860;
    private const double ThreeLaneWidth = 1284;
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
    public void ExistingHoleIsFilledWithoutMovingNeighbors()
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
        Assert.Equal(new GridRect(1, 0, 1, 1), placed.LocalBounds);
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
        Assert.Equal(OneGroupHeight, new WallMetrics().WallHeight(2));
        var noTitleRoom = WallBatchPlanner.TryPlan(empty, [Tile("one")], null, 0,
            "batch", OneLaneWidth, OneGroupHeight - 1);
        Assert.False(noTitleRoom.Success);
        Assert.Null(noTitleRoom.Proposal);

        var withTitleRoom = WallBatchPlanner.TryPlan(empty, [Tile("one")], null, 0,
            "batch", OneLaneWidth, OneGroupHeight);
        Assert.True(withTitleRoom.Success, withTitleRoom.Detail);
        Assert.Equal(2, withTitleRoom.Proposal!.Document.Rows);
    }

    [Fact]
    public void LastTileFailureRejectsWholeBatchAndLeavesSourceUnchanged()
    {
        var existing = Enumerable.Range(0, 7).Select(i => Tile($"old-{i}", i)).ToArray();
        var before = Document(Lane(Group("target", existing)));
        var serialized = WallJson.Serialize(before);
        var result = WallBatchPlanner.TryPlan(before, [Tile("first"), Tile("last")],
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
    public void NewLaneMustFitWorkAreaAndGroupIdsAvoidMemberIds()
    {
        var full = Enumerable.Range(0, 8).Select(i => Tile($"old-{i}", i)).ToArray();
        var before = Document(Lane(Group("target", full)));
        var blocked = WallBatchPlanner.TryPlan(before, [Tile("added")], "target", 0,
            "batch", OneLaneWidth, OneGroupHeight);
        Assert.False(blocked.Success);
        Assert.Null(blocked.Proposal);

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
}
