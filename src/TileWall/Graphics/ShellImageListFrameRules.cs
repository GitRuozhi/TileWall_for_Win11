namespace TileWall.Graphics;

/// <summary>Distinguishes Shell list-cell padding from an icon's authored transparent margins.</summary>
internal static class ShellImageListFrameRules
{
    internal static bool IsPaddedDuplicate(ReadOnlySpan<byte> large, int largeWidth, int largeHeight,
        ReadOnlySpan<byte> small, int smallWidth, int smallHeight)
    {
        if (largeWidth < 1 || largeHeight < 1 || smallWidth < 1 || smallHeight < 1 ||
            largeWidth <= smallWidth || largeHeight <= smallHeight ||
            !HasBgraLength(large, largeWidth, largeHeight) || !HasBgraLength(small, smallWidth, smallHeight))
            return false;
        var visible = false;
        for (var y = 0; y < largeHeight; y++)
        {
            if (y < smallHeight && !large.Slice(y * largeWidth * 4, smallWidth * 4)
                    .SequenceEqual(small.Slice(y * smallWidth * 4, smallWidth * 4)))
                return false;
            for (var x = 0; x < largeWidth; x++)
            {
                var alpha = large[(y * largeWidth + x) * 4 + 3];
                if (x < smallWidth && y < smallHeight) visible |= alpha != 0;
                else if (alpha != 0) return false;
            }
        }
        return visible;
    }

    private static bool HasBgraLength(ReadOnlySpan<byte> pixels, int width, int height) =>
        pixels.Length % 4 == 0 && (long)width * height == pixels.Length / 4;
}
