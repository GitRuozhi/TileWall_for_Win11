using TileWall.Core.Imaging;
using Xunit;

namespace TileWall.Core.Tests.Imaging;

public sealed class MemberImageInteractionTests
{
    [Theory]
    [InlineData(ImageDisplayMode.Fit)]
    [InlineData(ImageDisplayMode.Fill)]
    [InlineData(ImageDisplayMode.Original)]
    public void InteractiveDisplay_NearPresetReturnsExactPresetModeAndFractionalRatio(ImageDisplayMode mode)
    {
        var preset = MemberImageGeometry.Calculate(204, 100, 400, 300, 1.5,
            new MemberImageDisplay { Mode = mode });

        var display = MemberImageGeometry.InteractiveDisplay(preset.Scale * 1.01, 204, 100, 400, 300, 1.5, wheel: true);

        Assert.Equal(mode, display.Mode);
        Assert.Equal(preset, MemberImageGeometry.Calculate(204, 100, 400, 300, 1.5, display));
        if (mode != ImageDisplayMode.Fill)
            Assert.NotEqual(Math.Round(display.CustomScale * 100), display.CustomScale * 100);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InteractiveDisplay_AwayFromPresetsUsesWholeFillPercentage(bool wheel)
    {
        var display = MemberImageGeometry.InteractiveDisplay(0.51 * 1.234, 204, 100, 400, 300, 1.5, wheel);

        Assert.Equal(ImageDisplayMode.Custom, display.Mode);
        Assert.Equal(1.23, display.CustomScale, 10);
        Assert.Equal(123, display.CustomScale * 100, 10);
    }

    [Theory]
    [InlineData(1, 1, 412, 620)]
    [InlineData(10000, 10000, 48, 48)]
    public void InteractiveDisplay_OriginalPresetRetainsExactGeometryOutsideCustomRange(
        double sourceWidth, double sourceHeight, double width, double height)
    {
        var display = MemberImageGeometry.InteractiveDisplay(1, width, height, sourceWidth, sourceHeight, 1, wheel: true);

        Assert.Equal(ImageDisplayMode.Original, display.Mode);
        Assert.Equal(1, MemberImageGeometry.Calculate(width, height, sourceWidth, sourceHeight, 1, display).Scale, 10);
    }

    [Theory]
    [InlineData(0.0001, 0.01)]
    [InlineData(200, 100)]
    public void InteractiveDisplay_CustomValuesStayInsideEditableRange(double factor, double expected)
    {
        var display = MemberImageGeometry.InteractiveDisplay(0.51 * factor, 204, 100, 400, 300, 1.5, wheel: true);

        Assert.Equal(ImageDisplayMode.Custom, display.Mode);
        Assert.Equal(expected, display.CustomScale, 10);
    }
}
