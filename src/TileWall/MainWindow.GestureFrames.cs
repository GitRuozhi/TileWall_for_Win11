using System.Diagnostics;
using Microsoft.UI.Xaml.Media;
using TileWall.ApplicationModel;
using TileWall.Core.Wall;
using Windows.Foundation;

namespace TileWall;

public sealed partial class MainWindow
{
    private bool _frameSubscribed, _dragInputPending, _resizeInputPending;
    private Point _latestDragPoint, _latestResizePoint;
    private double _gestureScale;
    private WallDocument? _gestureDocument;
    private WallGeometry? _gestureGeometry;
    private Dictionary<string, WallMember> _gestureMembers = new(StringComparer.Ordinal);
    private Dictionary<string, StartGroup> _gestureGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<(WallAction Action, bool Prepend), WallLayoutResult> _dropCache = [];
    private WallLayoutResult? _drawnDrop;
    private readonly GesturePerformance _performance = new();

    private void BeginGestureFrames(WallGeometry geometry)
    {
        _gestureDocument = Document;
        _gestureGeometry = geometry;
        _gestureScale = Scale;
        _gestureGroups = Document.Lanes.SelectMany(l => l.Groups).ToDictionary(g => g.Id, StringComparer.Ordinal);
        _gestureMembers = _gestureGroups.Values.SelectMany(g => g.Members).ToDictionary(m => m.Id, StringComparer.Ordinal);
        _dropCache.Clear(); _drawnDrop = null; _dragInputPending = false;
        _performance.Begin("drag", Document, Scale, Surface.VisualCreationCount, Surface.GridCreationCount);
        RecordCompositionGateAtBegin();
        EnsureFrameSubscription();
    }

    private void EnsureFrameSubscription()
    {
        if (_frameSubscribed || _windowClosing) return;
        _frameSubscribed = true;
        BeginInteractionTimer();
        if (TryStartDisplayPulses()) return;
        CompositionTarget.Rendering += OnInteractionFrame;
    }

    private void StopFrameSubscriptionIfIdle()
    {
        // Keep the display callback alive between input samples. Re-subscribing for each
        // PointerMoved repeatedly incurs first-frame scheduling instead of a steady stream.
        if (!_frameSubscribed || !_windowClosing && (_gesture.Phase != GesturePhase.Idle ||
            _dragInputPending || _resizing || _viewportTransition is not null)) return;
        _frameSubscribed = false;
        if (_displayPulseActive)
        {
            _displayPulseSource?.Stop();
            _displayPulseActive = false;
        }
        else CompositionTarget.Rendering -= OnInteractionFrame;
        InvalidateInteractionCommit();
        EndInteractionTimer();
    }

    private void OnInteractionFrame(object? sender, object e)
    {
        if (_processingFrame) return;
        var started = Stopwatch.GetTimestamp();
        var displayPulse = _displayPulseActive;
        var didWork = _viewportTransition is not null;
        _processingFrame = true;
        try
        {
            AdvanceViewportTransition();
            if (_resizing)
            {
                // Native sampling is the fallback for mouse paths omitting DragDelta. Both
                // inputs write this same slot and there is only one consumer per display frame.
                if (!_nativeSizingActive && GetCursorPos(out var cursor)) QueueResizeSample(new Point(cursor.X, cursor.Y));
                if (_resizeInputPending)
                {
                    didWork = true;
                    _resizeInputPending = false;
                    UpdateResizeProposal(preview: true, _latestResizePoint);
                }
            }
            if (_dragInputPending)
            {
                didWork = true;
                _dragInputPending = false;
                ConsumeDragSample(_latestDragPoint);
            }
        }
        catch (Exception ex)
        {
            if (_resizing) { CancelResize(); Status(ex.Message); }
            else RecoverGestureError(ex);
        }
        finally
        {
            _processingFrame = false;
            if (_pendingWindowRect is { } rectangle)
            {
                didWork = true;
                _pendingWindowRect = null;
                MoveWindowIfChanged(rectangle, continuousResizeFrame: true);
            }
            if (didWork && _displayPulseActive) RequestInteractionCommit();
            _performance.Frame(started, didWork, displayPulse);
            StopFrameSubscriptionIfIdle();
        }
    }

    private void QueueResizeSample(Point point)
    {
        if (point == _resizeLastSample) return;
        _resizeLastSample = point;
        _latestResizePoint = point;
        _resizeInputPending = true;
        _resizeSampleCount++;
        _performance.Input();
        EnsureFrameSubscription();
    }

    private bool GestureSnapshotIsCurrent() => _gestureDocument is not null &&
        _gestureDocument.Generation == Document.Generation && _gestureScale == Scale;

    private void ConsumeDragSample(Point point)
    {
        if (!GestureSnapshotIsCurrent()) { TerminateGesture(); return; }
        if (!_gesture.Advance(point, 7)) return;
        var proposal = CalculateDrop(point);
        _gesture.SetPreview(proposal);
        if (!ReferenceEquals(_drawnDrop, proposal))
        {
            var start = Stopwatch.GetTimestamp();
            var preview = proposal?.Proposal;
            Surface.Render(preview?.Document ?? Document, preview?.Geometry ?? _gestureGeometry);
            Surface.RefreshClocks(_clock.UtcNow);
            ApplyViewport(preview?.Document ?? Document, preview?.Geometry ?? _gestureGeometry, animateMode: false);
            Surface.ShowDragCue(_gesture.MemberId, preview is not null, _gesture.GroupId);
            _drawnDrop = proposal;
            _performance.View(Stopwatch.GetTimestamp() - start);
        }
        var origin = AppWindow.Position;
        var originX = _pendingWindowRect?.X ?? origin.X;
        var originY = _pendingWindowRect?.Y ?? origin.Y;
        DrawGhost(new Point(point.X - (originX - _gesture.WindowOrigin.X) / Scale,
            point.Y - (originY - _gesture.WindowOrigin.Y) / Scale), proposal?.Proposal is not null);
        _performance.Ghost();
    }

    private WallLayoutResult CachedDrop(WallAction action, WallDocument? expanded = null)
    {
        var key = (action, expanded is not null);
        if (_dropCache.TryGetValue(key, out var result)) { _performance.CacheHit(); return result; }
        var start = Stopwatch.GetTimestamp();
        result = WallLayoutEngine.TryApply(expanded ?? _gestureDocument ?? Document, action,
            WorkWidthDip, WorkHeightDip, _metrics);
        _performance.Proposal(Stopwatch.GetTimestamp() - start);
        _dropCache.Add(key, result);
        return result;
    }
}
