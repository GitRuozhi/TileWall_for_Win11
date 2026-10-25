using TileWall.Core.Settings;
using TileWall.Core.Wall;
using TileWall.Resources;
using TileWall.Shell.Interop;
using TileWall.Views;

namespace TileWall.ApplicationModel;

internal sealed partial class WallApplicationLifetime
{
    // Settings, recorder admission and final shutdown share one resource owner.
    // A failed/uncompleted Stop retains this service and the process instance gate.
    private readonly SemaphoreSlim _inputTransition = new(1, 1);
    private readonly StartReplacementService _startReplacement;
    private readonly bool _isTest;
    private bool _startReplacementSuspended;
    private string? _startReplacementError;

    private void HandleGlobalHotKey()
    {
        if (_hotKeyRecording || _settingsBusy || !_started || _exitPending || _tearingDown) return;
        if (_startReplacement.IsActive)
        {
            if (!NativeStartMenu.TryOpen(_host.Hwnd))
                _window.ShowStatus(ProductStrings.Get("Settings.StartReplacementUnavailable"));
        }
        else Request(_show.State == TileWall.Core.Shell.WallShowState.Hidden || !_window.IsForegroundWallSession()
            ? TileWall.Core.Shell.WallShowTrigger.Show : TileWall.Core.Shell.WallShowTrigger.Toggle);
    }

    private void QueueStartReplacementToggle(long generation) => _window.DispatcherQueue.TryEnqueue(() =>
    {
        if (_hotKeyRecording || _settingsBusy || !_window.Settings.ReplaceStartMenu ||
            !_startReplacement.IsCurrentActivation(generation)) return;
        Request(TileWall.Core.Shell.WallShowTrigger.Toggle);
    });

    private void QueueStartReplacementFault(string failure)
    {
        _window.DispatcherQueue.TryEnqueue(() => _ = AcceptStartReplacementFaultAsync(failure));
    }

    private async Task AcceptStartReplacementFaultAsync(string failure)
    {
        await _inputTransition.WaitAsync();
        try
        {
            if (_tearingDown || !_window.Settings.ReplaceStartMenu || _startReplacement.IsActive ||
                _startReplacement.Fault != failure) return;
            _startReplacementError = StartReplacementFailure(failure);
            await StopStartReplacementAsync();
            if (!_exitPending) _window.ShowStatus(_startReplacementError!);
            _window.ActiveDialog?.RefreshStrings();
        }
        catch (Exception ex) { TileWall.Core.Shell.AppLog.Warn("StartReplacement", $"Fault notification: {ex.Message}"); }
        finally { _inputTransition.Release(); }
    }

    private async Task InitializeStartReplacementAsync()
    {
        await _inputTransition.WaitAsync();
        try
        {
            if (_window.Settings.ReplaceStartMenu && CanActivateStartReplacement)
                await ResumeStartReplacementAsync();
        }
        finally { _inputTransition.Release(); }
    }

    private bool CanActivateStartReplacement => !_isTest && !_tearingDown && !_exitPending &&
        !_hotKeyRecording && !_window.SettingsPersistenceBlocked;

    private static string StartReplacementFailure(string failure) =>
        string.Format(ProductStrings.Get("Settings.StartReplacementFailed"), failure);

