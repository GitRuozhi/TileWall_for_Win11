using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TileWall.Core.Wall;
using TileWall.Core.Shell;
using TileWall.Core.Entries;
using TileWall.Core.Storage;
using TileWall.Resources;
using TileWall.Graphics;
using TileWall.Shell.Interop;
using TileWall.Views;
using Windows.Foundation;
using Windows.Graphics;

namespace TileWall;

public sealed partial class MainWindow
{
    private int _frameWidth;
    private int _frameHeight;
    private bool _environmentQueued;
    private bool _displayObservationRequested;
    private bool _needsVisualRefresh;
    private bool _focusCheckQueued, _contextMenuOpen;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _focusCheckRetry;
    private double _lastEnvironmentScale;
    private WorkAreaRect _lastEnvironmentWork;
    private WorkAreaRect _lastEnvironmentScreen;
    private OwnedDialogWindow? _dialogHost;
    private TaskCompletionSource? _modalClosed;
    private Window DialogOwner => _dialogHost ?? throw new InvalidOperationException("No modal host is active.");

    internal FrameworkElement WallContent => WallRoot;
    internal bool ModalActive => _modal;
    internal IWallDialogSession? ActiveDialog { get; private set; }
    internal WallSettings Settings => _hasDocument ? Document.Settings : _openingDocument?.Settings ?? new();
    internal long SettingsGeneration => _hasDocument ? Document.Generation : -1;
    internal bool SettingsPersistenceBlocked => !_hasDocument || _session.ResultUnknown ||
        _session.RequiresReopen || _session.RecoveryBlocked;
    internal event Action? HideRequested;
    internal event Action? AutomaticHideRequested;
    internal event Action? SettingsRequested;
    internal event Action? DialogClosed;
    internal event Action? DialogOpened;

    internal async Task InitializeSessionAsync(bool showUpgradeProgress = false)
    {
        await OpenWallAsync(showUpgradeProgress);
        if (!_welcomeInitializationPending) return;
        var error = await InitializeWelcomeWallAsync(fresh: true);
        if (error is not null && !_viewBlocked) Status(error);
    }
    internal void ShowStatus(string message) => PersistentStatus(message);
    internal void ShowTransientStatus(string message) => TransientStatus(message);

    internal void ShowWall()
    {
        RefreshEnvironment(force: true);
        RefreshAppearance();
        CaptureBackdropBeforeShow();
        AppWindow.Show();
        _environmentWallShown = true;
        QueueEnvironmentAdaptation(observeDisplay: true);
        QueueExpansionReveal();
        FocusTopSession();
        UpdatePuzzleGate();
    }

    internal void HideWall()
    {
        PauseEnvironmentForHide();
        ClearTransientStatus();
        _focusCheckRetry?.Stop();
        CancelTransientState();
        AppWindow.Hide();
        UpdateWindowZOrder(foregroundInSession: false);
        ReleaseBackdropSnapshot();
        Surface.ClearTransientInteraction();
        _needsVisualRefresh = true;
        UpdatePuzzleGate();
    }

    private void OnContextMenuVisibilityChanged(bool open)
    {
        _contextMenuOpen = open;
        AppLog.Info("WindowFocus", $"Context menu open={open}");
        if (!open) QueueAutomaticHideCheck("ContextMenuClosed");
    }

