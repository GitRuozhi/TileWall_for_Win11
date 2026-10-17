using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

/// <summary>Current work-area boundary contract after the 48/4/52 DIP geometry change.</summary>
public sealed class WallAdaptationBoundaryTests
{
    [Fact]
    public void ResizeRespectsExactBudgetAndRejectsOverflowAtomically()
    {
        var metrics = new WallMetrics();
        Assert.Equal(48, metrics.Cell);
        Assert.Equal(4, metrics.Gap);
        Assert.Equal(52, metrics.Pitch);
        Assert.Equal(864, metrics.WallWidth(2));
        Assert.Equal(216, metrics.WallHeight(4));

        var current = WallDocument.Empty(1, 4);
        var before = WallJson.Serialize(current);
        var resize = new ResizeWallAction(0, 2, 4);
        var exact = WallLayoutEngine.TryApply(current, resize, 864, 216, metrics);
        Assert.True(exact.Success, exact.Detail);
        Assert.Equal(2, exact.Proposal!.Document.Columns);

        var beyondMonitor = WallLayoutEngine.TryApply(current, resize, 1, 1, metrics);
        Assert.False(beyondMonitor.Success);
        Assert.Equal(WallRejection.WorkAreaTooSmall, beyondMonitor.Rejection);
        Assert.Null(beyondMonitor.Proposal);
        Assert.Equal(before, WallJson.Serialize(current));
    }
}

