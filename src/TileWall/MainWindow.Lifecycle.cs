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
using TileWall.Views;
using Windows.Foundation;
using Windows.Graphics;

namespace TileWall;

public sealed partial class MainWindow
{
    private int _frameWidth;
    private int _frameHeight;
    private bool _environmentQueued;
    private bool _needsVisualRefresh;
    private bool _focusCheckQueued, _contextMenuOpen;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _focusCheckRetry;
    private double _lastEnvironmentScale;
    private WorkAreaRect _lastEnvironmentWork;
    private OwnedDialogWindow? _dialogHost;
    private TaskCompletionSource? _modalClosed;
    private Window DialogOwner => _dialogHost ?? throw new InvalidOperationException("No modal host is active.");

    internal FrameworkElement WallContent => WallRoot;
    internal double RasterScale => Scale;
    internal bool ModalActive => _modal;
    internal IWallDialogSession? ActiveDialog { get; private set; }
    internal WallSettings Settings => _hasDocument ? Document.Settings : new();
    internal event Action? HideRequested;
    internal event Action? AutomaticHideRequested;
    internal event Action? SettingsRequested;
    internal event Action? DialogClosed;
    internal event Action? DialogOpened;

    internal void InitializeSession() => OpenWall();
    internal void ShowStatus(string message) => Status(message);

    internal void ShowWall()
    {
        RefreshEnvironment(force: true);
        RefreshAppearance();
        CaptureBackdropBeforeShow();
        AppWindow.Show();
        FocusTopSession();
        UpdatePuzzleGate();
    }