    private async Task<bool> StopStartReplacementAsync(bool suspend = false)
    {
        try
        {
            if (suspend) await _startReplacement.SuspendAsync();
            else await _startReplacement.StopAsync();
            return true;
        }
        catch (Exception ex)
        {
            _startReplacementError = StartReplacementFailure(ex.Message);
            TileWall.Core.Shell.AppLog.Warn("StartReplacement", $"Stop incomplete: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ResumeStartReplacementAsync()
    {
        if (!CanActivateStartReplacement || !_window.Settings.ReplaceStartMenu) return false;
        try
        {
            await _startReplacement.PrepareAsync();
            if (!CanActivateStartReplacement || !_window.Settings.ReplaceStartMenu)
            {
                await StopStartReplacementAsync();
                return false;
            }
            await _startReplacement.ActivateAsync();
            _startReplacementSuspended = false;
            _startReplacementError = null;
            return true;
        }
        catch (Exception ex)
        {
            _startReplacementError = StartReplacementFailure(ex.Message);
            await StopStartReplacementAsync();
            return false;
        }
    }

    private async Task StopInputForBlockedPersistenceAsync()
    {
        await _inputTransition.WaitAsync();
        try
        {
            if (_tearingDown || !_window.SettingsPersistenceBlocked) return;
            _startReplacementSuspended = false;
            await StopStartReplacementAsync();
        }
        finally { _inputTransition.Release(); }
    }

    private async Task<SettingsDialogState> RetryStartReplacementAsync()
    {
        if (_settingsBusy) return CurrentSettingsState() with { Error = ProductStrings.Get("Wall.SettingsSaving") };
        _settingsBusy = true;
        await _inputTransition.WaitAsync();
        try
        {
            if (!CanActivateStartReplacement || !_window.Settings.ReplaceStartMenu) return CurrentSettingsState();
            if (!await StopStartReplacementAsync()) return CurrentSettingsState();
            await ResumeStartReplacementAsync();
            return CurrentSettingsState();
        }
        finally { _settingsBusy = false; _inputTransition.Release(); }
    }

    private async Task SetHotKeyRecordingAsync(bool recording)
    {
        await _inputTransition.WaitAsync();
        try
        {
            if (_hotKeyRecording == recording) return;
            if (recording)
            {
                if (_exitPending || _tearingDown) throw new OperationCanceledException("Application exit is pending.");
                _hotKeyRecording = true;
                _hotKey.Unregister();
                _startReplacementSuspended = !_isTest && _window.Settings.ReplaceStartMenu &&
                    _startReplacement.Fault is null && _startReplacementError is null;
                if (await StopStartReplacementAsync(suspend: true)) return;
                _hotKeyRecording = false;
                _startReplacementSuspended = false;
                RestoreCommittedHotKey();
                throw new InvalidOperationException(_startReplacementError);
            }
            _hotKeyRecording = false;
            // The recorder has already unhooked. Resume only the latest committed
            // preference; never restore a gesture captured before a settings commit.
            if (_exitPending || _tearingDown) return;
            var needResume = _startReplacementSuspended && _startReplacementError is null;
            _startReplacementSuspended = false;
            RestoreCommittedHotKey();
            if (needResume) await ResumeStartReplacementAsync();
        }
        finally { _inputTransition.Release(); }
    }

    private void RestoreCommittedHotKey()
    {
        if (_hotKeyRecording || _tearingDown) return;
        if (HotKeyGesture.TryParse(_window.Settings.HotKey, out var gesture) &&
            _hotKey.CurrentGesture != gesture && !_hotKey.TryRegister(gesture!))
            _window.ShowStatus(ProductStrings.Get("Wall.HotKeyUnavailable"));
    }

    private SettingsDialogState CurrentSettingsState() => new(_window.Settings,
        _hotKey.CurrentGesture?.ToConfigString(), _loginEnabled,
        StartReplacementActive: _startReplacement.IsActive,
        StartReplacementSuspended: _startReplacementSuspended,
        StartReplacementError: _startReplacementError ??
            (_window.Settings.ReplaceStartMenu && _startReplacement.Fault is { } fault
                ? StartReplacementFailure(fault) : null));

    private async Task<SettingsDialogState> ApplySettingsAsync(WallSettings proposed, bool restoreDefaults)
    {
        if (_window.PersistenceMutationBlocked || _settingsBusy || _hotKeyRecording || _exitPending || _tearingDown)
            return CurrentSettingsState() with { Error = ProductStrings.Get("Wall.SettingsSaving") };
        if (!HotKeyGesture.TryParse(proposed.HotKey, out var nextKey, out var error))
            return CurrentSettingsState() with { Error = error };
        _settingsBusy = true;
        await _inputTransition.WaitAsync();
        var old = _window.Settings;
        var oldGeneration = _window.SettingsGeneration;
        var oldKey = _hotKey.CurrentGesture;
        bool? oldLoginEnabled = null;
        var hotKeyChanged = oldKey != nextKey;
        var loginTouched = false;
        var replacementTouched = false;
        var commitAttempted = false;
        var committed = false;
        try
        {
            if (_exitPending || _tearingDown || _window.PersistenceMutationBlocked)
                return CurrentSettingsState() with { Error = ProductStrings.Get("Wall.SettingsSaving") };
            var oldLogin = await _login.ReadAsync();
            oldLoginEnabled = oldLogin.Enabled;
            var loginChanged = proposed.RunAtLogin != old.RunAtLogin ||
                restoreDefaults && proposed.RunAtLogin != (oldLogin.Enabled ?? old.RunAtLogin);
            if (proposed.ReplaceStartMenu != old.ReplaceStartMenu)
            {
                replacementTouched = true;
                if (proposed.ReplaceStartMenu && !_isTest)
                {
                    if (_window.SettingsPersistenceBlocked)
                        return await RejectAsync(ProductStrings.Get("Wall.SettingsBlocked"));
                    await _startReplacement.PrepareAsync();
                }
                else if (!await StopStartReplacementAsync())
                    return CurrentSettingsState() with { Error = _startReplacementError };
            }
            if (hotKeyChanged && !_hotKey.TryRegister(nextKey!))
                return await RejectAsync(_hotKey.LastFailureReason ?? ProductStrings.Get("Wall.HotKeyFailed"));
            if (loginChanged)
            {
                if (oldLogin.Enabled is null)
                    return await RejectAsync(oldLogin.Error ?? ProductStrings.Get("Wall.LoginReadFailed"));
                loginTouched = true;
                var result = await _login.SetAsync(proposed.RunAtLogin);
                _loginEnabled = result.Enabled;
                if (result.Error is not null || result.Enabled != proposed.RunAtLogin)
                    return await RejectAsync(result.Error ?? ProductStrings.Get("Wall.LoginFailed"));
            }
            if (_exitPending || _tearingDown)
                return await RejectAsync(ProductStrings.Get("Wall.SettingsSaving"));
            commitAttempted = true;
            try { error = _window.CommitSettings(proposed with { HotKey = nextKey!.ToConfigString() }); }
            catch (Exception ex) { error = string.Format(ProductStrings.Get("Wall.OperationFailed"), ex.Message); }
            committed = _window.SettingsGeneration != oldGeneration;
            if (!committed) return await RejectAsync(error ?? ProductStrings.Get("Wall.SettingsBlocked"));
            // The accepted snapshot remains authoritative even if view refresh or
            // post-commit UI work fails. Only known rejection permits rollback.
            if (_window.SettingsPersistenceBlocked)
                await StopStartReplacementAsync();
            else if (replacementTouched && proposed.ReplaceStartMenu && CanActivateStartReplacement)
            {
                try { await _startReplacement.ActivateAsync(); _startReplacementError = null; }
                catch (Exception ex)
                {
                    _startReplacementError = StartReplacementFailure(ex.Message);
                    await StopStartReplacementAsync();
                }
            }
            if (!proposed.ReplaceStartMenu) { _startReplacementError = null; _startReplacementSuspended = false; }
            ProductStrings.ApplyLanguage(proposed.LanguageMode);
            _window.RefreshAppearance();
            _window.RefreshProductStrings();
            UpdateTray();
            return CurrentSettingsState() with { Error = error };
        }
        catch (Exception ex)
        {
            committed |= _window.SettingsGeneration != oldGeneration;
            var failure = string.Format(ProductStrings.Get("Wall.OperationFailed"), ex.Message);
            return committed ? CurrentSettingsState() with { Error = failure } : await RejectAsync(failure);
        }
        finally { _settingsBusy = false; _inputTransition.Release(); }

        async Task<SettingsDialogState> RejectAsync(string failure)
        {
            if (commitAttempted && _window.SettingsPersistenceBlocked)
            {
                await StopStartReplacementAsync();
                return CurrentSettingsState() with { Error = failure };
            }
            if (replacementTouched) await StopStartReplacementAsync();
            if (hotKeyChanged)
            {
                if (oldKey is null) _hotKey.Unregister();
                else if (!_hotKey.TryRegister(oldKey))
                    failure += " " + string.Format(ProductStrings.Get("Wall.HotKeyRestoreFailed"), _hotKey.LastFailureReason);
            }
            if (loginTouched && oldLoginEnabled is { } enabled)
            {
                var restored = await _login.SetAsync(enabled);
                _loginEnabled = restored.Enabled;
                if (restored.Error is not null)
                    failure += " " + string.Format(ProductStrings.Get("Wall.LoginRestoreFailed"), restored.Error);
            }
            if (replacementTouched && old.ReplaceStartMenu && CanActivateStartReplacement &&
                !_startReplacement.HasRetainedResources)
                await ResumeStartReplacementAsync();
            UpdateTray();
            return CurrentSettingsState() with { Error = failure };
        }
    }

    private async Task<bool> StopInputForExitAsync()
    {
        await _inputTransition.WaitAsync();
        try
        {
            _startReplacementSuspended |= _startReplacement.IsActive;
            if (await StopStartReplacementAsync()) return true;
            _window.ShowStatus(_startReplacementError!);
            return false;
        }
        finally { _inputTransition.Release(); }
    }

    private async Task RestoreInputAfterCanceledExitAsync()
    {
        await _inputTransition.WaitAsync();
        try
        {
            if (_tearingDown || _hotKeyRecording) return;
            var needResume = _startReplacementSuspended && _startReplacementError is null;
            _startReplacementSuspended = false;
            RestoreCommittedHotKey();
            if (needResume) await ResumeStartReplacementAsync();
        }
        finally { _inputTransition.Release(); }
    }
}
