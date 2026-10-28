using Microsoft.UI.Xaml;
using TileWall.Core.Settings;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Resources;
using TileWall.Shell;
using TileWall.Shell.Interop;
using TileWall.Views;

namespace TileWall.ApplicationModel;

/// <summary>The single owner of process resources, visibility decisions and settings side effects.</summary>
internal sealed partial class WallApplicationLifetime : IWallShowHost, IExitHost
{
    private readonly MainWindow _window;
    private readonly ShellMessageHost _host;
    private readonly SingleInstanceGate _instance;
    private readonly ShellNotifyIconHost _tray;
    private readonly ExplorerAddNotification _notifications;
    private readonly GlobalHotKeyRegistrar _hotKey;
    private readonly GlobalHotKeyRegistrar _secondaryHotKey;
    private readonly LoginStartupController _login;
    private readonly Microsoft.UI.Xaml.Application _application;
    private readonly WallShowMachine _show;
    private readonly ShowHideAnimator _animation;
    private readonly ExitCoordinator _exit;
    private readonly Queue<ExplorerAddMessage> _pendingAdds = new();
    private int _queuedPaths;
    private bool? _loginEnabled;
    private bool _settingsBusy;
    private int _hotKeyRecordingSlots;
    private int _pausedHotKeySlots;
    private bool _hotKeyRecording => _hotKeyRecordingSlots != 0;
    private string? _hotKeyError, _secondaryHotKeyError;
    private bool _exitPending;
    private bool _tearingDown;
    private bool _started;
    private Task? _startup;
    private bool _showWhenStarted;
    private bool _drainQueued;
    private Task? _addDrain;
    private CancellationTokenSource? _addPreparation;
    private bool _inAddSequence;
    private int _addSucceeded, _addFailed, _addCount;
    private string? _lastAddFailure;
    private bool _addTruncated;

