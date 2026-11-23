using TileWall.Core.Imaging;

namespace TileWall.Core.Wall;

/// <summary>Resolution choice never changes a confirmed icon's source or presentation policy.</summary>
public static class AutomaticIconSeriesRules
{
    /// <summary>The displayed native plane is stable across snapshots and logical-size bookkeeping.</summary>
    public static bool SameSelectedFrame(AutomaticIconSeries? left, AutomaticIconSeries? right)
    {
        if (ReferenceEquals(left, right)) return true;
        return left is not null && right is not null && left.Identity == right.Identity &&
            left.Frames is not null && right.Frames is not null &&
            left.SelectedFrame >= 0 && left.SelectedFrame < left.Frames.Count &&
            right.SelectedFrame >= 0 && right.SelectedFrame < right.Frames.Count &&
            SameNativeFrame(left.Frames[left.SelectedFrame], right.Frames[right.SelectedFrame]);
    }

    /// <summary>Full confirmed state equality, independent of the immutable collection instance.</summary>
    public static bool SameSeries(AutomaticIconSeries? left, AutomaticIconSeries? right)
    {
        if (ReferenceEquals(left, right)) return true;
        return left is not null && right is not null && left.Identity == right.Identity &&
            left.SelectedFrame == right.SelectedFrame && left.Columns == right.Columns && left.Rows == right.Rows &&
            left.Frames is not null && right.Frames is not null && SameNativeFrames(left.Frames, right.Frames);
    }

    // Optional unknown metadata is retained by storage but does not change native pixels or geometry.
    private static bool SameNativeFrame(AutomaticIconFrame? left, AutomaticIconFrame? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null &&
        left.Width == right.Width && left.Height == right.Height && left.Media == right.Media;

    private static bool SameNativeFrames(IReadOnlyList<AutomaticIconFrame> left, IReadOnlyList<AutomaticIconFrame> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
            if (!SameNativeFrame(left[index], right[index])) return false;
        return true;
    }

    public static int SelectFrame(AutomaticIconSeries series, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (series.Frames is null || series.Frames.Count == 0 ||
            series.Frames.Any(frame => frame is null || frame.Width <= 0 || frame.Height <= 0))
            throw new ArgumentException("An automatic icon series needs valid native frames.", nameof(series));
        return SelectNativeFrame(series.Frames, width, height, static frame => (frame.Width, frame.Height));
    }

    /// <summary>Choose among admitted native frames; equal areas retain the source's tie order.</summary>
    public static int SelectNativeFrame<TFrame>(IReadOnlyList<TFrame> frames, double width, double height,
        Func<TFrame, (int Width, int Height)> nativeSize)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(nativeSize);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        var sufficient = -1;
        var sufficientArea = long.MaxValue;
        var largest = -1;
        var largestArea = long.MinValue;
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = nativeSize(frames[index]);
            var area = (long)frame.Width * frame.Height;
            if (area > largestArea) { largest = index; largestArea = area; }
            if (frame.Width < width || frame.Height < height) continue;
            if (area < sufficientArea) { sufficient = index; sufficientArea = area; }
        }
        return sufficient >= 0 ? sufficient : largest;
    }

    public static AutomaticIconSeries SelectForSize(AutomaticIconSeries series, int columns, int rows,
        double width, double height)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (columns <= 0 || rows <= 0) throw new ArgumentOutOfRangeException(nameof(columns));
        return series.Columns == columns && series.Rows == rows ? series : series with
        { Columns = columns, Rows = rows };
    }

    /// <summary>Choose a native frame for the requested fill-relative scale, with a 1:1 fallback.</summary>
    public static (AutomaticIconSeries Series, MemberImageDisplay Display) NewImageDefaults(
        AutomaticIconSeries series, int physicalWidth, int physicalHeight, int fitPercent = 100)
    {
        if (fitPercent is < 20 or > 200) throw new ArgumentOutOfRangeException(nameof(fitPercent));
        var scale = fitPercent / 100d;
        var display = new MemberImageDisplay
        {
            Mode = fitPercent == 100 ? ImageDisplayMode.Fill : ImageDisplayMode.Custom,
            CustomScale = scale,
        };
        var index = SelectFrame(series, physicalWidth, physicalHeight);
        if (fitPercent != 100)
        {
            // Use the actual fill geometry of each native frame, including non-square icons.
            var bestArea = long.MaxValue;
            var cover = -1;
            for (var candidate = 0; candidate < series.Frames.Count; candidate++)
            {
                var frame = series.Frames[candidate];
                var area = (long)frame.Width * frame.Height;
                if (area < bestArea && Covers(frame, display))
                {
                    cover = candidate;
                    bestArea = area;
                }
            }
            index = cover >= 0 ? cover : SelectFrame(series, double.MaxValue, double.MaxValue);
        }
        var selected = series.Frames[index];
        var sufficient = fitPercent == 100
            ? selected.Width >= physicalWidth && selected.Height >= physicalHeight
            : Covers(selected, display);

        bool Covers(AutomaticIconFrame frame, MemberImageDisplay sizing)
        {
            var demand = ImageDecodeSizing.DisplayDemand(frame.Width, frame.Height, physicalWidth, physicalHeight, sizing);
            return frame.Width >= demand.Width && frame.Height >= demand.Height;
        }
        return (series with { SelectedFrame = index },
            sufficient ? display : new MemberImageDisplay { Mode = ImageDisplayMode.Original });
    }

    /// <summary>Display resolution may change; the confirmed frame remains the geometry reference.</summary>
    public static int SelectForDisplay(AutomaticIconSeries series, int width, int height, MemberImageDisplay display)
    {
        ArgumentNullException.ThrowIfNull(series);
        return ImageDecodeSizing.ForSeries(series, width, height, display).FrameIndex;
    }
}
