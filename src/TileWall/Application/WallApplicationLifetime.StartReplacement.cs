using TileWall.Core.Settings;
using TileWall.Core.Wall;
using TileWall.Resources;
using TileWall.Shell.Interop;
using TileWall.Views;

namespace TileWall.ApplicationModel;

internal sealed partial class WallApplicationLifetime
{
    // Fixed-channel input owners share settings/recording/shutdown admission.
    // A failed Stop retains its owner and the process instance gate.
    private readonly SemaphoreSlim _inputTransition = new(1, 1);
    private readonly StartReplacementService _startReplacement;
    private readonly StartReplacementService _taskbarStartReplacement;
    private readonly bool _isTest;
    private bool _startReplacementSuspended;
    private string? _startReplacementError;
    private bool _taskbarStartReplacementSuspended;
    private string? _taskbarStartReplacementError;

    private StartReplacementService StartInput(bool taskbar) => taskbar ? _taskbarStartReplacement : _startReplacement;
    private static bool StartInputEnabled(WallSettings settings, bool taskbar) => taskbar
        ? settings.RedirectTaskbarStartButton : settings.ReplaceStartMenu;
    private string? StartInputError(bool taskbar) => taskbar ? _taskbarStartReplacementError : _startReplacementError;
    private void SetStartInputError(bool taskbar, string? error)
    {
        if (taskbar) _taskbarStartReplacementError = error;
        else _startReplacementError = error;
    }
    private void SetStartInputSuspended(bool taskbar, bool suspended)
    {
        if (taskbar) _taskbarStartReplacementSuspended = suspended;
        else _startReplacementSuspended = suspended;
    }

    private void HandleGlobalHotKey(int slot)
    {
        if (_hotKeyRecording || _settingsBusy || !_started || _exitPending || _tearingDown) return;
        if (slot == 1 ? !_hotKey.IsRegistered : slot != 2 || !_secondaryHotKey.IsRegistered) return;
        if (_startReplacement.IsActive)
        {
            if (!NativeStartMenu.TryOpen(_host.Hwnd))
                _window.ShowStatus(ProductStrings.Get("Settings.StartReplacementUnavailable"));
            return;
        }
        Request(_show.State == TileWall.Core.Shell.WallShowState.Hidden || !_window.IsForegroundWallSession()
            ? TileWall.Core.Shell.WallShowTrigger.Show : TileWall.Core.Shell.WallShowTrigger.Toggle);
    }

    private void QueueStartReplacementToggle(long generation, bool taskbar = false) => _window.DispatcherQueue.TryEnqueue(() =>
    {
        if (_hotKeyRecording || _settingsBusy || !StartInputEnabled(_window.Settings, taskbar) ||
            !StartInput(taskbar).IsCurrentActivation(generation)) return;
        Request(TileWall.Core.Shell.WallShowTrigger.Toggle);
    });

    private void QueueStartReplacementFault(string failure, bool taskbar = false)
    {
        _window.DispatcherQueue.TryEnqueue(() => _ = AcceptStartReplacementFaultAsync(failure, taskbar));
    }

    private async Task AcceptStartReplacementFaultAsync(string failure, bool taskbar)
    {
        await _inputTransition.WaitAsync();
        try
        {
            var service = StartInput(taskbar);
            if (_tearingDown || !StartInputEnabled(_window.Settings, taskbar) || service.IsActive ||
                service.Fault != failure) return;
            SetStartInputError(taskbar, StartReplacementFailure(failure));
            await StopStartReplacementAsync(taskbar: taskbar);
            if (!_exitPending) _window.ShowStatus(StartInputError(taskbar)!);
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
            if (CanActivateStartReplacement)
                foreach (var taskbar in new[] { false, true })
                    if (StartInputEnabled(_window.Settings, taskbar)) await ResumeStartReplacementAsync(taskbar);
        }
        finally { _inputTransition.Release(); }
    }

    private bool CanActivateStartReplacement => !_isTest && !_tearingDown && !_exitPending &&
        !_hotKeyRecording && !_window.SettingsPersistenceBlocked;

    private static string StartReplacementFailure(string failure) =>
        string.Format(ProductStrings.Get("Settings.StartReplacementFailed"), failure);

