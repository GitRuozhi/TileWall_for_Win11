using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TileWall.Core.Wall;
using TileWall.Resources;
using Windows.Foundation;
using Windows.Graphics;

namespace TileWall;

public sealed partial class MainWindow
{
    private WorkAreaRect _screenArea;
    private WallViewportBudget CurrentViewportBudget => RecoverableViewportBudget(Settings);
    private WallViewportBudget ViewportBudget(bool fullScreen) => ViewportBudget(Settings with
        { FullScreen = fullScreen });
    private WallViewportBudget ViewportBudget(WallSettings settings) => WallViewportPolicy.Create(
        Math.Max(1, _screenArea.Right - _screenArea.Left - _frameWidth) / Scale,
        Math.Max(1, _screenArea.Bottom - _screenArea.Top - _frameHeight) / Scale, settings.FullScreen, _metrics,
        settings.ExpandColumns, settings.MenuStart);

    private void ReadPrimaryScreen() => _screenArea = new WorkAreaRect
        { Left = 0, Top = 0, Right = GetSystemMetrics(0), Bottom = GetSystemMetrics(1) };

    private static string ViewportFailure(WallLayoutResult result)
    {
        if (result.Detail?.StartsWith("MEMBER_TOO_TALL:", StringComparison.Ordinal) == true)
        {
            var parts = result.Detail.Split(':');
            if (parts.Length == 3)
                return string.Format(ProductStrings.Get("Wall.MemberTooTall"), parts[1], parts[2]);
        }
        const string prefix = "GROUP_TOO_TALL:";
        return result.Detail?.StartsWith(prefix, StringComparison.Ordinal) == true
            ? string.Format(ProductStrings.Get("Wall.GroupTooTall"), result.Detail[prefix.Length..])
            : ProductStrings.Get("Wall.ContentTooLarge");
    }

    private WallLayoutResult ProposeMode(WallDocument source, WallSettings requested,
        int? columns = null, int? rows = null, AnchorCorner? anchor = null)
    {
        requested = WallViewportPolicy.ForModeTransition(source.Settings, requested);
        var budget = ViewportBudget(requested);
        if (requested.FullScreen)
        {
            var settings = source.Settings.FullScreen ? requested : requested with
            {
                WindowedColumns = source.Columns, WindowedRows = source.Rows,
                WindowedViewportWidthDip = Math.Max(1, (AppWindow.Size.Width - _frameWidth) / Scale),
            };
            return WallViewportPolicy.TryResize(source, Math.Min(source.Columns, budget.MaxColumns), budget.MaxRows, settings, budget, _metrics);
        }
        var targetColumns = requested.ExpandColumns ? source.Columns :
            columns ?? (source.Settings.FullScreen ? source.Settings.WindowedColumns : source.Columns) ?? source.Columns;
        var targetRows = rows ?? (source.Settings.FullScreen ? source.Settings.WindowedRows : source.Rows) ?? source.Rows;
        var targetAnchor = anchor ?? requested.AnchorCorner;
        targetColumns = Math.Min(targetColumns, budget.MaxColumns);
        targetRows = Math.Min(targetRows, budget.MaxRows);
        var maximumWidth = Math.Max(1, (_screenArea.Right - _screenArea.Left) * .9 / Scale - _frameWidth / Scale);
        if (source.Settings.FullScreen)
            requested = requested with { WindowedViewportWidthDip = requested.ManualWindowedViewportWidthDip ?? maximumWidth };
        else if (requested.ExpandColumns && !source.Settings.ExpandColumns)
            requested = requested with { WindowedViewportWidthDip = maximumWidth };
        if (!requested.ExpandColumns && (source.Settings.FullScreen || source.Settings.ExpandColumns))
        {
            var clientWidth = Math.Min(maximumWidth, requested.WindowedViewportWidthDip ?? maximumWidth);
            var tileWidth = Math.Max(1, clientWidth - WallViewportPolicy.FixedWidth(requested.MenuStart));
            var visibleColumns = Math.Clamp((int)Math.Floor((tileWidth - 2 * _metrics.Margin + _metrics.ColumnGap) /
                (_metrics.LaneWidth + _metrics.ColumnGap) + 1e-8), 0, budget.MaxColumns);
            budget = budget with { MaxWidthDip = tileWidth, MaxColumns = visibleColumns };
            targetColumns = Math.Min(targetColumns, visibleColumns);
        }
        var windowSettings = requested with { AnchorCorner = targetAnchor, WindowedColumns = targetColumns,
            WindowedRows = targetRows };
        var result = WallViewportPolicy.TryResize(source, targetColumns, targetRows, windowSettings, budget, _metrics);
        if (result.Proposal is null && !requested.ExpandColumns &&
            (source.Settings.FullScreen || source.Settings.ExpandColumns) && targetColumns < budget.MaxColumns)
            result = WallViewportPolicy.TryResize(source, budget.MaxColumns, targetRows,
                windowSettings with { WindowedColumns = budget.MaxColumns }, budget, _metrics);
        return result;
    }

