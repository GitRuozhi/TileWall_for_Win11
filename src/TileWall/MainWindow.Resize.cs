using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Input;
using TileWall.Core.Wall;
using TileWall.Resources;
using TileWall.Shell.Interop;
using TileWall.Views;
using Windows.Foundation;
using Windows.Graphics;

namespace TileWall;

public sealed partial class MainWindow
{
    private Point _resizeStart;
    private AnchorCorner _resizeAnchor;
    private AnchorCorner _resizePointerAnchor;
    private bool _resizeModeLocked;
    private bool _resizeModeTarget;
    private Point _resizeScrollOffset;
    private WallLayoutResult? _resizeFailure;
    private RectInt32 _resizeStartRect;
    private WallDocument? _resizeSnapshot;
    private WallProposal? _resizeProposal;
    private ResizeThumb? _resizeThumb;
    private bool _resizeWidth, _resizeHeight;
    private double _resizeScale;
    private int _resizeDeltaCount;
    private int _resizeLastColumns = -1, _resizeLastRows = -1;
    private bool _resizeLastNarrowing;
    private WallDocument? _resizePreviewDocument;
    private WallLayoutResult? _resizeFullResult;
    private WallLayoutResult? _resizeWindowedResult;
    private readonly Dictionary<(int Columns, int Rows), WallLayoutResult> _resizeFits = [];
    private Point _resizeLastSample;
    private int _resizeSampleCount;

