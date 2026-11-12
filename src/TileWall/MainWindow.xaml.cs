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
    private bool _initialOpenBlocked;
    private string? _openFailureMessage;
    private bool _resizing;
    private readonly IClock _clock = SystemClock.Instance;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _clockTimer;
    private readonly StatusOverlay _statusOverlay;
    private string? _suppressClickMember;
    private Point _gestureScrollOffset;
    private ulong _gesturePressTimestamp;
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
        StatusViewport.Children.Remove(StatusActions);
        _statusOverlay = new StatusOverlay(actions: StatusActions);
        _statusOverlay.TransientExpired += RestoreStatusAfterTransient;
        StatusViewport.Children.Add(_statusOverlay);
        ActionIcon.Set(RetryViewButton, ProductStrings.Get("Wall.Retry"), ActionIcon.Refresh);
        ActionIcon.Set(RetryOpenButton, ProductStrings.Get("Wall.RetryOpen"), ActionIcon.Refresh);
        ActionIcon.Set(OpenDataFolderButton, ProductStrings.Get("Wall.OpenDataFolder"), ActionIcon.OpenFolderHorizontal);
        CreateWindowsSidebar();
        _clockTimer = DispatcherQueue.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.IsRepeating = true;
        _clockTimer.Tick += (_, _) =>
        {
            if (_windowClosing || ShutdownStarted) return;
            if (_traceInput) AppLog.Info("ClockTimer", $"Tick visible={AppWindow.IsVisible} utc={_clock.UtcNow:O}");
            Surface.RefreshClocks(_clock.UtcNow);
        };
        _puzzles = new PuzzlePresentationCoordinator(_images, DispatcherQueue, () => Scale,
            PersistPuzzleCarousel, Status, _traceInput);
        _puzzles.ResolveEntryPath = _session.ManagedEntryPath;
        _puzzles.PersistAutomaticSeries = PersistAutomaticIconSeries;
        _puzzles.FrameChanged += Surface.UpdatePuzzleFrame;
        WallScroll.ViewChanged += (_, _) => UpdateVisibleWallFrames();
        InitializeMenuStart();
        _session.SetViewRefresh(RenderCommitted);
        InitializePersistence();
        Surface.ResolveEntryPath = _session.ManagedEntryPath;
        Surface.ResolveLocationTarget = member => EntryLauncher.ResolveLocalTarget(_session.ManagedEntryPath(member), _session.LinkFiles);
        Surface.ResolvePuzzleFrame = _puzzles.FrameFor;
        Surface.ResolvePuzzleProgress = _puzzles.ProgressFor;
        Surface.ResolvePuzzleTransition = _puzzles.TransitionFor;
        Surface.CanAdvanceMemberImage = _puzzles.CanAdvance;
        Title = $"TileWall {ProductVersion.Display}";
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
        Surface.MemberSizeRequested += ResizeMemberPreset;
        Surface.GroupRenameRequested += RenameGroup;
        Surface.NewTileRequested += (groupId, lane) => _ = CreateTileAsync(groupId, lane);
        Surface.MemberNextImageRequested += id =>
        {
            if (PersistenceInteractionBlocked || _modal || _viewBlocked ||
                FindMember(id)?.Images.Kind != MemberImageSourceKind.Folder) return;
            UpdatePuzzleGate();
            _puzzles.Advance(id);
        };
        Surface.NewClockRequested += (groupId, lane) => _ = CreateClockAsync(groupId, lane);
        Surface.SettingsRequested += () => SettingsRequested?.Invoke();
        Surface.ImportRequested += (groupId, lane) => _ = OpenImportAsync(groupId, lane);
        Surface.TitleEditFinished += () => ImportReadinessChanged?.Invoke();
        Surface.ContextMenuVisibilityChanged += OnContextMenuVisibilityChanged;
        AppWindow.Changed += (_, args) =>
        {
            if (_windowClosing || ShutdownStarted) return;
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
            if (ReferenceEquals(e.OriginalSource, Surface)) { CancelDeferredGesturePress(e.Pointer.PointerId); TerminateGesture(); }
        };
        Surface.PointerCanceled += (_, e) =>
        {
            TraceInput($"Canceled source={e.OriginalSource?.GetType().Name} phase={_gesture.Phase} pointer={e.Pointer.PointerId}");
            CancelDeferredGesturePress(e.Pointer.PointerId);
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
            if (_windowClosing || ShutdownStarted) return;
            var active = e.WindowActivationState != WindowActivationState.Deactivated;
            // The new native commit must include the activation Z-order changes.
            // Keep the prior opaque cover while the window is raised.
            if (active) UpdateWindowZOrder(IsForegroundWallSession(), raise: true);
            SetBackdropActive(active);
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
            _statusOverlay.TransientExpired -= RestoreStatusAfterTransient;
            _statusOverlay.Clear();
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
        var wasActive = _backdropActive;
        if (BackdropTrace.Enabled)
            BackdropTrace.Write($"FocusIntent begin active={wasActive}->{active} foreground={GetForegroundWindow()} generation={_snapshotGeneration}");
        _backdropActive = active;
        if (wasActive != active && Settings.BackgroundMode == WallBackgroundMode.Acrylic &&
            !_accessibilitySettings.HighContrast && _appearanceSettings.AdvancedEffectsEnabled &&
            AppWindow.IsVisible && !_backdropAnimationSuspended && (active || Settings.StayVisibleOnFocusLoss))
        {
            // Only hand off the already prepared background. Focus changes must
            // not capture the visible wall or alter its capture policy/styles.
            _backdropTransitionCover = true;
            UpdateBackgroundLayer();
        }
        _wallBackdrop.SetFocusIntent(active);
        if (BackdropTrace.Enabled)
            BackdropTrace.Write($"FocusIntent applied active={active} generation={_snapshotGeneration}");
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

    private WallDocument? _openingDocument;
    private bool _showUpgradeProgress;
    private bool _userDataUpgradeInProgress, _upgradeOwnsStatus;

    private Task OpenWallAsync(bool showUpgradeProgress)
    {
        if (_opened) return _pendingInitialOpen ?? Task.CompletedTask;
        _opened = true;
        _showUpgradeProgress = showUpgradeProgress;
        _openingWall = true;
        var operation = OpenWallCoreAsync();
        _pendingInitialOpen = operation.IsCompleted ? null : operation;
        return operation;
    }

    private async Task OpenWallCoreAsync()
    {
        try
        {
            if (!SystemParametersInfoW(SpiGetWorkArea, 0, out _workArea, 0))
            { BlockView(ProductStrings.Get("Wall.ReadWorkAreaFailed")); return; }
            RefreshNativeFrame();
            ReadPrimaryScreen();
            var (columns, rows) = ChooseInitialGrid(WorkWidthDip, WorkHeightDip);
            var result = await _session.OpenInitialAsync(columns, rows, ShowUpgradeStartingAsync);
            if (_windowClosing || ShutdownStarted) return;
            AcceptOpenedWall(result);
        }
        catch (Exception ex)
        {
            if (!_windowClosing && !ShutdownStarted)
                BlockView(string.Format(ProductStrings.Get("Common.SaveFailed"), ex.Message), initialOpen: true);
        }
        finally
        {
            _openingDocument = null;
            _openingWall = false;
            _pendingInitialOpen = null;
            EndUserDataUpgradeStatus();
            if (!_windowClosing && !ShutdownStarted)
            { UpdateWallHitTesting(); UpdateResizeZones(); }
        }
    }

    private Task ShowUpgradeStartingAsync(WallDocument trusted)
    {
        if (_windowClosing || ShutdownStarted) return Task.CompletedTask;
        _openingDocument = trusted;
        ProductStrings.ApplyLanguage(trusted.Settings.LanguageMode);
        _userDataUpgradeInProgress = true;
        _upgradeOwnsStatus = true;
        RefreshAppearance();
        DisplayStatus(ProductStrings.Get("Wall.UpgradingUserData"), transient: false);
        _upgradeOwnsStatus = true;
        if (_showUpgradeProgress && !AppWindow.IsVisible)
        {
            ApplyViewport(trusted, WallLayoutEngine.Measure(trusted, _metrics), animateMode: false);
            Surface.Visibility = Visibility.Collapsed;
            CaptureBackdropBeforeShow();
            AppWindow.Show(false);
            UpdateWindowZOrder(foregroundInSession: false);
        }
        return Task.CompletedTask;
    }

    private void EndUserDataUpgradeStatus()
    {
        _userDataUpgradeInProgress = false;
        if (!_upgradeOwnsStatus) return;
        _upgradeOwnsStatus = false;
        ClearStatus();
        if (_environmentOverflow && !_viewBlocked) PersistentStatus(ProductStrings.Get("Wall.EnvironmentScroll"));
    }

    private void AcceptOpenedWall(WallOpenResult result)
    {
        if (result.Document is null)
        {
            var reason = ProductStrings.Get("Wall.OpenReason." + result.BlockReason);
            _openFailureMessage = reason + "\n" + _session.DataRoot + "\n" + string.Join("；", result.Diagnostics);
            BlockView(_openFailureMessage, initialOpen: true);
            return;
        }
        _hasDocument = true;
        BeginFreshWelcomeInitialization(result);
        if (!_session.RefreshViewFromCurrent())
            BlockView(ProductStrings.Get("Wall.ViewFailed"), retry: true);
        else QueueEnvironmentAdaptation();
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
        RefreshAppearance();
        _puzzles.Sync(document);
        if (_carouselCommit) Surface.RefreshPuzzlePresentation(document);
        else Surface.Render(document);
        if (!_carouselCommit) RenderMenuStart(document);
        Surface.Visibility = Visibility.Visible;
        Surface.IsHitTestVisible = !PersistenceInteractionBlocked;
        _viewBlocked = false;
        // A carousel or automatic-icon commit can arrive while an operation notice is still visible.
        if (!_statusOverlay.HasTransient)
        {
            ClearStatus();
            if (_environmentOverflow) PersistentStatus(ProductStrings.Get("Wall.EnvironmentScroll"));
        }
        RetryViewButton.Visibility = Visibility.Collapsed;
        RetryOpenButton.Visibility = OpenDataFolderButton.Visibility = Visibility.Collapsed;
        _initialOpenBlocked = false;
        _openFailureMessage = null;
        if (!_carouselCommit) PlaceWall();
        RestoreCommittedScrollAnchor(document, scrollAnchor);
        UpdatePuzzleGate();
        Surface.RefreshClocks(_clock.UtcNow);
        QueueEnvironmentAdaptation();
    }

    private void BlockView(string message, bool retry = false, bool initialOpen = false)
    {
        _initialOpenBlocked = initialOpen && !_hasDocument;
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
        else if (_session.RecoveryBlocked && !_initialOpenBlocked)
        {
            message = WithDiagnostic(ProductStrings.Get("Wall.RecoveryBlocked"), message);
            retry = false;
        }
        _userDataUpgradeInProgress = _upgradeOwnsStatus = false;
        _viewBlocked = true;
        UpdateNativeSizingRegion();
        UpdatePuzzleGate();
        Surface.IsHitTestVisible = false;
        Surface.Visibility = Visibility.Collapsed;
        RetryViewButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        RetryOpenButton.Visibility = _initialOpenBlocked && !_session.ResultUnknown && !_session.RequiresReopen
            ? Visibility.Visible : Visibility.Collapsed;
        OpenDataFolderButton.Visibility = _initialOpenBlocked ? Visibility.Visible : Visibility.Collapsed;
        DisplayStatus(message, transient: false);
        AppWindow.MoveAndResize(new RectInt32(_workArea.Left, _workArea.Top,
            Math.Min(_workArea.Right - _workArea.Left, (int)(640 * Scale)),
            Math.Min(_workArea.Bottom - _workArea.Top, (int)(360 * Scale))));
    }

    private async void OnRetryOpenClicked(object sender, RoutedEventArgs e)
    {
        if (!_initialOpenBlocked || _hasDocument || PersistenceInteractionBlocked || ShutdownStarted || _windowClosing ||
            _session.ResultUnknown || _session.RequiresReopen) return;
        _showUpgradeProgress = true;
        _openingWall = true;
        RetryOpenButton.IsEnabled = OpenDataFolderButton.IsEnabled = false;
        var operation = RetryOpenWallCoreAsync();
        _pendingInitialOpen = operation.IsCompleted ? null : operation;
        await operation;
    }

    private async Task RetryOpenWallCoreAsync()
    {
        try
        {
            var (columns, rows) = ChooseInitialGrid(WorkWidthDip, WorkHeightDip);
            var result = await _session.RetryInitialOpenAsync(columns, rows, ShowUpgradeStartingAsync);
            if (_windowClosing || ShutdownStarted) return;
            AcceptOpenedWall(result);
            // Fresh initialization uses the same welcome path as the first Open.
            _openingWall = false;
            if (_welcomeInitializationPending)
            {
                var error = await InitializeWelcomeWallAsync(fresh: true);
                if (error is not null && !_viewBlocked) Status(error);
            }
        }
        catch (Exception ex)
        {
            if (!_windowClosing && !ShutdownStarted)
                BlockView(WithDiagnostic(_openFailureMessage ?? ProductStrings.Get("Wall.OpenReason.StorageAccess"), ex.Message), initialOpen: true);
        }
        finally
        {
            _openingWall = false;
            _pendingInitialOpen = null;
            _openingDocument = null;
            EndUserDataUpgradeStatus();
            if (!_windowClosing && !ShutdownStarted)
            {
                RetryOpenButton.IsEnabled = OpenDataFolderButton.IsEnabled = true;
                Surface.IsHitTestVisible = !_viewBlocked && !PersistenceInteractionBlocked;
                UpdateWallHitTesting();
                UpdateResizeZones();
                ImportReadinessChanged?.Invoke();
            }
        }
    }

    private void OnOpenDataFolderClicked(object sender, RoutedEventArgs e)
    {
        if (!_initialOpenBlocked || PersistenceInteractionBlocked || ShutdownStarted || _windowClosing) return;
        try
        {
            // This path comes only from the session's fixed application root;
            // recheck every existing component immediately before Shell dispatch.
            var root = _session.ValidatedDataRoot();
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(root)
                { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DisplayStatus(WithDiagnostic(_openFailureMessage ?? string.Empty,
                string.Format(ProductStrings.Get("Wall.OpenDataFolderFailed"), ex.Message)), transient: false);
        }
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
            RetryViewButton.Visibility = Visibility.Collapsed;
        }
        else DisplayStatus(ProductStrings.Get("Wall.RebuildFailed"), transient: false);
    }

    private void Status(string message) => TransientStatus(message);

    private void PersistentStatus(string message)
    {
        if (_viewBlocked) return;
        DisplayStatus(message, transient: false);
    }

    private void TransientStatus(string message)
    {
        if (_viewBlocked) return;
        DisplayStatus(message, transient: true);
    }

    private void DisplayStatus(string message, bool transient)
    {
        if (_windowClosing || ShutdownStarted || transient && _upgradeOwnsStatus) return;
        _upgradeOwnsStatus = false;
        PositionStatusViewport();
        _statusOverlay.SetActionsVisible(_viewBlocked && (RetryViewButton.Visibility == Visibility.Visible ||
            RetryOpenButton.Visibility == Visibility.Visible || OpenDataFolderButton.Visibility == Visibility.Visible));
        _statusOverlay.Show(message, transient, renew: true);
    }

    private void PositionStatusViewport()
    {
        var useTileViewport = !_viewBlocked && _scrollRegion.Width > 0 && _scrollRegion.Height > 0;
        StatusViewport.HorizontalAlignment = useTileViewport ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        StatusViewport.VerticalAlignment = useTileViewport ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        StatusViewport.Width = useTileViewport ? _scrollRegion.Width : double.NaN;
        StatusViewport.Height = useTileViewport ? _scrollRegion.Height : double.NaN;
        StatusViewport.Margin = useTileViewport ? new Thickness(_scrollRegion.X, _scrollRegion.Y, 0, 0) : new Thickness(0);
    }

    private void ClearStatus()
    {
        if (_userDataUpgradeInProgress && _upgradeOwnsStatus) return;
        _upgradeOwnsStatus = false;
        _statusOverlay.Clear();
    }

    private void ClearTransientStatus()
    {
        if (!_statusOverlay.HasTransient) return;
        ClearStatus();
        RestoreStatusAfterTransient();
    }

    private void RestoreStatusAfterTransient()
    {
        if (_environmentOverflow) PersistentStatus(ProductStrings.Get("Wall.EnvironmentScroll"));
    }

    private void PlaceWall()
    {
        ApplyViewport();
    }

    private string? CommitProposal(WallProposal proposal, IReadOnlyList<EntryChange>? changes = null,
        IReadOnlyList<PreparedMediaAsset>? preparedMedia = null,
        IReadOnlyDictionary<string, string>? expectedEntrySha256 = null)
    {
        if (PersistenceMutationBlocked) return ProductStrings.Get("Wall.SettingsSaving");
        if (_viewBlocked) return ProductStrings.Get("Wall.ViewBlocked");
        // Only a presentation preference can retain an already oversized grid.
        // The complete document comparison prevents this from admitting any
        // layout, member, recycle, or unrelated settings change.
        if (!IsTaskbarPresentationOnly(proposal.Document))
        {
            var fit = WallViewportPolicy.Validate(proposal, RecoverableViewportBudget(proposal.Document.Settings), _metrics);
            if (fit.Proposal is null) return ViewportFailure(fit);
        }
        if (WallLayoutEngine.ValidateLockedChange(Document, proposal.Document, _metrics) is not null)
            return ProductStrings.Get("Wall.LayoutLocked");
        try
        {
            var commitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var scale = Scale;
            var media = Task.Run(() => PrepareFormalMediaAsync(proposal, changes, scale)).GetAwaiter().GetResult();
            var result = _session.Commit(media.Proposal, changes,
                AcceptedMediaAssets(media.Proposal.Document, media.Assets, preparedMedia), expectedEntrySha256);
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

    private bool IsTaskbarPresentationOnly(WallDocument next) =>
        next.Generation == checked(Document.Generation + 1) &&
        next.Settings.ActivateTaskbar != Settings.ActivateTaskbar &&
        (next.Settings with { ActivateTaskbar = Settings.ActivateTaskbar }) == Settings &&
        (next with { Generation = Document.Generation, Settings = Settings }) == Document;

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
        if (result.NeedsCleanup) TransientStatus(ProductStrings.Get("Wall.CleanupPending"));
        return null;
    }

    private string? Apply(WallAction action, IReadOnlyList<EntryChange>? changes = null,
        IReadOnlyList<PreparedMediaAsset>? preparedMedia = null)
    {
        var result = _session.Propose(action, WorkWidthDip, WorkHeightDip, _metrics);
        if (result.Proposal is null)
        {
            AppLog.Warn("WallApply", $"action={action.GetType().Name} rejection={result.Rejection} detail={result.Detail}");
            if (result.Detail == "LAYOUT_LOCKED") return ProductStrings.Get("Wall.LayoutLocked");
            if (result.Detail is "CONTENT_TOO_LARGE" || result.Detail?.StartsWith("GROUP_TOO_TALL:", StringComparison.Ordinal) == true) return ViewportFailure(result);
            return string.Format(ProductStrings.Get("Wall.LocationUnavailable"),
                ProductStrings.WallRejectionReason(result.Rejection), string.Empty).TrimEnd();
        }
        var error = CommitProposal(result.Proposal, changes, preparedMedia);
        if (error is null && result.Detail is "LAYOUT_MOVED_GROUP_END" or "LAYOUT_MOVED_WALL_END" or "LAYOUT_MOVED_NEARBY")
            TransientStatus(ProductStrings.Get(result.Detail == "LAYOUT_MOVED_NEARBY" ? "Wall.LayoutMovedNearby" : result.Detail == "LAYOUT_MOVED_GROUP_END" ? "Wall.LayoutMovedGroupEnd" : "Wall.LayoutMovedWallEnd"));
        return error;
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
        return await CommitProposalAsync(result.Proposal, carousel: true, background: true);
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
        UpdateVisibleWallFrames();
        SetMenuImagesActive(!hidden && !_viewBlocked);
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

    private Task CreateTileAsync(string? groupId, int lane) => CreateMemberAsync(groupId, lane);

    private async Task CreateMemberAsync(string? groupId, int lane)
    {
        if (PersistenceInteractionBlocked || _modal || _viewBlocked || !_hasDocument) return;
        string? notice = null, duplicateId = null;
        try
        {
            if (!await BeginModalAsync()) return;
            var tile = new TileMember { Id = $"member-{Guid.NewGuid():N}", LocalBounds = new GridRect(0, 0, 2, 2),
                RotationInterval = MemberPresentationRules.NewRotationInterval };
            var destinationLane = Math.Clamp(WallViewportPolicy.MapCreationLane(Document, lane), 0, 63);
            var dialog = new SharedMemberEditorDialog(DialogOwner, tile, _images, null, _session.LinkFiles, MaxMemberRows(),
                (member, changes, assets) =>
                {
                    if (string.IsNullOrWhiteSpace(member.TargetInput) && member.Images.Kind is MemberImageSourceKind.Automatic or MemberImageSourceKind.None)
                    { notice = ProductStrings.Get("Editor.EmptyNotKept"); return null; }
                    var group = groupId is null ? null : Document.Lanes.SelectMany(l => l.Groups).FirstOrDefault(g => g.Id == groupId);
                    var identity = DesiredEditorIdentity(tile, member, changes, ImportIdentities);
                    var duplicate = identity is null || group is null ? null : group.Members.FirstOrDefault(other =>
                        StartMenuImportPreparation.TryIdentity(other, _session.DataRoot, ImportIdentities)?.Key == identity.Key);
                    if (duplicate is not null) { duplicateId = duplicate.Id; return null; }
                    var action = new AddMemberAction(Document.Generation, member, groupId, destinationLane,
                        destinationLane >= Document.Lanes.Count ? 0 : Document.Lanes[destinationLane].Groups.Count,
                        groupId is null ? $"group-{Guid.NewGuid():N}" : null);
                    return Apply(action, changes, assets);
                }, Settings.VisualStyle, isNew: true);
            ActiveDialog = dialog;
            AttachGalleryEditor(dialog);
            await dialog.ShowAsync();
            if (dialog.IsEmptyCreation) notice = ProductStrings.Get("Editor.EmptyNotKept");
        }
        catch (Exception ex) { notice = string.Format(ProductStrings.Get("Wall.EditFailed"), ex.Message); }
        finally
        {
            EndModal();
            if (duplicateId is not null) { Surface.FocusMember(duplicateId); notice = ProductStrings.Get("SharedEditor.DuplicateLocated"); }
            if (notice is not null) Status(notice);
        }
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
            if (FindMember(id) is not { } latest) return;
            member = latest;
            if (member is ClockMember clock)
            {
                var clockDialog = new ClockEditorDialog(DialogOwner, clock, MaxMemberRows(), _clock,
                    _metrics, Settings.VisualStyle,
                    (DialogOwner.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1,
                    replacement => Apply(new ReplaceMemberAction(Document.Generation, id, replacement)));
                ActiveDialog = clockDialog;
                await clockDialog.ShowAsync();
                return;
            }
            var dialog = new SharedMemberEditorDialog(DialogOwner, member, _images,
                _session.ManagedEntryPath(member), _session.LinkFiles, MaxMemberRows(),
                (replacement, changes, assets) => SaveSharedMember(replacement, changes, isNewPlaceholder,
                    existing => duplicateToFocus = existing, assets), Settings.VisualStyle, initialFrame: _puzzles.FrameFor(id),
                initialAutomaticSeries: _puzzles.PreparedSeriesFor(id));
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
        bool isNewPlaceholder, Action<string> locateDuplicate, IReadOnlyList<PreparedMediaAsset>? preparedMedia = null)
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
            if (changes.Count == 0 && AutomaticIconSeriesRules.SameSeries(current.Images.IconSeries, replacement.Images.IconSeries) &&
                !MemberPresentationRules.HasUserPropertyChanges(current, replacement)) return null;
            return Apply(new ReplaceMemberAction(Document.Generation, replacement.Id, replacement), changes, preparedMedia);
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

    private void ResizeMemberPreset(string id, GridSize size)
    {
        if (PersistenceInteractionBlocked || _modal || _viewBlocked || _windowClosing || ShutdownStarted) return;
        _ = QueueUserMutationAsync(() =>
        {
            if (PersistenceMutationBlocked || _modal || _viewBlocked || FindMember(id) is not TileMember current)
                return Task.CompletedTask;
            var replacement = current with { LocalBounds = current.LocalBounds with { Width = size.Columns, Height = size.Rows } };
            var error = SaveSharedMember(replacement, [], isNewPlaceholder: false, static _ => { });
            if (error is not null) Status(error);
            return Task.CompletedTask;
        });
    }
    private void RemoveMember(string id)
    {
        BeginUnpinMembers([id]);
    }

    private string? RenameGroup(string id, string? title)
    {
        if (PersistenceInteractionBlocked) return ProductStrings.Get("Wall.SettingsSaving");
        if (_modal || _viewBlocked) return ProductStrings.Get("Wall.ViewBlocked");
        if (_persistencePending || _userMutationReservations != 0)
        {
            _ = QueueUserMutationAsync(() =>
            {
                var failure = Apply(new RenameGroupAction(Document.Generation, id, title));
                if (failure is not null) Status(failure);
                return Task.CompletedTask;
            });
            return null;
        }
        var error = Apply(new RenameGroupAction(Document.Generation, id, title));
        if (error is not null) Status(error);
        return error;
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
        if (e.Key == Windows.System.VirtualKey.Escape && CancelDeferredGesturePress()) { e.Handled = true; return; }
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
        if (TryDeferGesturePress(e)) return;
        if (LayoutMutationBlocked || _modal || _viewBlocked || !e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed) return;
        try
        {
            if (_viewportTransition is { AutomaticHeight: true }) StopViewportTransition();
            var point = e.GetCurrentPoint(Surface).Position;
            Point? screenStart = e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse &&
                TryGetResizeScreenPoint(e.GetCurrentPoint(WallRoot).Position, Scale, out var screenPoint)
                    ? screenPoint : null;
            var origin = AppWindow.Position;
            var pointerScrollStart = new Point(WallScroll.HorizontalOffset, WallScroll.VerticalOffset);
            var marginStart = new Point(WallScroll.Margin.Left, WallScroll.Margin.Top);
            var hit = Surface.Hit(point);
            TraceInput($"PressHit member={hit.MemberId} group={hit.GroupId} handle={hit.GroupHandle} x={point.X:F1} y={point.Y:F1}");
            if (hit.MemberId is null && !hit.GroupHandle || Settings.LayoutLocked && hit.GroupHandle) return;
            // Capture a complete settled frame in the ghost, including a flip interrupted by this press.
            _puzzles.SetGate(MemberCarouselGate.Gesture);
            StopViewportTransition();
            var geometry = Surface.Geometry ?? WallLayoutEngine.Measure(Document, _metrics);
            var bounds = hit.MemberId is { } memberId
                ? geometry.Groups.SelectMany(g => g.Members).First(m => m.MemberId == memberId).Bounds
                : geometry.Groups.First(g => g.GroupId == hit.GroupId).Bounds;
            var offset = new Point(point.X - bounds.X, point.Y - bounds.Y);
            var ghost = Surface.CreateDragVisual(hit.MemberId, hit.GroupHandle ? hit.GroupId : null);
            _gestureScrollOffset = new(WallScroll.HorizontalOffset, WallScroll.VerticalOffset);
            var began = _gesture.Begin(e.Pointer.PointerId, point, offset, new Point(origin.X, origin.Y), ghost,
                hit.MemberId, hit.GroupHandle ? hit.GroupId : null);
            TraceInput($"Begin result={began} phase={_gesture.Phase}");
            if (_traceInput) TraceInput($"DragOrigin grab={offset.X:F1},{offset.Y:F1} window={origin.X},{origin.Y} bounds={bounds.X:F1},{bounds.Y:F1},{bounds.Width:F1},{bounds.Height:F1}");
            if (began)
            {
                _gesturePressTimestamp = e.GetCurrentPoint(Surface).Timestamp;
                _gestureThresholdDip = e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse ? 3 : 7;
                _gestureScreenStart = screenStart;
                _gesturePointerScrollStart = pointerScrollStart;
                _gestureMarginStart = marginStart;
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
        if (UpdateDeferredGesturePress(e)) return;
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
            LatchGesturePointerHistory(e);
            _latestDragPoint = CommittedPointerPoint(e);
            _gesture.Advance(_latestDragPoint, _gestureThresholdDip);
            _dragInputPending = true;
            _performance.Input();
            EnsureFrameSubscription();
            QueueDragFrameWake();
        }
        catch (Exception ex) { RecoverGestureError(ex); }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (ReleaseDeferredGesturePress(e)) return;
        TraceInput($"Release source={e.OriginalSource?.GetType().Name} pointer={e.Pointer.PointerId} phase={_gesture.Phase}");
        if (_gesture.Phase == GesturePhase.Idle || _gesture.PointerId != e.Pointer.PointerId) return;
        try
        {
            if (!GestureSnapshotIsCurrent()) { TerminateGesture(); return; }
            _dragInputPending = false;
            LatchGesturePointerHistory(e);
            var point = CommittedPointerPoint(e);
            _performance.Input();
            // A release can arrive before the next display callback. Consume and draw its
            // sample through the same path, then commit that exact displayed proposal.
            ConsumeDragSample(point);
            var dragged = _gesture.Phase == GesturePhase.Dragging;
            var proposal = dragged ? _gesture.Preview : null;
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
                Status(DescribeDropFailure(proposal));
                return;
            }
            _ = CommitProposalAsync(proposal.Proposal, settle: error => SettleReleasePersistence(error, automaticHeight: true));
        }
        catch (Exception ex) { RecoverGestureError(ex); }
    }

    private void LatchGesturePointerHistory(PointerRoutedEventArgs e)
    {
        if (_gesture.Phase != GesturePhase.Pressed || _gesture.PointerId != e.Pointer.PointerId) return;
        // Before the first drag preview the wall has not moved. These event-time
        // positions preserve an excursion even when the live cursor has returned.
        foreach (var sample in e.GetIntermediatePoints(Surface))
        {
            if (sample.Timestamp < _gesturePressTimestamp) continue;
            if (_gesture.Advance(sample.Position, _gestureThresholdDip)) break;
        }
        // Placement continues to use the real screen cursor below; historical
        // local points must never be combined with a window moved by a preview.
    }

    private Point CommittedPointerPoint(PointerRoutedEventArgs e)
    {
        // Resizing a bottom/right anchored preview can queue PointerMoved with coordinates
        // from before the native window move. Do not combine that old local point with
        // the new window origin: it fabricates movement while the mouse is stationary.
        if (_gestureScreenStart is { } start && GetCursorPos(out var cursor))
            return new Point(_gesture.Start.X + (cursor.X - start.X) / _gestureScale +
                WallScroll.HorizontalOffset - _gesturePointerScrollStart.X - WallScroll.Margin.Left + _gestureMarginStart.X,
                _gesture.Start.Y + (cursor.Y - start.Y) / _gestureScale +
                WallScroll.VerticalOffset - _gesturePointerScrollStart.Y - WallScroll.Margin.Top + _gestureMarginStart.Y);
        var local = e.GetCurrentPoint(Surface).Position;
        var origin = AppWindow.Position;
        return _gesture.ToCommittedPoint(local, new Point(origin.X, origin.Y), Scale);
    }

    private WallLayoutResult? CalculateDrop(Point point)
    {
        if (_gesture.MemberId is not null) return CalculateMemberDrop(point);
        var geometry = _gestureGeometry ?? WallLayoutEngine.Measure(Document, _metrics);
        var derivedLanes = Settings.FullScreen || Settings.ExpandColumns;
        var rightAnchor = !derivedLanes && Settings.AnchorCorner is AnchorCorner.TopRight or AnchorCorner.BottomRight;
        if (point.X < 0 || point.Y < 0 || point.X >= (derivedLanes ? Math.Max(geometry.WallWidth, WallScroll.ViewportWidth) : geometry.WallWidth) || point.Y >= (derivedLanes ? WallScroll.ViewportHeight : geometry.WallHeight))
            return InvalidDrop("Wall.OutsideWall");
        if (rightAnchor && point.X < _metrics.Margin)
        {
            if (Document.Columns >= 64)
                return InvalidDrop("Wall.OutsideLane");
            // Extending inward from a right anchor prepends a lane. The proposal still commits once
            // against the original generation, preserving every existing group/member identity.
            var expanded = Document with { Columns = Document.Columns + 1,
                Lanes = new[] { new WallLane { Groups = [] } }.Concat(Document.Lanes).ToArray() };
            WallAction action;
            if (_gesture.GroupId is { } movedGroup)
                action = new MoveGroupAction(Document.Generation, movedGroup, 0, 0);
            else return null;
            return CachedDrop(action, expanded);
        }
        var lane = !derivedLanes && !rightAnchor && point.X >= geometry.WallWidth - _metrics.Margin ? Document.Columns :
            (int)Math.Floor((point.X - _metrics.Margin) / (_metrics.LaneWidth + _metrics.ColumnGap));
        lane = WallViewportPolicy.MapCreationLane(Document, lane);
        if (lane < 0 || lane >= 64 || !derivedLanes && lane > Document.Columns)
            return InvalidDrop("Wall.OutsideLane");
        if (_gesture.GroupId is { } groupId)
        {
            var before = geometry.Groups.Count(g => g.LaneIndex == lane && g.GroupId != groupId &&
                g.TitleBounds.Y + g.TitleBounds.Height / 2 < point.Y);
            return CachedDrop(new MoveGroupAction(Document.Generation, groupId, lane, before));
        }
        return null;
    }

    private WallLayoutResult? CalculateMemberDrop(Point point)
    {
        if (_gesture.MemberId is not { } memberId || _gestureMembers.GetValueOrDefault(memberId) is not { } member ||
            _memberDropSession is null) return null;
        var dragged = WallMemberDropResolver.DragBounds(new DipPoint(point.X, point.Y),
            new DipPoint(_gesture.GrabOffset.X, _gesture.GrabOffset.Y), member.LocalBounds.Size, _metrics);
        var slot = _memberDropSession.Snap(dragged);
        if (_memberDropSlot == slot) return _memberDropResult;
        _memberDropSlot = slot;
        return _memberDropResult = ResolveMemberDrop(slot, member);
    }

    private WallLayoutResult ResolveMemberDrop(WallMemberDropSlot slot, WallMember member)
    {
        var original = _gestureGeometry!;
        var derivedLanes = Settings.FullScreen || Settings.ExpandColumns;
        var rightAnchor = !derivedLanes && Settings.AnchorCorner is AnchorCorner.TopRight or AnchorCorner.BottomRight;
        if (slot.Bounds.Row < 0)
            return InvalidDrop("Wall.OutsideWall");
        if (slot.LaneIndex == -1 && rightAnchor)
        {
            if (Document.Columns >= 64) return InvalidDrop("Wall.OutsideLane");
            if (slot.Bounds.Row == 0) return InvalidDrop("Wall.OutsideGroup");
            var expanded = Document with { Columns = Document.Columns + 1,
                Lanes = new[] { new WallLane { Groups = [] } }.Concat(Document.Lanes).ToArray() };
            var prepend = new MoveMemberAction(Document.Generation, member.Id,
                slot.Bounds with { Row = slot.Bounds.Row - 1 }, null, 0, 0, _gesture.NewGroupId);
            return CachedDrop(prepend, expanded);
        }
        var availableLanes = derivedLanes
            ? Math.Max(Document.Columns, (int)Math.Floor((Math.Max(original.WallWidth, WallScroll.ViewportWidth) -
                2 * _metrics.Margin + _metrics.ColumnGap) / (_metrics.LaneWidth + _metrics.ColumnGap)))
            : Document.Columns + (rightAnchor ? 0 : 1);
        if (slot.LaneIndex < 0 || slot.LaneIndex >= 64 || slot.LaneIndex >= availableLanes)
            return InvalidDrop("Wall.OutsideLane");
        var lane = WallViewportPolicy.MapCreationLane(_gestureDocument!, slot.LaneIndex);
        var action = _memberDropSession!.Resolve(slot with { LaneIndex = lane });
        if (_traceInput)
        {
            TraceInput($"MemberDrop grid={slot.LaneIndex}:{slot.Bounds.Column},{slot.Bounds.Row},{slot.Bounds.Width},{slot.Bounds.Height} target={action.TargetGroupId ?? action.NewGroupId} new={action.TargetGroupId is null} local={action.TargetBounds.Column},{action.TargetBounds.Row}");
        }
        if (action.TargetGroupId is { } targetId && _gestureGroups.TryGetValue(targetId, out var destination) &&
            !destination.Members.Any(other => other.Id == member.Id))
        {
            var desired = GestureLaunchIdentity(member);
            if (desired is not null && destination.Members.Any(other => other.Id != member.Id && GestureLaunchIdentity(other) == desired))
                return InvalidDrop("Wall.DropDuplicate");
        }
        if (action.TargetBounds.Column < 0 || action.TargetBounds.Right > 8 || action.TargetBounds.Row < 0)
            return InvalidDrop("Wall.OutsideGroup");
        return CachedDrop(action);
    }

    private readonly Dictionary<string, WallLayoutResult> _invalidDrops = new(StringComparer.Ordinal);
    private WallLayoutResult InvalidDrop(string reasonKey)
    {
        if (!_invalidDrops.TryGetValue(reasonKey, out var result))
            _invalidDrops.Add(reasonKey, result = new(null, WallRejection.InvalidAction, reasonKey));
        return result;
    }

    private string DescribeDropFailure(WallLayoutResult? result)
    {
        if (result?.Detail is { } detail)
        {
            if (detail == "LAYOUT_LOCKED") return ProductStrings.Get("Wall.LayoutLocked");
            if (detail == "CONTENT_TOO_LARGE" || detail.StartsWith("GROUP_TOO_TALL:", StringComparison.Ordinal) ||
                detail.StartsWith("MEMBER_TOO_TALL:", StringComparison.Ordinal)) return ViewportFailure(result);
            if (_invalidDrops.TryGetValue(detail, out var local) && ReferenceEquals(local, result))
                return string.Format(ProductStrings.Get("Wall.DropFailed"), ProductStrings.Get(detail));
        }
        return string.Format(ProductStrings.Get("Wall.DropFailed"),
            ProductStrings.WallRejectionReason(result?.Rejection ?? WallRejection.None));
    }

    private void ClearMemberDropNotice()
    {
        if (_memberDropNotice is not { } message) return;
        _memberDropNotice = null;
        if (_statusOverlay.Text == message) ClearTransientStatus();
    }

    private void DrawGhost(Point point)
    {
        var visual = _gesture.GhostVisual;
        if (visual is null) return;
        var ghost = _gesture.GhostHost;
        if (ghost is null)
        {
            ghost = new Border
            {
                Width = visual.Width,
                Height = visual.Height,
                CornerRadius = WallCornerStyle.Corners(Document.Settings.VisualStyle),
                Child = visual,
                RenderTransform = new TranslateTransform(),
            };
            _gesture.SetGhostHost(ghost);
            DragOverlay.Children.Add(ghost);
        }
        point = ContentToViewport(point);
        PositionGhost(ghost, point.X - _gesture.GrabOffset.X,
            point.Y - (_gesture.GroupId is null ? _gesture.GrabOffset.Y : 12));
    }

    private void TerminateGesture(bool restoreScroll = true, bool restoreView = true)
    {
        CancelDeferredGesturePress();
        if (_gesture.Phase == GesturePhase.Idle) return;
        TraceInput($"End phase={_gesture.Phase} member={_gesture.MemberId} group={_gesture.GroupId}");
        _dragInputPending = false;
        _gestureScreenStart = null;
        ClearGhostComposition();
        var wasDragging = _gesture.End();
        ClearMemberDropNotice();
        _dropCache.Clear(); _gestureDocument = null; _gestureGeometry = null; _drawnDrop = null;
        _memberDropSession = null; _memberDropSlot = null; _memberDropResult = null;
        StopFrameSubscriptionIfIdle();
        if (restoreView) _performance.End("cancel-or-click", Surface.VisualCreationCount, Surface.GridCreationCount);
        UpdatePuzzleGate();
        try { Surface.ReleasePointerCaptures(); }
        catch (Exception) { /* End already made capture loss idempotent. */ }
        DragOverlay.Children.Clear();
        if (restoreScroll) RestoreScrollPosition(_gestureScrollOffset);
        try
        {
            if (wasDragging && restoreView && !_viewBlocked)
            {
                if (!Settings.FullScreen && Surface.Geometry?.WallHeight != _metrics.WallHeight(Document.Rows))
                    Surface.BeginAutomaticHeightPresentation();
                Surface.Render(Document);
                Surface.RefreshClocks(_clock.UtcNow);
                ApplyViewport(animateMode: false, animateHeight: true);
            }
            Surface.ShowDragCue(null, false);
            ResumeEnvironmentAdaptation();
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
        if (BackdropTrace.Enabled)
        {
            // The narrow backdrop trace must not turn every input event into IO
            // or synchronously flush the background logger during a transition.
            if (message.StartsWith("Backdrop", StringComparison.Ordinal) || message.StartsWith("Activated ", StringComparison.Ordinal))
                BackdropTrace.Write($"{message} generation={_snapshotGeneration}");
            return;
        }
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
    [StructLayout(LayoutKind.Sequential)]
    private struct WorkAreaRect { public int Left; public int Top; public int Right; public int Bottom; }
}
