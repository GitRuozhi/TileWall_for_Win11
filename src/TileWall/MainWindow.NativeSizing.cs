using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using TileWall.Core.Wall;
using TileWall.Shell.Interop;
using TileWall.Views;
using Windows.Foundation;
using Windows.Graphics;

namespace TileWall;

public sealed partial class MainWindow
{
    // Opt-in, mouse-message prototype. The default build9 pointer path is unchanged.
    private readonly bool _nativeSizingEnabled = Environment.GetEnvironmentVariable("TILEWALL_NATIVE_SIZING") == "1";
    private Win32Api.SubclassProc? _nativeSizingCallback;
    private InputNonClientPointerSource? _nativeSizingPointerSource;
    private RectInt32? _nativeSizingRegionRect;
    private bool _nativeSizingRegionUpdating;
    private bool _nativeSizingOfficialObserving;
    private int _nativeSizingOfficialChangingCount, _nativeSizingWmSizingCount;
    private NativeSizingGetMessageProc? _nativeSizingGetMessageCallback;
    private IntPtr _nativeSizingHwnd, _nativeSizingMessageHook;
    private bool _nativeSizingActive, _nativeSizingLoopEntered, _nativeSizingLoopExited;
    private bool _nativeSizingCancelRequested;
    private string? _nativeSizingCancelReason;
    private Point? _nativeSizingReleasePoint;
    private Point _nativeSizingMessagePoint;
    private long _nativeSizingInputTimestamp;
    private RectInt32 _nativeSizingTargetRect;
    private ResizeThumb? _nativeSizingHover;
    private bool _nativeSizingTrackingLeave;
    private NativeSizingMeasurements? _nativeSizingMeasurements;
    private const nuint NativeSizingSubclassId = 2;

    private void InitializeNativeSizingPrototype()
    {
        if (!_nativeSizingEnabled) return;
        _nativeSizingHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        try
        {
            // Use the supported WinUI input partition before installing the parent
            // observer; the failed child HTTRANSPARENT experiment is removed entirely.
            _nativeSizingPointerSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            _nativeSizingPointerSource.EnteringMoveSize += NativeSizingOfficialEntering;
            _nativeSizingPointerSource.WindowRectChanging += NativeSizingOfficialChanging;
            _nativeSizingPointerSource.ExitedMoveSize += NativeSizingOfficialExited;
        }
        catch (Exception ex)
        {
            DisposeNativeSizingRegions();
            TraceInput($"NativeSizing official-source-failed hresult=0x{ex.HResult:X8}");
            return;
        }
        _nativeSizingCallback = NativeSizingWindowMessage;
        if (!Win32Api.SetWindowSubclass(_nativeSizingHwnd, _nativeSizingCallback, NativeSizingSubclassId, 0))
        {
            _nativeSizingCallback = null;
            DisposeNativeSizingRegions();
            TraceInput($"NativeSizing install-failed error={Marshal.GetLastPInvokeError()}");
            return;
        }
        WallRoot.Loaded += NativeSizingRootLoaded;
        // Enable the system sizing loop, while WM_NCCALCSIZE keeps the entire HWND
        // client-only. RemoveNativeFrame already suppresses the independent DWM border.
        var style = Win32Api.GetWindowLongPtrW(_nativeSizingHwnd, -16).ToInt64();
        Win32Api.SetWindowLongPtrW(_nativeSizingHwnd, -16, new IntPtr(style | 0x00040000L));
        Win32Api.SetWindowPos(_nativeSizingHwnd, IntPtr.Zero, 0, 0, 0, 0,
            0x0020 | 0x0001 | 0x0002 | 0x0004 | 0x0010);
        UpdateNativeSizingRegion();
        TraceInput("NativeSizing prototype-enabled backend=system-sizing-loop routing=official-left-border-only scope=BR-windowed input=thread-message-screen-point");
    }

