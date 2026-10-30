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
            left.Frames[left.SelectedFrame] == right.Frames[right.SelectedFrame];
    }

    /// <summary>Full confirmed state equality, independent of the immutable collection instance.</summary>
    public static bool SameSeries(AutomaticIconSeries? left, AutomaticIconSeries? right)
    {
        if (ReferenceEquals(left, right)) return true;
        return left is not null && right is not null && left.Identity == right.Identity &&
            left.SelectedFrame == right.SelectedFrame && left.Columns == right.Columns && left.Rows == right.Rows &&
            left.Frames is not null && right.Frames is not null && left.Frames.SequenceEqual(right.Frames);
    }

    public static int SelectFrame(AutomaticIconSeries series, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (series.Frames is null || series.Frames.Count == 0 ||
            series.Frames.Any(frame => frame is null || frame.Width <= 0 || frame.Height <= 0))
            throw new ArgumentException("An automatic icon series needs valid native frames.", nameof(series));
        var sufficient = -1;
        var largest = 0;
        for (var index = 0; index < series.Frames.Count; index++)
        {
            var frame = series.Frames[index];
            var area = (long)frame.Width * frame.Height;
            var maximum = series.Frames[largest];
            if (area > (long)maximum.Width * maximum.Height) largest = index;
            if (frame.Width < width || frame.Height < height) continue;
            if (sufficient < 0 || area < (long)series.Frames[sufficient].Width * series.Frames[sufficient].Height)
                sufficient = index;
        }
        return sufficient >= 0 ? sufficient : largest;
    }

    public static AutomaticIconSeries SelectForSize(AutomaticIconSeries series, int columns, int rows,
        double width, double height)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (columns <= 0 || rows <= 0) throw new ArgumentOutOfRangeException(nameof(columns));
        return series.Columns == columns && series.Rows == rows ? series : series with
        { SelectedFrame = SelectFrame(series, width, height), Columns = columns, Rows = rows };
    }
}