    internal void HideWall()
    {
        _focusCheckRetry?.Stop();
        CancelTransientState();
        AppWindow.Hide();
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
            if (_windowClosing || !AppWindow.IsVisible || _modal || _contextMenuOpen) return;
            // XAML popup HWND activation can precede its MenuFlyoutItem.Click route.
            // Let that route finish, then decide from the actual foreground session.
            var foreground = GetForegroundWindow();
            var thread = GetWindowThreadProcessId(foreground, out var processId);
            if (foreground == IntPtr.Zero || thread == 0)
            {
                AppLog.Info("WindowFocus", $"Automatic hide deferred: foreground transition reason={reason} retry={retryUnknown}");
                if (!retryUnknown) return;
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
            AppLog.Info("WindowFocus", $"Automatic hide check reason={reason} foreground={foreground} pid={processId} inSession={inSession}");
            if (!inSession) AutomaticHideRequested?.Invoke();
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
    }

    internal void CancelTransientState([CallerMemberName] string reason = "")
    {
        TerminateGesture();
        CancelResize(reason);
        _windowsSidebar?.Collapse();
    }

    internal string? FinishActiveTitleEditForExit() =>
        ActiveDialog is null ? Surface.FinishActiveTitleEdit(save: true) : null;

    internal void RefreshProductStrings()
    {
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

    internal string? CommitSettings(WallSettings settings)
    {
        if (PersistenceMutationBlocked) return ProductStrings.Get("Wall.SettingsSaving");
        if (!_hasDocument || _viewBlocked) return ProductStrings.Get("Wall.SettingsBlocked");
        if (settings.FullScreen != Settings.FullScreen || settings.ExpandColumns != Settings.ExpandColumns || settings.MenuStart != Settings.MenuStart)
        {
            var mode = ProposeMode(Document, settings);
            if (mode.Proposal is null) return ViewportFailure(mode);
            // Mode changes that resize/compact the wall are layout transactions. The
            // non-undo path is reserved for settings/carousel-only commits and keeps
            // the previous layout undo token when the grid itself did not change.
            var changesLayout = mode.Proposal.Document.Columns != Document.Columns ||
                mode.Proposal.Document.Rows != Document.Rows ||
                !LayoutCoordinates(mode.Proposal.Document).SequenceEqual(LayoutCoordinates(Document));
            return CommitProposal(mode.Proposal, recordUndo: changesLayout);
        }
        var next = Document with { Generation = checked(Document.Generation + 1), Settings = settings };
        var fit = WallViewportPolicy.Validate(next, ViewportBudget(settings), _metrics);
        if (fit.Proposal is null) return ViewportFailure(fit);
        return CommitProposal(fit.Proposal, recordUndo: false);
    }

    private static IEnumerable<(int Lane, string Group, string Member, TileWall.Core.Grid.GridRect Bounds)> LayoutCoordinates(WallDocument document) =>
        document.Lanes.SelectMany((lane, index) => lane.Groups.SelectMany(group => group.Members.Select(member =>
            (index, group.Id, member.Id, member.LocalBounds))));

    internal async Task OpenSettingsAsync(Func<SettingsDialogState> read,
        Func<WallSettings, bool, Task<SettingsDialogState>> apply,
        Action<bool>? hotKeyRecordingChanged = null)
    {
        if (PersistenceInteractionBlocked || _modal || !_hasDocument) return;
        try
        {
            if (!await BeginModalAsync()) return;
            var dialog = new SettingsDialog(DialogOwner, read, apply, hotKeyRecordingChanged);
            ActiveDialog = dialog;
            await dialog.ShowAsync();
        }
        catch (Exception ex) { Status(ex.Message); }
        finally { EndModal(); }
    }

    private Task CreateClockAsync(string? groupId, int lane)
    {
        if (PersistenceInteractionBlocked || _modal || _viewBlocked || !_hasDocument) return Task.CompletedTask;
        var clock = new ClockMember { Id = $"clock-{Guid.NewGuid():N}", LocalBounds = new(0, 0, 2, 2) };
        var error = AddDefaultMember(clock, groupId, lane);
        if (error is not null) Status(error);
        return Task.CompletedTask;
    }

    private async Task<bool> BeginModalAsync()
    {
        var titleError = Surface.FinishActiveTitleEdit(save: true);
        if (titleError is not null) { Status(titleError); return false; }
        _modalClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _modal = true;
        UpdateNativeSizingRegion(); // Remove the opt-in native edge before any asynchronous dialog setup.
        UpdatePuzzleGate();
        CancelTransientState();
        DialogOpened?.Invoke();
        _dialogHost = new OwnedDialogWindow(this, () => ActiveDialog?.RequestDismiss());
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
            if (_hasDocument && !_viewBlocked)
            {
                if (_needsVisualRefresh) { Surface.Render(Document); _needsVisualRefresh = false; }
            }
            UpdateResizeZones();
            UpdatePuzzleGate();
            DialogClosed?.Invoke();
            closed?.TrySetResult();
        }
        catch (Exception ex) { closed?.TrySetException(ex); throw; }
        finally { _modalClosed = null; }
    }

    internal Task WaitForModalClosedAsync() => _modalClosed?.Task ?? Task.CompletedTask;

    internal void QueueEnvironmentRefresh()
    {
        if (_environmentQueued || _windowClosing || ShutdownStarted) return;
        _environmentQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _environmentQueued = false;
            if (_windowClosing || ShutdownStarted) return;
            RefreshEnvironment(force: false);
        });
    }

    private void RefreshEnvironment(bool force)
    {
        if (_persistencePending) { _environmentAfterPersistence = true; return; }
        RefreshAppearance();
        if (!SystemParametersInfoW(SpiGetWorkArea, 0, out var area, 0)) return;
        RefreshNativeFrame();
        UpdateNativeSizingRegion();
        var scale = Scale;
        if (!force && _lastEnvironmentScale == scale && _lastEnvironmentWork.Equals(area)) return;
        if (_resizing) TraceInput($"ResizeEnvironment force={force} scale={_lastEnvironmentScale:F3}->{scale:F3} work={area.Left},{area.Top},{area.Right},{area.Bottom}");
        _workArea = area;
        ReadPrimaryScreen();
        _lastEnvironmentWork = area;
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

    private void ApplyViewport(WallDocument? previewDocument = null, WallGeometry? previewGeometry = null, bool animateMode = true)
    {
        if (!_hasDocument) return;
        RefreshNativeFrame();
        var document = previewDocument ?? Document;
        var fullScreen = document.Settings.FullScreen;
        var geometry = previewGeometry ?? Surface.Geometry ?? WallLayoutEngine.Measure(document, _metrics);
        var fixedWidth = WallViewportPolicy.FixedWidth(document.Settings.MenuStart);
        var contentWidth = geometry.WallWidth + fixedWidth;
        var visibleWidth = document.Settings.WindowedViewportWidthDip ??
            (document.Settings.ExpandColumns ? (_screenArea.Right - _screenArea.Left) * .9 / Scale : contentWidth);
        visibleWidth = Math.Max(visibleWidth, Math.Min((_screenArea.Right - _screenArea.Left) * .9 / Scale,
            _metrics.WallWidth(1) + fixedWidth));
        if (!document.Settings.ExpandColumns) visibleWidth = Math.Max(visibleWidth, contentWidth);
        var width = fullScreen ? _screenArea.Right - _screenArea.Left :
            (int)Math.Ceiling(visibleWidth * Scale) + _frameWidth;
        var height = fullScreen ? _screenArea.Bottom - _screenArea.Top :
            (int)Math.Ceiling(geometry.WallHeight * Scale) + _frameHeight;
        if (!fullScreen)
        {
            width = Math.Min(width, (int)Math.Floor((_screenArea.Right - _screenArea.Left) * .9));
            height = Math.Min(height, (int)Math.Floor((_screenArea.Bottom - _screenArea.Top) * .9));
        }

        Surface.Visibility = _viewBlocked ? Visibility.Collapsed : Visibility.Visible;
        Surface.IsHitTestVisible = !_viewBlocked && !PersistenceInteractionBlocked;
        var right = document.Settings.AnchorCorner is AnchorCorner.BottomRight or AnchorCorner.TopRight;
        var bottom = document.Settings.AnchorCorner is AnchorCorner.BottomLeft or AnchorCorner.BottomRight;
        // Both modes use the complete primary rectangle, preserving the opposite corner when exiting fullscreen.
        var x = right && !fullScreen ? _screenArea.Right - width : _screenArea.Left;
        var y = bottom && !fullScreen ? _screenArea.Bottom - height : _screenArea.Top;
        PresentViewport(new RectInt32(x, y, Math.Max(1, width), Math.Max(1, height)), fullScreen,
            document.Settings.AnchorCorner, animateMode, presentationSettings: document.Settings);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = Settings.AlwaysOnTop;
        UpdateResizeZones();
    }

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



