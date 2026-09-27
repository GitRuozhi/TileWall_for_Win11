using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

/// <summary>Current work-area boundary contract after the 48/4/52 DIP geometry change.</summary>
public sealed class WallAdaptationBoundaryTests
{
    [Fact]
    public void ResizeFitsAtExactCurrentBoundsAndRejectsSmallerWorkArea()
    {
        var metrics = new WallMetrics();
        Assert.Equal(48, metrics.Cell);
        Assert.Equal(4, metrics.Gap);
        Assert.Equal(52, metrics.Pitch);
        Assert.Equal(860, metrics.WallWidth(2));
        Assert.Equal(228, metrics.WallHeight(4));

        var current = WallDocument.Empty(1, 4);
        var resize = new ResizeWallAction(0, 2, 4);
        var exact = WallLayoutEngine.TryApply(current, resize, 860, 228, metrics);
        Assert.True(exact.Success, exact.Detail);
        Assert.Equal(2, exact.Proposal!.Document.Columns);

        Assert.Equal(WallRejection.WorkAreaTooSmall,
            WallLayoutEngine.TryApply(current, resize, 859.9, 228, metrics).Rejection);
        Assert.Equal(WallRejection.WorkAreaTooSmall,
            WallLayoutEngine.TryApply(current, resize, 860, 227.9, metrics).Rejection);
    }
}