    public WallApplicationLifetime(RuntimeScope scope, SingleInstanceGate instance,
        Microsoft.UI.Xaml.Application application)
    {
        _instance = instance;
        _application = application;
        _host = new ShellMessageHost(scope.ShellWindowName);
        _host.ActivateRequested += () => Request(WallShowTrigger.Show);
        _hotKey = new GlobalHotKeyRegistrar(_host, 1);
        _secondaryHotKey = new GlobalHotKeyRegistrar(_host, 2);
        _tray = new ShellNotifyIconHost(_host, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), scope.IsTest);
        _login = new LoginStartupController(scope);
        _window = new MainWindow(scope);
        _isTest = scope.IsTest;
        _startReplacement = new StartReplacementService(QueueStartReplacementToggle);
        _startReplacement.Faulted += QueueStartReplacementFault;
        _show = new WallShowMachine(WallShowState.Hidden, this);
        _animation = new ShowHideAnimator(_window.WallContent);
        _animation.ShowCompleted += _show.ShowAnimationCompleted;
        _animation.HideCompleted += _show.HideAnimationCompleted;
        _exit = new ExitCoordinator(this);
        _window.HideRequested += () => Request(WallShowTrigger.Hide);
        _window.AutomaticHideRequested += () =>
        {
            if (!_window.Settings.StayVisibleOnFocusLoss) Request(WallShowTrigger.Hide);
        };
        _window.SettingsRequested += OpenSettings;
        _window.DialogClosed += QueueAddDrain;
        _window.ImportReadinessChanged += () =>
        {
            if (_window.CanProcessExternalImport) QueueAddDrain();
            if (_started && _window.SettingsPersistenceBlocked) _ = StopInputForBlockedPersistenceAsync();
        };
        _window.DialogOpened += () => _show.Request(WallShowTrigger.Show);
        _window.AppWindow.Closing += (sender, e) =>
        {
            if (_tearingDown) return;
            e.Cancel = true;
            RequestExit();
        };
        _tray.ShowOrFocusRequested += () => Request(WallShowTrigger.Show);
        _tray.SettingsRequested += OpenSettings;
        _tray.ExitRequested += RequestExit;
        _host.HotKeyPressed += HandleGlobalHotKey;
        _host.DisplayChanged += () =>
        {
            _startReplacement.InvalidateButtons();
            _window.QueueEnvironmentRefresh();
        };
        _host.AcceptPaths = ReceiveAdd;
        _hotKey.RegistrationChanged += UpdateTray;
        _secondaryHotKey.RegistrationChanged += UpdateTray;
        _notifications = new ExplorerAddNotification(scope.IsTest, () =>
            _window.DispatcherQueue.TryEnqueue(() => Request(WallShowTrigger.Show)));
    }

    public Task StartAsync(StartupIntent intent) => _startup ??= StartCoreAsync(intent);

    private async Task StartCoreAsync(StartupIntent intent)
    {
        try
        {
            _notifications.Register(intent.NotificationActivation);
            // No Activate/Show is needed to open the committed session or register process resources.
            await _window.InitializeSessionAsync();
            if (_exitPending || _tearingDown) return;
            _window.ScheduleStartMenuStartupRefresh();
            ProductStrings.ApplyLanguage(_window.Settings.LanguageMode);
            _window.RefreshAppearance();
            _window.RefreshProductStrings();
            RestoreCommittedHotKeys();
            var login = await _login.ReadAsync();
            _loginEnabled = login.Enabled;
            await InitializeStartReplacementAsync();
            _started = true;
            if (_exitPending || _tearingDown) return;
            QueueAddDrain();
            if (intent.Add is { } add) ReceiveAdd(add);
            // External adds use the same background lifetime as login startup;
            // only an explicit show request may bring that cold start forward.
            if (_showWhenStarted || !intent.Background && intent.Add is null) Request(WallShowTrigger.Show);
            if (!_hotKey.IsRegistered && !_secondaryHotKey.IsRegistered) _window.ShowStatus(ProductStrings.Get("Wall.HotKeyUnavailable"));
            if (login.Error is not null) _window.ShowStatus(login.Error);
            if (_startReplacementError is not null) _window.ShowStatus(_startReplacementError);
            UpdateTray();
        }
        catch (OperationCanceledException) when (_exitPending) { }
        catch (Exception ex)
        {
            AppLog.Error("Startup", ex.ToString());
            // Startup may already own image/cache/storage work. Use the same
            // queued, asynchronous drain as every other exit route.
            RequestExit();
        }
    }

    private void Request(WallShowTrigger trigger)
    {
        if (_tearingDown || _exitPending) return;
        if (!_started) { if (trigger != WallShowTrigger.Hide) _showWhenStarted = true; return; }
        if (_window.ModalActive)
        {
            if (trigger != WallShowTrigger.Hide) _window.FocusTopSession();
            return;
        }
        _show.Request(trigger);
    }

    private void OpenSettings()
    {
        if (!_started || _tearingDown || _exitPending || _window.PersistenceBusy) return;
        if (_window.ModalActive) { _window.FocusTopSession(); return; }
        Request(WallShowTrigger.Show);
        _ = _window.OpenSettingsAsync(CurrentSettingsState, ApplySettingsAsync,
            SetHotKeyRecordingAsync, RetryStartReplacementAsync);
    }

    private bool ReceiveAdd(ExplorerAddMessage message)
    {
        if (_tearingDown || _exitPending) return false;
        if (_pendingAdds.Count + (_addPreparation is null ? 0 : 1) >= 64 || _queuedPaths + message.Paths.Count > 4096)
        {
            _window.DispatcherQueue.TryEnqueue(() =>
            {
                if (_tearingDown || _exitPending) return;
                _window.ShowStatus(ProductStrings.Get("Wall.QueueFull"));
                AppLog.Warn("ExternalAdd", "Add queue rejected an oversized/full batch.");
            });
            return false;
        }
        _pendingAdds.Enqueue(message);
        _queuedPaths += message.Paths.Count;
        QueueAddDrain();
        return true;
    }

    private void QueueAddDrain()
    {
        if (_drainQueued || _addDrain is { IsCompleted: false } || _pendingAdds.Count == 0 || _tearingDown || _exitPending) return;
        _drainQueued = _window.DispatcherQueue.TryEnqueue(DrainNextAdd);
    }

    private void DrainNextAdd()
    {
        _drainQueued = false;
        if (_addDrain is { IsCompleted: false }) return;
        var operation = DrainNextAddAsync();
        _addDrain = operation.IsCompleted ? null : operation;
    }

    private async Task DrainNextAddAsync()
    {
        if (!_started || _tearingDown || _exitPending || _pendingAdds.Count == 0) return;
        if (!_window.CanProcessExternalImport)
        {
            _window.ShowStatus($"{ProductStrings.Get("Wall.PendingAdd")} ({_pendingAdds.Count})");
            return;
        }
        var message = _pendingAdds.Dequeue();
        if (!_inAddSequence)
        {
            _addSucceeded = _addFailed = _addCount = 0;
            _lastAddFailure = null;
            _addTruncated = false;
        }
        using var preparation = new CancellationTokenSource();
        _addPreparation = preparation;
        ExternalImportResult result;
        try { result = await _window.ImportExternalBatchAsync(message, preparation.Token); }
        catch (OperationCanceledException) when (preparation.IsCancellationRequested)
        {
            _inAddSequence = false;
            return;
        }
        catch (Exception ex)
        {
            AppLog.Warn("ExternalAdd", ex.ToString());
            result = new(string.Format(ProductStrings.Get("Import.Failed"), ex.Message), false, 0);
        }
        finally
        {
            _queuedPaths -= message.Paths.Count;
            if (ReferenceEquals(_addPreparation, preparation)) _addPreparation = null;
            _addDrain = null;
        }
        if (result.Succeeded) { _addSucceeded++; _addCount += result.AddedCount; }
        else { _addFailed++; _lastAddFailure = result.Message; }
        _addTruncated |= message.Truncated;
        AppLog.Info("ExternalAdd", result.Message);
        var resultMessage = string.Format(ProductStrings.Get("Import.QueueResult"), _addSucceeded, _addFailed, _addCount)
            + (_lastAddFailure is null ? string.Empty : " " + _lastAddFailure)
            + (_addTruncated ? " " + ProductStrings.Get("Import.Truncated") : string.Empty);
        if (_addFailed == 0 && !_addTruncated) _window.ShowTransientStatus(resultMessage);
        else _window.ShowStatus(resultMessage);
        _inAddSequence = _pendingAdds.Count != 0;
        // ImportExternalBatchAsync returns a positive count only after the formal
        // commit succeeds. Notification delivery is outside that transaction.
        if (!_inAddSequence && !_exitPending && !_tearingDown && _addCount > 0)
            _notifications.ShowAdded(_addCount, _addFailed != 0 || _addTruncated);
        QueueAddDrain();
    }
    private void UpdateTray()
    {
        var available = new[] { _hotKey.CurrentGesture, _secondaryHotKey.CurrentGesture }
            .OfType<HotKeyGesture>().Select(gesture => gesture.ToDisplayString());
        var combinations = string.Join(" / ", available);
        _tray.UpdateTooltip($"TileWall · {(combinations.Length != 0 ? combinations : ProductStrings.Get("Wall.HotKeyUnavailable"))}");
    }

    private void RequestExit()
    {
        if (_exitPending || _tearingDown) return;
        _exitPending = true;
        _window.CancelWelcomeInitialization();
        _addPreparation?.Cancel();
        // Tray commands and AppWindow.Closing arrive inside native callbacks. Let
        // their menus/closing frames unwind before destroying any HWND or XAML state.
        if (!_window.DispatcherQueue.TryEnqueue(() => _ = RequestExitAsync()))
        {
            _exitPending = false;
            AppLog.Warn("Exit", "Exit request could not be queued because the UI dispatcher is shutting down.");
        }
    }

    private async Task RequestExitAsync()
    {
        try
        {
            // In packaged runs the login-state read can yield during startup.
            // Its continuation must finish before any owned resource is stopped.
            if (_startup is { } startup) await startup;
            if (_addDrain is { } adding) await adding;
            var titleError = _window.FinishActiveTitleEditForExit();
            if (titleError is not null)
            {
                _window.TraceExit("title-edit-rejected");
                _window.ShowStatus(titleError);
                return;
            }
            // Blur can commit a NumberBox's pending text or start a final settings
            // apply. Settle that while writes are still admitted, then freeze.
            var session = _window.ActiveDialog;
            if (session is not null) await session.WaitForPendingAsync();
            _window.TraceExit("admission-close");
            _window.SuspendPersistenceAdmission();
            await _window.DrainPersistenceAsync();
            session = _window.ActiveDialog;
            _window.TraceExit($"dialog-wait type={session?.GetType().Name ?? "none"}");
            if (session is not null) await session.WaitForPendingAsync();
            session = _window.ActiveDialog;
            var modalClosed = _window.WaitForModalClosedAsync();
            if (_exit.ShouldConfirmDraft(session))
            {
                _window.FocusTopSession();
                if (!_window.ApplyExitDecision(_exit, session!, await session!.ConfirmExitAsync())) return;
            }
            else _exit.CloseDraftlessSession();
            // ContentDialog.Hide starts an asynchronous close. Its ShowAsync/finally
            // must release the owned host before Application.Exit destroys XAML;
            // otherwise queued layout callbacks can still read a closed AppWindow.
            await modalClosed;
            _window.TraceExit("dialog-closed");
            if (!await StopInputForExitAsync()) return;
            _window.TraceExit("shutdown-workers");
            try { await _window.ShutdownPersistenceAsync(); }
            catch (Exception ex) { AppLog.Warn("Exit", $"Storage worker shutdown: {ex.Message}"); }
            _tearingDown = true;
            _window.TraceExit("teardown");
            _exit.RunTeardown();
        }
        catch (Exception ex) { _window.ShowStatus(string.Format(ProductStrings.Get("Wall.OperationFailed"), ex.Message)); }
        finally
        {
            if (!_tearingDown) _window.ResumePersistenceAdmission();
            _exitPending = false;
            if (!_tearingDown) await RestoreInputAfterCanceledExitAsync();
            QueueAddDrain();
        }
    }

    public void BeginShowAnimation() => _animation.BeginShow();
    public void BeginHideAnimation() => _animation.BeginHide();
    public void CancelAnimations() => _animation.Cancel();
    public void SnapShow() { _animation.ResetVisual(); _window.ShowWall(); }
    public void SnapHide() { _animation.ResetVisual(); _window.HideWall(); }
    public void FocusWall() => _window.FocusTopSession();
    public IExitParticipant? TopParticipant => _window.ActiveDialog;
    public void CloseTopSession() => _window.ActiveDialog?.Close();
    public void CancelTransientState() => _window.CancelTransientState();
    public void SnapHideWall() => SnapHide();
    public void UnregisterHotKey() { _hotKey.Dispose(); _secondaryHotKey.Dispose(); }
    public void RemoveTrayIcon() => _tray.Dispose();
    public void DestroyShellHostWindow()
    {
        _notifications.Dispose();
        _host.Dispose();
    }
    public void ReleaseSingleInstanceMutex()
    {
        // The next owner must never acquire the gate while this process still
        // owns the rolling file handle.
        try { AppLog.Shutdown(); }
        finally { _instance.Dispose(); }
    }
    // Application.Exit closes the XAML windows and raises their Closed cleanup
    // handlers. Closing the last window first would begin shutdown twice.
    public void ExitApplication()
    {
        try { AppLog.Shutdown(); }
        finally { _application.Exit(); }
    }
}
