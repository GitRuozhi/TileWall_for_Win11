using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TileWall.ApplicationModel;
using TileWall.Core.Wall;
using TileWall.Resources;
using TileWall.Shell.Interop;
using Windows.Foundation;

namespace TileWall;

public sealed partial class MainWindow
{
    private sealed class DeferredGesturePress
    {
        internal uint PointerId;
        internal string? MemberId, GroupId;
        internal Point ScreenStart, LatestScreen, FurthestScreen, GrabOffset;
        internal Point ContentStart;
        internal ulong PressTimestamp;
        internal double Scale;
        internal Action ReleaseCapture = static () => { };
    }

    private DeferredGesturePress? _deferredGesturePress;

    // Only a real mouse press during a background transaction can reserve this path.
    // Foreground saves and closed admission retain their existing interaction gate.
    private bool TryDeferGesturePress(PointerRoutedEventArgs e)
    {
        if (_deferredGesturePress is not null) return true;
        if (!_persistencePending || !_backgroundPersistencePending || _userMutationReservations != 0 ||
            _persistenceAdmissionClosed || _openingWall || _modal || _viewBlocked || !_hasDocument ||
            _windowClosing || ShutdownStarted || _resizing || _gesture.Phase != GesturePhase.Idle ||
            e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse ||
            !e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed || !GetCursorPos(out var cursor)) return false;
        if (_viewportTransition is { AutomaticHeight: true }) StopViewportTransition();
        var pressed = e.GetCurrentPoint(Surface);
        var point = pressed.Position;
        var hit = Surface.Hit(point);
        if (hit.MemberId is null && !hit.GroupHandle || Settings.LayoutLocked && hit.GroupHandle ||
            Surface.Geometry is not { } geometry) return false;
        var bounds = hit.MemberId is { } memberId
            ? geometry.Groups.SelectMany(group => group.Members).First(member => member.MemberId == memberId).Bounds
            : geometry.Groups.First(group => group.GroupId == hit.GroupId).Bounds;
        var pointer = e.Pointer;
        var press = new DeferredGesturePress
        {
            PointerId = pointer.PointerId, MemberId = hit.MemberId, GroupId = hit.GroupHandle ? hit.GroupId : null,
            ScreenStart = new(cursor.X, cursor.Y), LatestScreen = new(cursor.X, cursor.Y), FurthestScreen = new(cursor.X, cursor.Y),
            ContentStart = point, PressTimestamp = pressed.Timestamp,
            GrabOffset = new(point.X - bounds.X, point.Y - bounds.Y), Scale = Scale,
            ReleaseCapture = () => Surface.ReleasePointerCapture(pointer),
        };
        _deferredGesturePress = press;
        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, Surface);
            node = VisualTreeHelper.GetParent(node))
        {
            if (node is not Button button) continue;
            button.ReleasePointerCapture(pointer);
            break;
        }
        if (!Surface.CapturePointer(pointer))
        {
            _deferredGesturePress = null;
            return false;
        }
        e.Handled = true;
        Status(ProductStrings.Get("Wall.SettingsSaving"));
        _ = AwaitDeferredGesturePressAsync(press);
        return true;
    }

    private async Task AwaitDeferredGesturePressAsync(DeferredGesturePress press)
    {
        try
        {
            await QueueUserMutationAsync(() =>
            {
                try
                {
                    if (!ReferenceEquals(_deferredGesturePress, press) || _modal || _resizing || !_hasDocument ||
                        _gesture.Phase != GesturePhase.Idle || Scale != press.Scale || !AppWindow.IsVisible ||
                        (Win32Api.GetAsyncKeyState(1) & 0x8000) == 0 || !GetCursorPos(out var cursor)) return Task.CompletedTask;
                    ObserveDeferredGesturePoint(press, new(cursor.X, cursor.Y));
                    var geometry = Surface.Geometry ?? WallLayoutEngine.Measure(Document, _metrics);
                    var memberBounds = press.MemberId is { } id
                        ? geometry.Groups.SelectMany(group => group.Members).FirstOrDefault(member => member.MemberId == id)?.Bounds
                        : geometry.Groups.FirstOrDefault(group => group.GroupId == press.GroupId)?.Bounds;
                    if (memberBounds is not { } bounds || press.GroupId is not null && Settings.LayoutLocked)
                        return Task.CompletedTask;
                    _puzzles.SetGate(MemberCarouselGate.Gesture);
                    StopViewportTransition();
                    var point = new Point(bounds.X + press.GrabOffset.X, bounds.Y + press.GrabOffset.Y);
                    var origin = AppWindow.Position;
                    var ghost = Surface.CreateDragVisual(press.MemberId, press.GroupId);
                    _gestureScrollOffset = new(WallScroll.HorizontalOffset, WallScroll.VerticalOffset);
                    if (!_gesture.Begin(press.PointerId, point, press.GrabOffset, new(origin.X, origin.Y), ghost, press.MemberId, press.GroupId))
                    {
                        press.ReleaseCapture();
                        UpdatePuzzleGate();
                        return Task.CompletedTask;
                    }
                    _deferredGesturePress = null; // Capture now belongs to the admitted gesture.
                    _gesturePressTimestamp = press.PressTimestamp;
                    _gestureThresholdDip = 3; // Deferred admission accepts mouse presses only.
                    _gestureScreenStart = press.ScreenStart;
                    _gesturePointerScrollStart = _gestureScrollOffset;
                    _gestureMarginStart = new(WallScroll.Margin.Left, WallScroll.Margin.Top);
                    BeginGestureFrames(geometry);
                    // Preserve a threshold crossing observed while waiting, including a return to the press point.
                    _gesture.Advance(new(point.X + (press.FurthestScreen.X - press.ScreenStart.X) / press.Scale,
                        point.Y + (press.FurthestScreen.Y - press.ScreenStart.Y) / press.Scale), 3);
                    _latestDragPoint = new(point.X + (press.LatestScreen.X - press.ScreenStart.X) / press.Scale,
                        point.Y + (press.LatestScreen.Y - press.ScreenStart.Y) / press.Scale);
                    _dragInputPending = true;
                    UpdatePuzzleGate();
                    QueueDragFrameWake();
                    return Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    _deferredGesturePress = null;
                    press.ReleaseCapture();
                    RecoverGestureError(ex);
                    return Task.CompletedTask;
                }
            });
        }
        finally
        {
            if (ReferenceEquals(_deferredGesturePress, press))
            {
                CancelDeferredGesturePress(press.PointerId);
                UpdatePuzzleGate();
            }
        }
    }

    private bool UpdateDeferredGesturePress(PointerRoutedEventArgs e)
    {
        if (_deferredGesturePress is not { } press || press.PointerId != e.Pointer.PointerId) return false;
        ObserveDeferredGestureHistory(press, e);
        if (!e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed) CancelDeferredGesturePress(press.PointerId);
        else if (GetCursorPos(out var cursor)) press.LatestScreen = new(cursor.X, cursor.Y);
        e.Handled = true;
        return true;
    }

    private static void ObserveDeferredGesturePoint(DeferredGesturePress press, Point point)
    {
        press.LatestScreen = point;
        static double Distance(Point left, Point right) => Math.Abs(left.X - right.X) + Math.Abs(left.Y - right.Y);
        if (Distance(point, press.ScreenStart) > Distance(press.FurthestScreen, press.ScreenStart)) press.FurthestScreen = point;
    }

    private void ObserveDeferredGestureHistory(DeferredGesturePress press, PointerRoutedEventArgs e)
    {
        foreach (var sample in e.GetIntermediatePoints(Surface))
        {
            if (sample.Timestamp < press.PressTimestamp) continue;
            ObserveDeferredGesturePoint(press, new Point(
                press.ScreenStart.X + (sample.Position.X - press.ContentStart.X) * press.Scale,
                press.ScreenStart.Y + (sample.Position.Y - press.ContentStart.Y) * press.Scale));
        }
    }

    private bool ReleaseDeferredGesturePress(PointerRoutedEventArgs e)
    {
        if (_deferredGesturePress is not { } press || press.PointerId != e.Pointer.PointerId) return false;
        ObserveDeferredGestureHistory(press, e);
        var wasClick = Math.Abs(press.FurthestScreen.X - press.ScreenStart.X) +
            Math.Abs(press.FurthestScreen.Y - press.ScreenStart.Y) < 3 * press.Scale;
        if (!CancelDeferredGesturePress(e.Pointer.PointerId)) return false;
        e.Handled = true;
        if (wasClick && press.MemberId is { } memberId && FindMember(memberId) is not null)
        {
            _suppressClickMember = null;
            try { LaunchMember(memberId); }
            finally
            {
                _suppressClickMember = memberId;
                DispatcherQueue.TryEnqueue(() => _suppressClickMember = null);
            }
        }
        return true;
    }

    private bool CancelDeferredGesturePress(uint? pointerId = null)
    {
        if (_deferredGesturePress is not { } press || pointerId is { } id && press.PointerId != id) return false;
        _deferredGesturePress = null;
        _suppressClickMember = press.MemberId;
        DispatcherQueue.TryEnqueue(() => _suppressClickMember = null);
        press.ReleaseCapture();
        return true;
    }
}
