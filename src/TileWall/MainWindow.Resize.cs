using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using TileWall.Core.Wall;
using TileWall.Resources;
using TileWall.Shell.Interop;
using Windows.Foundation;
using Windows.Graphics;

namespace TileWall;

public sealed partial class MainWindow
{
    private Point _resizeStart;
    private RectInt32 _resizeStartRect;
    private WallDocument? _resizeSnapshot;
    private WallProposal? _resizeProposal;
    private Thumb? _resizeThumb;
    private bool _resizeWidth, _resizeHeight;
    private double _resizeScale;
    private int _resizeDeltaCount;
    private int _resizeLastColumns = -1, _resizeLastRows = -1;
    private WallDocument? _resizePreviewDocument;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _resizeTimer;
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
        var visibility = _modal || _viewBlocked || _adaptationBlocked ? Visibility.Collapsed : Visibility.Visible;
        ResizeGrip.Visibility = ResizeVerticalEdge.Visibility = ResizeHorizontalEdge.Visibility = visibility;
    }

    private void OnResizeStarted(object sender, DragStartedEventArgs e)
    {
        if (_modal || _viewBlocked || _adaptationBlocked || !_hasDocument || sender is not Thumb thumb) return;
        if (!GetCursorPos(out var cursor)) return;
        TerminateGesture();
        _resizing = true;
        _resizeThumb = thumb;
        _resizeWidth = !ReferenceEquals(thumb, ResizeHorizontalEdge);
        _resizeHeight = !ReferenceEquals(thumb, ResizeVerticalEdge);
        _resizeSnapshot = Document;
        _resizeScale = Scale;
        _resizeStart = new Point(cursor.X, cursor.Y);
        _resizeStartRect = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        _resizeProposal = null;
        _resizePreviewDocument = null;
        _resizeLastColumns = _resizeLastRows = -1;
        _resizeDeltaCount = 0;
        _resizeSampleCount = 0;
        _resizeLastSample = _resizeStart;
        if (_resizeTimer is null)
        {
            _resizeTimer = DispatcherQueue.CreateTimer();
            _resizeTimer.Interval = TimeSpan.FromMilliseconds(16);
            _resizeTimer.IsRepeating = true;
            _resizeTimer.Tick += (_, _) => SampleResizePointer();
        }
        // Thumb capture remains the gesture owner, but some WinUI mouse paths omit DragDelta.
        // Sample the native cursor while that gesture is alive so the viewport still follows it.
        _resizeTimer.Start();
        TraceInput($"ResizeStarted anchor={Settings.AnchorCorner} width={_resizeWidth} height={_resizeHeight} grid={Document.Columns}x{Document.Rows}");
        UpdatePuzzleGate();
    }

    private void SampleResizePointer()
    {
        if (!_resizing || !GetCursorPos(out var cursor)) return;
        var sample = new Point(cursor.X, cursor.Y);
        if (sample == _resizeLastSample) return;
        _resizeLastSample = sample;
        _resizeSampleCount++;
        try { UpdateResizeProposal(preview: true); }
        catch (Exception ex) { CancelResize(); Status(ex.Message); }
    }

    private void OnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_resizing) return;
        _resizeDeltaCount++;
        try { UpdateResizeProposal(preview: true); }
        catch (Exception ex) { CancelResize(); Status(ex.Message); }
    }

    private void UpdateResizeProposal(bool preview)
    {
        if (_resizeSnapshot is not { } source || !GetCursorPos(out var cursor))
        {
            _resizeProposal = null;
            return;
        }
        if (source.Generation != Document.Generation || _resizeScale != Scale)
        {
            CancelResize();
            return;
        }
        var right = source.Settings.AnchorCorner is AnchorCorner.BottomRight or AnchorCorner.TopRight;
        var bottom = source.Settings.AnchorCorner is AnchorCorner.BottomLeft or AnchorCorner.BottomRight;
        var dx = (cursor.X - _resizeStart.X) * (right ? -1 : 1);
        var dy = (cursor.Y - _resizeStart.Y) * (bottom ? -1 : 1);
        var width = Math.Clamp(_resizeStartRect.Width + (_resizeWidth ? dx : 0),
            Math.Ceiling(_metrics.WallWidth(1) * _resizeScale) + _frameWidth, _workArea.Right - _workArea.Left);
        var height = Math.Clamp(_resizeStartRect.Height + (_resizeHeight ? dy : 0),
            Math.Ceiling(_metrics.WallHeight(1) * _resizeScale) + _frameHeight, _workArea.Bottom - _workArea.Top);
        var widthDip = (width - _frameWidth) / _resizeScale;
        var heightDip = (height - _frameHeight) / _resizeScale;
        var columns = _resizeWidth ? Math.Clamp((int)Math.Floor((widthDip - 2 * _metrics.Margin + _metrics.ColumnGap) /
            (_metrics.LaneWidth + _metrics.ColumnGap) + 1e-8), 1, 64) : source.Columns;
        var rows = _resizeHeight ? Math.Clamp((int)Math.Floor((heightDip - _metrics.TopMargin - _metrics.Margin + _metrics.Gap) /
            _metrics.Pitch + 1e-8), 1, 256) : source.Rows;
        if (columns != _resizeLastColumns || rows != _resizeLastRows)
        {
            _resizeLastColumns = columns; _resizeLastRows = rows;
            var result = ProposeResize(source, columns, rows);
            _resizeProposal = result.Proposal;
            if (_resizeProposal is null)
            {
                // Empty-lane removal never merges nonempty lanes, so the legal width and
                // height bounds are independent. Search each bound without an O(C*R) sweep.
                // Every proposal uses the gesture's formal snapshot, never the last preview.
                var maxColumns = Math.Max(columns, source.Columns);
                var maxRows = Math.Max(rows, source.Rows);
                for (var c = columns; c <= maxColumns; c++)
                {
                    if (ProposeResize(source, c, maxRows).Proposal is null) continue;
                    for (var r = rows; r <= maxRows; r++)
                    {
                        var candidate = ProposeResize(source, c, r).Proposal;
                        if (candidate is null) continue;
                        _resizeProposal = candidate;
                        break;
                    }
                    break;
                }
            }
        }
        if (_resizeProposal is not { } proposal) return;
        // The native viewport follows each pointer movement, even between grid thresholds.
        // At the minimum legal content capacity, prevent clipping by clamping only that axis.
        width = Math.Max(width, Math.Ceiling(proposal.Geometry.WallWidth * _resizeScale) + _frameWidth);
        height = Math.Max(height, Math.Ceiling(proposal.Geometry.WallHeight * _resizeScale) + _frameHeight);
        if (preview)
        {
            if (!ReferenceEquals(_resizePreviewDocument, proposal.Document))
            {
                Surface.Render(proposal.Document);
                Surface.RefreshClocks(_clock.UtcNow);
                _resizePreviewDocument = proposal.Document;
            }
            var w = (int)Math.Ceiling(width); var h = (int)Math.Ceiling(height);
            var x = right ? _resizeStartRect.X + _resizeStartRect.Width - w : _resizeStartRect.X;
            var y = bottom ? _resizeStartRect.Y + _resizeStartRect.Height - h : _resizeStartRect.Y;
            AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        }
        TraceInput($"ResizeProposal preview={preview} grid={proposal.Document.Columns}x{proposal.Document.Rows} native={width:F1}x{height:F1}");
    }

    private WallLayoutResult ProposeResize(WallDocument source, int columns, int rows) =>
        WallLayoutEngine.TryApply(source, new ResizeWallAction(source.Generation, columns, rows), WorkWidthDip, WorkHeightDip, _metrics);

    private void OnResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_resizing) return;
        try
        {
            if (!e.Canceled) UpdateResizeProposal(preview: false);
            var proposal = e.Canceled ? null : _resizeProposal;
            CancelResize(e.Canceled ? "ThumbCanceled" : "ThumbCompleted");
            if (proposal is null || proposal.Document.Columns == Document.Columns && proposal.Document.Rows == Document.Rows) return;
            var error = CommitProposal(proposal);
            if (error is not null) Status(error);
        }
        catch (Exception ex) { CancelResize(); Status(ex.Message); }
    }

    private void CancelResize([CallerMemberName] string reason = "")
    {
        if (!_resizing) return;
        _resizing = false;
        _resizeTimer?.Stop();
        var thumb = _resizeThumb;
        _resizeThumb = null;
        _resizeSnapshot = null;
        _resizeProposal = null;
        _resizePreviewDocument = null;
        if (thumb?.IsDragging == true) thumb.CancelDrag();
        TraceInput($"ResizeCancel reason={reason} events={_resizeDeltaCount} samples={_resizeSampleCount}");
        if (_hasDocument && !_viewBlocked)
        {
            Surface.Render(Document);
            Surface.RefreshClocks(_clock.UtcNow);
            ApplyViewport();
        }
        UpdatePuzzleGate();
    }
}
