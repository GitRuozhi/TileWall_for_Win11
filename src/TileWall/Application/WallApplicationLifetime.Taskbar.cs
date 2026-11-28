using TileWall.Core.Shell;
using TileWall.Resources;
using TileWall.Shell.Interop;
using Windows.Graphics;

namespace TileWall.ApplicationModel;

internal sealed partial class WallApplicationLifetime
{
    private readonly TaskbarActivationService _taskbarActivation;
    private volatile bool _taskbarWallVisible, _taskbarEnabled, _taskbarClosing;
    private volatile bool _taskbarSettingsDisablePending;
    private bool _taskbarPlacementRefreshing;
    private long _taskbarPlacementVersion;
    private long _taskbarRequestVersion;
    private int _taskbarOpeningId;
    private Task _taskbarWork = Task.CompletedTask;
    private Dictionary<DisplayMonitor, TaskbarActivationObservation> _cachedTaskbarPlacements = [];
    private Dictionary<nint, TaskbarActivationObservation> _taskbarObservations = [];
    private TaskbarTarget[] _taskbarTargets = [];
    private string? _taskbarActivationFailure;

    private string? TaskbarActivationError => _taskbarActivationFailure is null
        ? null : ProductStrings.Get("Settings.TaskbarActivationFailed");

    private async Task RecoverPreviousTaskbarAsync()
    {
        try
        {
            var result = await _taskbarActivation.RecoverPreviousAsync();
            RecordTaskbarFailure(result.Failure);
        }
        catch (Exception ex) { RecordTaskbarFailure(ex.Message); }
    }

    private bool CanActivateTaskbar => !_isTest && !_taskbarClosing && !_taskbarSettingsDisablePending &&
        _window.Settings.ActivateTaskbar && !_window.SettingsPersistenceBlocked;

    // A wall can open before the first observation or after its display changed.
    // Use the monitor's work area until a matching placement is available.
    private bool ApplyTaskbarPlacement(IReadOnlyList<DisplayMonitor> displays)
    {
        var previous = _taskbarObservations;
        _taskbarObservations = [];
        TaskbarActivationObservation? primary = null;
        if (CanActivateTaskbar)
        {
            foreach (var display in displays)
            {
                var screen = display.OuterBounds;
                var work = display.WorkArea;
                var observed = _cachedTaskbarPlacements.TryGetValue(display, out var cached) ? cached :
                    new TaskbarActivationObservation(false, false, default, new(screen.X, screen.Y, screen.Width, screen.Height),
                        new(work.X, work.Y, work.Width, work.Height), new(work.X, work.Y, work.Width, work.Height));
                _taskbarObservations.Add(display.Id, observed);
                if (display.IsPrimary) primary = observed;
                else _window.SetSecondaryTaskbarPlacement(display.Id, TaskbarPlacement(observed), observed.Anchor);
            }
        }
        foreach (var id in previous.Keys)
            if (!_taskbarObservations.ContainsKey(id)) _window.SetSecondaryTaskbarPlacement(id, null, default);
        _window.SetTaskbarPlacement(TaskbarPlacement(primary), primary?.Anchor ?? default);
        return previous.Count != _taskbarObservations.Count || previous.Any(entry =>
            !_taskbarObservations.TryGetValue(entry.Key, out var current) || current != entry.Value);
    }

    private static RectInt32? TaskbarPlacement(TaskbarActivationObservation? observation) => observation is { EffectiveArea: var area }
        ? new RectInt32(area.X, area.Y, area.Width, area.Height) : null;

    private bool ApplyVisibleTaskbarPlacement()
    {
        var secondary = _window.VisibleSecondaryTaskbarTargets();
        return ApplyTaskbarPlacement(DisplayTopology.ReadMonitors().Where(display => display.IsPrimary
            ? _window.AppWindow.IsVisible : secondary.Any(target => target.DisplayId == display.Id)).ToArray());
    }

