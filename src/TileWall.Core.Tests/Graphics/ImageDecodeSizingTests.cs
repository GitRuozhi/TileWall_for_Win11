using TileWall.Core.Imaging;
using Xunit;

namespace TileWall.Core.Tests.Graphics;

public sealed class ImageDecodeSizingTests
{
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
