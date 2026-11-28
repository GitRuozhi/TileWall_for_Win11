using TileWall.ApplicationModel;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Resources;
using TileWall.Shell.Interop;
using TileWall.Views;
using Windows.Graphics;

namespace TileWall;

public sealed partial class MainWindow
{
    private readonly Dictionary<nint, SecondaryWallWindow> _secondaryWalls = [];
    private readonly Dictionary<nint, (RectInt32 Area, TaskbarWindowAnchor Anchor)> _secondaryTaskbarPlacements = [];
    private WallDocument? _committedSecondaryDocument, _presentedSecondaryDocument;
    private bool _secondaryRefreshQueued;

    internal bool HasVisibleSecondaryWalls => _secondaryWalls.Values.Any(wall => wall.IsVisible);
    internal bool CanShowSecondaryWalls => _hasDocument && !_viewBlocked && !ShutdownStarted;
    private bool SecondaryContextMenuOpen => _secondaryWalls.Values.Any(wall => wall.IsVisible && wall.MenuOpen);

    internal (nint DisplayId, nint LauncherWindow)[] VisibleSecondaryTaskbarTargets() => _secondaryWalls
        .Where(entry => entry.Value.IsVisible).Select(entry => (entry.Key, entry.Value.WindowHandle)).ToArray();

    internal void SetSecondaryTaskbarPlacement(nint displayId, RectInt32? effectiveArea, TaskbarWindowAnchor anchor)
    {
        if (effectiveArea is { } area) _secondaryTaskbarPlacements[displayId] = (area, anchor);
        else _secondaryTaskbarPlacements.Remove(displayId);
        if (_secondaryWalls.TryGetValue(displayId, out var wall)) wall.SetTaskbarPlacement(effectiveArea, anchor);
    }

    internal void SetSecondaryDisplays(IReadOnlyList<DisplayMonitor> attached,
        IReadOnlyList<DisplayMonitor> visible, bool captureBackdrop)
    {
        SynchronizeSecondaryDisplays(attached, visible);
        foreach (var display in visible) PrepareSecondaryDisplay(display, captureBackdrop);
        foreach (var display in visible) _secondaryWalls[display.Id].ShowPreparedView();
        UpdatePuzzleGate();
    }

    internal void SynchronizeSecondaryDisplays(IReadOnlyList<DisplayMonitor> attached, IReadOnlyList<DisplayMonitor> visible)
    {
        foreach (var id in _secondaryTaskbarPlacements.Keys.Where(id =>
            !attached.Any(display => !display.IsPrimary && display.Id == id)).ToArray())
            _secondaryTaskbarPlacements.Remove(id);
        foreach (var (id, wall) in _secondaryWalls.ToArray())
        {
            if (!attached.Any(display => !display.IsPrimary && display.Id == id))
            {
                _secondaryWalls.Remove(id);
                wall.CloseView();
            }
            else if (!visible.Any(display => display.Id == id)) wall.HideView();
        }
        RefreshSecondaryPresentation();
    }

    internal void PrepareSecondaryDisplay(DisplayMonitor display, bool captureBackdrop)
    {
        var id = display.Id;
        if (!_secondaryWalls.TryGetValue(id, out var wall))
        {
            wall = new SecondaryWallWindow(display, _snapshotSlot, TrackAppearanceWork, _runtimeScope.IsTest,
                _images, _menuStart.ExpandedGroups);
            _secondaryWalls.Add(id, wall);
            wall.Surface.ResolveEntryPath = _session.ManagedEntryPath;
            wall.Surface.ResolveTargetCommand = _session.TargetCommand;
            wall.Surface.ResolvePuzzleFrame = _puzzles.FrameFor;
            wall.Surface.ResolvePuzzleProgress = _puzzles.ProgressFor;
            wall.Surface.ResolvePuzzleTransition = _puzzles.TransitionFor;
            wall.Surface.ReadCommittedMenuSettings = () => Settings;
            wall.Surface.MemberInvoked += member => LaunchMember(member, elevated: false, reportFailure: wall.ShowStatus);
            wall.MenuEntryInvoked += member => LaunchMember(member, elevated: false, reportFailure: wall.ShowStatus);
            wall.Surface.MemberAdminRequested += member => LaunchMember(member, elevated: true, reportFailure: wall.ShowStatus);
            wall.ForegroundChanged += () => QueueAutomaticHideCheck("SecondaryWallFocus");
            wall.HideRequested += () => HideRequested?.Invoke();
            wall.ViewChanged += UpdateVisibleWallFrames;
            if (_secondaryTaskbarPlacements.TryGetValue(id, out var taskbar))
                wall.SetTaskbarPlacement(taskbar.Area, taskbar.Anchor);
        }
        wall.UpdateDisplay(display);
        if (_presentedSecondaryDocument is { } document) wall.Present(document);
        SetSecondaryAppearance(wall);
        wall.SetInteractionEnabled(CanShowSecondaryWalls && !_modal && !PersistenceInteractionBlocked);
        wall.PrepareToShow(captureBackdrop);
    }

