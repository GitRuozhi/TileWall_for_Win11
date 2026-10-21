namespace TileWall.Core.Wall;

/// <summary>Logical capacity of a primary-screen viewport; never persisted as screen pixels.</summary>
public sealed record WallViewportBudget(double MaxWidthDip, double MaxHeightDip,
    int MaxColumns, int MaxRows, bool FullScreen);

public static class WallViewportPolicy
{
    public const int MaximumMemberRows = 12;

    public static WallViewportBudget Create(double screenWidthDip, double screenHeightDip,
        bool fullScreen, WallMetrics? metrics = null, bool expandColumns = true, bool menuStart = false)
    {
        metrics ??= new();
        // Fullscreen leaves five percent on the side opposite its fixed rail. Windowed
        // mode limits the entire outer width to ninety percent of the primary screen.
        var width = Math.Max(0, screenWidthDip * (fullScreen ? .95 : .9) - FixedWidth(menuStart));
        var height = Math.Max(0, screenHeightDip * .9);
        var columns = Math.Clamp((int)Math.Floor((width - 2 * metrics.Margin + metrics.ColumnGap) /
            (metrics.LaneWidth + metrics.ColumnGap) + 1e-8), 0, 64);
        var rows = Math.Clamp((int)Math.Floor((height - metrics.TopMargin - metrics.Margin + metrics.Gap) /
            metrics.Pitch + 1e-8), 0, 256);
        var expanded = expandColumns && columns > 0;
        return new(expanded ? metrics.WallWidth(64) : width, height,
            expanded ? 64 : columns, rows, fullScreen);
    }

    public const double SidebarWidthDip = 48;
    public const double MenuWidthDip = 256;

    public static double FixedWidth(bool menuStart) => SidebarWidthDip + (menuStart ? MenuWidthDip : 0);

    /// <summary>Display mode changes preserve the explicitly requested expansion and anchor.</summary>
    public static WallSettings ForModeTransition(WallSettings previous, WallSettings requested) => requested;

    /// <summary>Display-only filler lanes append at the real tail, never create persisted holes.</summary>
    public static int MapCreationLane(WallDocument document, int requestedLane)
    {
        if ((!document.Settings.FullScreen && !document.Settings.ExpandColumns) ||
            requestedLane < document.Columns) return requestedLane;
        return document.Settings.ExpandColumns && document.Lanes[^1].Groups.Count == 0
            ? document.Columns - 1 : document.Columns;
    }

    public static (double Width, double Height) FullScreenVisibleSize(double screenWidthDip,
        double screenHeightDip, WallMetrics? metrics = null, bool menuStart = false)
    {
        return (Math.Max(0, screenWidthDip * .95 - FixedWidth(menuStart)),
            Math.Max(0, screenHeightDip * .9));
    }

    /// <summary>
    /// Probes the ordinary layout on manual narrowing. An unsuccessful fit or a complete
    /// remaining empty lane keeps expansion enabled; the caller owns committing the proposal.
    /// </summary>
    public static WallLayoutResult? TryCloseWindowedExpansion(WallDocument source,
        WallViewportBudget visibleBudget, WallMetrics? metrics = null)
    {
        if (source.Settings.FullScreen || !source.Settings.ExpandColumns || visibleBudget.FullScreen ||
            visibleBudget.MaxColumns < 1 || visibleBudget.MaxRows < 1) return null;
        var fitted = TryResize(source, visibleBudget.MaxColumns,
            visibleBudget.MaxRows, source.Settings with { ExpandColumns = false },
            visibleBudget, metrics);
        return fitted.Proposal is { } candidate && candidate.Document.Lanes.All(lane => lane.Groups.Count > 0)
            ? fitted : null;
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

    /// <summary>
    /// Reuses geometry from a proposal that already came from layout, import, drag, resize, or settings validation.
    /// A mismatched geometry falls back to a full document check.
    /// </summary>
    public static WallLayoutResult Validate(WallProposal proposal, WallViewportBudget budget, WallMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        metrics ??= new();
        var document = proposal.Document;
        if (MemberHeightRejection(document) is { } memberRejection) return memberRejection;
        var expected = document.Lanes.SelectMany(lane => lane.Groups).Where(group => group.Members.Count > 0)
            .Select(group => group.Id).ToArray();
        var placed = proposal.Geometry.Groups;
        if (placed.Count != expected.Length || !placed.Select(group => group.GroupId).SequenceEqual(expected))
            return Validate(document, budget, metrics);
        foreach (var group in placed)
        {
            if (metrics.TopMargin + metrics.Margin + group.Bounds.Height > budget.MaxHeightDip + 1e-8)
            {
                var source = document.Lanes.SelectMany(lane => lane.Groups).First(item => item.Id == group.GroupId);
                var name = string.IsNullOrWhiteSpace(source.Title) ? source.Id : source.Title;
                return new(null, WallRejection.NoSpace, "GROUP_TOO_TALL:" + name);
            }
        }
        if (document.Columns > budget.MaxColumns || document.Rows > budget.MaxRows ||
            proposal.Geometry.WallWidth > budget.MaxWidthDip + 1e-8 ||
            proposal.Geometry.WallHeight > budget.MaxHeightDip + 1e-8)
            return new(null, WallRejection.WorkAreaTooSmall, "CONTENT_TOO_LARGE");
        return new(proposal, WallRejection.None);
    }

    /// <summary>Resize/compact a copy. Failed fits never expose partial lane or row removal.</summary>
    public static WallLayoutResult TryResize(WallDocument source, int columns, int rows,
        WallSettings settings, WallViewportBudget budget, WallMetrics? metrics = null)
    {
        metrics ??= new();
        if (MemberHeightRejection(source) is { } memberRejection) return memberRejection;
        if (columns < 1 || rows < 1 || columns > budget.MaxColumns || rows > budget.MaxRows)
            return new(null, WallRejection.WorkAreaTooSmall, "CONTENT_TOO_LARGE");
        var result = WallLayoutEngine.TryApply(source with { Settings = settings }, new ResizeWallAction(source.Generation, columns, rows),
            metrics.WallWidth(64), metrics.WallHeight(256), metrics);
        if (result.Proposal is null)
        {
            if (result.Rejection != WallRejection.NoSpace) return result;
            foreach (var group in source.Lanes.SelectMany(l => l.Groups))
            {
                var occupiedRows = WallLayoutEngine.MinimumGroupRows(group);
                if (occupiedRows > 0 && metrics.TopMargin + metrics.Margin + metrics.TitleHeight +
                    metrics.TitleToMembersGap + metrics.HeightOf(occupiedRows) > budget.MaxHeightDip + 1e-8)
                    return new(null, WallRejection.NoSpace, "GROUP_TOO_TALL:" + (string.IsNullOrWhiteSpace(group.Title) ? group.Id : group.Title));
            }
            return result;
        }
        return Validate(result.Proposal, budget, metrics);
    }

    private static WallLayoutResult? MemberHeightRejection(WallDocument document)
    {
        var oversized = document.Lanes.SelectMany(l => l.Groups).SelectMany(g => g.Members)
            .FirstOrDefault(member => member.LocalBounds.Height > MaximumMemberRows);
        return oversized is null ? null : new(null, WallRejection.NoSpace,
            $"MEMBER_TOO_TALL:{oversized.LocalBounds.Height}:{MaximumMemberRows}");
    }
}


