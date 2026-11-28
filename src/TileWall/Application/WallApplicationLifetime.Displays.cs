using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using TileWall.Core.Shell;
using TileWall.Graphics;
using TileWall.Shell.Interop;
using Windows.Graphics;
using CaptureFailure = TileWall.Graphics.DesktopBackdropSnapshot.CaptureFailure;
using CaptureRequest = TileWall.Graphics.DesktopBackdropSnapshot.CaptureRequest;

namespace TileWall.ApplicationModel;

internal sealed partial class WallApplicationLifetime
{
    private nint? _requestedDisplay;
    private CancellationTokenSource? _displaySummonRequest;
    private Task? _displaySummonWork;
    private bool _resumeDisplaySummon;
    private bool _refreshDisplaysAfterSummon;
    private bool _sharedDisplaySession;
    private bool HasVisibleDisplay => _window.AppWindow.IsVisible || _window.HasVisibleSecondaryWalls;

    // Display coordinates come from the taskbar click. The desktop origin belongs to the primary screen.
    private static nint? DisplayAt(PointInt32? point) => DisplayTopology.MonitorAt(point ?? default);

    private void RefreshOpenedDisplays()
    {
        if (_show.State == WallShowState.Visible && !_tearingDown && !_exitPending)
            PresentRequestedDisplays(activate: false);
    }