    internal void ShowPreparedSecondaryDisplay(nint id)
    {
        if (!_secondaryWalls.TryGetValue(id, out var wall)) return;
        wall.ShowPreparedView();
        UpdatePuzzleGate();
    }

    internal DesktopBackdropSnapshot.CaptureRequest? SharedSecondaryBackdropRequest(nint id) =>
        _secondaryWalls.TryGetValue(id, out var wall) ? wall.HiddenBackdropRequest() : null;

    internal Task<bool> PrepareSharedSecondaryBackdropAsync(nint id, DesktopBackdropSnapshot.CaptureRequest request,
        DesktopBackdropSnapshot.CapturedFrame frame, CancellationToken token) =>
        _secondaryWalls.TryGetValue(id, out var wall)
            ? wall.PrepareSharedBackdropAsync(request, frame, token)
            : Task.FromResult(false);

    internal static string DisplayCaptureFailureMessage(DesktopBackdropSnapshot.CaptureFailure failure)
    {
        var reason = ProductStrings.Get(failure switch
        {
            DesktopBackdropSnapshot.CaptureFailure.Geometry => "Wall.CaptureGeometry",
            DesktopBackdropSnapshot.CaptureFailure.Allocation => "Wall.CaptureAllocation",
            DesktopBackdropSnapshot.CaptureFailure.Read => "Wall.CaptureRead",
            DesktopBackdropSnapshot.CaptureFailure.Presentation => "Wall.CapturePresentation",
            _ => "Wall.CaptureCrop",
        });
        return string.Format(ProductStrings.Get("Wall.MultiDisplayCaptureFailed"), reason);
    }

    internal void ReportDisplayCaptureFailure(nint? displayId, DesktopBackdropSnapshot.CaptureFailure failure)
    {
        var message = DisplayCaptureFailureMessage(failure);
        if (displayId is { } id && _secondaryWalls.TryGetValue(id, out var wall) && wall.IsVisible)
            wall.ShowStatus(message);
        else if (AppWindow.IsVisible) ShowTransientStatus(message);
        else _secondaryWalls.Values.FirstOrDefault(secondary => secondary.IsVisible)?.ShowStatus(message);
    }

    internal bool FocusSecondaryDisplay(nint id)
    {
        if (!_secondaryWalls.TryGetValue(id, out var wall) || !wall.IsVisible) return false;
        wall.FocusView();
        return true;
    }

    internal void HideSecondaryWalls()
    {
        foreach (var wall in _secondaryWalls.Values) wall.HideView();
        UpdatePuzzleGate();
    }

    internal void DiscardHiddenSecondaryPreparations()
    {
        foreach (var wall in _secondaryWalls.Values)
            if (!wall.IsVisible) wall.HideView();
    }

    private void CloseSecondaryWalls()
    {
        foreach (var wall in _secondaryWalls.Values) wall.CloseView();
        _secondaryWalls.Clear();
        _secondaryTaskbarPlacements.Clear();
    }

    private void OnSecondaryCommit(WallDocument document)
    {
        _committedSecondaryDocument = document;
        QueueSecondaryPresentation();
    }

    private void QueueSecondaryPresentation()
    {
        if (_secondaryRefreshQueued || _secondaryWalls.Count == 0 ||
            ReferenceEquals(_committedSecondaryDocument, _presentedSecondaryDocument)) return;
        _secondaryRefreshQueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _secondaryRefreshQueued = false;
            RefreshSecondaryPresentation();
        });
    }

    private void RefreshSecondaryPresentation()
    {
        if (!CanShowSecondaryWalls || _gesture.Phase != GesturePhase.Idle || _resizing ||
            _drops.Count != 0 || _viewportTransition is not null ||
            _committedSecondaryDocument is not { } document || ReferenceEquals(document, _presentedSecondaryDocument)) return;
        _presentedSecondaryDocument = document;
        foreach (var wall in _secondaryWalls.Values) wall.Present(document);
        UpdateVisibleWallFrames();
        RefreshWallClocks();
    }

    private void RefreshSecondaryAppearance()
    {
        foreach (var wall in _secondaryWalls.Values) SetSecondaryAppearance(wall);
    }

    private void RefreshSecondaryProductStrings()
    {
        foreach (var wall in _secondaryWalls.Values) wall.RefreshProductStrings();
    }

    private void SetSecondaryAppearance(SecondaryWallWindow wall) => wall.SetAppearance(_themeDark,
        _accessibilitySettings.HighContrast, !_appearanceSettings.AdvancedEffectsEnabled, _backgroundFrame);

    private void RefreshWallClocks()
    {
        var now = _clock.UtcNow;
        if (AppWindow.IsVisible) Surface.RefreshClocks(now);
        foreach (var wall in _secondaryWalls.Values)
            if (wall.IsVisible) wall.Surface.RefreshClocks(now);
    }
}
