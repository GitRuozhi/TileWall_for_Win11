using TileWall.Graphics;
using Xunit;

namespace TileWall.Core.Tests.Graphics;

public sealed class ShellImageListFrameRulesTests
{
    [Fact]
    public void ExactNativeSmallImageWithEmptyCellRemainderIsRecognized()
    {
        var small = Image(48, 3, 7, 40, 32);
        var large = Embed(small, 48, 256);
        Assert.True(ShellImageListFrameRules.IsPaddedDuplicate(large, 256, 256, small, 48, 48));
    }

    [Fact]
    public void LargerArtworkIsNeverReplacedByTheSmallNativeFrame()
    {
        var small = Image(48, 0, 0, 48, 48);
        var large = Embed(small, 48, 256);
        large[(120 * 256 + 100) * 4 + 3] = 255;
        Assert.False(ShellImageListFrameRules.IsPaddedDuplicate(large, 256, 256, small, 48, 48));
    }

    [Fact]
    public void AuthoredPaddingAndCenteredArtworkAreNotAlphaCropped()
    {
        var small = Image(48, 18, 18, 12, 12);
        var large = Image(256, 96, 96, 64, 64);
        Assert.False(ShellImageListFrameRules.IsPaddedDuplicate(large, 256, 256, small, 48, 48));
    }

    [Fact]
    public void DifferentPixelsAtTheSameBoundsDoNotMatch()
    {
        var small = Image(48, 0, 0, 48, 48);
        var large = Embed(small, 48, 256);
        large[0] ^= 1;
        Assert.False(ShellImageListFrameRules.IsPaddedDuplicate(large, 256, 256, small, 48, 48));
    }

    [Fact]
    public void EmptyOrInconsistentBuffersCannotAuthorizeAReplacement()
    {
        Assert.False(ShellImageListFrameRules.IsPaddedDuplicate(new byte[256 * 256 * 4], 256, 256,
            new byte[48 * 48 * 4], 48, 48));
        Assert.False(ShellImageListFrameRules.IsPaddedDuplicate([1], 256, 256, [1], 48, 48));
        Assert.False(ShellImageListFrameRules.IsPaddedDuplicate([], int.MaxValue, int.MaxValue, [], 48, 48));
    }

    private static byte[] Image(int edge, int left, int top, int width, int height)
    {
        var pixels = new byte[edge * edge * 4];
        for (var y = top; y < top + height; y++)
        for (var x = left; x < left + width; x++)
        {
            pixels[(y * edge + x) * 4] = 24;
            pixels[(y * edge + x) * 4 + 3] = 255;
        }
        return pixels;
    }

    private static byte[] Embed(byte[] small, int smallEdge, int largeEdge)
    {
        var pixels = new byte[largeEdge * largeEdge * 4];
        for (var y = 0; y < smallEdge; y++)
            Buffer.BlockCopy(small, y * smallEdge * 4, pixels, y * largeEdge * 4, smallEdge * 4);
        return pixels;
    }
}
