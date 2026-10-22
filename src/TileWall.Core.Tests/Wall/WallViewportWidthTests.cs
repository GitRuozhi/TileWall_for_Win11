using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class WallViewportWidthTests
{
    private static readonly WallMetrics Metrics = new();

    private static WallDocument Wall(int occupiedColumns, bool expand = true, bool fullScreen = false) => new()
    {
        Columns = occupiedColumns + (expand ? 1 : 0), Rows = 4,
        Settings = new() { ExpandColumns = expand, FullScreen = fullScreen },
        Lanes = [.. Enumerable.Range(0, occupiedColumns).Select(index => new WallLane
        {
            Groups = [new StartGroup { Id = "group" + index,
                Members = [new TileMember { Id = "member" + index, LocalBounds = new(0, 0, 1, 1) }] }],
        }), .. expand ? new[] { new WallLane { Groups = [] } } : []],
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryWindowSettlesToTheAcceptedGeometryIncludingTheFixedRails(bool menu)
    {
        var document = Wall(2, expand: false) with { Settings = new() { MenuStart = menu } };
        var fixedWidth = WallViewportPolicy.FixedWidth(menu);
        var width = WallViewportPolicy.ResolveVisibleWidth(document.Settings, WallLayoutEngine.Measure(document),
            1400 - fixedWidth, 1728 - fixedWidth);
        Assert.Equal(Metrics.WallWidth(2) + fixedWidth, width + fixedWidth);
    }

    [Fact]
    public void OnlyTheEmptyTailOverflowingStillSettlesToCompleteVisibleColumns()
    {
        var document = Wall(2);
        var geometry = WallLayoutEngine.Measure(document);
        Assert.Equal(Metrics.WallWidth(3), geometry.WallWidth);
        Assert.True(geometry.WallWidth > 1000);
        Assert.Equal(Metrics.WallWidth(2),
            WallViewportPolicy.ResolveVisibleWidth(document.Settings, geometry, 1000, 1680));
        Assert.Equal(3, document.Columns);
        Assert.Empty(document.Lanes[^1].Groups);
    }

    [Fact]
    public void RealContentOverflowKeepsContinuousWidthUntilTheContentFits()
    {
        var document = Wall(3);
        var geometry = WallLayoutEngine.Measure(document);
        Assert.Equal(1000, WallViewportPolicy.ResolveVisibleWidth(document.Settings, geometry, 1000, 1680));
        Assert.Equal(Metrics.WallWidth(3),
            WallViewportPolicy.ResolveVisibleWidth(document.Settings, geometry, 1400, 1680));

        var reduced = Wall(2);
        Assert.Equal(Metrics.WallWidth(2), WallViewportPolicy.ResolveVisibleWidth(reduced.Settings,
            WallLayoutEngine.Measure(reduced), 1000, 1680));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullscreenUsesCompleteTileColumnsWhenActualContentFits(bool expand)
    {
        var document = Wall(2, expand, fullScreen: true);
        var available = WallViewportPolicy.FullScreenVisibleSize(1920, 1080).Width;
        var width = WallViewportPolicy.ResolveVisibleWidth(document.Settings, WallLayoutEngine.Measure(document),
            available, available);
        Assert.Equal(Metrics.WallWidth(4), width);
        Assert.True(width + WallViewportPolicy.FixedWidth(false) <= 1920 * .95);
    }

    [Fact]
    public void FullscreenStillAllowsPartialColumnsWhenExpandedContentReallyOverflows()
    {
        var document = Wall(5, fullScreen: true);
        var available = WallViewportPolicy.FullScreenVisibleSize(1920, 1080).Width;
        Assert.Equal(available, WallViewportPolicy.ResolveVisibleWidth(document.Settings,
            WallLayoutEngine.Measure(document), available, available));
    }

    [Fact]
    public void AnEmptyTailDoesNotRequireAWholeLaneOnAnExtremelyNarrowScreen()
    {
        var document = WallDocument.Empty(1, 4) with { Settings = new() { ExpandColumns = true } };
        Assert.Equal(300, WallViewportPolicy.ResolveVisibleWidth(document.Settings,
            WallLayoutEngine.Measure(document), 300, 300));
        Assert.Equal(0, WallViewportPolicy.CompleteColumns(300));
    }

    [Theory]
    [InlineData(-.01, true)]
    [InlineData(0, false)]
    [InlineData(.01, false)]
    public void OccupiedLaneBoundarySeparatesRealClippingFromSpareWidth(double delta, bool clipped)
    {
        var document = Wall(2);
        var requested = Metrics.WallWidth(2) + delta;
        var width = WallViewportPolicy.ResolveVisibleWidth(document.Settings, WallLayoutEngine.Measure(document),
            requested, 1680);
        Assert.Equal(clipped ? requested : Metrics.WallWidth(2), width);
    }
}
