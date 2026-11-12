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
    private bool _taskbarEnvironmentQueued;
    private long _taskbarRequestVersion;
    private int _taskbarOpeningId;
    private Task _taskbarWork = Task.CompletedTask;
    private TaskbarActivationObservation? _taskbarObservation;
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

    // This observation does not send messages or change Explorer. In particular,
    // it cannot wait for the recovery helper on the synchronous Show path.
    private void PrepareTaskbarPlacement()
    {
        if (_isTest || !_window.Settings.ActivateTaskbar || _window.SettingsPersistenceBlocked || _taskbarClosing)
        {
            _taskbarObservation = null;
            _window.SetTaskbarPlacement(null, 0);
            return;
        }
        try
        {
            var observed = _taskbarActivation.ObservePrimary();
            _taskbarObservation = observed;
            _window.SetTaskbarPlacement(observed.Available
                ? new RectInt32(observed.EffectiveArea.X, observed.EffectiveArea.Y,
                    observed.EffectiveArea.Width, observed.EffectiveArea.Height) : null,
                observed.Available ? observed.Window : 0);
            if (observed.Failure is not null) RecordTaskbarFailure(observed.Failure);
        }
        catch (Exception ex)
        {
            _taskbarObservation = null;
            _window.SetTaskbarPlacement(null, 0);
            RecordTaskbarFailure(ex.Message);
        }
    }

    private void TaskbarWallShown()
    {
        var firstShow = !_taskbarWallVisible;
        _taskbarWallVisible = _window.AppWindow.IsVisible;
        if (firstShow && _taskbarWallVisible) _taskbarOpeningId++;
        QueueTaskbarActivationUpdate();
    }

    private void TaskbarWallHidden()
    {
        _taskbarWallVisible = false;
        QueueTaskbarActivationUpdate();
    }

    private void QueueTaskbarEnvironmentRefresh()
    {
        if (_taskbarClosing || _taskbarEnvironmentQueued) return;
        _taskbarEnvironmentQueued = _window.DispatcherQueue.TryEnqueue(() =>
        {
            _taskbarEnvironmentQueued = false;
            if (_taskbarClosing || !_started) return;
            var previous = _taskbarObservation;
            PrepareTaskbarPlacement();
            // Ignore our reveal's position/animation notifications. A genuinely
            // different scope or Windows preference starts a new bounded request.
            if (SameTaskbarScope(previous, _taskbarObservation)) return;
            _taskbarOpeningId++;
            QueueTaskbarActivationUpdate();
        });
    }

    private static bool SameTaskbarScope(TaskbarActivationObservation? left, TaskbarActivationObservation? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.Available == right.Available && left.Window == right.Window && left.AutoHide == right.AutoHide &&
            left.Screen == right.Screen && left.WorkArea == right.WorkArea && left.EffectiveArea == right.EffectiveArea;
    }

    private void QueueTaskbarActivationUpdate()
    {
        _taskbarEnabled = !_isTest && !_taskbarClosing && !_taskbarSettingsDisablePending && _window.Settings.ActivateTaskbar &&
            !_window.SettingsPersistenceBlocked;
        var version = Interlocked.Increment(ref _taskbarRequestVersion);
        if (_taskbarClosing) return;
        var requested = _taskbarEnabled && _taskbarWallVisible;
        var previous = _taskbarWork;
        _taskbarWork = UpdateTaskbarAsync(previous, version, _taskbarOpeningId, requested);
    }

    private void QueueTaskbarActivationState(int openingId, Func<bool> stillRequested, TaskbarActivationResult result)
        => _window.DispatcherQueue.TryEnqueue(() =>
        {
            if (_taskbarClosing || openingId != _taskbarOpeningId || !_taskbarEnabled ||
                !_taskbarWallVisible || !stillRequested()) return;
            RecordTaskbarFailure(result.Failure);
            if (result.Active) PrepareTaskbarPlacement();
        });

    private async Task UpdateTaskbarAsync(Task previous, long version, int openingId, bool requested)
    {
        bool Current() => Interlocked.Read(ref _taskbarRequestVersion) == version && !_taskbarClosing;
        bool StillRequested() => Current() && _taskbarEnabled && _taskbarWallVisible;
        try
        {
            await previous;
            if (!Current()) return;
            var result = requested
                ? await _taskbarActivation.ApplyAsync(openingId, StillRequested)
                : await _taskbarActivation.RestoreAsync();
            if (!Current()) return;
            RecordTaskbarFailure(result.Failure);
            // Native completion is never an activation request. Geometry can be
            // refreshed only for this still-current, already visible wall.
            if (requested && StillRequested()) PrepareTaskbarPlacement();
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
        if (_isTest) return null;
        if (enabled)
        {
            // A preparation failure is a runtime availability failure. The user's
            // enabling preference may still be saved and retried on the next show.
            try
            {
                var prepared = await _taskbarActivation.PrepareAsync();
                RecordTaskbarFailure(prepared.Failure);
            }
            catch (Exception ex) { RecordTaskbarFailure(ex.Message); }
            return null;
        }
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
        _taskbarOpeningId++;
        if (!_window.Settings.ActivateTaskbar) RecordTaskbarFailure(null);
        PrepareTaskbarPlacement();
        QueueTaskbarActivationUpdate();
    }

    private void RestoreTaskbarAfterRejectedSettings()
    {
        _taskbarSettingsDisablePending = false;
        PrepareTaskbarPlacement();
        QueueTaskbarActivationUpdate();
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

    private async Task ShutdownTaskbarAsync()
    {
        try
        {
            await _taskbarWork;
            var result = await _taskbarActivation.StopAsync();
            if (result.Failure is not null) AppLog.Warn("TaskbarActivation", result.Failure);
            await _taskbarActivation.DisposeAsync();
        }
        catch (Exception ex)
        {
            // An owned, durable pending record remains the helper's obligation.
            // Never terminate that helper or delete its record during teardown.
            AppLog.Warn("TaskbarActivation", $"Shutdown recovery retained: {ex.Message}");
        }
    }
}
