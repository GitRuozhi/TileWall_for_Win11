using TileWall.Graphics;
using Xunit;

namespace TileWall.Core.Tests.Graphics;

public sealed class IconCompositionRulesTests
{
    [Theory]
    [InlineData(false, 0, 0, 0)]
    [InlineData(true, 180, 80, 20)]
    [InlineData(true, 100, 100, 100)]
    public void CenteredSmallFigureIsRejectedWithTransparentOrNearFlatSurroundings(
        bool opaquePlate, int blue, int green, int red)
    {
        const int width = 24, height = 24;
        var pixels = new byte[width * height * 4];
        if (opaquePlate)
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                // Slight spatial variation exercises near-color background admission.
                var variation = (byte)((x + y) % 3);
                Put(pixels, width, x, y, (byte)(blue + variation), (byte)(green + variation),
                    (byte)(red + variation), 255);
            }
        Rectangle(pixels, width, 8, 8, 8, 8, 240, 240, 240, 255);
        var original = pixels.ToArray();

        Assert.False(IconCompositionRules.Accept(pixels, width, height));
        Assert.Equal(original, pixels);
    }

    [Fact]
    public void FullLogoKeepsItsAuthoredMarginsAspectAndPixels()
    {
        const int width = 40, height = 20;
        var pixels = new byte[width * height * 4];
        Rectangle(pixels, width, 4, 2, 32, 16, 40, 160, 220, 255);
        // A separate mark also ensures the original composition remains intact.
        Rectangle(pixels, width, 18, 6, 4, 8, 0, 0, 0, 255);
        var original = pixels.ToArray();

        Assert.True(IconCompositionRules.Accept(pixels, width, height));
        Assert.Equal(original, pixels);
        Assert.Equal(0, pixels[(2 * width + 3) * 4 + 3]);
        Assert.Equal(255, pixels[(2 * width + 4) * 4 + 3]);
        Assert.Equal(0, pixels[(18 * width + 4) * 4 + 3]);
    }

    [Fact]
    public void WideThinLogoIsAcceptedAndRetainsItsNativeCanvas()
    {
        const int width = 48, height = 24;
        var pixels = new byte[width * height * 4];
        Rectangle(pixels, width, 4, 10, 40, 4, 80, 180, 240, 255);
        var original = pixels.ToArray();

        Assert.True(IconCompositionRules.Accept(pixels, width, height));
        Assert.Equal(original, pixels);
    }

    [Fact]
    public void FullyTransparentAndInvalidPremultipliedPixelsAreRejected()
    {
        Assert.False(IconCompositionRules.Accept(new byte[8 * 8 * 4], 8, 8));
        var invalid = new byte[8 * 8 * 4];
        Rectangle(invalid, 8, 1, 1, 6, 6, 30, 30, 30, 64);
        Put(invalid, 8, 4, 4, 65, 30, 30, 64);
        var original = invalid.ToArray();

        Assert.False(IconCompositionRules.Accept(invalid, 8, 8));
        Assert.Equal(original, invalid);
    }

    [Theory]
    [InlineData(0, 4, 0)]
    [InlineData(4, -1, 0)]
    [InlineData(4, 4, 63)]
    [InlineData(int.MaxValue, int.MaxValue, 4)]
    public void InvalidDimensionsOrPayloadShapeAreRejectedBeforePixelAccess(int width, int height, int length) =>
        Assert.False(IconCompositionRules.Accept(new byte[length], width, height));

    private static void Rectangle(byte[] pixels, int width, int left, int top, int columns, int rows,
        byte blue, byte green, byte red, byte alpha)
    {
        for (var y = top; y < top + rows; y++)
        for (var x = left; x < left + columns; x++)
            Put(pixels, width, x, y, blue, green, red, alpha);
    }

    private static void Put(byte[] pixels, int width, int x, int y, byte blue, byte green, byte red, byte alpha)
    {
        var offset = (y * width + x) * 4;
        pixels[offset] = blue; pixels[offset + 1] = green; pixels[offset + 2] = red; pixels[offset + 3] = alpha;
    }
}
