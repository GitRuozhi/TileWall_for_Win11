using System.Diagnostics;
using TileWall.Core.Wall;
using TileWall.Shell.Interop;
using Windows.Foundation;
using Windows.Graphics;

namespace TileWall;

public sealed partial class MainWindow
{
    private sealed record ViewportTransition(RectInt32 From, RectInt32 To, Rect FromClip, Rect ToClip,
        bool FullScreen, WallSettings Settings, long Started, long SegmentStarted, double Scale);
    private ViewportTransition? _viewportTransition;
    private bool? _lastPresentedFullScreen;
    private bool _processingFrame;
    private RectInt32? _pendingWindowRect;
    private bool _sidebarFullScreen;
    private AnchorCorner _sidebarAnchor;
    private readonly bool _resizeSetWindowPosDiagnostic = Environment.GetEnvironmentVariable("TILEWALL_RESIZE_SETWINDOWPOS") == "1";
    partial void CreateWindowsSidebar();
    partial void UpdateWindowsSidebar();

    private void MoveWindowIfChanged(RectInt32 rectangle, bool continuousResizeFrame = false)
    {
        if (_nativeSizingActive) return; // WM_SIZING owns the real HWND rectangle in the diagnostic prototype.
        if (_processingFrame)
        {
            _pendingWindowRect = rectangle;
            return;
        }
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        if (position.X == rectangle.X && position.Y == rectangle.Y &&
            size.Width == rectangle.Width && size.Height == rectangle.Height) return;
        var started = Stopwatch.GetTimestamp();
        _performance.BeginWindowSubmission();
        try
        {
            // This opt-in control changes only ordinary held resize submissions.
            // Release/cancel, mode transitions and every other caller retain AppWindow.
            var useSetWindowPos = _resizeSetWindowPosDiagnostic && continuousResizeFrame &&
                DispatcherQueue.HasThreadAccess && _resizing && _resizeThumb?.IsDragging == true &&
                _resizeSnapshot is { Settings.FullScreen: false } && !_resizeModeLocked &&
                !_sidebarFullScreen && _viewportTransition is null;
            var backend = "appwindow";
            if (useSetWindowPos)
            {
                const uint flags = 0x0214; // SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                if (Win32Api.SetWindowPos(hwnd, IntPtr.Zero, rectangle.X, rectangle.Y,
                    rectangle.Width, rectangle.Height, flags)) backend = "setwindowpos";
                else
                {
                    backend = "setwindowpos-failed-appwindow";
                    AppWindow.MoveAndResize(rectangle);
                }
            }
            else AppWindow.MoveAndResize(rectangle);
            var elapsed = Stopwatch.GetTimestamp() - started;
            bool? actualRectMatches = null;
            if (useSetWindowPos && _performance.Enabled &&
                GetWindowRect(WinRT.Interop.WindowNative.GetWindowHandle(this), out var actual))
                actualRectMatches = actual.Left == rectangle.X && actual.Top == rectangle.Y &&
                    actual.Right - actual.Left == rectangle.Width && actual.Bottom - actual.Top == rectangle.Height;
            // API time remains inclusive of synchronous messages. Ack is inside the
            // UI frame and input-to-rectangle latency, but outside the API-only interval.
            _performance.WindowMove(elapsed, backend, actualRectMatches);
        }
        finally { _performance.AbortWindowSubmission(); }
    }

    private void PresentViewport(RectInt32 rectangle, bool fullScreen, AnchorCorner anchor, bool animate,
        WallSettings? presentationSettings = null)
    {
        var settings = presentationSettings ?? Settings;
        _presentedViewportSettings = settings;
        _sidebarFullScreen = fullScreen;
        _sidebarAnchor = anchor;
        UpdateWindowsSidebar();
        var fromClip = _scrollRegion;
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        var from = _pendingWindowRect ?? new RectInt32(position.X, position.Y, size.Width, size.Height);
        var toClip = ScrollRegionFor(fullScreen, (rectangle.Width - _frameWidth) / Scale,
            (rectangle.Height - _frameHeight) / Scale, anchor, settings);
        if (_nativeSizingActive)
        {
            // The OS applies the WM_SIZING rectangle. Never race it with application
            // MoveAndResize or a mode-animation endpoint while its modal loop is active.
            _viewportTransition = null;
            _lastPresentedFullScreen = fullScreen;
            SetScrollRegion(fullScreen, toClip);
            return;
        }
        if (_viewportTransition is { } running && running.FullScreen == fullScreen && running.Settings == settings && running.ToClip == toClip &&
            running.To.X == rectangle.X && running.To.Y == rectangle.Y &&
            running.To.Width == rectangle.Width && running.To.Height == rectangle.Height)
            return; // A release adopting this exact endpoint keeps the in-flight transition.
        var changedMode = _lastPresentedFullScreen is { } previous && previous != fullScreen;
        _lastPresentedFullScreen = fullScreen;
        // Ordinary edge resizing is direct. Query the system animation setting only
        // when a mode transition can actually be created or continued.
        var animationsEnabled = false;
        if (_viewportTransition is not null || animate && changedMode)
        {
            try { animationsEnabled = _appearanceSettings.AnimationsEnabled; }
            catch { animationsEnabled = true; }
        }
        var now = Stopwatch.GetTimestamp();
        if (_viewportTransition is { } active && active.FullScreen == fullScreen && active.Scale == Scale &&
            animationsEnabled && (now - active.Started) * 1000d / Stopwatch.Frequency < 200)
        {
            // Continuous resize updates the legal endpoint while keeping the first mode
            // change's deadline. Rebase at the currently displayed/pending frame so a new
            // target cannot jump the clip or restart another full 200ms chase.
            _viewportTransition = active with { From = from, FromClip = fromClip, Settings = settings,
                To = rectangle, ToClip = toClip, SegmentStarted = now };
            Surface.SetViewportMinimum(Math.Max(fromClip.Width, toClip.Width), 0);
            EnsureFrameSubscription();
            return;
        }
        if (animate && changedMode && animationsEnabled && from.Width > 0 && fromClip.Width > 0)
        {
            // A replacement transition starts at the actual displayed rect/clip, never at
            // an old animation's origin. Normal pointer resizing does not use easing.
            _viewportTransition = new(from, rectangle, fromClip, toClip, fullScreen, settings, now, now, Scale);
            Surface.SetViewportMinimum(Math.Max(fromClip.Width, toClip.Width), 0);
            SetScrollRegion(fullScreen, fromClip, updateSurface: false);
            EnsureFrameSubscription();
            return;
        }
        _viewportTransition = null;
        SetScrollRegion(fullScreen, toClip);
        MoveWindowIfChanged(rectangle);
        StopFrameSubscriptionIfIdle();
    }

    private void AdvanceViewportTransition()
    {
        if (_viewportTransition is not { } transition) return;
        if (_windowClosing || transition.Scale != Scale)
        {
            StopViewportTransition(); return;
        }
        var elapsed = (Stopwatch.GetTimestamp() - transition.SegmentStarted) * 1000d / Stopwatch.Frequency;
        var remaining = Math.Max(0, 200d - (transition.SegmentStarted - transition.Started) * 1000d / Stopwatch.Frequency);
        var t = remaining <= 0 ? 1 : Math.Clamp(elapsed / remaining, 0, 1);
        var p = t * t * (3 - 2 * t);
        static double L(double a, double b, double p) => a + (b - a) * p;
        var clip = new Rect(L(transition.FromClip.X, transition.ToClip.X, p),
            L(transition.FromClip.Y, transition.ToClip.Y, p), L(transition.FromClip.Width, transition.ToClip.Width, p),
            L(transition.FromClip.Height, transition.ToClip.Height, p));
        SetScrollRegion(transition.FullScreen, clip, updateSurface: false, presentationSettings: transition.Settings);
        MoveWindowIfChanged(new RectInt32((int)Math.Round(L(transition.From.X, transition.To.X, p)),
            (int)Math.Round(L(transition.From.Y, transition.To.Y, p)),
            Math.Max(1, (int)Math.Round(L(transition.From.Width, transition.To.Width, p))),
            Math.Max(1, (int)Math.Round(L(transition.From.Height, transition.To.Height, p)))));
        if (t == 1)
        {
            _viewportTransition = null;
            SetScrollRegion(transition.FullScreen, transition.ToClip, presentationSettings: transition.Settings);
        }
    }

    private void StopViewportTransition()
    {
        if (_viewportTransition is not { } transition) return;
        _viewportTransition = null;
        SetScrollRegion(transition.FullScreen, transition.ToClip, presentationSettings: transition.Settings);
        MoveWindowIfChanged(transition.To);
        StopFrameSubscriptionIfIdle();
    }
}
