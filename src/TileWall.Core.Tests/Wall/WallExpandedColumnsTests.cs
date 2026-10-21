using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class WallExpandedColumnsTests
{
    private static readonly WallMetrics Metrics = new();
    private static StartGroup Group(string id, int row = 0) => new()
    {
        Id = id, Members = [new TileMember { Id = id + "-tile", LocalBounds = new GridRect(0, row, 1, 1) }],
    };

    private static WallDocument Expanded(int occupied, bool fullScreen = true) => new()
    {
        Columns = occupied + 1, Rows = 4,
        Settings = new() { FullScreen = fullScreen, ExpandColumns = true },
        Lanes = [.. Enumerable.Range(0, occupied).Select(index => new WallLane { Groups = [Group("g" + index)] }),
            new WallLane { Groups = [] }],
    };

    private static WallLayoutResult Apply(WallDocument current, WallAction action) =>
        WallLayoutEngine.TryApply(current, action, Metrics.WallWidth(64), Metrics.WallHeight(256));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NormalizeExpandedOnlyRemovesEmptyLanesAndDoesNotAdvanceGeneration(bool fullScreen)
    {
        var first = new WallLane { Groups = [Group("a", 2)], ImportBatchId = "first-batch" };
        var second = new WallLane { Groups = [Group("b", 1)], ImportBatchId = "second-batch" };
        var current = Expanded(0, fullScreen) with
        {
            Generation = 8, Columns = 6, Rows = 8,
            Lanes = [new() { Groups = [] }, first, new() { Groups = [] }, second,
                new() { Groups = [] }, new() { Groups = [] }],
        };
        var before = WallJson.Serialize(current);

        var result = WallLayoutEngine.NormalizeExpanded(current);

        Assert.True(result.Success, result.Detail);
        var next = result.Proposal!.Document;
        Assert.Equal(8, next.Generation);
        Assert.Equal(3, next.Columns);
        Assert.Same(first, next.Lanes[0]);
        Assert.Same(second, next.Lanes[1]);
        Assert.Empty(next.Lanes[2].Groups);
        Assert.Same(next, WallLayoutEngine.NormalizeExpanded(next).Proposal!.Document);
        Assert.Equal(before, WallJson.Serialize(current));
    }

    [Fact]
    public void DisabledExpansionPreservesExistingEmptyLanesInBothModes()
    {
        var current = Expanded(0) with
        {
            Columns = 3,
            Lanes = [new() { Groups = [] }, new() { Groups = [Group("a")] }, new() { Groups = [] }],
        };
        foreach (var fullScreen in new[] { false, true })
        {
            var source = current with { Settings = new() { FullScreen = fullScreen, ExpandColumns = false } };
            var result = WallLayoutEngine.NormalizeExpanded(source);
            Assert.True(result.Success, result.Detail);
            Assert.Same(source, result.Proposal!.Document);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FillingTheLastAvailableContentLaneAppendsItsTailInTheSameGeneration(bool fullScreen)
    {
        var current = Expanded(62, fullScreen);
        var result = Apply(current, new AddMemberAction(0,
            new TileMember { Id = "added", LocalBounds = new(0, 0, 1, 1) }, null, 62, 0, "added-group"));

        Assert.True(result.Success, result.Detail);
        var next = result.Proposal!.Document;
        Assert.Equal(64, next.Columns);
        Assert.Equal(1, next.Generation);
        Assert.Equal("added-group", Assert.Single(next.Lanes[62].Groups).Id);
        Assert.Empty(next.Lanes[63].Groups);
        Assert.All(next.Lanes.Take(63), lane => Assert.NotEmpty(lane.Groups));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddingASecondBatchLaneBeyondTheTailLimitRejectsTheEntireBatch(bool fullScreen)
    {
        var current = Expanded(62, fullScreen);
        var before = WallJson.Serialize(current);
        var result = Apply(current, new AddBatchAction(0,
        [
            new(new TileMember { Id = "first", LocalBounds = new(0, 0, 1, 1) }, null, 62, 0, "first-group"),
            new(new TileMember { Id = "second", LocalBounds = new(0, 0, 1, 1) }, null, 63, 0, "second-group"),
        ]));

        Assert.False(result.Success);
        Assert.Equal("COLUMN_LIMIT", result.Detail);
        Assert.Null(result.Proposal);
        Assert.Equal(before, WallJson.Serialize(current));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AtTheLimitMovingAnExistingGroupIntoTheTailStillSucceeds(bool fullScreen)
    {
        var current = Expanded(63, fullScreen);
        var result = Apply(current, new MoveGroupAction(0, "g0", 63, 0));

        Assert.True(result.Success, result.Detail);
        var next = result.Proposal!.Document;
        Assert.Equal(64, next.Columns);
        Assert.Equal("g1", Assert.Single(next.Lanes[0].Groups).Id);
        Assert.Equal("g0", Assert.Single(next.Lanes[62].Groups).Id);
        Assert.Empty(next.Lanes[63].Groups);
    }

    [Fact]
    public void TemporaryStackBeyondStructuralRowsCanRemoveOnlyTheNecessaryEmptyRow()
    {
        var current = WallDocument.Empty(1, 256);
        var result = WallLayoutEngine.TryApply(current, new AddMemberAction(0,
            new TileMember { Id = "tile", LocalBounds = new(0, 255, 1, 1) }, null, NewGroupId: "group"),
            Metrics.WallWidth(1), Metrics.WallHeight(256));

        Assert.True(result.Success, result.Detail);
        var next = result.Proposal!.Document;
        Assert.Equal(256, next.Rows);
        Assert.Equal(254, next.Lanes[0].Groups[0].Members[0].LocalBounds.Row);
        Assert.Empty(current.Lanes[0].Groups);
    }
}
