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
    private double _lastEnvironmentScale;
    private WorkAreaRect _lastEnvironmentWork;
    private OwnedDialogWindow? _dialogHost;
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
        AppWindow.Show();
        FocusTopSession();
        UpdatePuzzleGate();
    }

    internal void HideWall()
    {
        CancelTransientState();
        AppWindow.Hide();
        Surface.CancelImageRequests();
        _needsVisualRefresh = true;
        UpdatePuzzleGate();
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
    }

    internal void RefreshProductStrings()
    {
        RetryViewButton.Content = ProductStrings.Get("Wall.Retry");
        AdaptationButton.Content = ProductStrings.Get("Wall.AdaptTitle");
        foreach (var zone in new[] { ResizeGrip, ResizeHorizontalEdge, ResizeVerticalEdge })
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
        if (!_hasDocument || _viewBlocked) return ProductStrings.Get("Wall.SettingsBlocked");
        var next = Document with { Generation = checked(Document.Generation + 1), Settings = settings };
        return CommitProposal(new WallProposal(next, WallLayoutEngine.Measure(next, _metrics)), recordUndo: false);
    }

    internal async Task OpenSettingsAsync(Func<SettingsDialogState> read,
        Func<WallSettings, bool, Task<SettingsDialogState>> apply)
    {
        if (_modal || !_hasDocument) return;
        try
        {
            await BeginModalAsync();
            var dialog = new SettingsDialog(DialogOwner, read, apply);
            ActiveDialog = dialog;
            await dialog.ShowAsync();
        }
        catch (Exception ex) { Status(ex.Message); }
        finally { EndModal(); }
    }

    private Task CreateClockAsync(string? groupId, int lane)
    {
        if (_modal || _viewBlocked || _adaptationBlocked || !_hasDocument) return Task.CompletedTask;
        var clock = new ClockMember { Id = $"clock-{Guid.NewGuid():N}", LocalBounds = new(0, 0, 2, 2) };
        var error = AddDefaultMember(clock, groupId, lane);
        if (error is not null) Status(error);
        return Task.CompletedTask;
    }

    private async Task BeginModalAsync()
    {
        _modal = true;
        UpdatePuzzleGate();
        CancelTransientState();
        DialogOpened?.Invoke();
        _dialogHost = new OwnedDialogWindow(this, () => ActiveDialog?.RequestDismiss());
        await _dialogHost.InitializeAsync();
        UpdateResizeZones();
    }

    private void EndModal()
    {
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
    }

    internal void QueueEnvironmentRefresh()
    {
        if (_environmentQueued) return;
        _environmentQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _environmentQueued = false;
            RefreshEnvironment(force: false);
        });
    }

    private void RefreshEnvironment(bool force)
    {
        RefreshAppearance();
        if (!SystemParametersInfoW(SpiGetWorkArea, 0, out var area, 0)) return;
        RefreshNativeFrame();
        var scale = Scale;
        if (!force && _lastEnvironmentScale == scale && _lastEnvironmentWork.Equals(area)) return;
        if (_resizing) TraceInput($"ResizeEnvironment force={force} scale={_lastEnvironmentScale:F3}->{scale:F3} work={area.Left},{area.Top},{area.Right},{area.Bottom}");
        _workArea = area;
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

    private void ApplyViewport()
    {
        if (!_hasDocument) return;
        RefreshNativeFrame();
        var workWidth = _workArea.Right - _workArea.Left;
        var workHeight = _workArea.Bottom - _workArea.Top;
        var width = (int)Math.Ceiling(Surface.Width * Scale) + _frameWidth;
        var height = (int)Math.Ceiling(Surface.Height * Scale) + _frameHeight;
        var wasBlocked = _adaptationBlocked;
        _adaptationBlocked = width > workWidth || height > workHeight;
        AdaptationButton.Visibility = _adaptationBlocked && !_modal ? Visibility.Visible : Visibility.Collapsed;
        if (_adaptationBlocked)
        {
            Surface.Visibility = Visibility.Collapsed;
            Status(ProductStrings.Get("Wall.Adaptation"));
            RetryViewButton.Visibility = Visibility.Visible;
            width = Math.Min(workWidth, (int)Math.Ceiling(640 * Scale) + _frameWidth);
            height = Math.Min(workHeight, (int)Math.Ceiling(220 * Scale) + _frameHeight);
        }
        else
        {
            Surface.Visibility = Visibility.Visible;
            Surface.IsHitTestVisible = !_viewBlocked;
            if (wasBlocked) { StatusPanel.Visibility = Visibility.Collapsed; RetryViewButton.Visibility = Visibility.Collapsed; }
        }
        var right = Settings.AnchorCorner is AnchorCorner.BottomRight or AnchorCorner.TopRight;
        var bottom = Settings.AnchorCorner is AnchorCorner.BottomLeft or AnchorCorner.BottomRight;
        var x = right ? _workArea.Right - width : _workArea.Left;
        var y = bottom ? _workArea.Bottom - height : _workArea.Top;
        AppWindow.MoveAndResize(new RectInt32(x, y, Math.Max(1, width), Math.Max(1, height)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = Settings.AlwaysOnTop;
        UpdateResizeZones();
    }

    private async void OnAdaptationClicked(object sender, RoutedEventArgs e)
    {
        if (_modal || _viewBlocked || !_hasDocument) return;
        try
        {
            await BeginModalAsync();
            var dialog = new AdaptationDialog(DialogOwner, Document, WorkWidthDip, WorkHeightDip, _metrics, SaveAdaptation);
            ActiveDialog = dialog;
            await dialog.ShowAsync();
        }
        catch (Exception ex) { Status(ex.Message); }
        finally { EndModal(); }
    }

    private string? SaveAdaptation(WallDocument draft, IReadOnlyList<EntryChange> removals)
    {
        if (!SystemParametersInfoW(SpiGetWorkArea, 0, out _workArea, 0))
            return ProductStrings.Get("Wall.ReadWorkAreaFailed");
        RefreshNativeFrame();
        var columns = Enumerable.Range(1, 64).LastOrDefault(c => _metrics.WallWidth(c) <= WorkWidthDip);
        var rows = Enumerable.Range(1, 256).LastOrDefault(r => _metrics.WallHeight(r) <= WorkHeightDip);
        if (columns == 0 || rows == 0) return ProductStrings.Get("Wall.Adaptation");
        var latest = draft with { Generation = Document.Generation, Settings = Document.Settings };
        var result = WallLayoutEngine.TryApply(latest,
            new ResizeWallAction(Document.Generation, Math.Min(columns, draft.Columns), Math.Min(rows, draft.Rows)),
            WorkWidthDip, WorkHeightDip, _metrics);
        if (result.Proposal is null)
        {
            AppLog.Warn("WallAdaptation", $"rejection={result.Rejection} detail={result.Detail}");
            return string.Format(ProductStrings.Get("Wall.LocationUnavailable"),
                ProductStrings.WallRejectionReason(result.Rejection), string.Empty).TrimEnd();
        }
        return CommitProposal(result.Proposal, removals);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
