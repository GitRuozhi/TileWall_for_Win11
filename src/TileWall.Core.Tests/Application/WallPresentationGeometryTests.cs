using TileWall.ApplicationModel;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Application;

public sealed class WallPresentationGeometryTests
{
    [Theory]
    [InlineData(0, 0, 1920, 1040, 0, 0, 1280, 680)]
    [InlineData(0, 40, 1920, 1040, 0, 40, 1280, 720)]
    [InlineData(40, 0, 1880, 1080, 40, 0, 1280, 720)]
    [InlineData(0, 0, 1880, 1080, 0, 0, 1240, 720)]
    public void EveryTaskbarEdgeUsesTheSameAreaForFourAnchorsAndFullScreen(
        int x, int y, int width, int height, int left, int top, int right, int bottom)
    {
        var screen = new WallPresentationBounds(0, 0, 1920, 1080);
        var area = WallPresentationGeometry.ResolveArea(screen, new(x, y, width, height), true);
        Assert.Equal(new(left, top, 640, 360), WallPresentationGeometry.Anchor(area, 640, 360, AnchorCorner.TopLeft));
        Assert.Equal(new(right, top, 640, 360), WallPresentationGeometry.Anchor(area, 640, 360, AnchorCorner.TopRight));
        Assert.Equal(new(left, bottom, 640, 360), WallPresentationGeometry.Anchor(area, 640, 360, AnchorCorner.BottomLeft));
        Assert.Equal(new(right, bottom, 640, 360), WallPresentationGeometry.Anchor(area, 640, 360, AnchorCorner.BottomRight));
        Assert.Equal(area, WallPresentationGeometry.Anchor(area, 640, 360, AnchorCorner.BottomRight, true));
        Assert.Equal(screen, WallPresentationGeometry.ResolveArea(screen, area, false));
    }

    [Fact]
    public void TemporaryNarrowingClipsPixelsWithoutChangingTheRequestedSize()
    {
        var requested = new WallPresentationBounds(-200, -100, 1200, 900);
        var area = new WallPresentationBounds(40, 30, 800, 600);
        Assert.Equal(area, WallPresentationGeometry.Constrain(requested, area));
        Assert.Equal(new(-200, -100, 1200, 900), requested);
        Assert.Equal(new(40, 30, 640, 360), WallPresentationGeometry.Constrain(new(-100, -100, 640, 360), area));
        Assert.Equal(new(200, 270, 640, 360), WallPresentationGeometry.Constrain(new(700, 500, 640, 360), area));
    }

    [Fact]
    public void ScreenOriginSurvivesFullscreenAndCornerPlacement()
    {
        var area = new WallPresentationBounds(-1280, 120, 1240, 720);
        Assert.Equal(new(-680, 480, 640, 360), WallPresentationGeometry.Anchor(area, 640, 360, AnchorCorner.BottomRight));
        Assert.Equal(area, WallPresentationGeometry.Anchor(area, 1, 1, AnchorCorner.TopLeft, true));
    }

    [Theory]
    [InlineData(-1, 0, 1920, 1040)]
    [InlineData(0, 0, 1921, 1040)]
    [InlineData(0, 0, 1920, 1081)]
    [InlineData(0, 0, 0, 1040)]
    [InlineData(0, 0, 1920, -1)]
    [InlineData(int.MaxValue, 0, int.MaxValue, 1040)]
    public void UntrustedOrUnavailableAreaFallsBackToCompleteScreen(int x, int y, int width, int height)
    {
        var screen = new WallPresentationBounds(0, 0, 1920, 1080);
        Assert.Equal(screen, WallPresentationGeometry.ResolveArea(screen, new(x, y, width, height), true));
        Assert.Equal(screen, WallPresentationGeometry.ResolveArea(screen, null, true));
    }
}
