using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TileWall.ApplicationModel;
using TileWall.Core.Wall;
using TileWall.Resources;
using Windows.Foundation;
using Windows.Graphics;

namespace TileWall;

public sealed partial class MainWindow
{
    private WorkAreaRect _screenArea;
    private RectInt32? _taskbarPlacementArea;
    private WallPresentationBounds _taskbarPlacementScreen;
    private nint _taskbarWindow;
    private bool _presentationHorizontalOverflow, _presentationVerticalOverflow;
    private WallPresentationBounds ScreenPresentationBounds => new(_screenArea.Left, _screenArea.Top,
        Math.Max(1, _screenArea.Right - _screenArea.Left), Math.Max(1, _screenArea.Bottom - _screenArea.Top));
    private WallPresentationBounds PresentationBounds => WallPresentationGeometry.ResolveArea(ScreenPresentationBounds,
        _taskbarPlacementScreen == ScreenPresentationBounds && _taskbarPlacementArea is { } area
            ? new(area.X, area.Y, area.Width, area.Height) : null, Settings.ActivateTaskbar);

    // Observe/apply callers own Explorer effects. This entry only changes this window's
    // presentation and must never show, activate, capture, or publish a layout proposal.
    internal void SetTaskbarPlacement(RectInt32? area, nint taskbarWindow)
    {
        var previous = PresentationBounds;
        ReadPrimaryScreen();
        _taskbarPlacementArea = area;
        _taskbarPlacementScreen = ScreenPresentationBounds;
        _taskbarWindow = area is not null ? taskbarWindow : 0;
        if (previous != PresentationBounds)
        {
            CancelTransientState();
            StopViewportTransition();
            if (_hasDocument && !_viewBlocked && _environmentWallShown) ApplyViewport(animateMode: false);
            _dialogHost?.RefreshPlacementBounds();
        }
        UpdateWindowZOrder(IsForegroundWallSession());
    }

    internal RectInt32 ConstrainDialogWorkArea(RectInt32 workArea)
    {
        if (!Settings.ActivateTaskbar || _taskbarPlacementArea is null) return workArea;
        var screen = ScreenPresentationBounds;
        // A dragged page on another display keeps that display's ordinary work area.
        if ((long)workArea.X + workArea.Width <= screen.X || workArea.X >= (long)screen.X + screen.Width ||
            (long)workArea.Y + workArea.Height <= screen.Y || workArea.Y >= (long)screen.Y + screen.Height)
            return workArea;
        var area = PresentationBounds;
        var left = Math.Max(workArea.X, area.X); var top = Math.Max(workArea.Y, area.Y);
        var right = Math.Min((long)workArea.X + workArea.Width, (long)area.X + area.Width);
        var bottom = Math.Min((long)workArea.Y + workArea.Height, (long)area.Y + area.Height);
        return right > left && bottom > top ? new(left, top, (int)(right - left), (int)(bottom - top)) : workArea;
    }