    private void DisposeNativeSizingPrototype()
    {
        _nativeSizingCancelRequested = true;
        _nativeSizingCancelReason = "WindowClosed";
        WallRoot.Loaded -= NativeSizingRootLoaded;
        DisposeNativeSizingRegions();
        RemoveNativeSizingMessageHook();
        if (_nativeSizingCallback is { } callback)
            Win32Api.RemoveWindowSubclass(_nativeSizingHwnd, callback, NativeSizingSubclassId);
        _nativeSizingCallback = null;
        _nativeSizingActive = false;
    }

    private IntPtr NativeSizingWindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        var measured = _nativeSizingActive && message is 0x0214 or 0x0047 or 0x0005 or 0x0003;
        var started = measured ? Stopwatch.GetTimestamp() : 0;
        var forwarded = false;
        var forwardedResult = IntPtr.Zero;
        try
        {
            if (message == 0x0083) return IntPtr.Zero; // WM_NCCALCSIZE: no standard frame/caption.
            if (message == 0x0082) DisposeNativeSizingPrototype(); // WM_NCDESTROY safety net.
            if (message is 0x0231 or 0x0214 or 0x0232)
            {
                if (message == 0x0231) _nativeSizingWmSizingCount = 0;
                _nativeSizingMeasurements?.NativeLifecycle(message);
                if (message != 0x0214 || ++_nativeSizingWmSizingCount == 1)
                {
                    try { TraceInput($"NativeSizing native-message=0x{message:X4} active={_nativeSizingActive}"); }
                    catch { /* Lifecycle trace cannot affect native sizing. */ }
                }
            }
            if (message == 0x02A2 || message == 0x0006 && (wParam.ToInt64() & 0xFFFF) == 0)
            {
                _nativeSizingTrackingLeave = false; // WM_NCMOUSELEAVE / inactive WM_ACTIVATE.
                _nativeSizingHover = null;
                if (!_windowClosing && (!_nativeSizingActive || message == 0x0006)) ShowResizeHighlight(null);
            }
            if (message == 0x0084) // WM_NCHITTEST; do not expose any additional system resize borders.
            {
                var zone = NativeSizingZoneAt(PackedNativeScreenPoint(lParam));
                if (!_nativeSizingActive && !ReferenceEquals(zone, _nativeSizingHover))
                {
                    _nativeSizingHover = zone;
                    ShowResizeHighlight(zone);
                }
                if (zone is not null && !_nativeSizingTrackingLeave)
                {
                    var tracking = new NativeSizingMouseTracking { Size = (uint)Marshal.SizeOf<NativeSizingMouseTracking>(),
                        Flags = 0x0012, Hwnd = hwnd }; // TME_LEAVE | TME_NONCLIENT
                    _nativeSizingTrackingLeave = TrackMouseEvent(ref tracking);
                    if (!_nativeSizingTrackingLeave && !_nativeSizingActive)
                    {
                        _nativeSizingHover = null;
                        ShowResizeHighlight(null); // No leave notification means no persistent hover cue.
                    }
                }
                return new IntPtr(zone is null ? 1 : NativeSizingHitFor(zone)); // HTCLIENT otherwise.
            }
            if (message == 0x00A1 && !_nativeSizingActive) // WM_NCLBUTTONDOWN: lParam is the event screen point.
            {
                var pressedAt = PackedNativeScreenPoint(lParam);
                if (NativeSizingZoneAt(pressedAt) is { } zone)
                {
                    forwarded = true; // Run owns forwarding; its finally can run after the native loop returns.
                    forwardedResult = RunNativeSizingLoop(hwnd, wParam, lParam, zone, pressedAt);
                    return forwardedResult;
                }
            }
            if (_nativeSizingActive)
            {
                if (message == 0x0231) _nativeSizingLoopEntered = true; // WM_ENTERSIZEMOVE
                if (message == 0x0232) _nativeSizingLoopExited = true; // WM_EXITSIZEMOVE
                if (message == 0x001F) // WM_CANCELMODE
                {
                    _nativeSizingCancelRequested = true;
                    _nativeSizingCancelReason ??= "NativeCancelMode";
                }
                if (message == 0x0215 && _nativeSizingLoopEntered && lParam != hwnd && _nativeSizingReleasePoint is null)
                {
                    _nativeSizingCancelRequested = true; // Unexpected WM_CAPTURECHANGED, not ordinary release.
                    _nativeSizingCancelReason ??= "NativeCaptureLost";
                }
                if (message == 0x0214) // WM_SIZING: the system, not AppWindow, submits this RECT.
                {
                    if (!_nativeSizingCancelRequested && _resizing)
                    {
                        // Legal layout/minimum dimensions must be decided before returning
                        // this RECT; only visual preview work waits for the latest frame slot.
                        UpdateResizeProposal(preview: false, _nativeSizingMessagePoint);
                        if (_resizing && !_nativeSizingCancelRequested)
                        {
                            var target = _nativeSizingTargetRect;
                            var rectangle = new Win32Api.NativeRect { Left = target.X, Top = target.Y,
                                Right = target.X + target.Width, Bottom = target.Y + target.Height };
                            Marshal.StructureToPtr(rectangle, lParam, false);
                            QueueResizeSample(_nativeSizingMessagePoint);
                        }
                    }
                    return new IntPtr(1);
                }
            }
            forwarded = true;
            forwardedResult = Win32Api.DefSubclassProc(hwnd, message, wParam, lParam);
            if (message is 0x0005 or 0x02E0) UpdateNativeSizingRegion(); // WM_SIZE / WM_DPICHANGED.
            if (_nativeSizingActive && message == 0x0047 && GetWindowRect(hwnd, out var actual))
                _nativeSizingMeasurements?.Applied(_nativeSizingInputTimestamp,
                    new RectInt32(actual.Left, actual.Top, actual.Right - actual.Left, actual.Bottom - actual.Top));
            return forwardedResult;
        }
        catch (Exception ex)
        {
            // A managed exception must never unwind across a native callback boundary.
            try
            {
                RequestNativeSizingCancel($"NativeCallback:{ex.GetType().Name}:0x{ex.HResult:X8}");
                TraceInput($"NativeSizing callback-failed message=0x{message:X4} type={ex.GetType().Name} hresult=0x{ex.HResult:X8}");
            }
            catch { /* Callback recovery/diagnostics cannot unwind through native code. */ }
            // Never re-enter DefSubclassProc after it may already have run, including
            // a complete modal loop whose managed cleanup failed before returning.
            return forwarded ? forwardedResult : IntPtr.Zero;
        }
        finally
        {
            try { if (started != 0) _nativeSizingMeasurements?.Callback(message, started); }
            catch { /* Optional measurements cannot escape the native callback's finally. */ }
        }
    }

    private void NativeSizingRootLoaded(object sender, RoutedEventArgs e) => UpdateNativeSizingRegion();

    private void UpdateNativeSizingRegion()
    {
        if (!_nativeSizingEnabled || _nativeSizingPointerSource is not { } source || _nativeSizingRegionUpdating) return;
        _nativeSizingRegionUpdating = true;
        try
        {
            RectInt32? desired = null;
            if (!PersistenceInteractionBlocked && !_windowClosing && !_modal && !_viewBlocked && _hasDocument && !Settings.FullScreen &&
                Settings.AnchorCorner == AnchorCorner.BottomRight && Win32Api.GetClientRect(_nativeSizingHwnd, out var client))
            {
                var corner = (int)Math.Ceiling(14 * Scale);
                var width = Math.Min(client.Right - client.Left, (int)Math.Ceiling(8 * Scale));
                var height = client.Bottom - client.Top - 2 * corner;
                if (width > 0 && height > 0) desired = new RectInt32(0, corner, width, height);
            }
            if (desired is { } region)
            {
                if (_nativeSizingRegionRect is { } previous && previous.Equals(region)) return;
                source.SetRegionRects(NonClientRegionKind.LeftBorder, [region]);
                _nativeSizingRegionRect = region;
                var readBack = source.GetRegionRects(NonClientRegionKind.LeftBorder);
                var readBackText = string.Join(";", readBack.Select(r => $"{r.X},{r.Y},{r.Width},{r.Height}"));
                TraceInput($"NativeSizing official-region kind=LeftBorder rect={region.X},{region.Y},{region.Width},{region.Height} readBack={readBackText}");
            }
            else if (_nativeSizingRegionRect is not null)
            {
                source.ClearRegionRects(NonClientRegionKind.LeftBorder);
                _nativeSizingRegionRect = null;
                TraceInput("NativeSizing official-region-cleared kind=LeftBorder");
            }
        }
        catch (Exception ex)
        {
            try { TraceInput($"NativeSizing official-region-failed hresult=0x{ex.HResult:X8}"); }
            catch { /* Diagnostic failure cannot escape a native callback or modal setup. */ }
        }
        finally { _nativeSizingRegionUpdating = false; }
    }

    private void DisposeNativeSizingRegions()
    {
        var source = _nativeSizingPointerSource;
        _nativeSizingPointerSource = null;
        if (source is null) return;
        try { if (_nativeSizingRegionRect is not null) source.ClearRegionRects(NonClientRegionKind.LeftBorder); }
        catch { /* Window teardown may already have closed the source. */ }
        _nativeSizingRegionRect = null;
        try { source.EnteringMoveSize -= NativeSizingOfficialEntering; } catch { }
        try { source.WindowRectChanging -= NativeSizingOfficialChanging; } catch { }
        try { source.ExitedMoveSize -= NativeSizingOfficialExited; } catch { }
    }

    private void NativeSizingOfficialEntering(InputNonClientPointerSource sender, EnteringMoveSizeEventArgs args)
    {
        try { RecordNativeSizingOfficial("Entering", args.MoveSizeOperation, args.PointerScreenPoint, null); }
        catch { /* Observation only; never change the source event's behavior. */ }
    }

    private void NativeSizingOfficialChanging(InputNonClientPointerSource sender, WindowRectChangingEventArgs args)
    {
        try { RecordNativeSizingOfficial("Changing", args.MoveSizeOperation, args.PointerScreenPoint, args.NewWindowRect); }
        catch { /* Observation only; the parent WM_SIZING still owns legal rectangles. */ }
    }

    private void NativeSizingOfficialExited(InputNonClientPointerSource sender, ExitedMoveSizeEventArgs args)
    {
        try { RecordNativeSizingOfficial("Exited", args.MoveSizeOperation, args.PointerScreenPoint, null); }
        catch { /* Do not substitute this point for the existing exact release observation. */ }
    }

    private void RecordNativeSizingOfficial(string phase, MoveSizeOperation operation, PointInt32 point, RectInt32? rectangle)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try { RecordNativeSizingOfficial(phase, operation, point, rectangle); }
                catch { /* Diagnostics are marshaled to the same UI-owned collection. */ }
            });
            return;
        }
        if (_windowClosing || _nativeSizingPointerSource is null) return;
        if (phase == "Entering")
        {
            _nativeSizingOfficialObserving = true;
            _nativeSizingOfficialChangingCount = 0;
        }
        if (!_nativeSizingOfficialObserving) return;
        _nativeSizingMeasurements?.OfficialLifecycle(phase, operation, point, rectangle);
        if (phase != "Changing" || ++_nativeSizingOfficialChangingCount == 1)
            TraceInput($"NativeSizing official-{phase} operation={operation} screen={point.X},{point.Y} parentActive={_nativeSizingActive} changingCount={_nativeSizingOfficialChangingCount}");
        if (phase == "Exited") _nativeSizingOfficialObserving = false;
    }

    private IntPtr RunNativeSizingLoop(IntPtr hwnd, IntPtr wParam, IntPtr lParam, ResizeThumb zone, Point pressedAt)
    {
        // The hook is thread-local and exists only for this modal loop. It observes the
        // original release even when DefWindowProc consumes it instead of dispatching it.
        _nativeSizingGetMessageCallback = NativeSizingGetMessage;
        _nativeSizingMessageHook = SetWindowsHookExW(3, _nativeSizingGetMessageCallback, IntPtr.Zero,
            GetWindowThreadProcessId(hwnd, out _));
        if (_nativeSizingMessageHook == IntPtr.Zero)
        {
            TraceInput($"NativeSizing hook-failed error={Marshal.GetLastPInvokeError()}");
            return IntPtr.Zero; // Do not enter a loop whose exact release cannot be observed.
        }
        try
        {
            _nativeSizingLoopEntered = _nativeSizingLoopExited = _nativeSizingCancelRequested = false;
            _nativeSizingCancelReason = null;
            _nativeSizingReleasePoint = null;
            BeginResizeSession(zone, pressedAt);
            _nativeSizingActive = true;
            _nativeSizingMessagePoint = pressedAt;
            _nativeSizingInputTimestamp = Stopwatch.GetTimestamp();
            _nativeSizingTargetRect = _resizeStartRect;
            try { _nativeSizingMeasurements = new NativeSizingMeasurements(_resizeStartRect, pressedAt, Settings.FullScreen); }
            catch { _nativeSizingMeasurements = null; }
            // This call includes the modal wait. It is deliberately NOT a CPU measurement.
            return Win32Api.DefSubclassProc(hwnd, 0x00A1, wParam, lParam);
        }
        finally
        {
            _nativeSizingActive = false;
            RemoveNativeSizingMessageHook();
            var canceled = _nativeSizingCancelRequested || !_nativeSizingLoopEntered || !_nativeSizingLoopExited ||
                _nativeSizingReleasePoint is null || _windowClosing;
            try { TraceInput($"NativeSizing completed entered={_nativeSizingLoopEntered} exited={_nativeSizingLoopExited} canceled={canceled} reason={_nativeSizingCancelReason ?? "none"} release={_nativeSizingReleasePoint}"); }
            catch { /* Trace failure must not prevent release/cancel settling. */ }
            _nativeSizingHover = null;
            _pendingWindowRect = null;
            _viewportTransition = null;
            if (!_windowClosing && _nativeSizingCallback is not null)
                CompleteResizeSession(canceled, _nativeSizingReleasePoint);
            _nativeSizingMeasurements?.End(canceled, _nativeSizingCancelReason, _nativeSizingReleasePoint);
            _nativeSizingMeasurements = null;
        }
    }

    private IntPtr NativeSizingGetMessage(int code, IntPtr wParam, IntPtr lParam)
    {
        var nextResult = IntPtr.Zero;
        try
        {
            if (code >= 0 && wParam == new IntPtr(1) && _nativeSizingActive)
            {
                var message = Marshal.PtrToStructure<NativeSizingMessage>(lParam);
                // MSG.pt belongs to the event when posted, independent of subsequent
                // window origin, XAML layout, or current cursor movement.
                var belongsToWall = message.Hwnd == _nativeSizingHwnd ||
                    message.Hwnd != IntPtr.Zero && GetAncestor(message.Hwnd, 2) == _nativeSizingHwnd;
                if (belongsToWall && message.Message is 0x0200 or 0x00A0 or 0x0202 or 0x00A2)
                {
                    _nativeSizingMessagePoint = new Point(message.Point.X, message.Point.Y);
                    var ageMs = unchecked((uint)Environment.TickCount64 - message.Time);
                    _nativeSizingInputTimestamp = Stopwatch.GetTimestamp() -
                        (long)(ageMs * (double)Stopwatch.Frequency / 1000d);
                    if (message.Message is 0x0202 or 0x00A2)
                        _nativeSizingReleasePoint = _nativeSizingMessagePoint;
                }
                if (message.Message is 0x0100 or 0x0104 && message.WParam == new IntPtr(0x1B))
                {
                    _nativeSizingCancelRequested = true;
                    _nativeSizingCancelReason = "NativeEscape";
                }
            }
        }
        catch (Exception ex)
        {
            try { RequestNativeSizingCancel($"NativeInput:{ex.GetType().Name}:0x{ex.HResult:X8}"); }
            catch { /* Never unwind a hook callback through user32. */ }
        }
        finally
        {
            try { nextResult = CallNextHookEx(_nativeSizingMessageHook, code, wParam, lParam); }
            catch { /* Hook cleanup/diagnostic failure must not escape the unmanaged boundary. */ }
        }
        return nextResult;
    }

    private void RequestNativeSizingCancel(string reason)
    {
        if (!_nativeSizingActive) return;
        var first = !_nativeSizingCancelRequested;
        _nativeSizingCancelRequested = true;
        _nativeSizingCancelReason ??= reason;
        if (first) Win32Api.PostMessageW(_nativeSizingHwnd, 0x001F, IntPtr.Zero, IntPtr.Zero);
    }

    private void RemoveNativeSizingMessageHook()
    {
        if (_nativeSizingMessageHook != IntPtr.Zero) UnhookWindowsHookEx(_nativeSizingMessageHook);
        _nativeSizingMessageHook = IntPtr.Zero;
        // Keep the delegate rooted for the complete native callback stack.
    }

    private ResizeThumb? NativeSizingZoneAt(Point screenPoint)
    {
        // The supported-API experiment is deliberately only BR/windowed LEFT edge.
        // All corners, other anchors, the other edge and fullscreen keep the XAML path.
        if (PersistenceInteractionBlocked || _windowClosing || _modal || _viewBlocked || !_hasDocument || Settings.FullScreen ||
            Settings.AnchorCorner != AnchorCorner.BottomRight ||
            !GetWindowRect(_nativeSizingHwnd, out var rectangle)) return null;
        var scale = Scale;
        var x = (screenPoint.X - rectangle.Left) / scale;
        var y = (screenPoint.Y - rectangle.Top) / scale;
        var width = (rectangle.Right - rectangle.Left) / scale;
        var height = (rectangle.Bottom - rectangle.Top) / scale;
        if (x < 0 || y < 0 || x >= width || y >= height) return null;
        return x < 8 && y >= 14 && y < height - 14 ? ResizeVerticalEdge : null;
    }

    private int NativeSizingHitFor(ResizeThumb zone)
    {
        var left = zone.HorizontalAlignment == HorizontalAlignment.Left;
        var top = zone.VerticalAlignment == VerticalAlignment.Top;
        if (ReferenceEquals(zone, ResizeHorizontalEdge)) return top ? 12 : 15; // HTTOP / HTBOTTOM
        if (ReferenceEquals(zone, ResizeVerticalEdge)) return left ? 10 : 11; // HTLEFT / HTRIGHT
        return top ? left ? 13 : 14 : left ? 16 : 17; // HTTOPLEFT .. HTBOTTOMRIGHT
    }

    private static Point PackedNativeScreenPoint(IntPtr value) =>
        new(unchecked((short)(value.ToInt64() & 0xFFFF)), unchecked((short)((value.ToInt64() >> 16) & 0xFFFF)));

    private delegate IntPtr NativeSizingGetMessageProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSizingMouseTracking
    {
        public uint Size, Flags;
        public IntPtr Hwnd;
        public uint HoverTime;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSizingMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public CursorPoint Point;
        public uint Private;
    }
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int hook, NativeSizingGetMessageProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref NativeSizingMouseTracking tracking);

    private sealed class NativeSizingMeasurements(RectInt32 from, Point pressedAt, bool fullScreen)
    {
        private readonly List<(uint Message, long Start, long End)> _callbacks = [];
        private readonly List<(long Input, long Applied, RectInt32 Rectangle)> _applied = [];
        private readonly List<(uint Message, long At)> _nativeLifecycle = [];
        private readonly List<(string Phase, MoveSizeOperation Operation, PointInt32 Point, RectInt32? Rectangle, long At)> _officialLifecycle = [];
        internal void NativeLifecycle(uint message)
        {
            try { if (_nativeLifecycle.Count < 16384) _nativeLifecycle.Add((message, Stopwatch.GetTimestamp())); }
            catch { /* Optional lifecycle evidence only. */ }
        }
        internal void OfficialLifecycle(string phase, MoveSizeOperation operation, PointInt32 point, RectInt32? rectangle)
        {
            try { if (_officialLifecycle.Count < 16384) _officialLifecycle.Add((phase, operation, point, rectangle, Stopwatch.GetTimestamp())); }
            catch { /* Optional lifecycle evidence only. */ }
        }
        internal void Callback(uint message, long start)
        {
            try { if (_callbacks.Count < 16384) _callbacks.Add((message, start, Stopwatch.GetTimestamp())); }
            catch { /* Optional bounded collection only. */ }
        }
        internal void Applied(long input, RectInt32 rectangle)
        {
            try { if (_applied.Count < 16384) _applied.Add((input, Stopwatch.GetTimestamp(), rectangle)); }
            catch { /* Optional bounded collection only. */ }
        }
        internal void End(bool canceled, string? reason, Point? releasedAt)
        {
            try { WriteReport(canceled, reason, releasedAt); }
            catch { /* Serialization/scheduling must never affect the resize or native callback. */ }
        }
        private void WriteReport(bool canceled, string? reason, Point? releasedAt)
        {
            static object Summary(IEnumerable<double> source)
            {
                var values = source.Order().ToArray();
                double P(double fraction) => values.Length == 0 ? 0 :
                    values[Math.Clamp((int)Math.Ceiling(values.Length * fraction) - 1, 0, values.Length - 1)];
                return new { count = values.Length, p50 = P(.5), p95 = P(.95), p99 = P(.99), max = P(1) };
            }
            static double Ms(long value) => value * 1000d / Stopwatch.Frequency;
            var report = JsonSerializer.Serialize(new
            {
                backend = "system-nonclient-sizing-prototype", routing = "official-LeftBorder-BR-windowed-only",
                canceled, reason, from, pressedAt, releasedAt,
                startedFullScreen = fullScreen,
                nativeLifecycle = _nativeLifecycle.Select(e => new { message = $"0x{e.Message:X4}", at = e.At }),
                officialLifecycle = _officialLifecycle.Select(e => new { phase = e.Phase,
                    operation = e.Operation.ToString(), point = e.Point, rectangle = e.Rectangle, at = e.At }),
                callbackWallTimeMs = _callbacks.GroupBy(c => c.Message).ToDictionary(g => $"0x{g.Key:X4}",
                    g => Summary(g.Select(c => Ms(c.End - c.Start)))),
                nativeMessagePostedToActualRectMs = Summary(_applied.Select(a => Ms(a.Applied - a.Input))),
                actualRectangles = _applied.Select(a => new { input = a.Input, at = a.Applied, rectangle = a.Rectangle }),
                uiCallbackIntervals = _callbacks.Select(c => new { message = $"0x{c.Message:X4}", start = c.Start, end = c.End }),
                stopwatchFrequency = Stopwatch.Frequency,
                measurement = "UI native callback wall-time only, excluding the modal input wait. WM_WINDOWPOSCHANGED can include WM_MOVE and WM_SIZE; intervals overlap and percentile summaries must not be added or subtracted. OnInteractionFrame is measured separately by gesture diagnostics and can overlap these callbacks. Move/release input uses original removed MSG.pt/time; the initial press timestamp starts at native dispatch. Actual RECT is read after WM_WINDOWPOSCHANGED, not presentation. Final release transaction/snap remains in gesture commit diagnostics, outside this callback summary. This report alone cannot certify the full UI-frame budget or display latency. Mode transitions currently use immediate system RECT changes, without the legacy 200ms animation."
            }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
            _ = Task.Run(() =>
            {
                try
                {
                    var directory = Path.Combine(Path.GetTempPath(), "TileWall-performance");
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, $"native-sizing-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.json"), report);
                }
                catch { /* Optional prototype diagnostics never affect the resize transaction. */ }
            });
        }
    }
}