    private void RemoveNativeFrame()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        const long frameStyles = 0x00C00000L | 0x00040000L; // WS_CAPTION | WS_THICKFRAME
        var style = Win32Api.GetWindowLongPtrW(hwnd, -16).ToInt64();
        Win32Api.SetWindowLongPtrW(hwnd, -16, new IntPtr(style & ~frameStyles));
        Win32Api.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0020 | 0x0001 | 0x0002 | 0x0004 | 0x0010);
        // Windows 11's DWM border and rounded corners are independent of the Win32 frame.
        // Unsupported attributes on Windows 10 return E_INVALIDARG and leave the square frame alone.
        uint border = 0xFFFFFFFE, corner = 1;
        Win32Api.DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(uint));
        Win32Api.DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(uint));
    }

    private void UpdateResizeZones()
    {
        var right = Settings.AnchorCorner is AnchorCorner.BottomRight or AnchorCorner.TopRight;
        var bottom = Settings.AnchorCorner is AnchorCorner.BottomLeft or AnchorCorner.BottomRight;
        ResizeGrip.HorizontalAlignment = ResizeVerticalEdge.HorizontalAlignment = right ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        ResizeGrip.VerticalAlignment = ResizeHorizontalEdge.VerticalAlignment = bottom ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        ResizeHorizontalHighlight.VerticalAlignment = ResizeHorizontalEdge.VerticalAlignment;
        ResizeVerticalHighlight.HorizontalAlignment = ResizeVerticalEdge.HorizontalAlignment;
        ResizeHorizontalEdge.SetCursor(InputSystemCursorShape.SizeNorthSouth);
        ResizeVerticalEdge.SetCursor(InputSystemCursorShape.SizeWestEast);
        ResizeGrip.SetCursor(right == bottom ? InputSystemCursorShape.SizeNorthwestSoutheast : InputSystemCursorShape.SizeNortheastSouthwest);
        var visibility = PersistenceInteractionBlocked || _modal || _viewBlocked ? Visibility.Collapsed : Visibility.Visible;
        var fullScreen = Settings.FullScreen;
        ResizeGrip.Visibility = ResizeVerticalEdge.Visibility = ResizeHorizontalEdge.Visibility = fullScreen ? Visibility.Collapsed : visibility;
        foreach (var corner in new[] { ResizeTopLeft, ResizeTopRight, ResizeBottomLeft, ResizeBottomRight })
        {
            corner.Visibility = fullScreen ? visibility : Visibility.Collapsed;
            corner.SetCursor(ReferenceEquals(corner, ResizeTopLeft) || ReferenceEquals(corner, ResizeBottomRight)
                ? InputSystemCursorShape.SizeNorthwestSoutheast : InputSystemCursorShape.SizeNortheastSouthwest);
        }
        if (visibility == Visibility.Collapsed) ShowResizeHighlight(null);
        UpdateNativeSizingRegion();
    }

    private void ShowResizeHighlight(ResizeThumb? zone)
    {
        if (zone is not null && Settings.FullScreen)
        {
            ResizeHorizontalHighlight.VerticalAlignment = zone.VerticalAlignment;
            ResizeVerticalHighlight.HorizontalAlignment = zone.HorizontalAlignment;
        }
        var accent = new SolidColorBrush(_appearanceSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent));
        ResizeHorizontalHighlight.Background = ResizeVerticalHighlight.Background = accent;
        ResizeHorizontalHighlight.Visibility = zone is not null && !ReferenceEquals(zone, ResizeVerticalEdge) ? Visibility.Visible : Visibility.Collapsed;
        ResizeVerticalHighlight.Visibility = zone is not null && !ReferenceEquals(zone, ResizeHorizontalEdge) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnResizePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!PersistenceInteractionBlocked && !_resizing && !_modal && !_viewBlocked) ShowResizeHighlight(sender as ResizeThumb);
    }

    private void OnResizePointerExited(object sender, PointerRoutedEventArgs e) { if (!_resizing) ShowResizeHighlight(null); }
    private void OnResizePointerCaptureLost(object sender, PointerRoutedEventArgs e) { if (!_resizing) ShowResizeHighlight(null); }

    private void OnResizeStarted(object sender, ResizeDragStartedEventArgs e)
    {
        if (PersistenceInteractionBlocked || _nativeSizingActive || _modal || _viewBlocked || !_hasDocument || sender is not ResizeThumb thumb) return;
        if (!TryGetResizeScreenPoint(e.RootPoint, e.RasterizationScale, out var pressedAt))
        { thumb.CancelDrag(); return; }
        BeginResizeSession(thumb, pressedAt);
    }

    private void BeginResizeSession(ResizeThumb thumb, Point pressedAt)
    {
        TerminateGesture();
        StopViewportTransition();
        _resizing = true;
        _resizeThumb = thumb;
        _resizeWidth = !ReferenceEquals(thumb, ResizeHorizontalEdge);
        _resizeHeight = !ReferenceEquals(thumb, ResizeVerticalEdge);
        ShowResizeHighlight(thumb);
        _resizeSnapshot = Document;
        _resizeAnchor = Settings.AnchorCorner;
        _resizePointerAnchor = !Settings.FullScreen ? Settings.AnchorCorner : thumb.Tag?.ToString() switch
        {
            "TopLeft" => AnchorCorner.BottomRight, "TopRight" => AnchorCorner.BottomLeft,
            "BottomLeft" => AnchorCorner.TopRight, _ => AnchorCorner.TopLeft,
        };
        _resizeModeLocked = false;
        _resizeModeTarget = Settings.FullScreen;
        _resizeScrollOffset = new(WallScroll.HorizontalOffset, WallScroll.VerticalOffset);
        _resizeFailure = null;
        _resizeScale = Scale;
        _resizeStart = pressedAt;
        _resizeStartRect = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        _resizeProposal = null;
        _resizePreviewDocument = null;
        _resizeLastColumns = _resizeLastRows = -1;
        _resizeLastNarrowing = false;
        _resizeDeltaCount = 0;
        _resizeSampleCount = 0;
        _resizeLastSample = _resizeStart;
        _resizeFullResult = null;
        _resizeWindowedResult = null;
        _resizeFits.Clear();
        _resizeInputPending = false;
        _performance.Begin("resize", Document, Scale, Surface.VisualCreationCount, Surface.GridCreationCount);
        RecordCompositionGateAtBegin();
        EnsureFrameSubscription();
        TraceInput($"ResizeStarted anchor={Settings.AnchorCorner} width={_resizeWidth} height={_resizeHeight} grid={Document.Columns}x{Document.Rows} pressScreen={_resizeStart.X:F2},{_resizeStart.Y:F2} startRect={_resizeStartRect.X},{_resizeStartRect.Y},{_resizeStartRect.Width},{_resizeStartRect.Height} scale={_resizeScale:F3}");
        UpdatePuzzleGate();
    }

    private void OnResizeDelta(object sender, EventArgs e)
    {
        if (!_resizing || _nativeSizingActive) return;
        _resizeDeltaCount++;
        if (GetCursorPos(out var cursor)) QueueResizeSample(new Point(cursor.X, cursor.Y));
    }

    private void UpdateResizeProposal(bool preview, Point? sample = null)
    {
        if (sample is null && GetCursorPos(out var nativeCursor)) sample = new Point(nativeCursor.X, nativeCursor.Y);
        if (_resizeSnapshot is not { } source || sample is not { } cursor)
        {
            _resizeProposal = null;
            return;
        }
        if (source.Generation != Document.Generation || _resizeScale != Scale)
        {
            CancelResize();
            return;
        }
        var right = _resizeAnchor is AnchorCorner.BottomRight or AnchorCorner.TopRight;
        var bottom = _resizeAnchor is AnchorCorner.BottomLeft or AnchorCorner.BottomRight;
        var pointerRight = _resizePointerAnchor is AnchorCorner.BottomRight or AnchorCorner.TopRight;
        var pointerBottom = _resizePointerAnchor is AnchorCorner.BottomLeft or AnchorCorner.BottomRight;
        var dx = (cursor.X - _resizeStart.X) * (pointerRight ? -1 : 1);
        var dy = (cursor.Y - _resizeStart.Y) * (pointerBottom ? -1 : 1);
        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < 7 * _resizeScale) return;
        var rawWidth = _resizeStartRect.Width + (_resizeWidth ? dx : 0);
        var rawHeight = _resizeStartRect.Height + (_resizeHeight ? dy : 0);
        var maxWidth = Math.Floor((_screenArea.Right - _screenArea.Left) * .9);
        var maxHeight = Math.Floor((_screenArea.Bottom - _screenArea.Top) * .9);
        if (!_resizeModeLocked && !source.Settings.FullScreen && (rawWidth > maxWidth || rawHeight > maxHeight))
        {
            _resizeModeLocked = true; _resizeModeTarget = true;
        }
        else if (!_resizeModeLocked && source.Settings.FullScreen && dx < -7 * _resizeScale && dy < -7 * _resizeScale)
        {
            _resizeModeLocked = true; _resizeModeTarget = false;
        }
        if (source.Settings.FullScreen && !_resizeModeLocked) return;
        if (_resizeModeTarget)
        {
            var full = _resizeFullResult ??= ProposeMode(source, source.Settings with { FullScreen = true });
            _resizeProposal = full.Proposal; _resizeFailure = full.Proposal is null ? full : null;
            if (_nativeSizingActive && full.Proposal is not null)
                _nativeSizingTargetRect = new RectInt32(_screenArea.Left, _screenArea.Top,
                    _screenArea.Right - _screenArea.Left, _screenArea.Bottom - _screenArea.Top);
            if (preview && full.Proposal is { } fullscreen && !ReferenceEquals(_resizePreviewDocument, fullscreen.Document))
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                Surface.Render(fullscreen.Document, fullscreen.Geometry);
                _resizePreviewDocument = fullscreen.Document;
                PresentViewport(new RectInt32(_screenArea.Left, _screenArea.Top,
                    _screenArea.Right - _screenArea.Left, _screenArea.Bottom - _screenArea.Top), true, _resizeAnchor, animate: true,
                    presentationSettings: fullscreen.Document.Settings);
                _performance.View(System.Diagnostics.Stopwatch.GetTimestamp() - started);
            }
            return;
        }
        if (source.Settings.FullScreen)
        {
            // Dragging any fullscreen corner changes mode only. The selected anchor
            // and last committed manual window width belong to the settings, not the corner.
            var restored = _resizeWindowedResult ??= ProposeMode(source, source.Settings with { FullScreen = false });
            _resizeProposal = restored.Proposal; _resizeFailure = restored.Proposal is null ? restored : null;
            if (preview && restored.Proposal is { } windowed && !ReferenceEquals(_resizePreviewDocument, windowed.Document))
            {
                Surface.Render(windowed.Document, windowed.Geometry);
                _resizePreviewDocument = windowed.Document;
                ApplyViewport(windowed.Document, windowed.Geometry);
            }
            return;
        }
        var budget = ViewportBudget(false);
        var fixedWidth = WallViewportPolicy.FixedWidth(source.Settings.MenuStart);
        var width = Math.Clamp(rawWidth, Math.Min(maxWidth, Math.Ceiling((_metrics.WallWidth(1) + fixedWidth) * _resizeScale) + _frameWidth), maxWidth);
        var height = Math.Clamp(rawHeight, Math.Min(maxHeight, Math.Ceiling(_metrics.WallHeight(1) * _resizeScale) + _frameHeight), maxHeight);
        var widthDip = (width - _frameWidth) / _resizeScale - fixedWidth;
        var heightDip = (height - _frameHeight) / _resizeScale;
        var columns = _resizeWidth ? Math.Clamp((int)Math.Floor((widthDip - 2 * _metrics.Margin + _metrics.ColumnGap) /
            (_metrics.LaneWidth + _metrics.ColumnGap) + 1e-8), 1, Math.Max(1, budget.MaxColumns)) : source.Columns;
        var rows = _resizeHeight ? Math.Clamp((int)Math.Floor((heightDip - _metrics.TopMargin - _metrics.Margin + _metrics.Gap) /
            _metrics.Pitch + 1e-8), 1, Math.Max(1, budget.MaxRows)) : source.Rows;
        var narrowingExpansion = source.Settings.ExpandColumns && _resizeWidth && width < _resizeStartRect.Width;
        if (columns != _resizeLastColumns || rows != _resizeLastRows || narrowingExpansion != _resizeLastNarrowing)
        {
            _resizeLastColumns = columns; _resizeLastRows = rows;
            _resizeLastNarrowing = narrowingExpansion;
            var requested = source.Settings with { FullScreen = false, AnchorCorner = _resizeAnchor };
            var result = CachedResizeFit(source, requested, columns, rows);
            if (narrowingExpansion)
            {
                var visible = new WallViewportBudget(widthDip, heightDip, columns, rows, false);
                result = WallViewportPolicy.TryCloseWindowedExpansion(source, visible, _metrics) ?? result;
            }
            _resizeProposal = result.Proposal; _resizeFailure = result.Proposal is null ? result : null;
            // During ordinary window resizing keep the minimum legal grid visible. Exiting
            // fullscreen must fit the user's requested target, and therefore never grows it.
            if (_resizeProposal is null && !source.Settings.FullScreen)
            {
                for (var c = columns; c <= Math.Min(budget.MaxColumns, source.Columns); c++)
                {
                    var found = false;
                    for (var r = rows; r <= Math.Min(budget.MaxRows, source.Rows); r++)
                    {
                        var candidate = CachedResizeFit(source, requested, c, r);
                        if (candidate.Proposal is null) continue;
                        _resizeProposal = candidate.Proposal; _resizeFailure = null; found = true; break;
                    }
                    if (found) break;
                }
            }
        }
        if (_resizeProposal is not { } proposal) return;
        if (!proposal.Document.Settings.ExpandColumns)
            width = Math.Max(width, Math.Ceiling((proposal.Geometry.WallWidth + fixedWidth) * _resizeScale) + _frameWidth);
        height = Math.Max(height, Math.Ceiling(proposal.Geometry.WallHeight * _resizeScale) + _frameHeight);
        // Within one complete-cell budget only the visible width changes. Keep the
        // cached layout geometry, but persist the final DIP width with this same proposal.
        var clientWidthDip = (width - _frameWidth) / _resizeScale;
        proposal = proposal with { Document = proposal.Document with { Settings = proposal.Document.Settings with
        {
            WindowedViewportWidthDip = clientWidthDip,
            ManualWindowedViewportWidthDip = _resizeWidth ? clientWidthDip : source.Settings.ManualWindowedViewportWidthDip,
        } } };
        _resizeProposal = proposal;
        var w = (int)Math.Ceiling(width); var h = (int)Math.Ceiling(height);
        var x = right ? _resizeStartRect.X + _resizeStartRect.Width - w : _resizeStartRect.X;
        var y = bottom ? _resizeStartRect.Y + _resizeStartRect.Height - h : _resizeStartRect.Y;
        if (_nativeSizingActive) _nativeSizingTargetRect = new RectInt32(x, y, w, h);
        if (preview)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_resizePreviewDocument is null || !ReferenceEquals(Surface.Geometry, proposal.Geometry) ||
                _resizePreviewDocument.Settings.ExpandColumns != proposal.Document.Settings.ExpandColumns)
            {
                Surface.Render(proposal.Document, proposal.Geometry); Surface.RefreshClocks(_clock.UtcNow);
                _resizePreviewDocument = proposal.Document;
            }
            PresentViewport(new RectInt32(x, y, w, h), false, _resizeAnchor, animate: source.Settings.FullScreen,
                presentationSettings: proposal.Document.Settings);
            _performance.View(System.Diagnostics.Stopwatch.GetTimestamp() - started);
        }
    }
    private WallLayoutResult CachedResizeFit(WallDocument source, WallSettings requested, int columns, int rows)
    {
        if (_resizeFits.TryGetValue((columns, rows), out var cached)) { _performance.CacheHit(); return cached; }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = ProposeMode(source, requested, columns, rows, _resizeAnchor);
        _performance.Proposal(System.Diagnostics.Stopwatch.GetTimestamp() - started);
        _resizeFits[(columns, rows)] = result;
        return result;
    }

    private void OnResizeCompleted(object sender, ResizeDragCompletedEventArgs e)
    {
        if (!_resizing || _nativeSizingActive) return;
        try
        {
            Point? finalPoint = null;
            if (!e.Canceled)
            {
                if (e.RootPoint is not { } rootPoint || !TryGetResizeScreenPoint(rootPoint, e.RasterizationScale, out var releasedAt))
                { CancelResize("ReleaseCoordinatesUnavailable"); return; }
                finalPoint = releasedAt;
            }
            CompleteResizeSession(e.Canceled, finalPoint);
        }
        catch (Exception ex) { CancelResize(); _performance.End("error", Surface.VisualCreationCount, Surface.GridCreationCount); Status(ex.Message); }
    }

    private void CompleteResizeSession(bool canceled, Point? finalPoint)
    {
        if (!_resizing) return;
        try
        {
            if (!canceled)
            {
                if (finalPoint is not { } releasedAt)
                { CancelResize("ReleaseCoordinatesUnavailable"); return; }
                UpdateResizeProposal(preview: false, releasedAt);
            }
            var endPoint = finalPoint is { } point ? $"{point.X:F2},{point.Y:F2}" : "none";
            TraceInput($"ResizeCompleted canceled={canceled} pressScreen={_resizeStart.X:F2},{_resizeStart.Y:F2} endScreen={endPoint} startRect={_resizeStartRect.X},{_resizeStartRect.Y},{_resizeStartRect.Width},{_resizeStartRect.Height}");
            var proposal = canceled ? null : _resizeProposal;
            var failure = _resizeFailure;
            CancelResize(canceled ? "ThumbCanceled" : "ThumbCompleted", restoreView: proposal is null);
            if (!canceled && failure is not null) Status(ViewportFailure(failure));
            if (proposal is null) return;
            if (proposal.Document.Columns == Document.Columns && proposal.Document.Rows == Document.Rows && proposal.Document.Settings == Settings)
            {
                Surface.Render(Document); ApplyViewport(animateMode: false);
                _performance.End("release-no-change", Surface.VisualCreationCount, Surface.GridCreationCount); return;
            }
            var originalOffset = _resizeScrollOffset;
            _ = CommitProposalAsync(proposal, settle: error =>
            {
                SettleReleasePersistence(error);
                if (error is not null && !_viewBlocked && !_windowClosing)
                {
                    WallScroll.UpdateLayout();
                    RestoreScrollPosition(originalOffset);
                }
            });
        }
        catch (Exception ex) { CancelResize(); _performance.End("error", Surface.VisualCreationCount, Surface.GridCreationCount); Status(ex.Message); }
    }

    private void CancelResize([CallerMemberName] string reason = "", bool restoreView = true)
    {
        if (!_resizing) return;
        if (_nativeSizingActive)
        {
            RequestNativeSizingCancel(reason);
            return; // Restore only after DefSubclassProc has left the system sizing loop.
        }
        _resizing = false;
        ShowResizeHighlight(null);
        _resizeInputPending = false;
        if (restoreView) StopViewportTransition();
        var thumb = _resizeThumb;
        _resizeThumb = null;
        _resizeSnapshot = null;
        _resizeProposal = null;
        _resizePreviewDocument = null;
        _resizeFits.Clear();
        _resizeFullResult = null;
        _resizeWindowedResult = null;
        if (thumb?.IsDragging == true) thumb.CancelDrag();
        TraceInput($"ResizeCancel reason={reason} events={_resizeDeltaCount} samples={_resizeSampleCount}");
        StopFrameSubscriptionIfIdle();
        if (restoreView) _performance.End("cancel", Surface.VisualCreationCount, Surface.GridCreationCount);
        if (restoreView && _hasDocument && !_viewBlocked)
        {
            Surface.Render(Document);
            Surface.RefreshClocks(_clock.UtcNow);
            ApplyViewport(animateMode: false);
            WallScroll.UpdateLayout();
            RestoreScrollPosition(_resizeScrollOffset);
        }
        UpdatePuzzleGate();
        ResumeEnvironmentAdaptation();
    }

    private bool TryGetResizeScreenPoint(Point rootPoint, double scale, out Point screenPoint)
    {
        screenPoint = default;
        if (!double.IsFinite(scale) || scale <= 0 || !double.IsFinite(rootPoint.X) || !double.IsFinite(rootPoint.Y)) return false;
        var origin = new CursorPoint();
        if (!ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(this), ref origin)) return false;
        // XAML root points are DIPs; client origins and native samples are device pixels.
        screenPoint = new Point(origin.X + Math.Round(rootPoint.X * scale), origin.Y + Math.Round(rootPoint.Y * scale));
        return true;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref CursorPoint point);
}