    private void QueueTaskbarEnvironmentRefresh()
    {
        if (!CanActivateTaskbar) return;
        _taskbarPlacementVersion++;
        if (_taskbarPlacementRefreshing) return;
        _taskbarPlacementRefreshing = true;
        _ = RefreshTaskbarPlacementsAsync();
    }

    private void TaskbarExplorerRecreated()
    {
        _cachedTaskbarPlacements.Clear();
        QueueTaskbarEnvironmentRefresh();
    }

    private async Task RefreshTaskbarPlacementsAsync()
    {
        try
        {
            while (CanActivateTaskbar)
            {
                var version = _taskbarPlacementVersion;
                var displays = DisplayTopology.ReadMonitors();
                var placements = await Task.Run(() => ObserveTaskbarPlacements(displays));
                if (!CanActivateTaskbar) return;
                if (version != _taskbarPlacementVersion) continue;
                _cachedTaskbarPlacements = placements;
                if (_taskbarWallVisible && ApplyVisibleTaskbarPlacement()) TaskbarWallsChanged(force: true);
                return;
            }
        }
        catch (Exception ex)
        {
            if (!_taskbarClosing) AppLog.Warn("TaskbarActivation", $"Placement observation failed: {ex.Message}");
        }
        finally { _taskbarPlacementRefreshing = false; }
    }

    private static Dictionary<DisplayMonitor, TaskbarActivationObservation> ObserveTaskbarPlacements(
        IReadOnlyList<DisplayMonitor> displays)
    {
        var autoHide = TaskbarActivationBackend.AutoHideEnabled;
        var placements = new Dictionary<DisplayMonitor, TaskbarActivationObservation>(displays.Count);
        foreach (var display in displays)
        {
            var screen = display.OuterBounds;
            var work = display.WorkArea;
            placements.Add(display, TaskbarActivationBackend.ObserveDisplay(new(screen.X, screen.Y, screen.Width, screen.Height),
                new(work.X, work.Y, work.Width, work.Height), display.IsPrimary, autoHide));
        }
        return placements;
    }

    private void TaskbarWallsChanged(bool force = false)
    {
        var secondary = _window.VisibleSecondaryTaskbarTargets();
        var launchers = secondary.ToDictionary(target => target.DisplayId, target => target.LauncherWindow);
        var primaryVisible = _window.AppWindow.IsVisible;
        if (primaryVisible && DisplayTopology.MonitorAt(default) is { } primaryId)
            launchers[primaryId] = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        var targets = new List<TaskbarTarget>();
        foreach (var (display, launcher) in launchers)
        {
            if (!_taskbarObservations.TryGetValue(display, out var observation) ||
                observation is not { Available: true, AutoHide: true }) continue;
            var screen = observation.Screen;
            targets.Add(new(new(screen.X, screen.Y, screen.Width, screen.Height), observation.Anchor.Window, launcher));
        }
        var visible = primaryVisible || secondary.Length != 0;
        var requested = targets.OrderBy(target => target.LauncherWindow).ToArray();
        if (!force && _taskbarWallVisible == visible && _taskbarTargets.SequenceEqual(requested)) return;
        _taskbarWallVisible = visible;
        _taskbarTargets = requested;
        _taskbarOpeningId++;
        QueueTaskbarActivationUpdate();
    }

    private void QueueTaskbarActivationUpdate()
    {
        _taskbarEnabled = CanActivateTaskbar;
        var version = Interlocked.Increment(ref _taskbarRequestVersion);
        if (_taskbarClosing) return;
        var requested = _taskbarEnabled && _taskbarWallVisible ? _taskbarTargets : [];
        var observationFailure = _taskbarEnabled && _taskbarWallVisible
            ? _taskbarObservations.Values.FirstOrDefault(observation => observation.Failure is not null)?.Failure : null;
        var previous = _taskbarWork;
        _taskbarWork = UpdateTaskbarAsync(previous, version, _taskbarOpeningId, requested, observationFailure);
    }

