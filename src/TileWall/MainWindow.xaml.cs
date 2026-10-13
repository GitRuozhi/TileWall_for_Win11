using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TileWall.ApplicationModel;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Shell;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Core.Carousel;
using TileWall.Resources;
using TileWall.Shell;
using TileWall.Views;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;

namespace TileWall;

public sealed partial class MainWindow : Window
{
    private const uint SpiGetWorkArea = 0x0030;
    private readonly WallSession _session;
    private readonly EntryLauncher _launcher = new();
    private readonly WallImageService _images;
    private readonly PuzzlePresentationCoordinator _puzzles;
    private readonly GestureSession _gesture = new();
    private readonly WallMetrics _metrics = new();
    private readonly bool _traceInput;
    private WorkAreaRect _workArea;
    private bool _opened;
    private bool _modal;
    private bool _viewBlocked;
    private bool _carouselCommit;
    private bool _hasDocument;
    private bool _adaptationBlocked;
    private bool _resizing;
    private readonly IClock _clock = SystemClock.Instance;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _clockTimer;
    private string? _suppressClickMember;
    private uint _tracedMovePointerId;
    private GesturePhase _tracedMovePhase;
    private readonly WallBackdrop _wallBackdrop;
    private readonly NativeBackdropSurface _nativeBackdropSurface;
    private bool _backdropActive = true;
    private bool _backdropUpdateQueued;
    private bool _windowClosing;

