namespace TileWall.Core.Wall;

/// <summary>Logical capacity of a primary-screen viewport; never persisted as screen pixels.</summary>
public sealed record WallViewportBudget(double MaxWidthDip, double MaxHeightDip,
    int MaxColumns, int MaxRows, bool FullScreen);

public static class WallViewportPolicy
{
    public const int MaximumMemberRows = 12;

    public static WallViewportBudget Create(double screenWidthDip, double screenHeightDip,
        bool fullScreen, WallMetrics? metrics = null)
    {
        metrics ??= new();
        // Both modes budget complete cells inside ninety percent. Fullscreen's logical
        // horizontal extent remains independent of its centered, clipped visible viewport.
        var width = Math.Max(1, screenWidthDip * .9 - (fullScreen ? 0 : SidebarWidthDip));
        var height = Math.Max(1, screenHeightDip * .9);
        var columns = Math.Clamp((int)Math.Floor((width - 2 * metrics.Margin + metrics.ColumnGap) /
            (metrics.LaneWidth + metrics.ColumnGap) + 1e-8), 0, 64);
        var rows = Math.Clamp((int)Math.Floor((height - metrics.TopMargin - metrics.Margin + metrics.Gap) /
            metrics.Pitch + 1e-8), 0, 256);
        return new(fullScreen ? metrics.WallWidth(64) : width, height,
            fullScreen ? 64 : columns, rows, fullScreen);
    }

    public const double SidebarWidthDip = 48;

    public static (double Width, double Height) FullScreenVisibleSize(double screenWidthDip,
        double screenHeightDip, WallMetrics? metrics = null)
    {
        metrics ??= new();
        var widthLimit = Math.Max(1, Math.Min(screenWidthDip * .9, screenWidthDip - SidebarWidthDip * 2));
        var heightLimit = Math.Max(1, screenHeightDip * .9);
        // The viewport may clip a partial next lane horizontally; vertical capacity is
        // always a whole row, and its remaining pixels become symmetric outer whitespace.
        var rows = Math.Max(0, (int)Math.Floor((heightLimit - metrics.TopMargin - metrics.Margin + metrics.Gap) /
            metrics.Pitch + 1e-8));
        var columns = Math.Max(0, (int)Math.Floor((widthLimit - 2 * metrics.Margin + metrics.ColumnGap) /
            (metrics.LaneWidth + metrics.ColumnGap) + 1e-8));
        return (Math.Min(widthLimit, columns > 0 ? metrics.WallWidth(columns) : widthLimit),
            Math.Min(heightLimit, rows > 0 ? metrics.WallHeight(rows) : heightLimit));
    }

    public static WallLayoutResult Validate(WallDocument document, WallViewportBudget budget,
        WallMetrics? metrics = null)
    {
        metrics ??= new();
        if (MemberHeightRejection(document) is { } memberRejection) return memberRejection;
        foreach (var group in document.Lanes.SelectMany(l => l.Groups))
        {
            if (group.Members.Count == 0) continue;
            var height = metrics.TopMargin + metrics.Margin + metrics.TitleHeight +
                metrics.TitleToMembersGap + metrics.HeightOf(group.Members.Max(m => m.LocalBounds.Bottom));
            if (height > budget.MaxHeightDip + 1e-8)
                return new(null, WallRejection.NoSpace, "GROUP_TOO_TALL:" + (string.IsNullOrWhiteSpace(group.Title) ? group.Id : group.Title));
        }
        var geometry = WallLayoutEngine.Measure(document, metrics);
        if (document.Columns > budget.MaxColumns || document.Rows > budget.MaxRows ||
            geometry.WallWidth > budget.MaxWidthDip + 1e-8 || geometry.WallHeight > budget.MaxHeightDip + 1e-8)
            return new(null, WallRejection.WorkAreaTooSmall, "CONTENT_TOO_LARGE");
        return new(new(document, geometry), WallRejection.None);
    }

    /// <summary>Resize/compact a copy. Failed fits never expose partial lane or row removal.</summary>
    public static WallLayoutResult TryResize(WallDocument source, int columns, int rows,
        WallSettings settings, WallViewportBudget budget, WallMetrics? metrics = null)
    {
        metrics ??= new();
        if (MemberHeightRejection(source) is { } memberRejection) return memberRejection;
        if (columns < 1 || rows < 1 || columns > budget.MaxColumns || rows > budget.MaxRows)
            return new(null, WallRejection.WorkAreaTooSmall, "CONTENT_TOO_LARGE");
        var result = WallLayoutEngine.TryApply(source, new ResizeWallAction(source.Generation, columns, rows),
            metrics.WallWidth(64), metrics.WallHeight(256), metrics);
        if (result.Proposal is null)
        {
            if (result.Rejection != WallRejection.NoSpace) return result;
            foreach (var group in source.Lanes.SelectMany(l => l.Groups))
            {
                var occupiedRows = group.Members.SelectMany(m => Enumerable.Range(m.LocalBounds.Row, m.LocalBounds.Height)).Distinct().Count();
                if (occupiedRows > 0 && metrics.TopMargin + metrics.Margin + metrics.TitleHeight +
                    metrics.TitleToMembersGap + metrics.HeightOf(occupiedRows) > budget.MaxHeightDip + 1e-8)
                    return new(null, WallRejection.NoSpace, "GROUP_TOO_TALL:" + (string.IsNullOrWhiteSpace(group.Title) ? group.Id : group.Title));
            }
            return result;
        }
        return Validate(result.Proposal.Document with { Settings = settings }, budget, metrics);
    }

    private static WallLayoutResult? MemberHeightRejection(WallDocument document)
    {
        var oversized = document.Lanes.SelectMany(l => l.Groups).SelectMany(g => g.Members)
            .FirstOrDefault(member => member.LocalBounds.Height > MaximumMemberRows);
        return oversized is null ? null : new(null, WallRejection.NoSpace,
            $"MEMBER_TOO_TALL:{oversized.LocalBounds.Height}:{MaximumMemberRows}");
    }
}