    // The Surface and all hit/drop calculations use content coordinates. Only window overlays
    // consume viewport coordinates, so scrolling never changes stored member or pointer offsets.
    private Point ContentToViewport(Point point) => new(point.X - WallScroll.HorizontalOffset + WallScroll.Margin.Left,
        point.Y - WallScroll.VerticalOffset + WallScroll.Margin.Top);

    private Rect ScrollRegionFor(bool fullScreen, double width, double height, AnchorCorner? anchor = null,
        WallSettings? presentationSettings = null)
    {
        var settings = presentationSettings ?? PresentedViewportSettings;
        var right = (anchor ?? settings.AnchorCorner) is AnchorCorner.TopRight or AnchorCorner.BottomRight;
        double x, y, visibleWidth, visibleHeight;
        var sideWidth = WallViewportPolicy.FixedWidth(settings.MenuStart);
        if (fullScreen)
        {
            (visibleWidth, visibleHeight) = WallViewportPolicy.FullScreenVisibleSize(width, height, _metrics, settings.MenuStart);
            x = right ? width * .05 : sideWidth; y = height * .05;
        }
        else
        {
            visibleWidth = Math.Max(1, width - sideWidth);
            visibleHeight = Math.Max(1, height);
            x = right ? 0 : sideWidth; y = 0;
        }
        return new Rect(x, y, visibleWidth, visibleHeight);
    }

    private void SetScrollRegion(bool fullScreen, Rect region, bool updateSurface = true,
        WallSettings? presentationSettings = null)
    {
        if (presentationSettings is not null) _presentedViewportSettings = presentationSettings;
        var scrolling = PresentedViewportSettings.ExpandColumns || _environmentOverflow;
        if (_scrollMode != scrolling || _verticalScrollMode != _environmentOverflow)
        {
            _scrollMode = scrolling;
            _verticalScrollMode = _environmentOverflow;
            WallScroll.HorizontalScrollMode = scrolling ? ScrollMode.Enabled : ScrollMode.Disabled;
            WallScroll.HorizontalScrollBarVisibility = scrolling ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            WallScroll.VerticalScrollMode = _environmentOverflow ? ScrollMode.Enabled : ScrollMode.Disabled;
            WallScroll.VerticalScrollBarVisibility = _environmentOverflow ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            if (!scrolling || !_environmentOverflow) WallScroll.ChangeView(scrolling ? null : 0, _environmentOverflow ? null : 0, null, disableAnimation: true);
        }
        if (_scrollRegion != region)
        {
            _scrollRegion = region;
            WallScroll.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left;
            WallScroll.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Top;
            WallScroll.Width = region.Width; WallScroll.Height = region.Height;
            WallScroll.Margin = new Microsoft.UI.Xaml.Thickness(region.X, region.Y, 0, 0);
        }
        // The document owns its complete row budget. A larger clip must not create phantom rows.
        if (updateSurface) Surface.SetViewportMinimum(fullScreen || scrolling ? region.Width : 0, 0);
        UpdateWindowsSidebar();
        UpdateVisibleWallFrames();
    }

    private bool? _scrollMode;
    private bool _verticalScrollMode;
    private Rect _scrollRegion;
    private WallSettings? _presentedViewportSettings;
    private WallSettings PresentedViewportSettings => _presentedViewportSettings ?? Settings;

    private sealed record CommittedScrollAnchor(string GroupId, double RelativeX, double OriginalX,
        double OriginalOffset, long Generation, long Revision, double RasterizationScale);
    private WallGeometry? _committedScrollGeometry;
    private bool _committedScrollExpanded, _scrollAnchorTracking;
    private long _scrollAnchorRevision;
    private CommittedScrollAnchor? _pendingCommittedScrollAnchor;
    private const double ScrollAnchorEpsilon = .01;