    private void QueueTaskbarActivationState(int openingId, Func<bool> stillRequested, TaskbarActivationResult result)
        => _window.DispatcherQueue.TryEnqueue(() =>
        {
            if (_taskbarClosing || openingId != _taskbarOpeningId || !_taskbarEnabled ||
                !_taskbarWallVisible || !stillRequested()) return;
            RecordTaskbarFailure(result.Failure);
            if (result.Active) ApplyVisibleTaskbarPlacement();
        });

    private async Task UpdateTaskbarAsync(Task previous, long version, int openingId, TaskbarTarget[] targets, string? observationFailure)
    {
        bool Current() => Interlocked.Read(ref _taskbarRequestVersion) == version && !_taskbarClosing;
        bool StillRequested() => Current() && _taskbarEnabled && _taskbarWallVisible;
        try
        {
            await previous;
            if (!Current()) return;
            var result = targets.Length != 0
                ? await _taskbarActivation.ApplyAsync(targets, openingId, StillRequested)
                : await _taskbarActivation.RestoreAsync();
            if (!Current()) return;
            RecordTaskbarFailure(result.Failure ?? observationFailure);
            // Native completion is never an activation request. Geometry can be
            // refreshed only for this still-current, already visible wall.
            if (targets.Length != 0 && StillRequested()) ApplyVisibleTaskbarPlacement();
        }
        catch (Exception ex)
        {
            if (Current()) RecordTaskbarFailure(ex.Message);
        }
    }

    private void RecordTaskbarFailure(string? failure)
    {
        var changed = _taskbarActivationFailure != failure;
        _taskbarActivationFailure = failure;
        if (failure is not null && changed) AppLog.Warn("TaskbarActivation", failure);
        if (!changed || _taskbarClosing) return;
        _window.ActiveDialog?.RefreshStrings();
        if (failure is not null && _taskbarWallVisible && !_exitPending)
            _window.ShowTransientStatus(TaskbarActivationError!);
    }

    private async Task<string?> PrepareTaskbarSettingsAsync(bool enabled)
    {
        if (_isTest || enabled) return null;
        _taskbarSettingsDisablePending = true;
        _taskbarEnabled = false;
        Interlocked.Increment(ref _taskbarRequestVersion);
        await _taskbarWork;
        var restored = await _taskbarActivation.RestoreAsync();
        RecordTaskbarFailure(restored.Failure);
        return restored.Failure is null ? null : TaskbarActivationError;
    }

    private void TaskbarSettingsCommitted(bool changed)
    {
        if (!changed) return;
        _taskbarSettingsDisablePending = false;
        if (_window.Settings.ActivateTaskbar) QueueTaskbarEnvironmentRefresh();
        else
        {
            _cachedTaskbarPlacements.Clear();
            RecordTaskbarFailure(null);
        }
        ApplyVisibleTaskbarPlacement();
        TaskbarWallsChanged(force: true);
    }

    private void RestoreTaskbarAfterRejectedSettings()
    {
        _taskbarSettingsDisablePending = false;
        QueueTaskbarEnvironmentRefresh();
        ApplyVisibleTaskbarPlacement();
        TaskbarWallsChanged(force: true);
    }

    private void SuspendTaskbarForBlockedPersistence()
    {
        if (_taskbarClosing || !_taskbarEnabled) return;
        QueueTaskbarActivationUpdate();
    }

    private void BeginTaskbarShutdown()
    {
        _taskbarClosing = true;
        _taskbarEnabled = false;
        Interlocked.Increment(ref _taskbarRequestVersion);
    }

    private async Task<string?> ShutdownTaskbarAsync()
    {
        try
        {
            await _taskbarWork;
            var result = await _taskbarActivation.StopAsync();
            if (result.Failure is not null) AppLog.Warn("TaskbarActivation", result.Failure);
            await _taskbarActivation.DisposeAsync();
            return result.Failure;
        }
        catch (Exception ex)
        {
            // An owned, durable pending record remains the helper's obligation.
            // Never terminate that helper or delete its record during teardown.
            AppLog.Warn("TaskbarActivation", $"Shutdown recovery retained: {ex.Message}");
            return ex.Message;
        }
    }
}