    private void QueueAutomaticHideCheck(string reason, bool retryUnknown = true)
    {
        if (_focusCheckQueued || _windowClosing) return;
        _focusCheckQueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _focusCheckQueued = false;
            if (_windowClosing || !AppWindow.IsVisible) return;
            // XAML popup HWND activation can precede its MenuFlyoutItem.Click route.
            // Let that route finish, then decide from the actual foreground session.
            var foreground = GetForegroundWindow();
            var thread = GetWindowThreadProcessId(foreground, out var processId);
            if (BackdropTrace.Enabled)
                BackdropTrace.Write($"FocusCheck reason={reason} foreground={foreground} pid={processId} thread={thread} retryUnknown={retryUnknown} modal={_modal} menu={_contextMenuOpen}");
            if (foreground == IntPtr.Zero || thread == 0)
            {
                AppLog.Info("WindowFocus", $"Automatic hide deferred: foreground transition reason={reason} retry={retryUnknown}");
                if (!retryUnknown)
                {
                    // Unknown foreground preserves the prepared background;
                    // it never starts a capture or changes screenshot policy.
                    RefreshAppearance();
                    UpdateWindowZOrder(foregroundInSession: false);
                    return;
                }
                if (_focusCheckRetry is null)
                {
                    _focusCheckRetry = DispatcherQueue.CreateTimer();
                    _focusCheckRetry.Interval = TimeSpan.FromMilliseconds(80);
                    _focusCheckRetry.IsRepeating = false;
                    _focusCheckRetry.Tick += (_, _) => QueueAutomaticHideCheck("ForegroundSettled", retryUnknown: false);
                }
                _focusCheckRetry.Start();
                return;
            }
            var wall = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var inSession = processId == (uint)Environment.ProcessId || GetAncestor(foreground, 3) == wall;
            if (inSession)
            {
                UpdateWindowZOrder(foregroundInSession: true);
                RefreshAppearance();
            }
            if (_modal || _contextMenuOpen)
            {
                if (!inSession) UpdateWindowZOrder(foregroundInSession: false);
                return;
            }
            AppLog.Info("WindowFocus", $"Automatic hide check reason={reason} foreground={foreground} pid={processId} inSession={inSession}");
            if (!inSession)
            {
                UpdateWindowZOrder(foregroundInSession: false);
                AutomaticHideRequested?.Invoke();
            }
        });
    }

    internal void FocusTopSession()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var popup = GetLastActivePopup(hwnd);
        if (popup != IntPtr.Zero && popup != hwnd && IsWindowVisible(popup))
            SetForegroundWindow(popup);
        else
        {
            Activate();
            var activated = SetForegroundWindow(hwnd);
            if (_traceInput) TileWall.Core.Shell.AppLog.Info("WindowFocus", $"Explicit foreground requested hwnd={hwnd} accepted={activated}");
        }
        UpdateWindowZOrder(IsForegroundWallSession(), raise: true);
    }

    internal bool IsForegroundWallSession()
    {
        var foreground = GetForegroundWindow();
        return foreground != IntPtr.Zero && GetWindowThreadProcessId(foreground, out var processId) != 0 &&
            (processId == (uint)Environment.ProcessId ||
                GetAncestor(foreground, 3) == WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private bool IsForegroundOutsideWallSession()
    {
        var foreground = GetForegroundWindow();
        return foreground != IntPtr.Zero && GetWindowThreadProcessId(foreground, out var processId) != 0 &&
            processId != (uint)Environment.ProcessId &&
            GetAncestor(foreground, 3) != WinRT.Interop.WindowNative.GetWindowHandle(this);
    }

    private void UpdateWindowZOrder(bool foregroundInSession, bool raise = false)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        TaskbarActivationBackend.PrepareWall(hwnd, Settings.ActivateTaskbar);
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        // Retain the wall's ordinary-app preference; taskbar activation changes only
        // its relation to the verified Explorer taskbar, never Explorer's own band.
        var topmost = Settings.AlwaysOnTop || AppWindow.IsVisible && foregroundInSession;
        const uint flags = 0x0013; // SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE
        if (BackdropTrace.Enabled)
            BackdropTrace.Write($"ZOrder before hwnd={hwnd} foreground={GetForegroundWindow()} inSession={foregroundInSession} preference={Settings.AlwaysOnTop} presenter={presenter.IsAlwaysOnTop} actualTopmost={IsTopmostWindow(hwnd)} desired={topmost} raise={raise}");
        if (presenter.IsAlwaysOnTop != topmost) presenter.IsAlwaysOnTop = topmost;
        if (topmost)
        {
            // Inserting after an ordinary taskbar would move the wall out of the
            // topmost band. The effective work area already reserves that taskbar.
            var behindTaskbar = Settings.ActivateTaskbar && AppWindow.IsVisible &&
                TaskbarActivationBackend.IsCurrentTaskbar(_taskbarWindow) && IsTopmostWindow(_taskbarWindow);
            if (behindTaskbar && !Win32Api.SetWindowPos(hwnd, _taskbarWindow, 0, 0, 0, 0, flags))
                AppLog.Warn("WindowFocus", "Could not settle the wall behind the taskbar.");
            // Explorer can change bands between the style read and SetWindowPos.
            // Read the actual HWND rather than trusting the presenter's cached state.
            if ((!IsTopmostWindow(hwnd) || raise && !behindTaskbar) &&
                !Win32Api.SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, flags))
                AppLog.Warn("WindowFocus", "Could not raise the wall to the topmost band.");
        }
        else
        {
            if (IsTopmostWindow(hwnd) && !Win32Api.SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, flags))
                AppLog.Warn("WindowFocus", "Could not release the wall from the topmost band.");
            if (AppWindow.IsVisible && !foregroundInSession) PlaceBehindForegroundWindow(hwnd, flags);
        }
        if (BackdropTrace.Enabled)
            BackdropTrace.Write($"ZOrder after hwnd={hwnd} foreground={GetForegroundWindow()} presenter={presenter.IsAlwaysOnTop} actualTopmost={IsTopmostWindow(hwnd)}");
    }

    private static bool IsTopmostWindow(IntPtr hwnd) =>
        (Win32Api.GetWindowLongPtrW(hwnd, -20).ToInt64() & 8) != 0; // GWL_EXSTYLE / WS_EX_TOPMOST

    private static void PlaceBehindForegroundWindow(IntPtr hwnd, uint flags)
    {
        // Clearing TOPMOST alone can leave the wall first among ordinary windows.
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && GetWindowThreadProcessId(foreground, out var processId) != 0 &&
            processId != (uint)Environment.ProcessId && GetAncestor(foreground, 3) != hwnd)
        {
            var target = GetAncestor(foreground, 2); // GA_ROOT; keep an outside popup's own owner chain.
            if (target != IntPtr.Zero && target != hwnd && IsWindowVisible(target) && !IsTopmostWindow(target) &&
                GetForegroundWindow() == foreground && !Win32Api.SetWindowPos(hwnd, target, 0, 0, 0, 0, flags))
                AppLog.Warn("WindowFocus", "Could not settle the background wall below the foreground window.");
        }
    }

    internal void CancelTransientState([CallerMemberName] string reason = "")
    {
        TerminateGesture();
        CancelResize(reason);
        if (_viewportTransition is { AutomaticHeight: true } or { ResizeSettlement: true }) StopViewportTransition();
        _windowsSidebar?.Collapse();
    }

    internal string? FinishActiveTitleEditForExit() =>
        ActiveDialog is null ? Surface.FinishActiveTitleEdit(save: true) : null;

    internal void RefreshProductStrings()
    {
        // Settings commit configures the rail before the selected language is applied.
        // Refresh its text explicitly, including while the settings dialog is open.
        _windowsSidebar?.RefreshStrings();
        RefreshMenuStartStrings();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(WallHorizontalScrollBar, ProductStrings.Get("Settings.ExpandColumns"));
        ActionIcon.Set(RetryViewButton, ProductStrings.Get("Wall.Retry"), ActionIcon.Refresh);
        foreach (var zone in new[] { ResizeGrip, ResizeHorizontalEdge, ResizeVerticalEdge, ResizeTopLeft, ResizeTopRight, ResizeBottomLeft, ResizeBottomRight })
        {
            ToolTipService.SetToolTip(zone, ProductStrings.Get("Wall.Resize"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(zone, ProductStrings.Get("Wall.Resize"));
        }
        ActiveDialog?.RefreshStrings();
        if (_hasDocument && !_modal && !_viewBlocked && AppWindow.IsVisible)
        {
            Surface.Render(Document);
            Surface.RefreshClocks(_clock.UtcNow);
        }
        else _needsVisualRefresh = true;
    }

    internal string? CommitSettings(WallSettings settings, PreparedMediaAsset? preparedBackground = null)
    {
        if (PersistenceMutationBlocked) return ProductStrings.Get("Wall.SettingsSaving");
        if (!_hasDocument || _viewBlocked) return ProductStrings.Get("Wall.SettingsBlocked");
        if (preparedBackground is not null && (settings.BackgroundMode != WallBackgroundMode.Custom ||
            settings.BackgroundMedia != preparedBackground.Reference))
            return ProductStrings.Get("Wall.SettingsBlocked");
        IReadOnlyList<PreparedMediaAsset>? preparedMedia = preparedBackground is null ? null : [preparedBackground];
        if (settings.ActivateTaskbar != Settings.ActivateTaskbar &&
            (settings with { ActivateTaskbar = Settings.ActivateTaskbar }) == Settings)
        {
            // A taskbar lease changes visible pixels, not the formal grid or the
            // remembered manual width. Locked layouts accept the same geometry.
            var unchangedLayout = Document with { Generation = checked(Document.Generation + 1), Settings = settings };
            return CommitProposal(new(unchangedLayout, WallLayoutEngine.Measure(unchangedLayout, _metrics)), preparedMedia: preparedMedia);
        }
        if (Settings.LayoutLocked && settings.LayoutLocked && settings.AnchorCorner != Settings.AnchorCorner)
            return ProductStrings.Get("Wall.LayoutLocked");
        if (settings.LayoutLocked && !Settings.LayoutLocked)
            settings = settings with { WindowedViewportWidthDip = CurrentWindowedViewportWidthDip(Document) };
        if (settings.FullScreen != Settings.FullScreen || settings.ExpandColumns != Settings.ExpandColumns || settings.MenuStart != Settings.MenuStart)
        {
            var mode = ProposeMode(Document, settings);
            if (mode.Proposal is null) return ViewportFailure(mode);
            return CommitProposal(mode.Proposal, preparedMedia: preparedMedia);
        }
        var next = Document with { Generation = checked(Document.Generation + 1), Settings = settings };
        var fit = WallViewportPolicy.Validate(next, RecoverableViewportBudget(settings), _metrics);
        if (fit.Proposal is null) return ViewportFailure(fit);
        return CommitProposal(fit.Proposal, preparedMedia: preparedMedia);
    }

    internal async Task OpenSettingsAsync(Func<SettingsDialogState> read,
        Func<WallSettings, bool, PreparedMediaAsset?, Task<SettingsDialogState>> apply,
        Func<int, bool, Task>? hotKeyRecordingChanged = null,
        Func<bool, Task<SettingsDialogState>>? retryStartReplacement = null)
    {
        if (PersistenceInteractionBlocked || _modal || !_hasDocument) return;
        try
        {
            if (!await BeginModalAsync()) return;
            var dialog = new SettingsDialog(DialogOwner, _images, ExplorerContextMenu, read, apply, hotKeyRecordingChanged, retryStartReplacement,
                token => InitializeWelcomeWallAsync(fresh: false, token), ImportWindowsStartAsync);
            ActiveDialog = dialog;
            await dialog.ShowAsync();
        }
        catch (Exception ex) { Status(ex.Message); }
        finally { EndModal(); }
    }

    private async Task CreateClockAsync(string? groupId, int lane)
    {
        if (PersistenceInteractionBlocked || _modal || _viewBlocked || !_hasDocument) return;
        var clock = new ClockMember { Id = $"clock-{Guid.NewGuid():N}", LocalBounds = new(0, 0, 2, 2) };
        await QueueUserMutationAsync(() =>
        {
            var error = AddDefaultMember(clock, groupId, lane);
            if (error is not null) Status(error);
            return Task.CompletedTask;
        });
    }

    private async Task<bool> BeginModalAsync()
    {
        if (_windowClosing || ShutdownStarted || _persistenceAdmissionClosed) return false;
        var titleError = Surface.FinishActiveTitleEdit(save: true);
        if (titleError is not null) { Status(titleError); return false; }
        // A tray-opened page can be the first visible TileWall window. Reuse the
        // hidden capture entry before creating/showing its host; F preparation
        // remains asynchronous and never delays modal admission.
        CaptureBackdropBeforeShow();
        _modalClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _modal = true;
        UpdateWallHitTesting();
        Surface.ClearTransientInteraction();
        UpdateNativeSizingRegion(); // Remove the opt-in native edge before any asynchronous dialog setup.
        UpdatePuzzleGate();
        CancelTransientState();
        DialogOpened?.Invoke();
        await WaitForUserMutationsAsync();
        await DrainPersistenceAsync();
        if (_windowClosing || ShutdownStarted || _persistenceAdmissionClosed) return false;
        _dialogHost = new OwnedDialogWindow(this, () => ActiveDialog?.RequestDismiss());
        RefreshDialogAppearance();
        _dialogHost.Activated += (_, _) => QueueAutomaticHideCheck("DialogActivated");
        await _dialogHost.InitializeAsync();
        // Exit can close admission while the host is still bootstrapping, before
        // ActiveDialog exists. Let the caller's finally release that empty host.
        if (_persistenceAdmissionClosed) return false;
        UpdateResizeZones();
        return true;
    }

    private void EndModal()
    {
        if (!_modal) return;
        var closed = _modalClosed;
        try
        {
            DetachGalleryEditor();
            ActiveDialog = null;
            _dialogHost?.Release();
            _dialogHost = null;
            _modal = false;
            UpdateWallHitTesting();
            if (_hasDocument && !_viewBlocked)
            {
                if (_needsVisualRefresh) { Surface.Render(Document); _needsVisualRefresh = false; }
            }
            UpdateResizeZones();
            UpdatePuzzleGate();
            QueueExpansionReveal();
            QueueEnvironmentAdaptation();
            DialogClosed?.Invoke();
            closed?.TrySetResult();
        }
        catch (Exception ex) { closed?.TrySetException(ex); throw; }
        finally { _modalClosed = null; }
    }

    internal Task WaitForModalClosedAsync() => _modalClosed?.Task ?? Task.CompletedTask;

    private void UpdateWallHitTesting() => WallRoot.IsHitTestVisible = !_modal && !PersistenceInteractionBlocked;

    internal void QueueEnvironmentRefresh(bool observeDisplay = false)
    {
        if (_windowClosing || ShutdownStarted) return;
        _displayObservationRequested |= observeDisplay;
        if (_environmentQueued) return;
        _environmentQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _environmentQueued = false;
            if (_windowClosing || ShutdownStarted) return;
            RefreshEnvironment(force: false);
        });
    }

    private void RefreshEnvironment(bool force)
    {
        if (!force && !_environmentWallShown)
        {
            if (_dialogHost is not null) _dialogHost.RefreshPlacementBounds();
            return;
        }

        if (!force && _persistencePending)
        {
            _environmentAfterPersistence = true;
            // Sample while a write settles so a real screen change still starts
            // the correct waiting period. The existing render delay is separate.
            if (_displayObservationRequested)
                QueueEnvironmentAdaptation(observeDisplay: true);
            return;
        }

        var observeDisplay = _displayObservationRequested;
        _displayObservationRequested = false;
        RefreshAppearance();
        if (!SystemParametersInfoW(SpiGetWorkArea, 0, out var area, 0)) return;
        RefreshNativeFrame();
        UpdateNativeSizingRegion();
        var scale = Scale;
        ReadPrimaryScreen();
        var physicalChanged = _lastEnvironmentScale != scale || !_lastEnvironmentScreen.Equals(_screenArea);
        if (physicalChanged || observeDisplay) QueueEnvironmentAdaptation(observeDisplay: true);
        if (!force && !physicalChanged && _lastEnvironmentWork.Equals(area)) return;
        if (!force && !physicalChanged)
        {
            // A taskbar/work-area notification cannot cancel an in-flight wall
            // gesture or republish the document. The lifetime supplies stable
            // taskbar placement separately after its read-only observation.
            _workArea = _lastEnvironmentWork = area;
            _dialogHost?.RefreshPlacementBounds();
            return;
        }
        if (_resizing) TraceInput($"ResizeEnvironment force={force} scale={_lastEnvironmentScale:F3}->{scale:F3} work={area.Left},{area.Top},{area.Right},{area.Bottom}");
        _workArea = area;
        _lastEnvironmentWork = area;
        _lastEnvironmentScreen = _screenArea;
        _lastEnvironmentScale = scale;
        CancelTransientState();
        if (!_hasDocument || _viewBlocked) return;
        _puzzles.Sync(Document);
        if (force || AppWindow.IsVisible)
        {
            Surface.Render(Document);
            Surface.RefreshClocks(_clock.UtcNow);
            _needsVisualRefresh = false;
        }
        else _needsVisualRefresh = true;
        ApplyViewport();
        UpdatePuzzleGate();
        RefreshEnvironmentStatus();
    }

    private void RefreshNativeFrame()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (GetWindowRect(hwnd, out var outer) && GetClientRect(hwnd, out var client))
        {
            _frameWidth = Math.Max(0, outer.Right - outer.Left - client.Right);
            _frameHeight = Math.Max(0, outer.Bottom - outer.Top - client.Bottom);
        }
    }

    private void ApplyViewport(WallDocument? previewDocument = null, WallGeometry? previewGeometry = null,
        bool animateMode = true, bool animateHeight = false, bool animateResize = false)
    {
        if (!_hasDocument && previewDocument is null) return;
        RefreshNativeFrame();
        var document = previewDocument ?? Document;
        var fullScreen = document.Settings.FullScreen;
        var geometry = previewGeometry ?? Surface.Geometry ?? WallLayoutEngine.Measure(document, _metrics);
        var fixedWidth = WallViewportPolicy.FixedWidth(document.Settings.MenuStart);
        var maximumTileWidth = MaximumTileWidthDip(document.Settings);
        if (previewDocument is null)
        {
            var budget = ViewportBudget(document.Settings);
            _environmentOverflow = geometry.WallWidth > budget.MaxWidthDip || geometry.WallHeight > budget.MaxHeightDip;
        }
        if (maximumTileWidth <= 0) return;

        var contentWidth = geometry.WallWidth + fixedWidth;
        var visibleWidth = document.Settings.WindowedViewportWidthDip ??
            (document.Settings.ExpandColumns ? MaximumWindowedClientWidthDip : contentWidth);
        visibleWidth = fixedWidth + ResolvePresentedTileWidth(document.Settings, geometry,
            visibleWidth - fixedWidth, maximumTileWidth);
        var area = PresentationBounds;
        var width = fullScreen ? area.Width :
            (int)Math.Ceiling(visibleWidth * Scale) + _frameWidth;
        var height = fullScreen ? area.Height :
            (int)Math.Ceiling(geometry.WallHeight * Scale) + _frameHeight;
        if (!fullScreen)
        {
            width = Math.Min(width, MaximumPresentedWindowedWidthPixels);
            height = Math.Min(height, MaximumPresentedWindowedHeightPixels);
        }

        Surface.Visibility = _viewBlocked ? Visibility.Collapsed : Visibility.Visible;
        Surface.IsHitTestVisible = !_viewBlocked && !PersistenceInteractionBlocked;
        var placed = ApplicationModel.WallPresentationGeometry.Anchor(area, Math.Max(1, width), Math.Max(1, height),
            document.Settings.AnchorCorner, fullScreen);
        PresentViewport(new RectInt32(placed.X, placed.Y, placed.Width, placed.Height), fullScreen,
            document.Settings.AnchorCorner, animateMode, presentationSettings: document.Settings,
            animateHeight: animateHeight, animateResize: animateResize);
        UpdateWindowZOrder(IsForegroundWallSession());
        UpdateResizeZones();
    }

    private double ResolvePresentedTileWidth(WallSettings settings, WallGeometry geometry,
        double requestedWidth, double maximumWidth) =>
        _environmentOverflow && WallViewportPolicy.CompleteColumns(maximumWidth, _metrics) == 0
            ? maximumWidth
            : WallViewportPolicy.ResolveVisibleWidth(settings, geometry, requestedWidth, maximumWidth, _metrics);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}



