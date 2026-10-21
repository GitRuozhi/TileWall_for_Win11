using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TileWall.ApplicationModel;
using TileWall.Core.Application;
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
    private bool _resizing;
    private readonly IClock _clock = SystemClock.Instance;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _clockTimer;
    private string? _suppressClickMember;
    private double _gestureScrollOffset;
    private uint _tracedMovePointerId;
    private GesturePhase _tracedMovePhase;
    private readonly WallBackdrop _wallBackdrop;
    private readonly NativeBackdropSurface _nativeBackdropSurface;
    private bool _backdropActive = true;
    private bool _backdropUpdateQueued;
    private bool _windowClosing;

    internal MainWindow(RuntimeScope scope)
    {
        _traceInput = scope.IsTest || Environment.GetEnvironmentVariable("TILEWALL_TRACE_INPUT") == "1";
        _session = new WallSession(scope.DataRoot);
        _images = new WallImageService(_session.LinkFiles);
        InitializeComponent();
        CreateWindowsSidebar();
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
        _puzzles.ResolveEntryPath = _session.ManagedEntryPath;
        _puzzles.FrameChanged += Surface.UpdatePuzzleFrame;
        InitializeMenuStart();
        _session.SetViewRefresh(RenderCommitted);
        InitializePersistence();
        Surface.ResolveEntryPath = _session.ManagedEntryPath;
        Surface.ResolveLocationTarget = member => EntryLauncher.ResolveLocalTarget(_session.ManagedEntryPath(member), _session.LinkFiles);
        Surface.ResolvePuzzleFrame = _puzzles.FrameFor;
        Surface.ResolvePuzzleProgress = _puzzles.ProgressFor;
        Surface.ResolvePuzzleTransition = _puzzles.TransitionFor;
        Title = $"TileWall {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // RemoveNativeFrame owns the borderless chrome. Toggling the SDK title bar
            // also tears down its UIA providers and corrupts the heap on Windows 10.
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        RemoveNativeFrame();
        _nativeBackdropSurface = new NativeBackdropSurface(WinRT.Interop.WindowNative.GetWindowHandle(this),
            _performance.NativeErase, _performance.NativeMessage);
        InitializeNativeSizingPrototype();
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
        Surface.PuzzleNextPageRequested += () =>
        {
            if (PersistenceInteractionBlocked) return;
            UpdatePuzzleGate();
            _puzzles.AdvanceAll();
        };
        Surface.NewClockRequested += (groupId, lane) => _ = CreateClockAsync(groupId, lane);
        Surface.SettingsRequested += () => SettingsRequested?.Invoke();
        Surface.ImportRequested += (groupId, lane) => _ = OpenImportAsync(groupId, lane);
        Surface.TitleEditFinished += () => ImportReadinessChanged?.Invoke();
        Surface.ContextMenuVisibilityChanged += OnContextMenuVisibilityChanged;
        AppWindow.Changed += (_, args) =>
        {
            var started = _performance.NativeStageStart();
            if (args.DidPositionChange || args.DidSizeChange) InvalidateSnapshotIfGeometryChanged();
            try { if (args.DidVisibilityChange || args.DidPresenterChange || args.DidSizeChange) UpdatePuzzleGate(); }
            finally { _performance.WindowChanged(started); }
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
                QueueAutomaticHideCheck("Deactivated");
            }
        };
        Closed += (_, _) =>
        {
            _windowClosing = true;
            StopPersistenceAfterClose();
            DisposeNativeSizingPrototype();
            DisposeInteractionScheduling();
            _viewportTransition = null;
            CancelResize();
            _dragInputPending = _resizeInputPending = false;
            StopFrameSubscriptionIfIdle();
            _focusCheckRetry?.Stop();
            DisposeAppearance();
            _nativeBackdropSurface.Dispose();
            TerminateGesture();
            _puzzles.Dispose();
            _clockTimer.Stop();
            Surface.ClearTransientInteraction();
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
    private double WorkWidthDip => CurrentViewportBudget.MaxWidthDip;
    private double WorkHeightDip => CurrentViewportBudget.MaxHeightDip;

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
        ReadPrimaryScreen();
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
        var scrollAnchor = CaptureCommittedScrollAnchor(document);
        PrepareCommittedImportIcons(document);
        RefreshAppearance();
        _puzzles.Sync(document);
        if (_carouselCommit) Surface.RefreshPuzzlePresentation(document);
        else Surface.Render(document);
        if (!_carouselCommit) RenderMenuStart(document);
        Surface.Visibility = Visibility.Visible;
        Surface.IsHitTestVisible = !PersistenceInteractionBlocked;
        _viewBlocked = false;
        StatusPanel.Visibility = Visibility.Collapsed;
        RetryViewButton.Visibility = Visibility.Collapsed;
        if (!_carouselCommit) PlaceWall();
        RestoreCommittedScrollAnchor(document, scrollAnchor);
        UpdatePuzzleGate();
        Surface.RefreshClocks(_clock.UtcNow);
    }

    private void BlockView(string message, bool retry = false)
    {
        if (_session.ResultUnknown)
        {
            message = WithDiagnostic(ProductStrings.Get("Wall.ResultUnknown"), message);
            retry = false;
        }
        else if (_session.RequiresReopen)
        {
            message = WithDiagnostic(ProductStrings.Get("Wall.RequiresReopen"), message);
            retry = false;
        }
        else if (_session.RecoveryBlocked)
        {
            message = WithDiagnostic(ProductStrings.Get("Wall.RecoveryBlocked"), message);
            retry = false;
        }
        _viewBlocked = true;
        UpdateNativeSizingRegion();
        UpdatePuzzleGate();
        Surface.IsHitTestVisible = false;
        Surface.Visibility = Visibility.Collapsed;
        RetryViewButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        Status(message);
        AppWindow.MoveAndResize(new RectInt32(_workArea.Left, _workArea.Top, 560, 120));
    }

    private static string WithDiagnostic(string summary, string detail) =>
        string.IsNullOrWhiteSpace(detail) || string.Equals(detail, summary, StringComparison.Ordinal)
            ? summary : summary + "\n" + detail;

    private void OnRetryViewClicked(object sender, RoutedEventArgs e)
    {
        if (_session.RequiresReopen || _session.RecoveryBlocked || _session.ResultUnknown)
        {
            BlockView(ProductStrings.Get("Wall.RequiresReopen"));
            return;
        }
        Surface.ClearTransientInteraction();
        Surface.ResetProjection();
        if (_session.RefreshViewFromCurrent())
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            RetryViewButton.Visibility = Visibility.Collapsed;
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
        if (PersistenceMutationBlocked) return ProductStrings.Get("Wall.SettingsSaving");
        if (_viewBlocked) return ProductStrings.Get("Wall.ViewBlocked");
        var fit = WallViewportPolicy.Validate(proposal, ViewportBudget(proposal.Document.Settings), _metrics);
        if (fit.Proposal is null) return ViewportFailure(fit);
        try
        {
            var commitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var media = Task.Run(() => PrepareFormalMediaAsync(proposal)).GetAwaiter().GetResult();
            var result = _session.Commit(media.Proposal, changes, recordUndo, media.Assets);
            _performance.Commit(System.Diagnostics.Stopwatch.GetTimestamp() - commitStarted);
            return DescribeCommit(result);
        }
        catch (Exception ex)
        {
            var error = _session.ResultUnknown
                ? ProductStrings.Get("Wall.ResultUnknown")
                : string.Format(ProductStrings.Get("Common.SaveFailed"), ex.Message);
            if (_session.ViewBlocked) BlockView(error);
            return error;
        }
    }

    private string? DescribeCommit(WallSessionResult result)
    {
        if (!result.Committed)
        {
            var error = _session.ResultUnknown ? ProductStrings.Get("Wall.ResultUnknown")
                : _session.RequiresReopen ? ProductStrings.Get("Wall.RequiresReopen")
                : string.Format(ProductStrings.Get("Common.SaveFailed"), string.Join("；", result.Diagnostics));
            if (_session.ViewBlocked) BlockView(error);
            return error;
        }
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
            if (result.Detail is "CONTENT_TOO_LARGE" || result.Detail?.StartsWith("GROUP_TOO_TALL:", StringComparison.Ordinal) == true) return ViewportFailure(result);
            return string.Format(ProductStrings.Get("Wall.LocationUnavailable"),
                ProductStrings.WallRejectionReason(result.Rejection), string.Empty).TrimEnd();
        }
        return CommitProposal(result.Proposal, changes);
    }

    private async Task<string?> PersistPuzzleCarousel(string id, ImageCarouselState carousel, Func<bool> stillCurrent)
    {
        // A prepared capture may wait behind another member's accepted save. Build
        // its proposal only after that generation has been adopted on the UI.
        await DrainPersistenceAsync();
        if (!stillCurrent()) return null;
        if (_persistenceAdmissionClosed || _windowClosing)
            return ProductStrings.Get("Wall.SettingsSaving");
        if (FindMember(id) is not { } member || member is not (TileMember or PuzzleMember))
            return ProductStrings.Get("Wall.PuzzleMissing");
        var result = _session.Propose(new ReplaceMemberAction(Document.Generation, id,
            member with { Carousel = carousel }), WorkWidthDip, WorkHeightDip, _metrics);
        if (result.Proposal is null)
        {
            AppLog.Warn("WallCarousel", $"member={id} rejection={result.Rejection} detail={result.Detail}");
            if (result.Detail is "CONTENT_TOO_LARGE" || result.Detail?.StartsWith("GROUP_TOO_TALL:", StringComparison.Ordinal) == true) return ViewportFailure(result);
            return string.Format(ProductStrings.Get("Wall.LocationUnavailable"),
                ProductStrings.WallRejectionReason(result.Rejection), string.Empty).TrimEnd();
        }
        return await CommitProposalAsync(result.Proposal, recordUndo: false, carousel: true);
    }

    private void UpdatePuzzleGate()
    {
        var hidden = !AppWindow.IsVisible || AppWindow.Presenter is OverlappedPresenter
            { State: OverlappedPresenterState.Minimized };
        var imagesWereActive = Surface.ImagesActive;
        Surface.ImagesActive = !hidden && !_viewBlocked;
        if (!Surface.ImagesActive) Surface.ClearTransientInteraction();
        else if (!imagesWereActive && _hasDocument)
        {
            Surface.Render(Document);
            _needsVisualRefresh = false;
        }
        _puzzles.SetGate(hidden || _viewBlocked ? MemberCarouselGate.Hidden
            : _modal ? MemberCarouselGate.Modal
            : _resizing || _gesture.Phase != GesturePhase.Idle ? MemberCarouselGate.Gesture : MemberCarouselGate.Ready);
        if (hidden || _viewBlocked || !Surface.HasClocks)
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
        if (PersistenceInteractionBlocked || _modal || _viewBlocked || !_hasDocument) return;
        var tile = new TileMember { Id = $"tile-{Guid.NewGuid():N}", LocalBounds = new GridRect(0, 0, 2, 2) };
        var error = AddDefaultMember(tile, groupId, lane);
        if (error is not null) { Status(error); return; }
        await EditMemberAsync(tile.Id, isNewPlaceholder: true);
    }

    private async Task CreatePuzzleAsync(string? groupId, int lane)
    {
        if (PersistenceInteractionBlocked || _modal || _viewBlocked || !_hasDocument) return;
        var puzzle = new PuzzleMember { Id = $"puzzle-{Guid.NewGuid():N}", LocalBounds = new GridRect(0, 0, 4, 4),
            Partitions = [new GridRect(0, 0, 2, 2), new GridRect(2, 0, 2, 2),
                new GridRect(0, 2, 2, 2), new GridRect(2, 2, 2, 2)] };
        var error = AddDefaultMember(puzzle, groupId, lane);
        if (error is not null) { Status(error); return; }
        await EditMemberAsync(puzzle.Id, isNewPlaceholder: true);
    }

    private string? AddDefaultMember(WallMember member, string? groupId, int lane)
    {
        lane = WallViewportPolicy.MapCreationLane(Document, lane);
        lane = Math.Clamp(lane, 0, Settings.FullScreen || Settings.ExpandColumns ? 63 : Document.Columns);
        return Apply(new AddMemberAction(Document.Generation, member, groupId, lane,
            lane >= Document.Columns ? 0 : Document.Lanes[lane].Groups.Count,
            groupId is null ? $"group-{Guid.NewGuid():N}" : null));
    }

    private async Task EditMemberAsync(string id, bool isNewPlaceholder = false)
    {
        if (PersistenceInteractionBlocked || _modal || _viewBlocked || FindMember(id) is not { } member ||
            member is not (TileMember or PuzzleMember or ClockMember)) return;
        string? duplicateToFocus = null;
        try
        {
            if (!await BeginModalAsync()) return;
            if (member is ClockMember clock)
            {
                var clockDialog = new ClockEditorDialog(DialogOwner, clock, MaxMemberRows(), _clock,
                    replacement => Apply(new ReplaceMemberAction(Document.Generation, id, replacement)));
                ActiveDialog = clockDialog;
                await clockDialog.ShowAsync();
                return;
            }
            var dialog = new SharedMemberEditorDialog(DialogOwner, member, _images,
                _session.ManagedEntryPath(member), _session.LinkFiles, MaxMemberRows(),
                (replacement, changes) => SaveSharedMember(replacement, changes, isNewPlaceholder,
                    existing => duplicateToFocus = existing), Settings.VisualStyle);
            ActiveDialog = dialog;
            AttachGalleryEditor(dialog);
            await dialog.ShowAsync();
        }
        catch (Exception ex) { Status(string.Format(ProductStrings.Get("Wall.EditFailed"), ex.Message)); }
        finally
        {
            EndModal();
            if (duplicateToFocus is not null)
            {
                Surface.FocusMember(duplicateToFocus);
                Status(ProductStrings.Get("SharedEditor.DuplicateLocated"));
            }
        }
    }

    private string? SaveSharedMember(WallMember replacement, IReadOnlyList<EntryChange> changes,
        bool isNewPlaceholder, Action<string> locateDuplicate)
    {
        if (FindMember(replacement.Id) is not { } current) return ProductStrings.Get("Wall.PuzzleMissing");
        try
        {
            // Title/colour/size saves cannot change launch identity. Scanning every
            // member's shortcut (COM + file IO) was the dominant cost of those saves.
            var identityMayChange = replacement.TargetInput != current.TargetInput ||
                replacement.Entry != current.Entry ||
                changes.Any(c => c is EntryChange.CopyShortcut or EntryChange.CreateLink or
                    EntryChange.CreateUrl or EntryChange.ChangeTarget);
            if (identityMayChange)
            {
                var identities = ImportIdentities;
                var desired = DesiredEditorIdentity(current, replacement, changes, identities);
                if (desired is not null)
                {
                    var duplicate = Document.Lanes.SelectMany(l => l.Groups).Where(g => g.Members.Any(m => m.Id == current.Id)).SelectMany(g => g.Members)
                        .FirstOrDefault(other => other.Id != replacement.Id &&
                            StartMenuImportPreparation.TryIdentity(other, _session.DataRoot, identities)?.Key == desired.Key);
                    if (duplicate is not null)
                    {
                        if (!isNewPlaceholder) return ProductStrings.Get("SharedEditor.Duplicate");
                        IReadOnlyList<EntryChange> removals = current.Entry is null ? [] : [new EntryChange.Remove(current.Id)];
                        var error = Apply(new RemoveMemberAction(Document.Generation, current.Id), removals);
                        if (error is not null) return error;
                        locateDuplicate(duplicate.Id);
                        return null;
                    }
                }
            }
            if (changes.Count == 0 && !MemberPresentationRules.HasUserPropertyChanges(current, replacement)) return null;
            return Apply(new ReplaceMemberAction(Document.Generation, replacement.Id, replacement), changes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            System.Runtime.InteropServices.COMException)
        { return string.Format(ProductStrings.Get("Common.SaveFailed"), ex.Message); }
    }

    private LaunchIdentity? DesiredEditorIdentity(WallMember current, WallMember replacement,
        IReadOnlyList<EntryChange> changes, LaunchIdentityService identities)
    {
        if (changes.OfType<EntryChange.Remove>().Any()) return null;
        if (changes.OfType<EntryChange.CopyShortcut>().FirstOrDefault() is { } copy)
            return identities.ResolveInput(copy.SourcePath);
        if (changes.OfType<EntryChange.CreateLink>().FirstOrDefault() is { } createLink)
            return identities.ResolveInput(createLink.TargetPath);
        if (changes.OfType<EntryChange.CreateUrl>().FirstOrDefault() is { } createUrl)
            return identities.ResolveInput(createUrl.Url);
        if (changes.OfType<EntryChange.ChangeTarget>().FirstOrDefault() is { } target)
        {
            if (current.Entry?.Kind == ManagedEntryKind.Lnk && _session.ManagedEntryPath(current) is { } managed)
            {
                var fields = _session.LinkFiles.Read(managed);
                return LaunchIdentityService.ForPath(target.TargetPathOrUrl, fields.Arguments, fields.WorkingDirectory);
            }
            return identities.ResolveInput(target.TargetPathOrUrl);
        }
        return identities.ResolveMember(current, _session.ManagedEntryPath(current)) ??
            identities.ResolveInput(replacement.TargetInput ?? string.Empty);
    }

    private int MaxMemberRows() => Math.Max(0,
        Math.Min(WallViewportPolicy.MaximumMemberRows, CurrentViewportBudget.MaxRows - 1));
    private void RemoveMember(string id)
    {
        if (PersistenceInteractionBlocked || _modal || _viewBlocked || FindMember(id) is not { } member) return;
        var error = Apply(new UnpinMemberAction(Document.Generation, id));
        if (error is not null) Status(error);
    }

    private void RenameGroup(string id, string? title)
    {
        if (PersistenceInteractionBlocked || _modal || _viewBlocked) return;
        var error = Apply(new RenameGroupAction(Document.Generation, id, title));
        if (error is not null) Status(error);
    }

    private WallMember? FindMember(string id) => Document.Lanes.SelectMany(l => l.Groups)
        .SelectMany(g => g.Members).FirstOrDefault(m => m.Id == id);

    private void LaunchMember(string id) => LaunchMember(id, elevated: false);

    private void LaunchMember(string id, bool elevated)
    {
        TraceInput($"LaunchRequested member={id} elevated={elevated} phase={_gesture.Phase} modal={_modal} blocked={_viewBlocked} suppressed={_suppressClickMember == id}");
        if (PersistenceInteractionBlocked || _modal || _viewBlocked || _suppressClickMember == id ||
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
        if (PersistenceInteractionBlocked || _modal || _viewBlocked || FindMember(id) is not { } member) return;
        var entryPath = _session.ManagedEntryPath(member);
        if (entryPath is null) return;
        var result = _launcher.RevealInExplorer(entryPath, _session.LinkFiles);
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
        if (PersistenceInteractionBlocked) { e.Handled = true; return; }
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
            WallSessionResult result;
            try
            {
                result = _session.Undo(candidate =>
                {
                    var fit = WallViewportPolicy.Validate(candidate, CurrentViewportBudget, _metrics);
                    return fit.Success ? null : ViewportFailure(fit);
                });
            }
            catch (Exception ex)
            {
                BlockView(_session.ResultUnknown ? ProductStrings.Get("Wall.ResultUnknown")
                    : string.Format(ProductStrings.Get("Wall.UndoViewFailed"), ex.Message));
                e.Handled = true;
                return;
            }
            if (result.ViewFailed || _session.ViewBlocked)
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
        if (PersistenceInteractionBlocked || _modal || _viewBlocked || !e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed) return;
        try
        {
            var point = e.GetCurrentPoint(Surface).Position;
            var hit = Surface.Hit(point);
            TraceInput($"PressHit member={hit.MemberId} group={hit.GroupId} handle={hit.GroupHandle} x={point.X:F1} y={point.Y:F1}");
            if (hit.MemberId is null && !hit.GroupHandle) return;
            // Capture a complete settled frame in the ghost, including a flip interrupted by this press.
            _puzzles.SetGate(MemberCarouselGate.Gesture);
            StopViewportTransition();
            var geometry = Surface.Geometry ?? WallLayoutEngine.Measure(Document, _metrics);
            var bounds = hit.MemberId is { } memberId
                ? geometry.Groups.SelectMany(g => g.Members).First(m => m.MemberId == memberId).Bounds
                : geometry.Groups.First(g => g.GroupId == hit.GroupId).Bounds;
            var offset = new Point(point.X - bounds.X, point.Y - bounds.Y);
            var ghost = Surface.CreateDragVisual(hit.MemberId, hit.GroupHandle ? hit.GroupId : null);
            var origin = AppWindow.Position;
            _gestureScrollOffset = WallScroll.HorizontalOffset;
            var began = _gesture.Begin(e.Pointer.PointerId, point, offset, new Point(origin.X, origin.Y), ghost,
                hit.MemberId, hit.GroupHandle ? hit.GroupId : null);
            TraceInput($"Begin result={began} phase={_gesture.Phase}");
            if (began)
            {
                BeginGestureFrames(geometry);
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
            _latestDragPoint = CommittedPointerPoint(e);
            _dragInputPending = true;
            _performance.Input();
            EnsureFrameSubscription();
        }
        catch (Exception ex) { RecoverGestureError(ex); }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        TraceInput($"Release source={e.OriginalSource?.GetType().Name} pointer={e.Pointer.PointerId} phase={_gesture.Phase}");
        if (_gesture.Phase == GesturePhase.Idle || _gesture.PointerId != e.Pointer.PointerId) return;
        try
        {
            if (!GestureSnapshotIsCurrent()) { TerminateGesture(); return; }
            _dragInputPending = false;
            var point = CommittedPointerPoint(e);
            _performance.Input();
            var dragged = _gesture.Phase == GesturePhase.Dragging || _gesture.Advance(point, 7);
            var proposal = dragged ? CalculateDrop(point) : null;
            var id = _gesture.MemberId;
            TraceInput($"ReleaseDecision dragged={dragged} member={id} proposal={proposal?.Proposal is not null}");
            TerminateGesture(restoreScroll: false, restoreView: proposal?.Proposal is null);
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
                Status(proposal?.Detail is "CONTENT_TOO_LARGE" || proposal?.Detail?.StartsWith("GROUP_TOO_TALL:", StringComparison.Ordinal) == true
                    ? ViewportFailure(proposal!) : string.Format(ProductStrings.Get("Wall.DropFailed"),
                        ProductStrings.WallRejectionReason(proposal?.Rejection ?? WallRejection.None)));
                return;
            }
            _ = CommitProposalAsync(proposal.Proposal, settle: SettleReleasePersistence);
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
        var geometry = _gestureGeometry ?? WallLayoutEngine.Measure(Document, _metrics);
        var derivedLanes = Settings.FullScreen || Settings.ExpandColumns;
        var rightAnchor = !derivedLanes && Settings.AnchorCorner is AnchorCorner.TopRight or AnchorCorner.BottomRight;
        if (point.X < 0 || point.Y < 0 || point.X >= (derivedLanes ? Math.Max(geometry.WallWidth, WallScroll.ViewportWidth) : geometry.WallWidth) || point.Y >= (derivedLanes ? WallScroll.ViewportHeight : geometry.WallHeight))
            return InvalidDrop(ProductStrings.Get("Wall.OutsideWall"));
        if (rightAnchor && point.X < _metrics.Margin)
        {
            if (Document.Columns >= 64)
                return InvalidDrop(ProductStrings.Get("Wall.OutsideLane"));
            // Extending inward from a right anchor prepends a lane. The proposal still commits once
            // against the original generation, preserving every existing group/member identity.
            var expanded = Document with { Columns = Document.Columns + 1,
                Lanes = new[] { new WallLane { Groups = [] } }.Concat(Document.Lanes).ToArray() };
            WallAction action;
            if (_gesture.GroupId is { } movedGroup)
                action = new MoveGroupAction(Document.Generation, movedGroup, 0, 0);
            else if (_gesture.MemberId is { } movedId && _gestureMembers.GetValueOrDefault(movedId) is { } moved)
                action = new MoveMemberAction(Document.Generation, movedId,
                    new TileWall.Core.Grid.GridRect(0, 0, moved.LocalBounds.Width, moved.LocalBounds.Height),
                    null, 0, 0, _gesture.NewGroupId);
            else return null;
            return CachedDrop(action, expanded);
        }
        var lane = !derivedLanes && !rightAnchor && point.X >= geometry.WallWidth - _metrics.Margin ? Document.Columns :
            (int)Math.Floor((point.X - _metrics.Margin) / (_metrics.LaneWidth + _metrics.ColumnGap));
        lane = WallViewportPolicy.MapCreationLane(Document, lane);
        if (lane < 0 || lane >= 64 || !derivedLanes && lane > Document.Columns)
            return InvalidDrop(ProductStrings.Get("Wall.OutsideLane"));
        if (_gesture.GroupId is { } groupId)
        {
            var before = geometry.Groups.Count(g => g.LaneIndex == lane && g.GroupId != groupId &&
                g.TitleBounds.Y + g.TitleBounds.Height / 2 < point.Y);
            return CachedDrop(new MoveGroupAction(Document.Generation, groupId, lane, before));
        }
        if (_gesture.MemberId is not { } memberId || _gestureMembers.GetValueOrDefault(memberId) is not { } member) return null;
        var target = geometry.Groups.FirstOrDefault(g => g.LaneIndex == lane &&
            point.Y >= g.Bounds.Y && point.Y < g.Bounds.Y + g.Bounds.Height);
        var sameSource = _gestureGroups.Values.FirstOrDefault(g =>
            g.Members.Any(m => m.Id == memberId));
        if (target is not null)
        {
            if (sameSource?.Id != target.GroupId && _gestureGroups.TryGetValue(target.GroupId, out var destination))
            {
                var desired = GestureLaunchIdentity(member);
                if (desired is not null && destination.Members.Any(other => other.Id != memberId && GestureLaunchIdentity(other) == desired))
                    return InvalidDrop(ProductStrings.Get("SharedEditor.Duplicate"));
            }
            var first = target.Members.First();
            var firstMember = _gestureMembers[first.MemberId];
            var memberOriginY = first.Bounds.Y - _metrics.MemberY(firstMember.LocalBounds.Row);
            var col = (int)Math.Round((point.X - _gesture.GrabOffset.X - target.Bounds.X) / _metrics.Pitch);
            var row = (int)Math.Round((point.Y - _gesture.GrabOffset.Y - memberOriginY) / _metrics.Pitch);
            if (col < 0 || col + member.LocalBounds.Width > 8 || row < 0)
                return InvalidDrop(ProductStrings.Get("Wall.OutsideGroup"));
            return CachedDrop(new MoveMemberAction(Document.Generation, memberId,
                new GridRect(col, row, member.LocalBounds.Width, member.LocalBounds.Height), target.GroupId));
        }
        var sourceDisappears = sameSource?.Members.Count == 1;
        var insert = geometry.Groups.Count(g => g.LaneIndex == lane &&
            (!sourceDisappears || g.GroupId != sameSource?.Id) &&
            g.TitleBounds.Y + g.TitleBounds.Height / 2 < point.Y);
        return CachedDrop(new MoveMemberAction(Document.Generation, memberId,
            new GridRect(0, 0, member.LocalBounds.Width, member.LocalBounds.Height),
            null, lane, insert, _gesture.NewGroupId));
    }

    private readonly Dictionary<string, WallLayoutResult> _invalidDrops = new(StringComparer.Ordinal);
    private WallLayoutResult InvalidDrop(string reason)
    {
        if (!_invalidDrops.TryGetValue(reason, out var result))
            _invalidDrops.Add(reason, result = new(null, WallRejection.InvalidAction, reason));
        return result;
    }

    private void DrawGhost(Point point, bool allowed)
    {
        var visual = _gesture.GhostVisual;
        if (visual is null) return;
        var inset = _gesture.GroupId is null ? 1d : 0d;
        var ghost = _gesture.GhostHost;
        if (ghost is null)
        {
            ghost = new Border
            {
                Width = visual.Width + inset * 2,
                Height = visual.Height + inset * 2,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(inset),
                CornerRadius = WallCornerStyle.Corners(Document.Settings.VisualStyle),
                Child = visual,
                RenderTransform = new TranslateTransform(),
            };
            _gesture.SetGhostHost(ghost);
            DragOverlay.Children.Add(ghost);
        }
        var color = allowed ? Color.FromArgb(210, 255, 255, 255) : Microsoft.UI.Colors.OrangeRed;
        if (ghost.BorderBrush is not SolidColorBrush brush || brush.Color != color) ghost.BorderBrush = new SolidColorBrush(color);
        point = ContentToViewport(point);
        PositionGhost(ghost, point.X - _gesture.GrabOffset.X - inset,
            point.Y - (_gesture.GroupId is null ? _gesture.GrabOffset.Y : 12) - inset);
    }

    private void TerminateGesture(bool restoreScroll = true, bool restoreView = true)
    {
        if (_gesture.Phase == GesturePhase.Idle) return;
        TraceInput($"End phase={_gesture.Phase} member={_gesture.MemberId} group={_gesture.GroupId}");
        _dragInputPending = false;
        ClearGhostComposition();
        var wasDragging = _gesture.End();
        _dropCache.Clear(); _gestureDocument = null; _gestureGeometry = null; _drawnDrop = null;
        StopFrameSubscriptionIfIdle();
        if (restoreView) _performance.End("cancel-or-click", Surface.VisualCreationCount, Surface.GridCreationCount);
        UpdatePuzzleGate();
        try { Surface.ReleasePointerCaptures(); }
        catch (Exception) { /* End already made capture loss idempotent. */ }
        DragOverlay.Children.Clear();
        if (restoreScroll) WallScroll.ChangeView(_gestureScrollOffset, 0, null, disableAnimation: true);
        try
        {
            if (wasDragging && restoreView && !_viewBlocked)
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
        _performance.End("error", Surface.VisualCreationCount, Surface.GridCreationCount);
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