    private void PresentRequestedDisplays(bool activate)
    {
        if (_displaySummonRequest is { } pending)
        {
            if (!activate) _refreshDisplaysAfterSummon = true;
            else if (pending.IsCancellationRequested) _resumeDisplaySummon = true;
            else if (activate && HasVisibleDisplay) FocusPresentedDisplay(_requestedDisplay);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var displays = DisplayTopology.ReadMonitors();
        var primary = displays.FirstOrDefault(display => display.IsPrimary);
        var requested = displays.FirstOrDefault(display => display.Id == _requestedDisplay) ?? primary;
        _requestedDisplay = requested?.Id;
        var showAll = _window.CanShowSecondaryWalls && _window.Settings.MultiDisplaySummon;
        var useSecondary = _window.CanShowSecondaryWalls && requested is { IsPrimary: false } && !_window.ModalActive;
        var keepCurrentDisplays = _window.ModalActive || _sharedDisplaySession && showAll && HasVisibleDisplay;
        var showPrimary = keepCurrentDisplays ? _window.ModalActive || _window.AppWindow.IsVisible : showAll || !useSecondary;
        var openPrimary = showPrimary && !_window.AppWindow.IsVisible;
        var captureBackdrop = !_window.AppWindow.IsVisible && !_window.HasVisibleSecondaryWalls;
        var currentSecondaries = keepCurrentDisplays ? _window.VisibleSecondaryTaskbarTargets() : [];
        var visible = displays.Where(display => display.IsPrimary ? showPrimary :
            keepCurrentDisplays ? showAll && currentSecondaries.Any(wall => wall.DisplayId == display.Id) :
            showAll || useSecondary && display.Id == _requestedDisplay).ToArray();
        if (captureBackdrop && !_window.ModalActive && showAll && requested is not null && visible.Length > 1)
        {
            var request = new CancellationTokenSource();
            _displaySummonRequest = request;
            _sharedDisplaySession = true;
            _displaySummonWork = PresentSharedDisplaysAsync(displays, visible, requested, activate, started, request);
            return;
        }
        var geometryReady = Stopwatch.GetTimestamp();
        ApplyTaskbarPlacement(visible);
        var taskbarsReady = Stopwatch.GetTimestamp();
        if (openPrimary) _window.PrepareWallBeforeShow(captureBackdrop);
        var primaryReady = Stopwatch.GetTimestamp();
        _window.SetSecondaryDisplays(displays, visible.Where(display => !display.IsPrimary).ToArray(), captureBackdrop);
        var secondaryVisible = Stopwatch.GetTimestamp();
        if (openPrimary) _window.ShowWall();
        if (activate) FocusPresentedDisplay(useSecondary ? _requestedDisplay : null);
        if (!showPrimary && _window.AppWindow.IsVisible)
            _window.HideWall();
        TaskbarWallsChanged();
        if (activate && captureBackdrop)
        {
            var completed = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds;
            if (elapsed >= 750)
                AppLog.Warn("WallShow", $"Slow display request: walls={visible.Length} elapsedMs={elapsed:F0} " +
                    $"monitorsMs={Milliseconds(started, geometryReady):F0} taskbarsMs={Milliseconds(geometryReady, taskbarsReady):F0} " +
                    $"primaryPrepMs={Milliseconds(taskbarsReady, primaryReady):F0} secondaryMs={Milliseconds(primaryReady, secondaryVisible):F0} " +
                    $"finalShowAndFocusMs={Milliseconds(secondaryVisible, completed):F0}");
        }

        static double Milliseconds(long start, long end) => Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
    }

    private void CancelDisplaySummon()
    {
        _resumeDisplaySummon = false;
        if (_displaySummonRequest is not { IsCancellationRequested: false } request) return;
        request.Cancel();
        if (!_window.AppWindow.IsVisible) _window.CancelSharedPrimaryBackdrop();
        _window.DiscardHiddenSecondaryPreparations();
    }

    private bool CanContinueDisplaySummon(CancellationToken token) =>
        !token.IsCancellationRequested && !_tearingDown && !_exitPending && !_window.ModalActive &&
        _show.State == WallShowState.Visible && _window.Settings.MultiDisplaySummon && _window.CanShowSecondaryWalls;

    private async Task PresentSharedDisplaysAsync(IReadOnlyList<DisplayMonitor> attached, DisplayMonitor[] displays,
        DisplayMonitor first, bool activate, long started, CancellationTokenSource request)
    {
        var token = request.Token;
        Task<(CaptureFailure? Failure, DesktopBackdropSnapshot.DesktopCapture? Source)>? read = null;
        DesktopBackdropSnapshot.DesktopCapture? desktop = null;
        var crops = new List<Task>();
        var reveals = new List<Task<CaptureFailure?>>();
        CaptureFailure? failure = null;
        var firstShown = false;
        try
        {
            _window.SynchronizeSecondaryDisplays(attached, displays.Where(display => !display.IsPrimary).ToArray());
            if (_window.NeedsSharedDesktopCapture)
                read = Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var failure = DesktopBackdropSnapshot.CaptureDesktop(out var source);
                    return (failure, source);
                });

            var firstPrepared = TryPrepareSharedDisplay(first, token);
            if (read is not null)
            {
                var result = await read;
                read = null;
                desktop = result.Source;
                failure = result.Failure;
                if (failure is not null || !CanContinueDisplaySummon(token)) return;
            }
            if (firstPrepared)
            {
                var initial = CropSharedDisplay(first, desktop, token, crops);
                failure = await ShowSharedDisplayAsync(first, initial, token);
                firstShown = failure is null && HasVisibleDisplay;
                if (firstShown && activate && CanContinueDisplaySummon(token)) FocusPresentedDisplay(first.Id);
            }
            else failure = CaptureFailure.Geometry;

            foreach (var display in displays)
            {
                if (display.Id == first.Id) continue;
                if (!await YieldDisplayPreparationAsync() || !CanContinueDisplaySummon(token)) break;
                if (!TryPrepareSharedDisplay(display, token))
                {
                    failure ??= CaptureFailure.Geometry;
                    continue;
                }
                var pending = CropSharedDisplay(display, desktop, token, crops);
                reveals.Add(ShowSharedDisplayAsync(display, pending, token, activate && !firstShown));
            }
            await Task.WhenAll(crops);
            desktop?.Dispose();
            desktop = null;
            foreach (var result in await Task.WhenAll(reveals))
                failure ??= result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failure = ex is OutOfMemoryException ? CaptureFailure.Allocation : CaptureFailure.Presentation;
            AppLog.Warn("WallShow", $"Shared display request failed: {ex}");
        }
        finally
        {
            // Await work admitted before a cancellation or synchronous UI failure.
            // A new capture or teardown can only follow this request's cleanup.
            try
            {
                if (read is not null) (await read).Source?.Dispose();
                await Task.WhenAll(crops);
            }
            catch (Exception) { } // The originating path handles capture and cancellation.
            finally { desktop?.Dispose(); }
            try { await Task.WhenAll(reveals); }
            catch (Exception) { } // The originating path handles display errors.

            var reportFailure = failure is not null && CanContinueDisplaySummon(token);
            var resume = _resumeDisplaySummon;
            var refresh = _refreshDisplaysAfterSummon;
            _displaySummonRequest = null;
            _displaySummonWork = null;
            _resumeDisplaySummon = false;
            _refreshDisplaysAfterSummon = false;
            request.Dispose();
            if (!_window.AppWindow.IsVisible) _window.CancelSharedPrimaryBackdrop();
            _window.DiscardHiddenSecondaryPreparations();

            if (reportFailure) ReportSharedDisplayFailure(first.Id, failure!.Value);
            if (resume && _show.State == WallShowState.Visible && !_exitPending && !_tearingDown)
                _window.DispatcherQueue.TryEnqueue(() => PresentRequestedDisplays(activate: true));
            else
            {
                if (refresh) RefreshOpenedDisplays();
                QueueWindowsStartInvitation();
            }
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (elapsed >= 750 && !token.IsCancellationRequested)
                AppLog.Warn("WallShow", $"Slow shared display request: walls={displays.Length} elapsedMs={elapsed:F0}");
        }
    }

    private bool TryPrepareSharedDisplay(DisplayMonitor target, CancellationToken token)
    {
        if (!CanContinueDisplaySummon(token)) return false;
        var displays = DisplayTopology.ReadMonitors();
        var current = displays.FirstOrDefault(display => SamePhysicalDisplay(display, target));
        if (current is null) return false;
        ApplyTaskbarPlacement(displays);
        if (target.IsPrimary) _window.PrepareWallBeforeShow(captureBackdrop: false);
        else _window.PrepareSecondaryDisplay(current, captureBackdrop: false);
        return CanContinueDisplaySummon(token);
    }

    private (CaptureRequest? Request, Task<(CaptureFailure? Failure, DesktopBackdropSnapshot.CapturedFrame? Frame)>? Crop)
        CropSharedDisplay(DisplayMonitor display, DesktopBackdropSnapshot.DesktopCapture? desktop, CancellationToken token, List<Task> crops)
    {
        var request = display.IsPrimary ? _window.SharedPrimaryBackdropRequest() : _window.SharedSecondaryBackdropRequest(display.Id);
        if (request is not { } region || desktop is null) return (request, null);
        var work = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var failure = desktop.Crop(region, out var frame);
            return (failure, frame);
        });
        crops.Add(work);
        return (request, work);
    }

    private async Task<CaptureFailure?> ShowSharedDisplayAsync(DisplayMonitor display,
        (CaptureRequest? Request, Task<(CaptureFailure? Failure, DesktopBackdropSnapshot.CapturedFrame? Frame)>? Crop) background,
        CancellationToken token, bool focusIfNoWall = false)
    {
        if (background is { Request: { } region, Crop: { } work })
        {
            var result = await work;
            if (!CanContinueDisplaySummon(token)) return null;
            if (result.Failure is { } failure) return failure;
            if (result.Frame is not { } frame) return CaptureFailure.Crop;
            var ready = display.IsPrimary
                ? await _window.PrepareSharedPrimaryBackdropAsync(region, frame, token)
                : await _window.PrepareSharedSecondaryBackdropAsync(display.Id, region, frame, token);
            if (!CanContinueDisplaySummon(token)) return null;
            if (!ready)
            {
                BackdropTrace.Write($"SharedSummon background rejected id={display.Id} primary={display.IsPrimary}");
                return CaptureFailure.Presentation;
            }
        }
        else if (_window.NeedsSharedDesktopCapture) return CaptureFailure.Presentation;

        if (!CanContinueDisplaySummon(token)) return null;
        var attached = DisplayTopology.ReadMonitors();
        if (!attached.Any(current => SamePhysicalDisplay(current, display))) return CaptureFailure.Geometry;
        ApplyTaskbarPlacement(attached);
        var focus = focusIfNoWall && !HasVisibleDisplay;
        if (display.IsPrimary)
        {
            _window.ShowWall();
            _window.SetBackdropVisibility(visible: true);
        }
        else _window.ShowPreparedSecondaryDisplay(display.Id);
        if (focus) FocusPresentedDisplay(display.Id);
        TaskbarWallsChanged();
        BackdropTrace.Write($"SharedSummon shown id={display.Id} primary={display.IsPrimary}");
        return null;
    }

    private static bool SamePhysicalDisplay(DisplayMonitor current, DisplayMonitor planned) =>
        current.Id == planned.Id && current.IsPrimary == planned.IsPrimary && current.OuterBounds == planned.OuterBounds;

    private Task<bool> YieldDisplayPreparationAsync()
    {
        var resumed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => resumed.SetResult(true)))
            resumed.SetResult(false);
        return resumed.Task;
    }

    private void FocusPresentedDisplay(nint? display)
    {
        if (display is { } id && _window.FocusSecondaryDisplay(id)) return;
        if (_window.AppWindow.IsVisible) _window.FocusTopSession();
    }

    private void ReportSharedDisplayFailure(nint id, CaptureFailure failure)
    {
        AppLog.Warn("BackdropSnapshot", $"Shared display capture unavailable: {failure}.");
        if (HasVisibleDisplay) _window.ReportDisplayCaptureFailure(id, failure);
        else
        {
            _show.Request(WallShowTrigger.Hide);
            var message = MainWindow.DisplayCaptureFailureMessage(failure);
            if (!_notifications.ShowFailure(message))
                _ = MessageBoxW(_host.Hwnd, message, "TileWall", 0x00000010);
        }
    }

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint type);
}
