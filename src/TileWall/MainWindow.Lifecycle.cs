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
    private Point _resizeStart;
    private int _resizeStartColumns;
    private int _resizeStartRows;
    private WallProposal? _resizeProposal;
    private string? _resizeError;
    private int _resizeDeltaCount;

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
        ToolTipService.SetToolTip(ResizeGrip, ProductStrings.Get("Wall.Resize"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ResizeGrip, ProductStrings.Get("Wall.Resize"));
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
        _settingsCommit = true;
        try { return CommitProposal(new WallProposal(next, WallLayoutEngine.Measure(next, _metrics)), recordUndo: false); }
        finally { _settingsCommit = false; }
    }

    internal async Task OpenSettingsAsync(Func<SettingsDialogState> read,
        Func<WallSettings, Task<SettingsDialogState>> apply)
    {
        if (_modal || !_hasDocument) return;
        BeginModal();
        try
        {
            var dialog = new SettingsDialog(this, read, apply);
            ActiveDialog = dialog;
            await dialog.ShowAsync();
        }
        catch (Exception ex) { Status(ex.Message); }
        finally { EndModal(); }
    }

    private async Task CreateClockAsync(string? groupId, int lane)
    {
        if (_modal || _viewBlocked || _adaptationBlocked) return;
        BeginModal();
        try
        {
            var id = $"clock-{Guid.NewGuid():N}";
            var dialog = new ClockEditorDialog(this, null, id, MaxMemberRows(), _clock, clock =>
                Apply(new AddMemberAction(Document.Generation, clock, groupId, lane,
                    Document.Lanes[lane].Groups.Count, groupId is null ? $"group-{Guid.NewGuid():N}" : null)));
            ActiveDialog = dialog;
            await dialog.ShowAsync();
        }
        catch (Exception ex) { Status(ex.Message); }
        finally { EndModal(); }
    }

    private void BeginModal()
    {
        _modal = true;
        UpdatePuzzleGate();
        CancelTransientState();
        DialogOpened?.Invoke();
        ApplyViewport();
    }

    private void EndModal()
    {
        ActiveDialog = null;
        _modal = false;
        if (_hasDocument && !_viewBlocked)
        {
            if (_needsVisualRefresh) { Surface.Render(Document); _needsVisualRefresh = false; }
            ApplyViewport();
        }
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
        if (_modal)
        {
            width = Math.Min(workWidth, Math.Max(width, (int)Math.Ceiling(760 * Scale) + _frameWidth));
            height = Math.Min(workHeight, Math.Max(height, (int)Math.Ceiling(700 * Scale) + _frameHeight));
        }
        var right = Settings.AnchorCorner is AnchorCorner.BottomRight or AnchorCorner.TopRight;
        var bottom = Settings.AnchorCorner is AnchorCorner.BottomLeft or AnchorCorner.BottomRight;
        var x = right ? _workArea.Right - width : _workArea.Left;
        var y = bottom ? _workArea.Bottom - height : _workArea.Top;
        AppWindow.MoveAndResize(new RectInt32(x, y, Math.Max(1, width), Math.Max(1, height)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = Settings.AlwaysOnTop;
        ResizeGrip.HorizontalAlignment = right ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        ResizeGrip.VerticalAlignment = bottom ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        ResizeGrip.Visibility = _modal || _viewBlocked ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnAdaptationClicked(object sender, RoutedEventArgs e)
    {
        if (_modal || _viewBlocked || !_hasDocument) return;
        BeginModal();
        try
        {
            var dialog = new AdaptationDialog(this, Document, WorkWidthDip, WorkHeightDip, _metrics, SaveAdaptation);
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

    private void OnResizeStarted(object sender, DragStartedEventArgs e)
    {
        if (_modal || _viewBlocked || !_hasDocument)
        {
            TraceInput($"ResizeStarted ignored modal={_modal} blocked={_viewBlocked} document={_hasDocument}");
            return;
        }
        if (!GetCursorPos(out var cursor)) { TraceInput("ResizeStarted cursor unavailable"); return; }
        TerminateGesture();
        _resizing = true;
        _resizeStart = new Point(cursor.X, cursor.Y);
        _resizeStartColumns = Document.Columns;
        _resizeStartRows = Document.Rows;
        _resizeProposal = null;
        _resizeError = null;
        _resizeDeltaCount = 0;
        TraceInput($"ResizeStarted anchor={Settings.AnchorCorner} cursor={cursor.X},{cursor.Y} grid={_resizeStartColumns}x{_resizeStartRows} scale={Scale:F3} window={AppWindow.Position.X},{AppWindow.Position.Y},{AppWindow.Size.Width},{AppWindow.Size.Height} thumbDragging={ResizeGrip.IsDragging}");
        UpdatePuzzleGate();
    }

    private void OnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        _resizeDeltaCount++;
        if (_resizeDeltaCount <= 32)
            TraceInput($"ResizeDelta event={_resizeDeltaCount} active={_resizing} delta={e.HorizontalChange:F2},{e.VerticalChange:F2} thumbDragging={ResizeGrip.IsDragging}");
        else if (_resizeDeltaCount == 33) TraceInput("ResizeDelta further event traces suppressed");
        if (_resizing) UpdateResizeProposal(preview: true);
    }

    private void UpdateResizeProposal(bool preview)
    {
        if (!GetCursorPos(out var cursor))
        {
            _resizeProposal = null;
            if (!preview || _resizeDeltaCount <= 32) TraceInput("ResizeProposal cursor unavailable");
            return;
        }
        var right = Settings.AnchorCorner is AnchorCorner.BottomRight or AnchorCorner.TopRight;
        var bottom = Settings.AnchorCorner is AnchorCorner.BottomLeft or AnchorCorner.BottomRight;
        var dx = (cursor.X - _resizeStart.X) / Scale * (right ? -1 : 1);
        var dy = (cursor.Y - _resizeStart.Y) / Scale * (bottom ? -1 : 1);
        var columns = Math.Clamp(_resizeStartColumns + (int)Math.Round(dx / (_metrics.LaneWidth + _metrics.ColumnGap)), 1, 64);
        var rows = Math.Clamp(_resizeStartRows + (int)Math.Round(dy / _metrics.Pitch), 1, 256);
        var result = _session.Propose(new ResizeWallAction(Document.Generation, columns, rows), WorkWidthDip, WorkHeightDip, _metrics);
        _resizeProposal = result.Proposal;
        _resizeError = result.Proposal is null
            ? string.Format(ProductStrings.Get("Wall.ResizeFailed"), ProductStrings.WallRejectionReason(result.Rejection))
            : null;
        if (preview)
        {
            Surface.Render(result.Proposal?.Document ?? Document);
            Surface.RefreshClocks(_clock.UtcNow);
            ApplyViewport();
            if (_resizeError is not null) Status(_resizeError);
        }
        if (!preview || _resizeDeltaCount <= 32)
            TraceInput($"ResizeProposal phase={(preview ? "delta" : "completed")} cursor={cursor.X},{cursor.Y} grid={columns}x{rows} rejection={result.Rejection} detail={result.Detail} proposal={result.Proposal is not null} proposedDip={result.Proposal?.Geometry.WallWidth:F1}x{result.Proposal?.Geometry.WallHeight:F1} surface={Surface.Width:F1}x{Surface.Height:F1} window={AppWindow.Position.X},{AppWindow.Position.Y},{AppWindow.Size.Width},{AppWindow.Size.Height}");
    }

    private void OnResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        TraceInput($"ResizeCompleted active={_resizing} canceled={e.Canceled} events={_resizeDeltaCount} delta={e.HorizontalChange:F2},{e.VerticalChange:F2}");
        if (!_resizing) return;
        // The release can arrive before the final DragDelta. Commit the release position, not an older preview.
        if (!e.Canceled) UpdateResizeProposal(preview: false);
        var proposal = e.Canceled ? null : _resizeProposal;
        var error = _resizeError;
        CancelResize(e.Canceled ? "ThumbCanceled" : "ThumbCompleted");
        if (proposal is not null && (proposal.Document.Columns != Document.Columns || proposal.Document.Rows != Document.Rows))
        {
            error = CommitProposal(proposal);
            TraceInput($"ResizeCommit grid={proposal.Document.Columns}x{proposal.Document.Rows} generation={Document.Generation} error={error}");
        }
        if (error is not null) Status(error);
    }

    private void CancelResize([CallerMemberName] string reason = "")
    {
        if (!_resizing) return;
        TraceInput($"ResizeCancel reason={reason} events={_resizeDeltaCount} preview={_resizeProposal?.Document.Columns}x{_resizeProposal?.Document.Rows} generation={Document.Generation}");
        _resizing = false;
        _resizeProposal = null;
        if (_hasDocument && !_viewBlocked) { Surface.Render(Document); ApplyViewport(); }
        UpdatePuzzleGate();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