    private CommittedScrollAnchor? CaptureCommittedScrollAnchor(WallDocument next)
    {
        EnsureCommittedScrollTracking();
        var revision = ++_scrollAnchorRevision;
        _pendingCommittedScrollAnchor = null;
        if (_windowClosing || !_hasDocument || _viewBlocked || !_committedScrollExpanded ||
            !next.Settings.ExpandColumns || _scrollMode != true ||
            _resizing || _gesture.Phase != ApplicationModel.GesturePhase.Idle ||
            _committedScrollGeometry is not { } previous || Surface.Geometry is not { } displayed)
            return null;

        // A released drag/resize may leave its proposal drawn until storage settles.
        // Its numeric offset belongs to the existing preview/cancel path, not this anchor.
        if (previous.WallWidth != displayed.WallWidth || previous.Groups.Count != displayed.Groups.Count ||
            !previous.Groups.Zip(displayed.Groups).All(pair => pair.First.GroupId == pair.Second.GroupId &&
                pair.First.LaneIndex == pair.Second.LaneIndex && pair.First.Bounds.X == pair.Second.Bounds.X))
            return null;

        var offset = WallScroll.HorizontalOffset;
        var width = WallScroll.ViewportWidth;
        if (!double.IsFinite(offset) || !double.IsFinite(width) || width <= 0) return null;
        var surviving = next.Lanes.SelectMany(lane => lane.Groups).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var group = previous.Groups.Where(placement => surviving.Contains(placement.GroupId) &&
                placement.Bounds.X + placement.Bounds.Width > offset && placement.Bounds.X < offset + width)
            .OrderBy(placement => placement.Bounds.X).ThenBy(placement => placement.Bounds.Y).FirstOrDefault();
        return group is null ? null : new(group.GroupId, group.Bounds.X - offset, group.Bounds.X,
            offset, next.Generation, revision, Scale);
    }

    private void RestoreCommittedScrollAnchor(WallDocument committed, CommittedScrollAnchor? anchor)
    {
        _committedScrollGeometry = Surface.Geometry;
        _committedScrollExpanded = committed.Settings.ExpandColumns;
        if (anchor is null || !CommittedScrollAnchorIsCurrent(anchor) ||
            Surface.Geometry?.Groups.FirstOrDefault(placement => placement.GroupId == anchor.GroupId) is not { } group ||
            Math.Abs(group.Bounds.X - anchor.OriginalX) <= ScrollAnchorEpsilon)
            return;

        _pendingCommittedScrollAnchor = anchor;
        // Wait until Render/PlaceWall have released their layout stack before reading
        // the new extent. User input or a later committed projection cancels this turn.
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!ReferenceEquals(_pendingCommittedScrollAnchor, anchor) || !CommittedScrollAnchorIsCurrent(anchor)) return;
            try
            {
                WallScroll.UpdateLayout();
                if (!ReferenceEquals(_pendingCommittedScrollAnchor, anchor) || !CommittedScrollAnchorIsCurrent(anchor)) return;
                var maximum = Math.Max(0, WallScroll.ScrollableWidth);
                var target = Math.Clamp(group.Bounds.X - anchor.RelativeX, 0, maximum);
                _pendingCommittedScrollAnchor = null;
                if (Math.Abs(WallScroll.HorizontalOffset - target) > ScrollAnchorEpsilon)
                    WallScroll.ChangeView(target, null, null, disableAnimation: true);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                _pendingCommittedScrollAnchor = null;
                TileWall.Core.Shell.AppLog.Warn("ScrollAnchor", ex.GetType().Name);
            }
        })) _pendingCommittedScrollAnchor = null;
    }

    private bool CommittedScrollAnchorIsCurrent(CommittedScrollAnchor anchor) =>
        !_windowClosing && _hasDocument && !_viewBlocked && _scrollMode == true &&
        !_resizing && _gesture.Phase == ApplicationModel.GesturePhase.Idle &&
        anchor.Revision == _scrollAnchorRevision && _session.Current.Generation == anchor.Generation &&
        Scale == anchor.RasterizationScale && Settings.ExpandColumns;

    private void EnsureCommittedScrollTracking()
    {
        if (_scrollAnchorTracking) return;
        _scrollAnchorTracking = true;
        void Invalidate()
        {
            ++_scrollAnchorRevision;
            _pendingCommittedScrollAnchor = null;
        }
        WallScroll.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => Invalidate()), true);
        WallScroll.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => Invalidate()), true);
        WallScroll.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, _) => Invalidate()), true);
        WallScroll.ViewChanging += (_, args) =>
        {
            if (_pendingCommittedScrollAnchor is not { } pending) return;
            // Extent shrinkage may clamp the old offset during layout. Any other
            // newer view request (including inertial scrolling) takes precedence.
            var clamped = Math.Clamp(pending.OriginalOffset, 0, Math.Max(0, WallScroll.ScrollableWidth));
            if (Math.Abs(args.NextView.HorizontalOffset - clamped) > ScrollAnchorEpsilon &&
                Math.Abs(args.NextView.HorizontalOffset - WallScroll.HorizontalOffset) > ScrollAnchorEpsilon)
                Invalidate();
        };
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
