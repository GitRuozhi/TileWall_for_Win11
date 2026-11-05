using TileWall.Core.Wall;

namespace TileWall.Core.Imaging;

internal readonly record struct ImageDecodeDimensions(
    int ScaledRawWidth,
    int ScaledRawHeight,
    int Width,
    int Height);

/// <summary>Computes integer dimensions from the requested image size without an implicit quality ceiling.</summary>
internal static class ImageDecodeSizing
{
    internal const int MaxDecodedEdge = int.MaxValue;

    // Physical canvas dimensions are supplied by the consumer. This chooses a
    // display plane from the same source, never another native icon frame.
    internal static (int Width, int Height) ForDisplay(int sourceWidth, int sourceHeight,
        int canvasWidthPixels, int canvasHeightPixels, MemberImageDisplay display)
    {
        var demand = DisplayDemand(sourceWidth, sourceHeight, canvasWidthPixels, canvasHeightPixels, display);
        return (Math.Min(sourceWidth, demand.Width), Math.Min(sourceHeight, demand.Height));
    }

    internal static (int Width, int Height) DisplayDemand(int referenceWidth, int referenceHeight,
        int canvasWidthPixels, int canvasHeightPixels, MemberImageDisplay display)
    {
        var placed = MemberImageGeometry.Calculate(canvasWidthPixels, canvasHeightPixels,
            referenceWidth, referenceHeight, 1, display);
        return (checked((int)Math.Max(1, Math.Ceiling(placed.Bounds.Width))),
            checked((int)Math.Max(1, Math.Ceiling(placed.Bounds.Height))));
    }

    internal static (int FrameIndex, int Width, int Height) ForSeries(AutomaticIconSeries series,
        int canvasWidthPixels, int canvasHeightPixels, MemberImageDisplay display)
    {
        var reference = series.Frames[series.SelectedFrame];
        var demand = DisplayDemand(reference.Width, reference.Height, canvasWidthPixels, canvasHeightPixels, display);
        var index = AutomaticIconSeriesRules.SelectFrame(series, demand.Width, demand.Height);
        var frame = series.Frames[index];
        return (index, Math.Min(frame.Width, demand.Width), Math.Min(frame.Height, demand.Height));
    }

    internal static ImageDecodeDimensions Calculate(
        int rawWidth, int rawHeight, int orientedWidth, int orientedHeight,
        int requiredWidthPx, int requiredHeightPx, int maxEdge)
    {
        if (rawWidth <= 0 || rawHeight <= 0 || orientedWidth <= 0 || orientedHeight <= 0 ||
            requiredWidthPx <= 0 || requiredHeightPx <= 0)
            throw new ArgumentOutOfRangeException(nameof(rawWidth), "Image dimensions must be positive.");
        if (maxEdge < 1)
            throw new ArgumentOutOfRangeException(nameof(maxEdge));

        var scale = Math.Min(1.0,
            Math.Max((double)requiredWidthPx / orientedWidth, (double)requiredHeightPx / orientedHeight));
        scale = Math.Min(scale, (double)maxEdge / Math.Max(rawWidth, rawHeight));

        var scaledRawWidth = Math.Max(1, (int)Math.Round(rawWidth * scale));
        var scaledRawHeight = Math.Max(1, (int)Math.Round(rawHeight * scale));
        // An explicit caller sizing request may round one axis just beyond its bound.
        while (Math.Max(scaledRawWidth, scaledRawHeight) > maxEdge)
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
