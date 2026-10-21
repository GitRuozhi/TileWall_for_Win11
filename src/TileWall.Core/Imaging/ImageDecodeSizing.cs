namespace TileWall.Core.Imaging;

internal readonly record struct ImageDecodeDimensions(
    int ScaledRawWidth,
    int ScaledRawHeight,
    int Width,
    int Height);

/// <summary>Computes bounded integer dimensions for the app's WIC image frames.</summary>
internal static class ImageDecodeSizing
{
    internal const int MaxDecodedEdge = 4096;
    internal const long MaxDecodedPixels = 16_000_000;

    internal static ImageDecodeDimensions Calculate(
        int rawWidth, int rawHeight, int orientedWidth, int orientedHeight,
        int requiredWidthPx, int requiredHeightPx, int maxEdge)
    {
        if (rawWidth <= 0 || rawHeight <= 0 || orientedWidth <= 0 || orientedHeight <= 0 ||
            requiredWidthPx <= 0 || requiredHeightPx <= 0)
            throw new ArgumentOutOfRangeException(nameof(rawWidth), "Image dimensions must be positive.");
        if (maxEdge is < 1 or > MaxDecodedEdge)
            throw new ArgumentOutOfRangeException(nameof(maxEdge));

        var maxPixels = Math.Min(MaxDecodedPixels, (long)maxEdge * maxEdge);
        var scale = Math.Min(1.0,
            Math.Max((double)requiredWidthPx / orientedWidth, (double)requiredHeightPx / orientedHeight));
        scale = Math.Min(scale, (double)maxEdge / Math.Max(rawWidth, rawHeight));
        scale = Math.Min(scale, Math.Sqrt((double)maxPixels / ((long)rawWidth * rawHeight)));

        var scaledRawWidth = Math.Max(1, (int)Math.Round(rawWidth * scale));
        var scaledRawHeight = Math.Max(1, (int)Math.Round(rawHeight * scale));
        // Rounding both axes can move a legal floating-point size over either integer
        // limit. Trim the larger dimension first to preserve the source aspect ratio.
        while ((long)scaledRawWidth * scaledRawHeight > maxPixels ||
               Math.Max(scaledRawWidth, scaledRawHeight) > maxEdge)
        {
            if (scaledRawWidth >= scaledRawHeight && scaledRawWidth > 1)
                scaledRawWidth--;
            else if (scaledRawHeight > 1)
                scaledRawHeight--;
            else if (scaledRawWidth > 1)
                scaledRawWidth--;
            else
                break;
        }

        var swapsAxes = rawWidth != rawHeight && orientedWidth == rawHeight && orientedHeight == rawWidth;
        return swapsAxes
            ? new(scaledRawWidth, scaledRawHeight, scaledRawHeight, scaledRawWidth)
            : new(scaledRawWidth, scaledRawHeight, scaledRawWidth, scaledRawHeight);
    }
}