    internal MainWindow(RuntimeScope scope)
    {
        _traceInput = scope.IsTest;
        _session = new WallSession(scope.DataRoot);
        _images = new WallImageService(_session.LinkFiles, scope.CacheRoot);
        InitializeComponent();
        _clockTimer = DispatcherQueue.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.IsRepeating = true;
        _clockTimer.Tick += (_, _) =>
        {
            if (_traceInput) AppLog.Info("ClockTimer", $"Tick visible={AppWindow.IsVisible} utc={_clock.UtcNow:O}");
            Surface.RefreshClocks(_clock.UtcNow);
        };
        _puzzles = new PuzzlePresentationCoordinator(_images, DispatcherQueue, () => Scale,
            PersistPuzzleCarousel, Status, _traceInput);
        _puzzles.FrameChanged += Surface.UpdatePuzzleFrame;
        _session.SetViewRefresh(RenderCommitted);
        Surface.Images = _images;
        Surface.ResolveEntryPath = _session.ManagedEntryPath;
        Surface.ResolvePuzzleFrame = _puzzles.FrameFor;
        Surface.ResolvePuzzleProgress = _puzzles.ProgressFor;
        Title = $"TileWall {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        RemoveNativeFrame();
        _nativeBackdropSurface = new NativeBackdropSurface(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _wallBackdrop = new WallBackdrop(DispatcherQueue, OnMaterialFallbackChanged, scope.IsTest);
        InitializeAppearance();
        SystemBackdrop = _wallBackdrop;

        Surface.MemberInvoked += LaunchMember;
        Surface.MemberEditRequested += id => _ = EditMemberAsync(id);
        Surface.MemberRemoveRequested += RemoveMember;
        Surface.MemberAdminRequested += id => LaunchMember(id, elevated: true);
        Surface.MemberLocationRequested += ShowMemberLocation;
        Surface.GroupRenameRequested += RenameGroup;
        Surface.NewTileRequested += (groupId, lane) => _ = CreateTileAsync(groupId, lane);
        Surface.NewPuzzleRequested += (groupId, lane) => _ = CreatePuzzleAsync(groupId, lane);
        Surface.NewClockRequested += (groupId, lane) => _ = CreateClockAsync(groupId, lane);
        Surface.SettingsRequested += () => SettingsRequested?.Invoke();
        Surface.ImportRequested += (groupId, lane) => _ = OpenImportAsync(groupId, lane);
        Surface.TitleEditFinished += () => ImportReadinessChanged?.Invoke();
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidVisibilityChange || args.DidPresenterChange || args.DidSizeChange) UpdatePuzzleGate();
        };
        Surface.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        Surface.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerMoved), true);
        Surface.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPointerReleased), true);
        Surface.PointerCaptureLost += (_, e) =>
        {
            TraceInput($"CaptureLost source={e.OriginalSource?.GetType().Name} phase={_gesture.Phase} pointer={e.Pointer.PointerId}");
            // Capturing on the surface transfers capture away from the child Button.
            // Its routed loss is expected; only loss of the surface's own capture cancels the gesture.
            if (ReferenceEquals(e.OriginalSource, Surface)) TerminateGesture();
        };
        Surface.PointerCanceled += (_, e) =>
        {
            TraceInput($"Canceled source={e.OriginalSource?.GetType().Name} phase={_gesture.Phase} pointer={e.Pointer.PointerId}");
            TerminateGesture();
        };
        WallRoot.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        WallRoot.Loaded += (_, _) =>
        {
            if (WallRoot.XamlRoot is { } root) root.Changed += (_, _) => QueueEnvironmentRefresh();
            QueueEnvironmentRefresh();
        };
        Activated += (_, e) =>
        {
            SetBackdropActive(e.WindowActivationState != WindowActivationState.Deactivated);
            TraceInput($"Activated state={e.WindowActivationState} phase={_gesture.Phase}");
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                TerminateGesture();
                CancelResize();
                if (!_modal) AutomaticHideRequested?.Invoke();
            }
        };
        Closed += (_, _) =>
        {
            _windowClosing = true;
            DisposeAppearance();
            _nativeBackdropSurface.Dispose();
            TerminateGesture();
            _puzzles.Dispose();
            _clockTimer.Stop();
            Surface.CancelImageRequests();
            _images.Dispose();
        };
    }

    private void SetBackdropActive(bool active)
    {
        _backdropActive = active;
        if (_backdropUpdateQueued || _windowClosing) return;
        _backdropUpdateQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _backdropUpdateQueued = false;
            if (_windowClosing) return;
            RefreshAppearance();
        });
    }

    private WallDocument Document => _session.Current;
    private double Scale => WallRoot.XamlRoot?.RasterizationScale ?? Math.Max(96u, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))) / 96d;
    private double WorkWidthDip => Math.Max(1, _workArea.Right - _workArea.Left - _frameWidth) / Scale;
    private double WorkHeightDip => Math.Max(1, _workArea.Bottom - _workArea.Top - _frameHeight) / Scale;

    private void OpenWall()
    {
        if (_opened) return;
        _opened = true;
        if (!SystemParametersInfoW(SpiGetWorkArea, 0, out _workArea, 0))
        {
            BlockView(ProductStrings.Get("Wall.ReadWorkAreaFailed"));
            return;
        }
        RefreshNativeFrame();
        var width = WorkWidthDip;
        var height = WorkHeightDip;
        var (columns, rows) = ChooseInitialGrid(width, height);
        var result = _session.Open(columns, rows);
        if (result.Document is null)
        {
            BlockView(string.Format(ProductStrings.Get("Wall.OpenFailed"), result.Status, string.Join("；", result.Diagnostics)));
            return;
        }
        _hasDocument = true;
        if (!_session.RefreshViewFromCurrent())
            BlockView(ProductStrings.Get("Wall.ViewFailed"), retry: true);
    }

    private (int Columns, int Rows) ChooseInitialGrid(double workWidth, double workHeight)
    {
        var targetArea = workWidth * workHeight * 0.49;
        var best = (Columns: 1, Rows: 1);
        var bestError = double.PositiveInfinity;
        for (var columns = 1; columns <= 64 && _metrics.WallWidth(columns) <= workWidth; columns++)
        {
            var wallWidth = _metrics.WallWidth(columns);
            if (columns > 1 && wallWidth > workWidth * 0.80) break;
            for (var rows = 1; rows <= 256 && _metrics.WallHeight(rows) <= workHeight * 0.82; rows++)
            {
                var area = wallWidth * _metrics.WallHeight(rows);
                var error = Math.Abs(area - targetArea);
                if (error >= bestError) continue;
                best = (columns, rows);
                bestError = error;
            }
        }
        return best;
    }

    private void RenderCommitted(WallDocument document)
    {
        RefreshAppearance();
        _puzzles.Sync(document);
        if (_carouselCommit) Surface.RefreshPuzzlePresentation(document);
        else Surface.Render(document);
        Surface.Visibility = Visibility.Visible;
        Surface.IsHitTestVisible = true;
        _viewBlocked = false;
        StatusPanel.Visibility = Visibility.Collapsed;
        RetryViewButton.Visibility = Visibility.Collapsed;
        if (!_carouselCommit) PlaceWall();
        UpdatePuzzleGate();
        Surface.RefreshClocks(_clock.UtcNow);
    }

    private void BlockView(string message, bool retry = false)
    {
        _viewBlocked = true;
        UpdatePuzzleGate();
        Surface.IsHitTestVisible = false;
        Surface.Visibility = Visibility.Collapsed;
        RetryViewButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        Status(message);
        AppWindow.MoveAndResize(new RectInt32(_workArea.Left, _workArea.Top, 560, 120));
    }

    private void OnRetryViewClicked(object sender, RoutedEventArgs e)
    {
        Surface.CancelImageRequests();
        Surface.Children.Clear();
        if (_session.RefreshViewFromCurrent())
        {
            if (!_adaptationBlocked)
            {
                StatusPanel.Visibility = Visibility.Collapsed;
                RetryViewButton.Visibility = Visibility.Collapsed;
            }
        }
        else Status(ProductStrings.Get("Wall.RebuildFailed"));
    }

    private void Status(string message)
    {
        StatusText.Text = message;
        StatusPanel.Visibility = Visibility.Visible;
    }

    private void PlaceWall()
    {
        ApplyViewport();
    }

    private string? CommitProposal(WallProposal proposal, IReadOnlyList<EntryChange>? changes = null,
        bool recordUndo = true)
    {
        if (_viewBlocked) return ProductStrings.Get("Wall.ViewBlocked");
        var result = _session.Commit(proposal, changes, recordUndo);
        if (!result.Committed) return string.Format(ProductStrings.Get("Common.SaveFailed"), string.Join("；", result.Diagnostics));
        if (result.ViewFailed)
            BlockView(string.Format(ProductStrings.Get("Wall.CommitViewFailed"), string.Join("；", result.Diagnostics)), retry: true);
        if (result.NeedsCleanup) Status(ProductStrings.Get("Wall.CleanupPending"));
        return null;
    }

    private string? Apply(WallAction action, IReadOnlyList<EntryChange>? changes = null)
    {
        var result = _session.Propose(action, WorkWidthDip, WorkHeightDip, _metrics);
        if (result.Proposal is null)
        {
            AppLog.Warn("WallApply", $"action={action.GetType().Name} rejection={result.Rejection} detail={result.Detail}");
            return string.Format(ProductStrings.Get("Wall.LocationUnavailable"),
                ProductStrings.WallRejectionReason(result.Rejection), string.Empty).TrimEnd();
        }
        return CommitProposal(result.Proposal, changes);
    }

    private string? PersistPuzzleCarousel(string id, PuzzleCarouselState carousel)
    {
        if (FindMember(id) is not PuzzleMember puzzle) return ProductStrings.Get("Wall.PuzzleMissing");
        var result = _session.Propose(new ReplaceMemberAction(Document.Generation, id,
            puzzle with { Carousel = carousel }), WorkWidthDip, WorkHeightDip, _metrics);
        if (result.Proposal is null)
        {
            AppLog.Warn("WallCarousel", $"member={id} rejection={result.Rejection} detail={result.Detail}");
            return string.Format(ProductStrings.Get("Wall.LocationUnavailable"),
                ProductStrings.WallRejectionReason(result.Rejection), string.Empty).TrimEnd();
        }
        _carouselCommit = true;
        try { return CommitProposal(result.Proposal, recordUndo: false); }
        finally { _carouselCommit = false; }
    }

    private void UpdatePuzzleGate()
    {
        var hidden = !AppWindow.IsVisible || AppWindow.Presenter is OverlappedPresenter
            { State: OverlappedPresenterState.Minimized };
        var imagesWereActive = Surface.ImagesActive;
        Surface.ImagesActive = !hidden && !_viewBlocked && !_adaptationBlocked;
        if (!Surface.ImagesActive) Surface.CancelImageRequests();
        else if (!imagesWereActive && _hasDocument)
        {
            Surface.Render(Document);
            _needsVisualRefresh = false;
        }
        _puzzles.SetGate(hidden || _viewBlocked || _adaptationBlocked ? PuzzleCarouselGate.Hidden
            : _modal ? PuzzleCarouselGate.Modal
            : _resizing || _gesture.Phase != GesturePhase.Idle ? PuzzleCarouselGate.Gesture : PuzzleCarouselGate.Ready);
        if (hidden || _viewBlocked || _adaptationBlocked || !Surface.HasClocks)
        {
            if (_traceInput && _clockTimer.IsRunning) AppLog.Info("ClockTimer", $"Stop visible={!hidden} utc={_clock.UtcNow:O}");
            _clockTimer.Stop();
        }
        else
        {
            if (_traceInput && !_clockTimer.IsRunning) AppLog.Info("ClockTimer", $"Start immediate-refresh utc={_clock.UtcNow:O}");
            Surface.RefreshClocks(_clock.UtcNow); _clockTimer.Start();
        }
        ImportReadinessChanged?.Invoke();
    }

    private async Task CreateTileAsync(string? groupId, int lane)
    {
        if (_modal || _viewBlocked || _adaptationBlocked || !_hasDocument) return;
        var tile = new TileMember { Id = $"tile-{Guid.NewGuid():N}", LocalBounds = new GridRect(0, 0, 1, 1) };
        var error = AddDefaultMember(tile, groupId, lane);
        if (error is not null) { Status(error); return; }
        await EditMemberAsync(tile.Id);
    }

    private async Task CreatePuzzleAsync(string? groupId, int lane)
    {
        if (_modal || _viewBlocked || _adaptationBlocked || !_hasDocument) return;
        var puzzle = new PuzzleMember { Id = $"puzzle-{Guid.NewGuid():N}", LocalBounds = new GridRect(0, 0, 4, 4),
            Partitions = Enumerable.Range(0, 16).Select(n => new GridRect(n % 4, n / 4, 1, 1)).ToArray() };
        var error = AddDefaultMember(puzzle, groupId, lane);
        if (error is not null) { Status(error); return; }
        await EditMemberAsync(puzzle.Id);
    }

    private string? AddDefaultMember(WallMember member, string? groupId, int lane) =>
        Apply(new AddMemberAction(Document.Generation, member, groupId, lane,
            Document.Lanes[lane].Groups.Count, groupId is null ? $"group-{Guid.NewGuid():N}" : null));

    private async Task EditMemberAsync(string id)
    {
        if (_modal || _viewBlocked || FindMember(id) is not { } tile ||
            tile is not (TileMember or PuzzleMember or ClockMember)) return;
        try
        {
            await BeginModalAsync();
            if (tile is ClockMember clock)
            {
                var clockDialog = new ClockEditorDialog(DialogOwner, clock, MaxMemberRows(), _clock,
                    replacement => Apply(new ReplaceMemberAction(Document.Generation, id, replacement)));
                ActiveDialog = clockDialog;
                await clockDialog.ShowAsync();
                return;
            }
            var entryPath = _session.ManagedEntryPath(tile);
            string? target = null;
            var idList = false;
            var opaque = false;
            string? entryDiagnostic = null;
            if (entryPath is not null)
            {
                try
                {
                    if (tile.Entry!.Kind == ManagedEntryKind.Lnk)
                    {
                        var fields = _session.LinkFiles.Read(entryPath);
                        target = fields.TargetPath;
                        idList = fields.HasIdList;
                        opaque = string.IsNullOrEmpty(target);
                    }
                    else
                    {
                        target = UrlShortcut.ReadUrlLine(File.ReadAllBytes(entryPath));
                        opaque = string.IsNullOrEmpty(target);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    System.Runtime.InteropServices.COMException)
                {
                    opaque = true;
                    entryDiagnostic = string.Format(ProductStrings.Get("Wall.EntryReadFailed"), ex.Message);
                }
            }
            if (tile is PuzzleMember puzzle)
            {
                var dialog = new PuzzleEditorDialog(DialogOwner, puzzle, target, idList, opaque,
                    entryDiagnostic, MaxMemberRows(), _images, entryPath, submission =>
                    Apply(new ReplaceMemberAction(Document.Generation, id, submission.Puzzle), submission.EntryChanges), _traceInput);
                ActiveDialog = dialog;
                await dialog.ShowAsync();
            }
            else
            {
                var dialog = new TileEditorDialog(DialogOwner, (TileMember)tile, target, idList, opaque,
                    entryDiagnostic, MaxMemberRows(), _images, entryPath, submission =>
                    Apply(new ReplaceMemberAction(Document.Generation, id, submission.Tile), submission.EntryChanges));
                ActiveDialog = dialog;
                await dialog.ShowAsync();
            }
        }
        catch (Exception ex) { Status(string.Format(ProductStrings.Get("Wall.EditFailed"), ex.Message)); }
        finally { EndModal(); }
    }

    private int MaxMemberRows() => Math.Clamp(
        (int)Math.Floor((WorkHeightDip - _metrics.TopMargin - _metrics.Margin + _metrics.Gap) / _metrics.Pitch), 1, 256);

    private void RemoveMember(string id)
    {
        if (_modal || _viewBlocked || FindMember(id) is not { } member) return;
        var changes = member.Entry is null ? [] : new EntryChange[] { new EntryChange.Remove(id) };
        var error = Apply(new RemoveMemberAction(Document.Generation, id), changes);
        if (error is not null) Status(error);
    }

    private void RenameGroup(string id, string? title)
    {
        if (_modal || _viewBlocked) return;
        var error = Apply(new RenameGroupAction(Document.Generation, id, title));
        if (error is not null) Status(error);
    }

    private WallMember? FindMember(string id) => Document.Lanes.SelectMany(l => l.Groups)
        .SelectMany(g => g.Members).FirstOrDefault(m => m.Id == id);

    private void LaunchMember(string id) => LaunchMember(id, elevated: false);

    private void LaunchMember(string id, bool elevated)
    {
        TraceInput($"LaunchRequested member={id} elevated={elevated} phase={_gesture.Phase} modal={_modal} blocked={_viewBlocked} suppressed={_suppressClickMember == id}");
        if (_modal || _viewBlocked || _suppressClickMember == id ||
            _gesture.Phase != GesturePhase.Idle && _gesture.MemberId == id)
        {
            TraceInput("LaunchBlocked by modal/view/suppression/active press");
            return;
        }
        var member = FindMember(id);
        if (member is null) { TraceInput("LaunchBlocked missing member"); return; }
        var entryPath = _session.ManagedEntryPath(member);
        if (entryPath is null) { TraceInput("LaunchBlocked no managed entry"); return; } // decoration has no activation
        var result = elevated ? _launcher.LaunchElevated(entryPath)
            : _launcher.Launch(entryPath);
        TraceInput($"LaunchResult outcome={result.Outcome} reason={result.FailureReason}");
        if (result.Outcome != LaunchOutcome.Launched)
        {
            AppLog.Warn("EntryLaunch", $"outcome={result.Outcome} reason={result.FailureReason}");
            Status(result.Outcome switch
            {
                LaunchOutcome.EntryMissing => ProductStrings.Get("Wall.EntryMissing"),
                LaunchOutcome.ElevatedDeclined => ProductStrings.Get("Wall.ElevationDeclined"),
                _ => ProductStrings.Get("Wall.LaunchFailed"),
            });
        }
        else AutomaticHideRequested?.Invoke();
    }

    private void ShowMemberLocation(string id)
    {
        if (_modal || _viewBlocked || FindMember(id) is not { } member) return;
        var entryPath = _session.ManagedEntryPath(member);
        if (entryPath is null) return;
        var result = _launcher.RevealInExplorer(entryPath);
        if (result.Outcome != LaunchOutcome.Launched)
        {
            AppLog.Warn("EntryLocation", $"outcome={result.Outcome} reason={result.FailureReason}");
            Status(result.Outcome == LaunchOutcome.EntryMissing
                ? ProductStrings.Get("Wall.EntryMissing")
                : ProductStrings.Get("Wall.LocationFailed"));
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && !_modal && _gesture.Phase == GesturePhase.Idle)
        {
            if (_resizing) CancelResize();
            else if (!WithinTextBox(e.OriginalSource as DependencyObject)) HideRequested?.Invoke();
            e.Handled = true;
            return;
        }
        if (e.Key == Windows.System.VirtualKey.Escape && _gesture.Phase != GesturePhase.Idle)
        {
            TerminateGesture();
            e.Handled = true;
        }
        if (e.Key == Windows.System.VirtualKey.Z && (GetKeyState(0x11) & 0x8000) != 0 &&
            !_modal && !_viewBlocked && !WithinTextBox(e.OriginalSource as DependencyObject))
        {
            var result = _session.Undo();
            if (result.ViewFailed)
                BlockView(string.Format(ProductStrings.Get("Wall.UndoViewFailed"), string.Join("；", result.Diagnostics)), retry: true);
            else if (!result.Committed) Status(string.Join("；", result.Diagnostics));
            e.Handled = true;
        }
    }

    private static bool WithinTextBox(DependencyObject? source)
    {
        for (var node = source; node is not null;
             node = VisualTreeHelper.GetParent(node))
            if (node is TextBox) return true;
        return false;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _tracedMovePointerId = 0;
        _tracedMovePhase = GesturePhase.Idle;
        TraceInput($"Press source={e.OriginalSource?.GetType().Name} pointer={e.Pointer.PointerId} phase={_gesture.Phase} left={e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed}");
        if (_modal || _viewBlocked || !e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed) return;
        try
        {
            var point = e.GetCurrentPoint(Surface).Position;
            var hit = Surface.Hit(point);
            TraceInput($"PressHit member={hit.MemberId} group={hit.GroupId} handle={hit.GroupHandle} x={point.X:F1} y={point.Y:F1}");
            if (hit.MemberId is null && !hit.GroupHandle) return;
            // Capture a complete settled frame in the ghost, including a flip interrupted by this press.
            _puzzles.SetGate(PuzzleCarouselGate.Gesture);
            var geometry = WallLayoutEngine.Measure(Document, _metrics);
            var bounds = hit.MemberId is { } memberId
                ? geometry.Groups.SelectMany(g => g.Members).First(m => m.MemberId == memberId).Bounds
                : geometry.Groups.First(g => g.GroupId == hit.GroupId).Bounds;
            var offset = new Point(point.X - bounds.X, point.Y - bounds.Y);
            var ghost = Surface.CreateDragVisual(hit.MemberId, hit.GroupHandle ? hit.GroupId : null);
            var origin = AppWindow.Position;
            var began = _gesture.Begin(e.Pointer.PointerId, point, offset, new Point(origin.X, origin.Y), ghost,
                hit.MemberId, hit.GroupHandle ? hit.GroupId : null);
            TraceInput($"Begin result={began} phase={_gesture.Phase}");
            if (began)
            {
                UpdatePuzzleGate();
                for (var source = e.OriginalSource as DependencyObject;
                     source is not null && !ReferenceEquals(source, Surface);
                     source = VisualTreeHelper.GetParent(source))
                {
                    if (source is not Button button) continue;
                    button.ReleasePointerCapture(e.Pointer);
                    TraceInput("Released child Button capture for surface gesture");
                    break;
                }
                var captured = Surface.CapturePointer(e.Pointer);
                TraceInput($"Capture result={captured} phase={_gesture.Phase}");
                if (!captured)
                {
                    TerminateGesture();
                    Status(ProductStrings.Get("Wall.CaptureFailed"));
                }
            }
            else UpdatePuzzleGate();
        }
        catch (Exception ex) { RecoverGestureError(ex); }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_traceInput && e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed &&
            (_tracedMovePointerId != e.Pointer.PointerId || _tracedMovePhase != _gesture.Phase))
        {
            TraceInput($"Move source={e.OriginalSource?.GetType().Name} pointer={e.Pointer.PointerId} phase={_gesture.Phase}");
            _tracedMovePointerId = e.Pointer.PointerId;
            _tracedMovePhase = _gesture.Phase;
        }
        if (_gesture.Phase == GesturePhase.Idle || _gesture.PointerId != e.Pointer.PointerId) return;
        try
        {
            var point = CommittedPointerPoint(e);
            if (!_gesture.Advance(point, 7)) return;
            var proposal = CalculateDrop(point);
            _gesture.SetPreview(proposal);
            Surface.Render(proposal?.Proposal?.Document ?? Document);
            Surface.RefreshClocks(_clock.UtcNow);
            PlaceWall(); // preview client bounds and anchor are as real as the proposal
            Surface.ShowDragCue(_gesture.MemberId, proposal?.Proposal is not null);
            var current = AppWindow.Position;
            DrawGhost(new Point(point.X - (current.X - _gesture.WindowOrigin.X) / Scale,
                point.Y - (current.Y - _gesture.WindowOrigin.Y) / Scale),
                proposal?.Proposal is not null);
        }
        catch (Exception ex) { RecoverGestureError(ex); }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        TraceInput($"Release source={e.OriginalSource?.GetType().Name} pointer={e.Pointer.PointerId} phase={_gesture.Phase}");
        if (_gesture.Phase == GesturePhase.Idle || _gesture.PointerId != e.Pointer.PointerId) return;
        try
        {
            var point = CommittedPointerPoint(e);
            var dragged = _gesture.Phase == GesturePhase.Dragging || _gesture.Advance(point, 7);
            var proposal = dragged ? CalculateDrop(point) : null;
            var id = _gesture.MemberId;
            TraceInput($"ReleaseDecision dragged={dragged} member={id} proposal={proposal?.Proposal is not null}");
            TerminateGesture();
            if (!dragged)
            {
                if (id is not null) LaunchMember(id);
                _suppressClickMember = id;
                DispatcherQueue.TryEnqueue(() => _suppressClickMember = null);
                return;
            }
            _suppressClickMember = id;
            DispatcherQueue.TryEnqueue(() => _suppressClickMember = null);
            if (proposal?.Proposal is null)
            {
                AppLog.Warn("WallDrop", $"rejection={proposal?.Rejection} detail={proposal?.Detail}");
                Status(string.Format(ProductStrings.Get("Wall.DropFailed"),
                    ProductStrings.WallRejectionReason(proposal?.Rejection ?? WallRejection.None)));
                return;
            }
            var error = CommitProposal(proposal.Proposal);
            if (error is not null) Status(error);
        }
        catch (Exception ex) { RecoverGestureError(ex); }
    }

    private Point CommittedPointerPoint(PointerRoutedEventArgs e)
    {
        var local = e.GetCurrentPoint(Surface).Position;
        var origin = AppWindow.Position;
        return _gesture.ToCommittedPoint(local, new Point(origin.X, origin.Y), Scale);
    }

    private WallLayoutResult? CalculateDrop(Point point)
    {
        var geometry = WallLayoutEngine.Measure(Document, _metrics);
        var rightAnchor = Settings.AnchorCorner is AnchorCorner.TopRight or AnchorCorner.BottomRight;
        var bottomAnchor = Document.Settings.AnchorCorner is AnchorCorner.BottomLeft or AnchorCorner.BottomRight;
        var minX = rightAnchor ? -(WorkWidthDip - geometry.WallWidth) : _metrics.Margin;
        var maxX = rightAnchor ? geometry.WallWidth : WorkWidthDip;
        var minY = bottomAnchor ? -(WorkHeightDip - geometry.WallHeight) : 0;
        var maxY = bottomAnchor ? geometry.WallHeight : WorkHeightDip;
        if (point.X < minX || point.Y < minY || point.X >= maxX || point.Y >= maxY)
            return InvalidDrop(ProductStrings.Get("Wall.OutsideWorkArea"));
        if (rightAnchor && point.X < _metrics.Margin)
        {
            if (_metrics.WallWidth(Document.Columns + 1) > WorkWidthDip)
                return InvalidDrop(ProductStrings.Get("Wall.OutsideLane"));
            // Extending inward from a right anchor prepends a lane. The proposal still commits once
            // against the original generation, preserving every existing group/member identity.
            var expanded = Document with { Columns = Document.Columns + 1,
                Lanes = new[] { new WallLane { Groups = [] } }.Concat(Document.Lanes).ToArray() };
            WallAction action;
            if (_gesture.GroupId is { } movedGroup)
                action = new MoveGroupAction(Document.Generation, movedGroup, 0, 0);
            else if (_gesture.MemberId is { } movedId && FindMember(movedId) is { } moved)
                action = new MoveMemberAction(Document.Generation, movedId,
                    new TileWall.Core.Grid.GridRect(0, 0, moved.LocalBounds.Width, moved.LocalBounds.Height),
                    null, 0, 0, _gesture.NewGroupId);
            else return null;
            return WallLayoutEngine.TryApply(expanded, action, WorkWidthDip, WorkHeightDip, _metrics);
        }
        var lane = (int)Math.Floor((point.X - _metrics.Margin) /
            (_metrics.LaneWidth + _metrics.ColumnGap));
        if (lane > Document.Columns || lane < 0 ||
            lane == Document.Columns && _metrics.WallWidth(Document.Columns + 1) > WorkWidthDip)
            return InvalidDrop(ProductStrings.Get("Wall.OutsideLane"));
        if (lane < Document.Columns && point.X > _metrics.WallWidth(Document.Columns) &&
            _metrics.WallWidth(Document.Columns + 1) > WorkWidthDip)
            return InvalidDrop(ProductStrings.Get("Wall.OutsideWall"));
        if (_gesture.GroupId is { } groupId)
        {
            var before = geometry.Groups.Count(g => g.LaneIndex == lane && g.GroupId != groupId &&
                g.TitleBounds.Y + g.TitleBounds.Height / 2 < point.Y);
            return _session.Propose(new MoveGroupAction(Document.Generation, groupId, lane, before),
                WorkWidthDip, WorkHeightDip, _metrics);
        }
        if (_gesture.MemberId is not { } memberId || FindMember(memberId) is not { } member) return null;
        var target = geometry.Groups.FirstOrDefault(g => g.LaneIndex == lane &&
            point.Y >= g.Bounds.Y && point.Y < g.Bounds.Y + g.Bounds.Height);
        var sameSource = Document.Lanes.SelectMany(l => l.Groups).FirstOrDefault(g =>
            g.Members.Any(m => m.Id == memberId));
        if (target is not null)
        {
            var first = target.Members.First();
            var firstMember = Document.Lanes.SelectMany(l => l.Groups).First(g => g.Id == target.GroupId)
                .Members.First(m => m.Id == first.MemberId);
            var memberOriginY = first.Bounds.Y - _metrics.MemberY(firstMember.LocalBounds.Row);
            var col = (int)Math.Round((point.X - _gesture.GrabOffset.X - target.Bounds.X) / _metrics.Pitch);
            var row = (int)Math.Round((point.Y - _gesture.GrabOffset.Y - memberOriginY) / _metrics.Pitch);
            if (col < 0 || col + member.LocalBounds.Width > 8 || row < 0)
                return InvalidDrop(ProductStrings.Get("Wall.OutsideGroup"));
            return _session.Propose(new MoveMemberAction(Document.Generation, memberId,
                new GridRect(col, row, member.LocalBounds.Width, member.LocalBounds.Height),
                target.GroupId), WorkWidthDip, WorkHeightDip, _metrics);
        }
        var sourceDisappears = sameSource?.Members.Count == 1;
        var insert = geometry.Groups.Count(g => g.LaneIndex == lane &&
            (!sourceDisappears || g.GroupId != sameSource?.Id) &&
            g.TitleBounds.Y + g.TitleBounds.Height / 2 < point.Y);
        return _session.Propose(new MoveMemberAction(Document.Generation, memberId,
            new GridRect(0, 0, member.LocalBounds.Width, member.LocalBounds.Height),
            null, lane, insert, _gesture.NewGroupId), WorkWidthDip, WorkHeightDip, _metrics);
    }

    private static WallLayoutResult InvalidDrop(string reason) =>
        new(null, WallRejection.InvalidAction, reason);

    private void DrawGhost(Point point, bool allowed)
    {
        var visual = _gesture.GhostVisual;
        if (visual is null) return;
        var ghost = _gesture.GhostHost;
        if (ghost is null)
        {
            ghost = new Border
            {
                Width = visual.Width + 4,
                Height = visual.Height + 4,
                Background = new SolidColorBrush(Color.FromArgb(180, 30, 49, 68)),
                BorderThickness = new Thickness(2),
                CornerRadius = WallCornerStyle.Corners(Document.Settings.VisualStyle),
                Child = visual,
            };
            _gesture.SetGhostHost(ghost);
            DragOverlay.Children.Add(ghost);
        }
        ghost.BorderBrush = new SolidColorBrush(allowed
            ? Microsoft.UI.Colors.LimeGreen : Microsoft.UI.Colors.OrangeRed);
        Canvas.SetLeft(ghost, point.X - _gesture.GrabOffset.X - 2);
        Canvas.SetTop(ghost, point.Y - _gesture.GrabOffset.Y - 2);
    }

    private void TerminateGesture()
    {
        if (_gesture.Phase == GesturePhase.Idle) return;
        TraceInput($"End phase={_gesture.Phase} member={_gesture.MemberId} group={_gesture.GroupId}");
        var wasDragging = _gesture.End();
        UpdatePuzzleGate();
        try { Surface.ReleasePointerCaptures(); }
        catch (Exception) { /* End already made capture loss idempotent. */ }
        DragOverlay.Children.Clear();
        try
        {
            if (wasDragging && !_viewBlocked)
            {
                Surface.Render(Document);
                Surface.RefreshClocks(_clock.UtcNow);
                PlaceWall();
            }
            Surface.ShowDragCue(null, false);
        }
        catch (Exception ex)
        {
            BlockView(string.Format(ProductStrings.Get("Wall.GestureViewFailed"), ex.Message), retry: true);
        }
    }

    private void RecoverGestureError(Exception error)
    {
        TraceInput($"GestureError {error.GetType().Name}: {error.Message}");
        TerminateGesture();
        UpdatePuzzleGate();
        if (!_viewBlocked) Status(string.Format(ProductStrings.Get("Wall.GestureFailed"), error.Message));
    }

    private void TraceInput(string message)
    {
        if (!_traceInput) return;
        AppLog.Warn("Phase2Input", message);
        AppLog.Flush();
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint parameter, out WorkAreaRect rect, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out WorkAreaRect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out WorkAreaRect rect);
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);
    [StructLayout(LayoutKind.Sequential)]
    private struct WorkAreaRect { public int Left; public int Top; public int Right; public int Bottom; }
}