    private async Task<bool> StopStartReplacementAsync(bool suspend = false, CancellationToken stopDeadline = default, bool taskbar = false)
    {
        try
        {
            if (suspend) await StartInput(taskbar).SuspendAsync(stopDeadline);
            else await StartInput(taskbar).StopAsync(stopDeadline);
            return true;
        }
        catch (Exception ex)
        {
            SetStartInputError(taskbar, StartReplacementFailure(ex.Message));
            TileWall.Core.Shell.AppLog.Warn("StartReplacement", $"{(taskbar ? "Taskbar" : "Win key")} stop incomplete: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> StopAllStartReplacementAsync(bool suspend = false, CancellationToken stopDeadline = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stopDeadline);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        // Start both stops before awaiting either: both admissions close immediately,
        // and native pairing/UIA cleanup consume the same remaining deadline.
        var results = await Task.WhenAll(StopStartReplacementAsync(suspend, budget.Token),
            StopStartReplacementAsync(suspend, budget.Token, taskbar: true));
        return results.All(result => result);
    }

    private async Task<bool> ResumeStartReplacementAsync(bool taskbar = false)
    {
        if (!CanActivateStartReplacement || !StartInputEnabled(_window.Settings, taskbar)) return false;
        var service = StartInput(taskbar);
        try
        {
            await service.PrepareAsync();
            if (!CanActivateStartReplacement || !StartInputEnabled(_window.Settings, taskbar))
            {
                await StopStartReplacementAsync(taskbar: taskbar);
                return false;
            }
            await service.ActivateAsync();
            SetStartInputSuspended(taskbar, false);
            SetStartInputError(taskbar, null);
            return true;
        }
        catch (Exception ex)
        {
            SetStartInputError(taskbar, StartReplacementFailure(ex.Message));
            await StopStartReplacementAsync(taskbar: taskbar);
            return false;
        }
    }

    private async Task StopInputForBlockedPersistenceAsync()
    {
        if (_tearingDown || !_window.SettingsPersistenceBlocked) return;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _startReplacement.CloseAdmissionForStop();
        _taskbarStartReplacement.CloseAdmissionForStop();
        var entered = false;
        try
        {
            await _inputTransition.WaitAsync(budget.Token);
            entered = true;
            if (_tearingDown) return;
            _startReplacementSuspended = _taskbarStartReplacementSuspended = false;
            var stopped = await StopAllStartReplacementAsync(stopDeadline: budget.Token);
            // A successful reopen can supersede this queued stop. Finish the real
            // stop before restoring only healthy channels from the current snapshot.
            if (stopped && CanActivateStartReplacement)
                foreach (var taskbar in new[] { false, true })
                    if (StartInputError(taskbar) is null && StartInputEnabled(_window.Settings, taskbar))
                        await ResumeStartReplacementAsync(taskbar);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            _startReplacementError = _taskbarStartReplacementError = StartReplacementFailure(
                "Input owner did not stop within the five-second budget; resources are retained.");
        }
        finally { if (entered) _inputTransition.Release(); }
    }

    private async Task<SettingsDialogState> RetryStartReplacementAsync(bool taskbar)
    {
        if (_settingsBusy) return CurrentSettingsState() with { Error = ProductStrings.Get("Wall.SettingsSaving") };
        _settingsBusy = true;
        using var stopBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await _inputTransition.WaitAsync(stopBudget.Token); }
        catch (OperationCanceledException) when (stopBudget.IsCancellationRequested)
        {
            _settingsBusy = false;
            SetStartInputError(taskbar, StartReplacementFailure("Input owner did not stop within the five-second budget; resources are retained."));
            return CurrentSettingsState();
        }
        try
        {
            if (!CanActivateStartReplacement) return CurrentSettingsState();
            if (StartInputEnabled(_window.Settings, taskbar) && !StartInput(taskbar).IsActive &&
                await StopStartReplacementAsync(stopDeadline: stopBudget.Token, taskbar: taskbar)) await ResumeStartReplacementAsync(taskbar);
            return CurrentSettingsState();
        }
        finally { _settingsBusy = false; _inputTransition.Release(); }
    }

    private async Task SetHotKeyRecordingAsync(int slot, bool recording)
    {
        if (slot is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(slot));
        var bit = 1 << (slot - 1);
        using var stopBudget = recording ? new CancellationTokenSource(TimeSpan.FromSeconds(5)) : null;
        try { await _inputTransition.WaitAsync(stopBudget?.Token ?? CancellationToken.None); }
        catch (OperationCanceledException) when (stopBudget?.IsCancellationRequested == true)
        {
            throw new InvalidOperationException(StartReplacementFailure("Input owner did not stop within the five-second budget; resources are retained."));
        }
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
                _taskbarStartReplacementSuspended = !_isTest && _window.Settings.RedirectTaskbarStartButton &&
                    _taskbarStartReplacement.Fault is null && _taskbarStartReplacementError is null;
                if (await StopAllStartReplacementAsync(suspend: true, stopDeadline: stopBudget!.Token)) return;
                _hotKeyRecordingSlots &= ~bit;
                RestorePausedHotKeys();
                await RestoreSuspendedStartInputsAsync();
                throw new InvalidOperationException(_startReplacementError ?? _taskbarStartReplacementError);
            }
            if ((_hotKeyRecordingSlots & bit) == 0) return;
            _hotKeyRecordingSlots &= ~bit;
            if (_hotKeyRecording) return;
            // The recorder has already unhooked. Resume only the latest committed
            // preference; never restore a gesture captured before a settings commit.
            if (_exitPending || _tearingDown) return;
            RestorePausedHotKeys();
            await RestoreSuspendedStartInputsAsync();
        }
        finally { _inputTransition.Release(); }
    }

    private async Task RestoreSuspendedStartInputsAsync()
    {
        foreach (var taskbar in new[] { false, true })
        {
            var suspended = taskbar ? _taskbarStartReplacementSuspended : _startReplacementSuspended;
            SetStartInputSuspended(taskbar, false);
            if (suspended && StartInputError(taskbar) is null && !StartInput(taskbar).HasRetainedResources)
                await ResumeStartReplacementAsync(taskbar);
        }
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
        TaskbarStartReplacementActive: _taskbarStartReplacement.IsActive,
        TaskbarStartReplacementSuspended: _taskbarStartReplacementSuspended,
        TaskbarStartReplacementError: _taskbarStartReplacementError ??
            (_window.Settings.RedirectTaskbarStartButton && _taskbarStartReplacement.Fault is { } taskbarFault
                ? StartReplacementFailure(taskbarFault) : null),
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
        using var replacementStopBudget = (!proposed.ReplaceStartMenu && _window.Settings.ReplaceStartMenu ||
            !proposed.RedirectTaskbarStartButton && _window.Settings.RedirectTaskbarStartButton)
            ? new CancellationTokenSource(TimeSpan.FromSeconds(5)) : null;
        try { await _inputTransition.WaitAsync(replacementStopBudget?.Token ?? CancellationToken.None); }
        catch (OperationCanceledException) when (replacementStopBudget?.IsCancellationRequested == true)
        {
            _settingsBusy = false;
            var failure = StartReplacementFailure("Input owner did not stop within the five-second budget; resources are retained.");
            foreach (var taskbar in new[] { false, true })
                if (!StartInputEnabled(proposed, taskbar) && StartInputEnabled(_window.Settings, taskbar))
                    SetStartInputError(taskbar, failure);
            TileWall.Core.Shell.AppLog.Warn("StartReplacement", "Stop incomplete: settings input transition timed out.");
            return CurrentSettingsState() with { Error = failure };
        }
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
        var replacementTouched = new bool[2];
        var replacementPrepared = new bool[2];
        var replacementStopAttempted = new bool[2];
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
            foreach (var taskbar in new[] { false, true })
            {
                if (StartInputEnabled(proposed, taskbar) == StartInputEnabled(old, taskbar)) continue;
                replacementTouched[taskbar ? 1 : 0] = true;
                if (StartInputEnabled(proposed, taskbar) && !_isTest)
                {
                    if (_window.SettingsPersistenceBlocked)
                        return await RejectAsync(ProductStrings.Get("Wall.SettingsBlocked"));
                    await StartInput(taskbar).PrepareAsync();
                    replacementPrepared[taskbar ? 1 : 0] = true;
                }
                else
                {
                    replacementStopAttempted[taskbar ? 1 : 0] = true;
                    if (!await StopStartReplacementAsync(stopDeadline: replacementStopBudget?.Token ?? CancellationToken.None, taskbar: taskbar))
                        return await RejectAsync(StartInputError(taskbar)!);
                }
            }
            var oldLogin = await _login.ReadAsync();
            oldLoginEnabled = oldLogin.Enabled;
            var loginChanged = proposed.RunAtLogin != displayedLoginEnabled ||
                restoreDefaults && proposed.RunAtLogin != (oldLogin.Enabled ?? old.RunAtLogin);
            if (!loginChanged) _loginEnabled = oldLogin.Enabled;
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
                await StopAllStartReplacementAsync(stopDeadline: replacementStopBudget?.Token ?? CancellationToken.None);
            else if (CanActivateStartReplacement)
            {
                foreach (var taskbar in new[] { false, true })
                {
                    if (!replacementTouched[taskbar ? 1 : 0] || !StartInputEnabled(proposed, taskbar)) continue;
                    try { await StartInput(taskbar).ActivateAsync(); SetStartInputError(taskbar, null); }
                    catch (Exception ex)
                    {
                        SetStartInputError(taskbar, StartReplacementFailure(ex.Message));
                        await StopStartReplacementAsync(taskbar: taskbar);
                    }
                }
            }
            if (!proposed.ReplaceStartMenu) { _startReplacementError = null; _startReplacementSuspended = false; }
            if (!proposed.RedirectTaskbarStartButton) { _taskbarStartReplacementError = null; _taskbarStartReplacementSuspended = false; }
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
                await StopAllStartReplacementAsync(stopDeadline: replacementStopBudget?.Token ?? CancellationToken.None);
                return CurrentSettingsState() with { Error = failure };
            }
            using (var rollbackStopBudget = CancellationTokenSource.CreateLinkedTokenSource(replacementStopBudget?.Token ?? CancellationToken.None))
            {
                rollbackStopBudget.CancelAfter(TimeSpan.FromSeconds(5));
                await Task.WhenAll(new[] { false, true }.Where(taskbar => replacementPrepared[taskbar ? 1 : 0] &&
                        !replacementStopAttempted[taskbar ? 1 : 0])
                    .Select(taskbar => StopStartReplacementAsync(stopDeadline: rollbackStopBudget.Token, taskbar: taskbar)));
            }
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
            if (CanActivateStartReplacement)
                foreach (var taskbar in new[] { false, true })
                    if (replacementTouched[taskbar ? 1 : 0] && StartInputEnabled(old, taskbar) &&
                        !StartInput(taskbar).HasRetainedResources)
                        await ResumeStartReplacementAsync(taskbar);
            UpdateTray();
            return CurrentSettingsState() with { Error = failure };
        }
    }

    private async Task<bool> StopInputForExitAsync()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _startReplacementSuspended |= _startReplacement.IsActive;
        _taskbarStartReplacementSuspended |= _taskbarStartReplacement.IsActive;
        _startReplacement.CloseAdmissionForStop();
        _taskbarStartReplacement.CloseAdmissionForStop();
        var entered = false;
        try
        {
            // Include the lifetime owner in the same budget passed into the native service.
            await _inputTransition.WaitAsync(budget.Token);
            entered = true;
            if (await StopAllStartReplacementAsync(stopDeadline: budget.Token)) return true;
            _window.ShowStatus((_startReplacementError ?? _taskbarStartReplacementError)!);
            return false;
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            _startReplacementError = StartReplacementFailure("Input owner did not stop within the five-second budget; resources are retained.");
            TileWall.Core.Shell.AppLog.Warn("StartReplacement", "Stop incomplete: input owner transition timed out.");
            _window.ShowStatus(_startReplacementError);
            return false;
        }
        finally { if (entered) _inputTransition.Release(); }
    }

    private async Task RestoreInputAfterCanceledExitAsync()
    {
        // A failed stop returns to the application immediately. Do not append another owner wait in exit's finally,
        // or restore registrations while an older hook/worker is still retained or its failure is unresolved.
        if (_startReplacementError is not null || _taskbarStartReplacementError is not null ||
            _startReplacement.HasRetainedResources && !_startReplacement.IsActive ||
            _taskbarStartReplacement.HasRetainedResources && !_taskbarStartReplacement.IsActive)
        {
            // Never add a second wait for a failed stop. A different channel can
            // still restore its own fully released owner without touching held
            // input or registrations belonging to the failed channel.
            if (!_inputTransition.Wait(0))
            {
                _startReplacementSuspended = _taskbarStartReplacementSuspended = false;
                return;
            }
            try
            {
                if (!_tearingDown && !_hotKeyRecording) await RestoreSuspendedStartInputsAsync();
            }
            finally { _inputTransition.Release(); }
            return;
        }
        if (!await _inputTransition.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            _startReplacementError = StartReplacementFailure("Input owner is still busy; input restoration is incomplete.");
            TileWall.Core.Shell.AppLog.Warn("StartReplacement", "Canceled-exit input restoration timed out; registrations were retained.");
            _window.ShowStatus(_startReplacementError);
            return;
        }
        try
        {
            if (_tearingDown || _hotKeyRecording) return;
            RestorePausedHotKeys();
            await RestoreSuspendedStartInputsAsync();
        }
        finally { _inputTransition.Release(); }
    }
}
