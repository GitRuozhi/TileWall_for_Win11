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

    private void HandleGlobalHotKey(int slot)
    {
        if (_hotKeyRecording || _settingsBusy || !_started || _exitPending || _tearingDown) return;
        if (slot == 1 ? !_hotKey.IsRegistered : slot != 2 || !_secondaryHotKey.IsRegistered) return;
        Request(_show.State == TileWall.Core.Shell.WallShowState.Hidden || !_window.IsForegroundWallSession()
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

    private async Task SetHotKeyRecordingAsync(int slot, bool recording)
    {
        if (slot is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(slot));
        var bit = 1 << (slot - 1);
        await _inputTransition.WaitAsync();
        try
        {
            if (recording)
            {
                if ((_hotKeyRecordingSlots & bit) != 0) return;
                if (_exitPending || _tearingDown) throw new OperationCanceledException("Application exit is pending.");
                var first = !_hotKeyRecording;
                _hotKeyRecordingSlots |= bit;
                if (!first) return;
                _pausedHotKeySlots = (_hotKey.IsRegistered ? 1 : 0) | (_secondaryHotKey.IsRegistered ? 2 : 0);
                _hotKey.Unregister();
                _secondaryHotKey.Unregister();
                _startReplacementSuspended = !_isTest && _window.Settings.ReplaceStartMenu &&
                    _startReplacement.Fault is null && _startReplacementError is null;
                if (await StopStartReplacementAsync(suspend: true)) return;
                _hotKeyRecordingSlots &= ~bit;
                _startReplacementSuspended = false;
                RestorePausedHotKeys();
                throw new InvalidOperationException(_startReplacementError);
            }
            if ((_hotKeyRecordingSlots & bit) == 0) return;
            _hotKeyRecordingSlots &= ~bit;
            if (_hotKeyRecording) return;
            // The recorder has already unhooked. Resume only the latest committed
            // preference; never restore a gesture captured before a settings commit.
            if (_exitPending || _tearingDown) return;
            var needResume = _startReplacementSuspended && _startReplacementError is null;
            _startReplacementSuspended = false;
            RestorePausedHotKeys();
            if (needResume) await ResumeStartReplacementAsync();
        }
        finally { _inputTransition.Release(); }
    }

    private void RestorePausedHotKeys()
    {
        if (_hotKeyRecording || _tearingDown) return;
        RestoreCommittedHotKeys(_pausedHotKeySlots);
        _pausedHotKeySlots = 0;
    }

    private void RestoreCommittedHotKeys(int slots = 3)
    {
        if (_hotKeyRecording || _tearingDown) return;
        var primaryValid = HotKeyGesture.TryParse(_window.Settings.HotKey, out var primary, out var primaryError);
        var secondaryValid = HotKeyGesture.TryParse(_window.Settings.SecondaryHotKey, out var secondary, out var secondaryError);
        if ((slots & 1) != 0)
        {
            _hotKeyError = primaryError;
            if (primaryValid)
            {
                if (_hotKey.CurrentGesture != primary && !_hotKey.TryRegister(primary!)) _hotKeyError = _hotKey.LastFailureReason;
            }
            else _hotKey.Unregister();
        }
        // A V1 primary can equal the newly introduced secondary default. Keep
        // that primary, diagnose the second slot, and never register it twice.
        if (primaryValid && secondaryValid && primary == secondary)
        {
            _secondaryHotKey.Unregister();
            _secondaryHotKeyError = ProductStrings.Get("Settings.HotKeyDuplicate");
        }
        else if ((slots & 2) != 0)
        {
            _secondaryHotKeyError = secondaryError;
            if (secondaryValid)
            {
                if (_secondaryHotKey.CurrentGesture != secondary && !_secondaryHotKey.TryRegister(secondary!))
                    _secondaryHotKeyError = _secondaryHotKey.LastFailureReason;
            }
            else _secondaryHotKey.Unregister();
        }
        UpdateTray();
    }

    private SettingsDialogState CurrentSettingsState() => new(_window.Settings with
        { RunAtLogin = _loginEnabled ?? _window.Settings.RunAtLogin },
        _hotKey.CurrentGesture?.ToConfigString(), _loginEnabled,
        StartReplacementActive: _startReplacement.IsActive,
        StartReplacementSuspended: _startReplacementSuspended,
        StartReplacementError: _startReplacementError ??
            (_window.Settings.ReplaceStartMenu && _startReplacement.Fault is { } fault
                ? StartReplacementFailure(fault) : null),
        RegisteredSecondaryHotKey: _secondaryHotKey.CurrentGesture?.ToConfigString(),
        HotKeyError: _hotKeyError, SecondaryHotKeyError: _secondaryHotKeyError);

    private async Task<SettingsDialogState> ApplySettingsAsync(WallSettings proposed, bool restoreDefaults)
    {
        if (_window.PersistenceMutationBlocked || _settingsBusy || _hotKeyRecording || _exitPending || _tearingDown)
            return CurrentSettingsState() with { Error = ProductStrings.Get("Wall.SettingsSaving") };
        if (!HotKeyGesture.TryParse(proposed.HotKey, out var nextKey, out var error))
            return CurrentSettingsState() with { Error = error, HotKeyError = error };
        if (!HotKeyGesture.TryParse(proposed.SecondaryHotKey, out var nextSecondaryKey, out error))
            return CurrentSettingsState() with { Error = error, SecondaryHotKeyError = error };
        // The dialog edits the last displayed OS state, which can differ from
        // the saved preference after uninstall or an external startup change.
        var displayedLoginEnabled = _loginEnabled ?? _window.Settings.RunAtLogin;
        _settingsBusy = true;
        await _inputTransition.WaitAsync();
        var old = _window.Settings;
        var oldGeneration = _window.SettingsGeneration;
        var oldRegisteredKey = _hotKey.CurrentGesture;
        var oldRegisteredSecondaryKey = _secondaryHotKey.CurrentGesture;
        var oldHotKeyError = _hotKeyError;
        var oldSecondaryHotKeyError = _secondaryHotKeyError;
        bool? oldLoginEnabled = null;
        // Registration can be unavailable while the committed preference is valid.
        // Unrelated settings must not retry that unchanged, possibly occupied key.
        var hotKeyChanged = !HotKeyGesture.TryParse(old.HotKey, out var oldConfiguredKey) || oldConfiguredKey != nextKey;
        var secondaryHotKeyChanged = !HotKeyGesture.TryParse(old.SecondaryHotKey, out var oldConfiguredSecondaryKey) || oldConfiguredSecondaryKey != nextSecondaryKey;
        var rejectDuplicate = nextKey == nextSecondaryKey && (hotKeyChanged || secondaryHotKeyChanged);
        // Changing a grandfathered duplicate primary frees the previously
        // suppressed secondary. This is an internal dependency, not a retry of
        // an unrelated unchanged native registration conflict.
        var restoreSuppressedSecondary = hotKeyChanged && oldConfiguredKey is not null &&
            oldConfiguredKey == oldConfiguredSecondaryKey && oldRegisteredSecondaryKey is null;
        var hotKeyTouched = false;
        var secondaryHotKeyTouched = false;
        var loginTouched = false;
        var replacementTouched = false;
        var commitAttempted = false;
        var committed = false;
        try
        {
            if (_exitPending || _tearingDown || _window.PersistenceMutationBlocked)
                return CurrentSettingsState() with { Error = ProductStrings.Get("Wall.SettingsSaving") };
            if (rejectDuplicate)
            {
                var duplicate = ProductStrings.Get("Settings.HotKeyDuplicate");
                return CurrentSettingsState() with { Error = duplicate, SecondaryHotKeyError = duplicate };
            }
            var oldLogin = await _login.ReadAsync();
            oldLoginEnabled = oldLogin.Enabled;
            var loginChanged = proposed.RunAtLogin != displayedLoginEnabled ||
                restoreDefaults && proposed.RunAtLogin != (oldLogin.Enabled ?? old.RunAtLogin);
            if (!loginChanged) _loginEnabled = oldLogin.Enabled;
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
            if (hotKeyChanged)
            {
                hotKeyTouched = true;
                _hotKey.Unregister();
            }
            if (secondaryHotKeyChanged || restoreSuppressedSecondary)
            {
                secondaryHotKeyTouched = true;
                _secondaryHotKey.Unregister();
            }
            // Release all changed slots before registering either replacement,
            // so swapping two saved combinations does not conflict with itself.
            if (hotKeyTouched)
            {
                var registered = _hotKey.TryRegister(nextKey!);
                _hotKeyError = registered ? null : _hotKey.LastFailureReason ?? ProductStrings.Get("Wall.HotKeyFailed");
                if (!registered) return await RejectAsync(_hotKeyError!);
            }
            if (secondaryHotKeyTouched)
            {
                var registered = _secondaryHotKey.TryRegister(nextSecondaryKey!);
                _secondaryHotKeyError = registered ? null : _secondaryHotKey.LastFailureReason ?? ProductStrings.Get("Wall.HotKeyFailed");
                // An unchanged slot freed from a grandfathered duplicate is a
                // best-effort dependency restore. Its native conflict must not
                // block the independently changed primary from being saved.
                if (!registered && secondaryHotKeyChanged) return await RejectAsync(_secondaryHotKeyError!);
            }
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
            try { error = _window.CommitSettings(proposed with
                { HotKey = nextKey!.ToConfigString(), SecondaryHotKey = nextSecondaryKey!.ToConfigString() }); }
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
            // Clear both touched replacements first, including a successful
            // half of a rejected swap, before restoring their original owners.
            if (hotKeyTouched) _hotKey.Unregister();
            if (secondaryHotKeyTouched) _secondaryHotKey.Unregister();
            _hotKeyError = oldHotKeyError;
            _secondaryHotKeyError = oldSecondaryHotKeyError;
            if (hotKeyTouched)
            {
                if (oldRegisteredKey is not null && !_hotKey.TryRegister(oldRegisteredKey))
                {
                    _hotKeyError = _hotKey.LastFailureReason;
                    failure += " " + string.Format(ProductStrings.Get("Wall.HotKeyRestoreFailed"), _hotKeyError);
                }
            }
            if (secondaryHotKeyTouched)
            {
                if (oldRegisteredSecondaryKey is not null && !_secondaryHotKey.TryRegister(oldRegisteredSecondaryKey))
                {
                    _secondaryHotKeyError = _secondaryHotKey.LastFailureReason;
                    failure += " " + string.Format(ProductStrings.Get("Wall.HotKeyRestoreFailed"), _secondaryHotKeyError);
                }
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
            RestorePausedHotKeys();
            if (needResume) await ResumeStartReplacementAsync();
        }
        finally { _inputTransition.Release(); }
    }
}
