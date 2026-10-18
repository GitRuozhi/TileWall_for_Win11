using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class WallViewportPolicyTests
{
    private static StartGroup Group(string id, int height = 2) => new()
    {
        Id = id, Title = id, Members = [new TileMember { Id = id + "-tile", LocalBounds = new(0, 0, 2, height) }],
        ImportSource = new() { BatchId = "batch", GroupBucketKey = id, OriginGroupId = id },
    };

    [Fact]
    public void WindowedUsesBothNinetyPercentLimitsAndFullscreenAllowsLogicalExtraColumns()
    {
        var windowed = WallViewportPolicy.Create(1920, 1080, false);
        var full = WallViewportPolicy.Create(1920, 1080, true);
        Assert.Equal(1680, windowed.MaxWidthDip);
        Assert.Equal(972, windowed.MaxHeightDip);
        Assert.Equal(3, windowed.MaxColumns);
        Assert.Equal(18, windowed.MaxRows);
        Assert.Equal(64, full.MaxColumns);
        Assert.Equal(18, full.MaxRows);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(800, 600)]
    public void FullscreenVisibleViewportLeavesSymmetricRoomForSidebarAndCompleteRows(double width, double height)
    {
        var metrics = new WallMetrics();
        var visible = WallViewportPolicy.FullScreenVisibleSize(width, height, metrics);
        Assert.True(visible.Width <= width * .9);
        Assert.True(visible.Height <= height * .9);
        Assert.True((width - visible.Width) / 2 >= WallViewportPolicy.SidebarWidthDip);
        var budget = WallViewportPolicy.Create(width, height, true, metrics);
        Assert.Equal(metrics.WallHeight(budget.MaxRows), visible.Height);
        Assert.Equal(64, budget.MaxColumns);
        Assert.True(budget.MaxWidthDip > visible.Width);
    }

    [Theory]
    [InlineData(4, 4, true)]
    [InlineData(5, 4, false)]
    [InlineData(4, 5, false)]
    public void FinalLayoutBoundaryEnforcesOrdinaryTileMaximum(int width, int height, bool allowed)
    {
        var source = WallDocument.Empty(1, 10);
        var result = WallLayoutEngine.TryApply(source,
            new AddMemberAction(0, new TileMember { Id = "size-tile", LocalBounds = new(0, 0, width, height) }, null, 0, 0, "size-group"),
            1920, 1080);
        Assert.Equal(allowed, result.Success);
        Assert.Empty(source.Lanes[0].Groups);
    }

    [Fact]
    public void WindowedContentOverflowRejectsAnEntireActionWithoutChangingItsSnapshot()
    {
        var source = WallDocument.Empty(4, 8);
        var before = WallJson.Serialize(source);
        var budget = WallViewportPolicy.Create(1920, 1080, false);
        var result = WallLayoutEngine.TryApply(source,
            new AddMemberAction(0, new TileMember { Id = "tile", LocalBounds = new(0, 0, 2, 2) }, null, 4, 0, "group"),
            budget.MaxWidthDip, budget.MaxHeightDip);
        Assert.False(result.Success);
        Assert.Equal("CONTENT_TOO_LARGE", result.Detail);
        Assert.Equal(before, WallJson.Serialize(source));
    }

    [Fact]
    public void FixedHeightImportAddsMoreThanThreeLanesInOriginalWholeGroupOrder()
    {
        var source = WallDocument.Empty(1, 4);
        var metrics = new WallMetrics();
        var budget = new WallViewportBudget(metrics.WallWidth(64), metrics.WallHeight(4), 64, 4, true);
        var groups = Enumerable.Range(0, 5).Select(i => Group("g" + i)).ToArray();
        var result = StartMenuImportPlanner.TryPlan(source, 0, groups, metrics, budget);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(6, result.Proposal!.Document.Columns);
        Assert.Equal(4, result.Proposal.Document.Rows);
        Assert.Equal(groups.Select(g => g.Id), result.Proposal.Document.Lanes.Take(5).Select(l => Assert.Single(l.Groups).Id));
    }

    [Fact]
    public void FixedHeightImportRejectsOversizedGroupByNameAndDoesNotRaiseRows()
    {
        var source = WallDocument.Empty(1, 4);
        var before = WallJson.Serialize(source);
        var result = StartMenuImportPlanner.TryPlan(source, 0, [Group("too-tall", 4)]);
        Assert.False(result.Success);
        Assert.Equal("GROUP_TOO_TALL:too-tall", result.Detail);
        Assert.Equal(before, WallJson.Serialize(source));
    }

    [Fact]
    public void WindowedImportBeyondAvailableColumnsIsAtomic()
    {
        var metrics = new WallMetrics();
        var source = WallDocument.Empty(1, 4) with { Lanes = [new WallLane { Groups = [Group("retained")] }] };
        var budget = new WallViewportBudget(metrics.WallWidth(2), metrics.WallHeight(4), 2, 4, false);
        var result = StartMenuImportPlanner.TryPlan(source, 0, [Group("one"), Group("two")], metrics, budget);
        Assert.False(result.Success);
        Assert.Equal("CONTENT_TOO_LARGE", result.Detail);
        Assert.Single(source.Lanes);
    }

    [Fact]
    public void FailedFullscreenExitPreservesModeAndEveryLane()
    {
        var metrics = new WallMetrics();
        var source = new WallDocument
        {
            Columns = 3, Rows = 4, Settings = new() { FullScreen = true, WindowedColumns = 1, WindowedRows = 4 },
            Lanes = Enumerable.Range(0, 3).Select(i => new WallLane { Groups = [Group("g" + i)] }).ToArray(),
        };
        var before = WallJson.Serialize(source);
        var budget = new WallViewportBudget(metrics.WallWidth(1), metrics.WallHeight(4), 1, 4, false);
        var result = WallViewportPolicy.TryResize(source, 1, 4, source.Settings with { FullScreen = false }, budget);
        Assert.False(result.Success);
        Assert.Equal(before, WallJson.Serialize(source));
    }

    [Theory]
    [InlineData(0, 4, AnchorCorner.BottomLeft)]
    [InlineData(65, 4, AnchorCorner.BottomLeft)]
    [InlineData(1, 257, AnchorCorner.BottomLeft)]
    [InlineData(1, 4, (AnchorCorner)50)]
    public void PersistedRestoreFieldsAreValidated(int columns, int rows, AnchorCorner anchor)
    {
        var source = WallDocument.Empty(1, 4) with { Settings = new()
            { WindowedColumns = columns, WindowedRows = rows, WindowedAnchorCorner = anchor } };
        Assert.NotEmpty(WallLayoutEngine.Validate(source));
    }

    [Fact]
    public void OptionalModeFieldsRoundTripWithoutSchemaChange()
    {
        var source = WallDocument.Empty(1, 4) with { Settings = new()
            { FullScreen = true, WindowedColumns = 2, WindowedRows = 8, WindowedAnchorCorner = AnchorCorner.TopRight } };
        var next = WallJson.Deserialize(WallJson.Serialize(source));
        Assert.Equal(3, next.SchemaVersion);
        Assert.Equal(source.Settings, next.Settings);
        Assert.Equal(10, new WallSettings().BackgroundTransparency);
    }
}