    private RectInt32 ConstrainPresentationRectangle(RectInt32 rectangle)
    {
        var bounded = WallPresentationGeometry.Constrain(new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height), PresentationBounds);
        return new(bounded.X, bounded.Y, bounded.Width, bounded.Height);
    }

    private int MaximumPresentedWindowedWidthPixels => Math.Max(1, Math.Min(MaximumWindowedWidthPixels, PresentationBounds.Width));
    private int MaximumPresentedWindowedHeightPixels => Math.Max(1, Math.Min(
        (int)Math.Floor((_screenArea.Bottom - _screenArea.Top) * .9), PresentationBounds.Height));

    private double CurrentWindowedViewportWidthDip(WallDocument source) =>
        !source.Settings.FullScreen && PresentationBounds != ScreenPresentationBounds
            ? source.Settings.WindowedViewportWidthDip ?? (source.Settings.ExpandColumns
                ? MaximumWindowedClientWidthDip
                : WallLayoutEngine.Measure(source, _metrics).WallWidth + WallViewportPolicy.FixedWidth(source.Settings.MenuStart))
            : Math.Max(1, (AppWindow.Size.Width - _frameWidth) / Scale);
    private WallViewportBudget CurrentViewportBudget => RecoverableViewportBudget(Settings);
    private WallViewportBudget ViewportBudget(bool fullScreen) => ViewportBudget(Settings with
        { FullScreen = fullScreen });
    private WallViewportBudget ViewportBudget(WallSettings settings) => WallViewportPolicy.Create(
        Math.Max(1, _screenArea.Right - _screenArea.Left - _frameWidth) / Scale,
        Math.Max(1, _screenArea.Bottom - _screenArea.Top - _frameHeight) / Scale, settings.FullScreen, _metrics,
        settings.ExpandColumns, settings.MenuStart);

    private int MaximumWindowedWidthPixels =>
        (int)Math.Floor((_screenArea.Right - _screenArea.Left) * WallViewportPolicy.WindowedWidthFraction);

    private double MaximumWindowedClientWidthDip =>
        Math.Max(0, (MaximumWindowedWidthPixels - _frameWidth) / Scale);

    private double MaximumTileWidthDip(WallSettings settings) => settings.FullScreen
        ? WallViewportPolicy.FullScreenVisibleSize((_screenArea.Right - _screenArea.Left - _frameWidth) / Scale,
            0, _metrics, settings.MenuStart).Width
        : Math.Max(0, MaximumWindowedClientWidthDip - WallViewportPolicy.FixedWidth(settings.MenuStart));

    private void ReadPrimaryScreen()
    {
        _screenArea = new WorkAreaRect { Left = 0, Top = 0, Right = GetSystemMetrics(0), Bottom = GetSystemMetrics(1) };
        _nativeBackground.SetScreenExtent(_screenArea.Right, _screenArea.Bottom);
    }

    private static string ViewportFailure(WallLayoutResult result)
    {
        if (result.Detail == "LAYOUT_LOCKED") return ProductStrings.Get("Wall.LayoutLocked");
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
        if (source.Settings.LayoutLocked && requested.LayoutLocked &&
            (requested.FullScreen != source.Settings.FullScreen || requested.AnchorCorner != source.Settings.AnchorCorner ||
             requested.MenuStart != source.Settings.MenuStart || requested.ExpandColumns != source.Settings.ExpandColumns))
            return new(null, WallRejection.InvalidAction, "LAYOUT_LOCKED");
        if (requested.LayoutLocked && !source.Settings.LayoutLocked)
            requested = requested with { WindowedViewportWidthDip = CurrentWindowedViewportWidthDip(source) };
        requested = WallViewportPolicy.ForModeTransition(source.Settings, requested);
        var maximumTileWidth = MaximumTileWidthDip(requested);
        if (maximumTileWidth > 0 && WallViewportPolicy.CompleteColumns(maximumTileWidth, _metrics) == 0)
            requested = requested with { ExpandColumns = true };
        var budget = ViewportBudget(requested);
        if (requested.FullScreen)
        {
            var settings = source.Settings.FullScreen ? requested : requested with
            {
                WindowedColumns = source.Columns, WindowedRows = source.Rows,
                WindowedViewportWidthDip = CurrentWindowedViewportWidthDip(source),
            };
            return WallViewportPolicy.TryResize(source, Math.Min(source.Columns, budget.MaxColumns), budget.MaxRows, settings, budget, _metrics);
        }
        var targetColumns = requested.ExpandColumns ? source.Columns :
            columns ?? (source.Settings.FullScreen ? source.Settings.WindowedColumns : source.Columns) ?? source.Columns;
        var targetRows = rows ?? (source.Settings.FullScreen ? source.Settings.WindowedRows : source.Rows) ?? source.Rows;
        var targetAnchor = anchor ?? requested.AnchorCorner;
        targetColumns = Math.Min(targetColumns, budget.MaxColumns);
        targetRows = Math.Min(targetRows, budget.MaxRows);
        var maximumWidth = MaximumWindowedClientWidthDip;
        if (source.Settings.FullScreen && !requested.ExpandColumns)
            return WallViewportPolicy.TryRestoreWindowed(source, targetColumns, targetRows,
                requested with { AnchorCorner = targetAnchor }, budget, maximumWidth, _metrics);
        if (source.Settings.FullScreen)
            requested = requested with { WindowedViewportWidthDip = requested.ManualWindowedViewportWidthDip ?? maximumWidth };
        else if (requested.ExpandColumns && !source.Settings.ExpandColumns)
            requested = requested with { WindowedViewportWidthDip = maximumWidth };
        if (!requested.ExpandColumns && source.Settings.ExpandColumns)
        {
            var clientWidth = Math.Min(maximumWidth, requested.WindowedViewportWidthDip ?? maximumWidth);
            var tileWidth = Math.Max(1, clientWidth - WallViewportPolicy.FixedWidth(requested.MenuStart));
            var visibleColumns = Math.Min(WallViewportPolicy.CompleteColumns(tileWidth, _metrics), budget.MaxColumns);
            budget = budget with { MaxWidthDip = tileWidth, MaxColumns = visibleColumns };
            targetColumns = Math.Min(targetColumns, visibleColumns);
        }
        var windowSettings = requested with { AnchorCorner = targetAnchor, WindowedColumns = targetColumns,
            WindowedRows = targetRows };
        var result = WallViewportPolicy.TryResize(source, targetColumns, targetRows, windowSettings, budget, _metrics);
        if (result.Proposal is null && !requested.ExpandColumns &&
            source.Settings.ExpandColumns && targetColumns < budget.MaxColumns)
            result = WallViewportPolicy.TryResize(source, budget.MaxColumns, targetRows,
                windowSettings with { WindowedColumns = budget.MaxColumns }, budget, _metrics);
        if (result.Proposal is not { } proposal) return result;
        var fixedWidth = WallViewportPolicy.FixedWidth(requested.MenuStart);
        var desiredWidth = requested.WindowedViewportWidthDip ??
            (requested.ExpandColumns ? maximumWidth : proposal.Geometry.WallWidth + fixedWidth);
        var settledWidth = fixedWidth + WallViewportPolicy.ResolveVisibleWidth(proposal.Document.Settings,
            proposal.Geometry, desiredWidth - fixedWidth, maximumTileWidth, _metrics);
        return result with { Proposal = proposal with { Document = proposal.Document with
        {
            Settings = proposal.Document.Settings with { WindowedViewportWidthDip = settledWidth },
        } } };
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
            if (Surface.Geometry is { } geometry)
                visibleWidth = ResolvePresentedTileWidth(settings, geometry, visibleWidth, visibleWidth);
            x = right ? width - sideWidth - visibleWidth : sideWidth; y = height * .05;
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
        var expanded = PresentedViewportSettings.ExpandColumns;
        var scrolling = expanded || _environmentOverflow || _presentationHorizontalOverflow;
        var vertical = _environmentOverflow || _presentationVerticalOverflow;
        if (_scrollMode != scrolling || _verticalScrollMode != vertical)
        {
            _scrollMode = scrolling;
            _verticalScrollMode = vertical;
            WallScroll.HorizontalScrollMode = scrolling ? ScrollMode.Enabled : ScrollMode.Disabled;
            WallScroll.VerticalScrollMode = vertical ? ScrollMode.Enabled : ScrollMode.Disabled;
            WallScroll.VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            if (!scrolling || !vertical) WallScroll.ChangeView(scrolling ? null : 0, vertical ? null : 0, null, disableAnimation: true);
        }
        WallScroll.HorizontalScrollBarVisibility = expanded ? ScrollBarVisibility.Hidden :
            scrolling ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        WallHorizontalScrollBar.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (_scrollRegion != region)
        {
            _scrollRegion = region;
            WallScroll.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left;
            WallScroll.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Top;
            WallScroll.Width = region.Width; WallScroll.Height = region.Height;
            WallScroll.Margin = new Microsoft.UI.Xaml.Thickness(region.X, region.Y, 0, 0);
            WallHorizontalScrollBar.Width = region.Width;
            WallHorizontalScrollBar.Margin = new Thickness(region.X,
                region.Y + Math.Max(0, region.Height - WallHorizontalScrollBar.Height), 0, 0);
            PositionStatusViewport();
        }
        // The document owns its complete row budget. A larger clip must not create phantom rows.
        if (updateSurface) Surface.SetViewportMinimum(fullScreen || scrolling ? region.Width : 0, 0);
        UpdateWindowsSidebar();
        UpdateVisibleWallFrames();
        QueueExpansionReveal();
    }

    private bool _syncingWallScrollBar;

    private void InitializeWallScrollRail()
    {
        AutomationProperties.SetName(WallHorizontalScrollBar, ProductStrings.Get("Settings.ExpandColumns"));
        WallScroll.RegisterPropertyChangedCallback(ScrollViewer.ScrollableWidthProperty, (_, _) => SyncWallScrollRail());
        WallScroll.RegisterPropertyChangedCallback(ScrollViewer.ViewportWidthProperty, (_, _) => SyncWallScrollRail());
        WallScroll.ViewChanged += (_, _) => SyncWallScrollRail();
        WallHorizontalScrollBar.ValueChanged += (_, args) =>
        {
            if (_syncingWallScrollBar) return;
            _pendingExpansionLane = null;
            _pendingCommittedScrollAnchor = null;
            WallScroll.ChangeView(Math.Clamp(args.NewValue, 0, WallScroll.ScrollableWidth), null, null, disableAnimation: true);
        };
    }

    private void SyncWallScrollRail()
    {
        _syncingWallScrollBar = true;
        try
        {
            var width = WallScroll.ScrollableWidth;
            WallHorizontalScrollBar.Maximum = width;
            WallHorizontalScrollBar.ViewportSize = WallScroll.ViewportWidth;
            WallHorizontalScrollBar.SmallChange = _metrics.Pitch;
            WallHorizontalScrollBar.LargeChange = Math.Max(_metrics.Pitch, WallScroll.ViewportWidth - _metrics.ColumnGap);
            WallHorizontalScrollBar.Value = Math.Clamp(WallScroll.HorizontalOffset, 0, width);
            WallHorizontalScrollBar.IsEnabled = width > ScrollAnchorEpsilon;
        }
        finally { _syncingWallScrollBar = false; }
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
    private int? _pendingExpansionLane;
    private bool _expansionRevealQueued;

    private void CaptureExpansionReveal(WallDocument next)
    {
        if (!next.Settings.ExpandColumns || _pendingExpansionLane >= next.Columns)
            _pendingExpansionLane = null;
        if (!next.Settings.ExpandColumns || !_hasDocument || _viewBlocked || _committedScrollExpanded ||
            _committedScrollGeometry is null || _scrollRegion.Width <= 0)
            return;

        var oldRight = WallScroll.HorizontalOffset + _scrollRegion.Width;
        var firstHidden = WallViewportPolicy.CompleteColumns(oldRight, _metrics);
        if (firstHidden < next.Columns) _pendingExpansionLane = firstHidden;
    }

    private bool CanRevealExpansion => !_windowClosing && !ShutdownStarted && _hasDocument && !_viewBlocked &&
        Settings.ExpandColumns && !_modal && !_resizing && _viewportTransition is null && AppWindow.IsVisible;

    private void QueueExpansionReveal()
    {
        if (_pendingExpansionLane is null || _expansionRevealQueued || !CanRevealExpansion) return;
        _expansionRevealQueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _expansionRevealQueued = false;
            if (!CanRevealExpansion || _pendingExpansionLane is not { } lane) return;
            WallScroll.UpdateLayout();
            if (!CanRevealExpansion || _pendingExpansionLane != lane || WallScroll.ViewportWidth <= 0) return;
            _pendingExpansionLane = null;
            var left = _metrics.Margin + lane * (_metrics.LaneWidth + _metrics.ColumnGap);
            var offset = WallScroll.HorizontalOffset;
            var width = WallScroll.ViewportWidth;
            var target = width < _metrics.LaneWidth ? left :
                Math.Clamp(offset, left + _metrics.LaneWidth - width, left);
            target = Math.Clamp(target, 0, WallScroll.ScrollableWidth);
            if (Math.Abs(offset - target) > ScrollAnchorEpsilon)
                WallScroll.ChangeView(target, null, null, disableAnimation: true);
        });
    }

    private CommittedScrollAnchor? CaptureCommittedScrollAnchor(WallDocument next)
    {
        EnsureCommittedScrollTracking();
        CaptureExpansionReveal(next);
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
            _pendingExpansionLane = null;
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
