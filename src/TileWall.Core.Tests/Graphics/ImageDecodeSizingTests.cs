using TileWall.Core.Imaging;
using Xunit;

namespace TileWall.Core.Tests.Graphics;

public sealed class ImageDecodeSizingTests
{
    [Theory]
    [InlineData(512, 512, 48, 48, ImageDisplayMode.Fill, 1, 48, 48)]
    [InlineData(512, 512, 100, 100, ImageDisplayMode.Fill, 1, 100, 100)]
    [InlineData(600, 200, 48, 48, ImageDisplayMode.Fit, 1, 48, 16)]
    [InlineData(600, 200, 48, 48, ImageDisplayMode.Fill, 1, 144, 48)]
    [InlineData(512, 512, 48, 48, ImageDisplayMode.Custom, 2, 96, 96)]
    [InlineData(512, 512, 48, 48, ImageDisplayMode.Original, 1, 512, 512)]
    [InlineData(16, 16, 100, 100, ImageDisplayMode.Fill, 1, 16, 16)]
    public void DisplayPlaneFollowsFinalPhysicalGeometryWithoutUpscalingTheSource(int sourceWidth, int sourceHeight,
        int width, int height, ImageDisplayMode mode, double customScale, int expectedWidth, int expectedHeight)
    {
        var actual = ImageDecodeSizing.ForDisplay(sourceWidth, sourceHeight, width, height,
            new() { Mode = mode, CustomScale = customScale });
        Assert.Equal((expectedWidth, expectedHeight), actual);
    }

    [Theory]
    [InlineData(5000, 4900, 5000, 4900)]
    [InlineData(5120, 5000, 5120, 5000)]
    public void OriginalResolutionRequests_KeepNativeDimensionsAboveFormerCeilings(
        int sourceWidth, int sourceHeight, int expectedWidth, int expectedHeight)
    {
        var dimensions = ImageDecodeSizing.Calculate(
            sourceWidth, sourceHeight, sourceWidth, sourceHeight,
            sourceWidth, sourceHeight, ImageDecodeSizing.MaxDecodedEdge);

        Assert.Equal(expectedWidth, dimensions.Width);
        Assert.Equal(expectedHeight, dimensions.Height);
        Assert.InRange(Math.Max(dimensions.Width, dimensions.Height), 1, ImageDecodeSizing.MaxDecodedEdge);
    }

    [Fact]
    public void NormalSizeImage_KeepsSourceDimensions()
    {
        var dimensions = ImageDecodeSizing.Calculate(1200, 800, 1200, 800, 1200, 800,
            ImageDecodeSizing.MaxDecodedEdge);

        Assert.Equal(1200, dimensions.Width);
        Assert.Equal(800, dimensions.Height);
    }

    [Fact]
    public void ExplicitRequestedEdge_LimitsTheLongSide()
    {
        var dimensions = ImageDecodeSizing.Calculate(8000, 4000, 8000, 4000, 8000, 4000,
            4096);

        Assert.Equal(4096, dimensions.Width);
        Assert.Equal(2048, dimensions.Height);
    }

    [Fact]
    public void ExifAxisSwap_ReportsOrientedOutputDimensions()
    {
        var dimensions = ImageDecodeSizing.Calculate(5120, 5000, 5000, 5120, 5000, 5120,
            ImageDecodeSizing.MaxDecodedEdge);

        Assert.Equal(5120, dimensions.ScaledRawWidth);
        Assert.Equal(5000, dimensions.ScaledRawHeight);
        Assert.Equal(5000, dimensions.Width);
        Assert.Equal(5120, dimensions.Height);
    }
}
