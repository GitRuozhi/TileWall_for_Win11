using TileWall.ApplicationModel;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Application;

public sealed class FullscreenResizeGestureTests
{
    [Theory]
    [InlineData(AnchorCorner.BottomLeft, "Top,Right,TopLeft,TopRight,BottomRight")]
    [InlineData(AnchorCorner.BottomRight, "Top,Left,TopLeft,TopRight,BottomLeft")]
    [InlineData(AnchorCorner.TopLeft, "Bottom,Right,TopRight,BottomLeft,BottomRight")]
    [InlineData(AnchorCorner.TopRight, "Bottom,Left,TopLeft,BottomLeft,BottomRight")]
    public void ExactlyOppositeEdgesAndOtherThreeCornersAreEnabled(AnchorCorner anchor, string expected)
    {
        var enabled = Enum.GetValues<ResizeZone>().Where(zone => FullscreenResizeGesture.IsEnabled(zone, anchor)).Select(zone => zone.ToString());
        Assert.Equal(expected.Split(',').Order(), enabled.Order());
    }

    public static TheoryData<string, int, int> InwardAxes => new()
    {
        { "Top", 0, 1 }, { "Bottom", 0, -1 }, { "Left", 1, 0 }, { "Right", -1, 0 },
        { "TopLeft", 1, 0 }, { "TopLeft", 0, 1 }, { "TopRight", -1, 0 }, { "TopRight", 0, 1 },
        { "BottomLeft", 1, 0 }, { "BottomLeft", 0, -1 }, { "BottomRight", -1, 0 }, { "BottomRight", 0, -1 },
    };

    [Theory]
    [MemberData(nameof(InwardAxes))]
    public void EachAxisAloneRequiresStrictlyMoreThanSixteenDipAtEveryScale(string name, int x, int y)
    {
        var zone = Enum.Parse<ResizeZone>(name);
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2, 2.5 })
        {
            Assert.False(FullscreenResizeGesture.ShouldExit(zone, x * 15.99 * scale, y * 15.99 * scale, scale));
            Assert.False(FullscreenResizeGesture.ShouldExit(zone, x * 16 * scale, y * 16 * scale, scale));
            Assert.True(FullscreenResizeGesture.ShouldExit(zone, x * 16.01 * scale, y * 16.01 * scale, scale));
            Assert.False(FullscreenResizeGesture.ShouldExit(zone, x * -100 * scale, y * -100 * scale, scale));
        }
    }

    [Theory]
    [InlineData("Top", 100, 16)]
    [InlineData("Bottom", -100, -16)]
    [InlineData("Left", 16, -100)]
    [InlineData("Right", -16, 100)]
    public void EdgesIgnoreLargeMovementOnOtherAxis(string name, double x, double y) =>
        Assert.False(FullscreenResizeGesture.ShouldExit(Enum.Parse<ResizeZone>(name), x, y, 1));

    [Theory]
    [InlineData("TopLeft", 17, -100)]
    [InlineData("TopRight", -17, -100)]
    [InlineData("BottomLeft", -100, -17)]
    [InlineData("BottomRight", -17, 100)]
    public void CornersAcceptOneInwardAxisEvenWhenOtherAxisPointsOut(string name, double x, double y) =>
        Assert.True(FullscreenResizeGesture.ShouldExit(Enum.Parse<ResizeZone>(name), x, y, 1));

    [Theory]
    [InlineData("Top", AnchorCorner.BottomRight)]
    [InlineData("Bottom", AnchorCorner.TopLeft)]
    [InlineData("Left", AnchorCorner.BottomRight)]
    [InlineData("Right", AnchorCorner.TopLeft)]
    [InlineData("TopLeft", AnchorCorner.BottomRight)]
    [InlineData("TopRight", AnchorCorner.BottomLeft)]
    [InlineData("BottomLeft", AnchorCorner.TopRight)]
    [InlineData("BottomRight", AnchorCorner.TopLeft)]
    public void EveryZoneHasExplicitPointerDirection(string name, AnchorCorner expected) =>
        Assert.Equal(expected, FullscreenResizeGesture.PointerAnchor(Enum.Parse<ResizeZone>(name)));

    [Fact]
    public void InvalidScaleOrCoordinatesCannotExit()
    {
        foreach (var scale in new[] { 0, -1, double.NaN, double.PositiveInfinity })
            Assert.False(FullscreenResizeGesture.ShouldExit(ResizeZone.TopLeft, 100, 100, scale));
        Assert.False(FullscreenResizeGesture.ShouldExit(ResizeZone.TopLeft, double.NaN, 100, 1));
        Assert.False(FullscreenResizeGesture.ShouldExit(ResizeZone.TopLeft, 100, double.PositiveInfinity, 1));
    }
}
