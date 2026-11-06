using TileWall.Core.Grid;
using TileWall.Core.Import;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class WallLayoutLockTests
{
    private static readonly WallMetrics Metrics = new();
    private static TileMember Tile(string id, int column = 0, int row = 0, int width = 1, int height = 1) =>
        new() { Id = id, LocalBounds = new(column, row, width, height) };
    private static StartGroup Group(string id, params WallMember[] members) => new() { Id = id, Members = members };
    private static WallDocument Locked(int rows, params WallLane[] lanes) => new()
    {
        Columns = lanes.Length, Rows = rows, Lanes = lanes,
        Settings = new() { LayoutLocked = true },
    };
    private static WallLayoutResult Apply(WallDocument source, WallAction action) =>
        WallLayoutEngine.TryApply(source, action, Metrics.WallWidth(64), Metrics.WallHeight(source.Rows), Metrics);
    private static DipRect Position(WallDocument document, string id) => WallLayoutEngine.Measure(document, Metrics)
        .Groups.SelectMany(group => group.Members).Single(member => member.MemberId == id).Bounds;

    [Fact]
    public void EmptyCellMoveKeepsBottomSourceExtentAndFollowingGroup()
    {
        var source = Locked(9, new WallLane() { Groups = [Group("source", Tile("stays"), Tile("moving", row: 4)), Group("after", Tile("after-tile"))] });
        var result = Apply(source, new MoveMemberAction(0, "moving", new(1, 0, 1, 1), "source"));
        Assert.True(result.Success, result.Detail);
        var next = result.Proposal!.Document;
        Assert.Equal(5, next.Lanes[0].Groups[0].ReservedRows);
        Assert.Equal(Position(source, "after-tile"), Position(next, "after-tile"));
        Assert.Equal(Position(source, "stays"), Position(next, "stays"));
        Assert.Empty(WallLayoutEngine.ValidateGeometry(WallJson.Deserialize(WallJson.Serialize(next))));
    }

    [Fact]
    public void MovingLastMemberKeepsAnEmptySourceGroupAcrossSerialization()
    {
        var source = Locked(5, new WallLane() { Groups = [Group("source", Tile("moving")), Group("target", Tile("stays"))] });
        var result = Apply(source, new MoveMemberAction(0, "moving", new(2, 0, 1, 1), "target"));
        Assert.True(result.Success, result.Detail);
        var next = WallJson.Deserialize(WallJson.Serialize(result.Proposal!.Document));
        Assert.Empty(next.Lanes[0].Groups[0].Members);
        Assert.Equal(1, next.Lanes[0].Groups[0].ReservedRows);
        Assert.Equal(Position(source, "stays"), Position(next, "stays"));
    }

    [Fact]
    public void OccupiedDropAndGroupOrWallDragRejectWithoutChangingTheSource()
    {
        var source = Locked(4, new WallLane() { Groups = [Group("group", Tile("moving"), Tile("stays", 2))] });
        var before = WallJson.Serialize(source);
        Assert.False(Apply(source, new MoveMemberAction(0, "moving", new(2, 0, 1, 1), "group")).Success);
        Assert.False(Apply(source, new MoveGroupAction(0, "group", 1, 0)).Success);
        Assert.False(Apply(source, new ResizeWallAction(0, 2, 4)).Success);
        Assert.Equal(before, WallJson.Serialize(source));
    }

    [Fact]
    public void ResizeTriesGroupEndWithoutAvoidingTheNeighbour()
    {
        var moving = Tile("moving", width: 2, height: 2);
        var source = Locked(5, new WallLane() { Groups = [Group("group", moving, Tile("neighbour", 2, width: 2, height: 2))] });
        var result = Apply(source, new ReplaceMemberAction(0, moving.Id, moving with { LocalBounds = new(0, 0, 4, 2) }));
        Assert.True(result.Success, result.Detail);
        Assert.Equal("LAYOUT_MOVED_GROUP_END", result.Detail);
        Assert.Equal(new GridRect(4, 0, 4, 2), result.Proposal!.Document.EnumerateAllMembers().Single(member => member.Id == "moving").LocalBounds);
        Assert.Equal(Position(source, "neighbour"), Position(result.Proposal.Document, "neighbour"));
    }

    [Fact]
    public void ResizeFallsBackToNewTailGroupInOneProposalAndKeepsOldGeometry()
    {
        var moving = Tile("moving", width: 2, height: 2);
        var source = Locked(10, new WallLane() { Groups = [Group("source", moving, Tile("blocked", 2, width: 2, height: 2), Tile("blocked-right", 4, width: 4, height: 2)),
            Group("after", Tile("after-tile", width: 4, height: 2), Tile("after-right", 4, width: 4, height: 2))] }, new WallLane() { Groups = [] });
        source = source with { Settings = source.Settings with { ExpandColumns = true } };
        var result = Apply(source, new ReplaceMemberAction(0, moving.Id, moving with { LocalBounds = new(0, 0, 4, 4) }));
        Assert.True(result.Success, result.Detail);
        var next = result.Proposal!.Document;
        Assert.Equal("LAYOUT_MOVED_WALL_END", result.Detail);
        Assert.Equal(1, next.Generation);
        Assert.Equal(10, next.Rows);
        Assert.Equal(Position(source, "blocked"), Position(next, "blocked"));
        Assert.Equal(Position(source, "after-tile"), Position(next, "after-tile"));
        Assert.Empty(next.Lanes[^1].Groups);
        Assert.Equal("moving", Assert.Single(next.Lanes[1].Groups[0].Members).Id);
    }

    [Fact]
    public void AllResizeAttemptsFailWithoutChangingChosenSizeOrOriginalDocument()
    {
        var moving = Tile("moving", width: 2, height: 2);
        var source = Locked(3, new WallLane() { Groups = [Group("group", moving)] });
        var before = WallJson.Serialize(source);
        var resized = moving with { LocalBounds = new(0, 0, 4, 4) };
        Assert.False(Apply(source, new ReplaceMemberAction(0, moving.Id, resized)).Success);
        Assert.Equal(new GridRect(0, 0, 4, 4), resized.LocalBounds);
        Assert.Equal(before, WallJson.Serialize(source));
    }

    [Fact]
    public void ProtectedExpandedNormalizationKeepsInteriorLaneIndices()
    {
        var source = Locked(4, new WallLane() { Groups = [] }, new WallLane() { Groups = [Group("group", Tile("tile"))] },
            new WallLane() { Groups = [] }, new WallLane() { Groups = [Group("other", Tile("other-tile"))] }, new WallLane() { Groups = [] });
        source = source with { Settings = source.Settings with { ExpandColumns = true } };
        var result = WallLayoutEngine.NormalizeExpanded(source, Metrics);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(Position(source, "tile"), Position(result.Proposal!.Document, "tile"));
        Assert.Equal(Position(source, "other-tile"), Position(result.Proposal.Document, "other-tile"));
        Assert.Equal(5, result.Proposal.Document.Columns);
        Assert.Empty(result.Proposal.Document.Lanes[^1].Groups);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportHorizontalFallbackPreservesTheHeightAndUsesOneGeneration(bool locked)
    {
        var source = Locked(3, new WallLane() { Groups = [Group("old", Tile("old-tile", width: 4, height: 2), Tile("old-right", 4, width: 4, height: 2))] });
        source = source with { Settings = source.Settings with { LayoutLocked = locked } };
        var result = StartMenuImportPlanner.TryPlan(source, 0, [Group("new", Tile("new-tile", width: 4, height: 2), Tile("new-right", 4, width: 4, height: 2))], Metrics,
            new(Metrics.WallWidth(1), Metrics.WallHeight(3), 1, 3, false));
        Assert.True(result.Success, result.Detail);
        var next = result.Proposal!.Document;
        Assert.True(next.Settings.ExpandColumns);
        Assert.Equal(1, next.Generation);
        Assert.Equal(3, next.Rows);
        Assert.Empty(next.Lanes[^1].Groups);
        if (locked) Assert.Equal(Position(source, "old-tile"), Position(next, "old-tile"));
    }

    [Fact]
    public void ExplorerLockedAppendExpandsOnlyAfterOrdinaryCapacityFails()
    {
        var source = Locked(3, new WallLane() { Groups = [Group("old", Tile("old-tile", width: 4, height: 2), Tile("old-right", 4, width: 4, height: 2))] });
        var result = WallBatchPlanner.TryPlan(source, [Tile("new-tile", width: 4, height: 2)], "old", null, "external",
            Metrics.WallWidth(1), Metrics.WallHeight(3), Metrics, appendOnly: true);
        Assert.True(result.Success, result.Detail);
        Assert.True(result.Proposal!.Document.Settings.ExpandColumns);
        Assert.Equal(1, result.Proposal.Document.Generation);
        Assert.Equal(Position(source, "old-tile"), Position(result.Proposal.Document, "old-tile"));
    }
}
