using TileWall.ApplicationModel;
using TileWall.Core.Application;
using TileWall.Core.Shell;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using TileWall.Resources;

namespace TileWall;

public sealed partial class MainWindow
{
    private StaStorageExecutor? _storageExecutor;
    private Task<string?>? _pendingPersistence;
    private Task? _pendingInitialOpen;
    private bool _openingWall;
    private Task? _shutdown;
    private bool _shutdownStarted;
    private bool ShutdownStarted => _shutdownStarted;
    private bool _persistencePending, _persistenceAdmissionClosed, _pendingCarouselCommit;
    private bool _backgroundPersistencePending;
    private int _userMutationReservations;
    private Task _pendingUserMutations = Task.CompletedTask;
    private bool _environmentAfterPersistence;
    private readonly ExitDraftSavePermission _exitDraftSave = new();
    private bool PersistenceInteractionBlocked => _persistenceAdmissionClosed || _openingWall ||
        _persistencePending && !_backgroundPersistencePending;
    private bool PersistenceWriteBlocked => _persistencePending || _persistenceAdmissionClosed || _openingWall;
    private bool LayoutMutationBlocked => PersistenceWriteBlocked || _userMutationReservations != 0;
    private bool CanAcceptBackgroundWrite => !PersistenceWriteBlocked && _userMutationReservations == 0 &&
        !_modal && !_resizing && _gesture.Phase == GesturePhase.Idle && !Surface.HasActiveTitleEdit &&
        !_viewBlocked && _hasDocument && !_windowClosing && !ShutdownStarted && AppWindow.IsVisible;
    internal bool PersistenceBusy => PersistenceInteractionBlocked;
    internal void TraceExit(string stage) => TraceInput("Exit " + stage);
    internal bool PersistenceMutationBlocked =>
        _exitDraftSave.BlocksMutation(_persistencePending, _persistenceAdmissionClosed, ActiveDialog);

    internal bool ApplyExitDecision(ExitCoordinator exit, TileWall.Views.IWallDialogSession participant,
        ExitDecision decision)
    {
        if (!ReferenceEquals(participant, ActiveDialog)) return false;
        // Interaction admission remains closed throughout. Only this existing
        // participant's synchronous TrySaveNow chain receives write permission.
        return decision == ExitDecision.SaveAndExit
            ? _exitDraftSave.Run(participant, () => ActiveDialog, () => exit.ApplyDecision(participant, decision))
            : exit.ApplyDecision(participant, decision);
    }

    private void InitializePersistence()
    {
        _storageExecutor = new StaStorageExecutor();
        _session.ConfigureAsyncExecution(_storageExecutor, AcceptStorageOnUiAsync);
        _images.ConfigureStorage(_session.DataRoot,
            action => _storageExecutor.ExecuteAsync(() => { action(); return true; }),
            _session.AcquireResourceReadLease);
    }

    private Task AcceptStorageOnUiAsync(Action accept)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Apply()
        {
            if (_windowClosing)
            { completed.TrySetException(new InvalidOperationException("The wall dispatcher is closing.")); return; }
            var previous = _carouselCommit;
            _carouselCommit = _pendingCarouselCommit;
            try { accept(); completed.TrySetResult(); }
            catch (Exception ex) { completed.TrySetException(ex); }
            finally { _carouselCommit = previous; }
        }
        if (DispatcherQueue.HasThreadAccess) Apply();
        else if (!DispatcherQueue.TryEnqueue(Apply))
            completed.TrySetException(new InvalidOperationException("The wall dispatcher cannot accept the committed result."));
        return completed.Task;
    }

    private Task<string?> CommitProposalAsync(WallProposal proposal,
        IReadOnlyList<EntryChange>? changes = null, bool carousel = false,
        Action<string?>? settle = null, IReadOnlyList<PreparedMediaAsset>? preparedMedia = null,
        IReadOnlyDictionary<string, string>? expectedEntrySha256 = null,
        IReadOnlyList<PreparedManagedGallery>? managedGalleries = null, bool background = false,
        bool useWelcomeIconDefaults = false)
    {
        Task<string?> Reject(string error) { settle?.Invoke(error); return Task.FromResult<string?>(error); }
        if (PersistenceWriteBlocked || background && !CanAcceptBackgroundWrite) return Reject(ProductStrings.Get("Wall.SettingsSaving"));
        if (_viewBlocked) return Reject(ProductStrings.Get("Wall.ViewBlocked"));
        var fit = WallViewportPolicy.Validate(proposal, RecoverableViewportBudget(proposal.Document.Settings), _metrics);
        if (fit.Proposal is null) return Reject(ViewportFailure(fit));
        if (WallLayoutEngine.ValidateLockedChange(Document, fit.Proposal.Document, _metrics) is not null)
            return Reject(ProductStrings.Get("Wall.LayoutLocked"));
        _persistencePending = true;
        _backgroundPersistencePending = background;
        _pendingCarouselCommit = carousel;
        _puzzles.SetPersistenceBusy(true);
        UpdateWallHitTesting();
        Surface.IsHitTestVisible = !_viewBlocked && !PersistenceInteractionBlocked;
        UpdateResizeZones();
        var operation = CommitProposalAsyncCore(proposal, changes, settle, preparedMedia, expectedEntrySha256, managedGalleries,
            useWelcomeIconDefaults);
        _pendingPersistence = operation.IsCompleted ? null : operation;
        return operation;
    }

    private async Task<string?> CommitProposalAsyncCore(WallProposal proposal,
        IReadOnlyList<EntryChange>? changes, Action<string?>? settle, IReadOnlyList<PreparedMediaAsset>? preparedMedia,
        IReadOnlyDictionary<string, string>? expectedEntrySha256, IReadOnlyList<PreparedManagedGallery>? managedGalleries,
        bool useWelcomeIconDefaults)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        string? error = null;
        try
        {
            var media = await PrepareFormalMediaAsync(proposal, changes, Scale, useWelcomeIconDefaults);
            var result = await _session.CommitAsync(media.Proposal, changes,
                AcceptedMediaAssets(media.Proposal.Document, media.Assets, preparedMedia), expectedEntrySha256, managedGalleries);
            _performance.Commit(System.Diagnostics.Stopwatch.GetTimestamp() - started);
            error = DescribeCommit(result);
        }
        catch (Exception ex)
        {
            error = _session.ResultUnknown ? ProductStrings.Get("Wall.ResultUnknown")
                : _session.RequiresReopen ? ProductStrings.Get("Wall.RequiresReopen")
                : string.Format(ProductStrings.Get("Common.SaveFailed"), ex.Message);
            if (_session.ViewBlocked && !_windowClosing) BlockView(error);
        }
        finally
        {
            try
            {
                // Drain includes release diagnostics and failed-preview restoration.
                // Event handlers have no post-await UI continuation to race teardown.
                settle?.Invoke(error);
            }
            catch (Exception ex)
            {
                error = string.Format(ProductStrings.Get("Common.SaveFailed"), ex.Message);
                BlockView(error, retry: true);
            }
            finally
            {
                _persistencePending = false;
                _backgroundPersistencePending = false;
                _pendingCarouselCommit = false;
                _pendingPersistence = null;
                if (!_windowClosing)
                {
                    Surface.IsHitTestVisible = !_viewBlocked && !_persistenceAdmissionClosed;
                    UpdateWallHitTesting();
                    _puzzles.SetPersistenceBusy(_persistenceAdmissionClosed || _userMutationReservations != 0);
                    UpdateResizeZones();
                    if (_environmentAfterPersistence)
                    { _environmentAfterPersistence = false; QueueEnvironmentRefresh(); QueueEnvironmentAdaptation(); }
                    if (_appearanceAfterPersistence)
                    { _appearanceAfterPersistence = false; QueueAppearanceRefresh(); }
                    ImportReadinessChanged?.Invoke();
                }
            }
        }
        return error;
    }

    private void SettleReleasePersistence(string? error, bool automaticHeight = false)
    {
        _performance.End(error is null ? "release" : "commit-rejected", Surface.VisualCreationCount, Surface.GridCreationCount);
        if (error is not null)
        {
            if (!_viewBlocked && !_windowClosing && !ShutdownStarted)
            {
                if (automaticHeight && !Settings.FullScreen && Surface.Geometry?.WallHeight != _metrics.WallHeight(Document.Rows))
                    Surface.BeginAutomaticHeightPresentation();
                Surface.Render(Document);
                ApplyViewport(animateMode: false, animateHeight: automaticHeight);
            }
            Status(error);
        }
    }

    internal void SuspendPersistenceAdmission()
    {
        _persistenceAdmissionClosed = true;
        _puzzles.SetPersistenceBusy(true);
        UpdateWallHitTesting();
        Surface.IsHitTestVisible = false;
        UpdateResizeZones();
        CancelTransientState();
    }

    internal void ResumePersistenceAdmission()
    {
        _persistenceAdmissionClosed = false;
        if (_windowClosing) return;
        _puzzles.SetPersistenceBusy(_persistencePending || _userMutationReservations != 0);
        UpdateWallHitTesting();
        Surface.IsHitTestVisible = !PersistenceInteractionBlocked && !_viewBlocked;
        UpdateResizeZones();
        ImportReadinessChanged?.Invoke();
    }

    private Task QueueUserMutationAsync(Func<Task> action)
    {
        var previous = _pendingUserMutations;
        _userMutationReservations++;
        _puzzles.SetPersistenceBusy(true);
        async Task RunAsync()
        {
            try
            {
                await previous;
                await DrainPersistenceAsync();
                if (_windowClosing || ShutdownStarted || _persistenceAdmissionClosed || _viewBlocked) return;
                await action();
            }
            catch (Exception ex)
            {
                if (!_windowClosing && !ShutdownStarted) Status(string.Format(ProductStrings.Get("Common.SaveFailed"), ex.Message));
            }
            finally
            {
                _userMutationReservations--;
                _puzzles.SetPersistenceBusy(PersistenceWriteBlocked || _userMutationReservations != 0);
            }
        }
        return _pendingUserMutations = RunAsync();
    }

    internal async Task WaitForUserMutationsAsync()
    {
        while (_userMutationReservations != 0) await _pendingUserMutations;
    }

    internal async Task DrainPersistenceAsync()
    {
        while (_pendingInitialOpen is { } opening) await opening;
        while (_pendingPersistence is { } pending) await pending;
    }

    internal Task ShutdownPersistenceAsync() => _shutdown ??= ShutdownPersistenceCoreAsync();

    private async Task ShutdownPersistenceCoreAsync()
    {
        _shutdownStarted = true;
        CancelWelcomeInitialization();
        await WaitForWelcomeInitializationAsync();
        _statusOverlay.Clear();
        _clockTimer.Stop();
        _focusCheckRetry?.Stop();
        _viewportTransition = null;
        Surface.EndAutomaticHeightPresentation();
        CancelTransientState();
        StopFrameSubscriptionIfIdle();
        await ShutdownInteractionSchedulingAsync();
        TraceInput("Shutdown drain-persistence");
        await WaitForUserMutationsAsync();
        await DrainPersistenceAsync();
        TraceInput("Shutdown drain-presentations");
        await _puzzles.ShutdownAsync();
        await ShutdownMenuStartAsync();
        TraceInput("Shutdown stop-start-menu");
        await StopStartMenuRefreshAsync();
        TraceInput("Shutdown drain-appearance");
        await ShutdownAppearanceAsync();
        TraceInput("Shutdown drain-images");
        await _images.ShutdownAsync();
        TraceInput("Shutdown stop-storage");
        if (_storageExecutor is { } executor) await executor.ShutdownAsync();
        TraceInput("Shutdown complete");
    }

    private async void StopPersistenceAfterClose()
    {
        // Normal exit has already drained. This also closes an idle worker if a
        // window is closed through an unexpected path; never join from the UI.
        try { await ShutdownPersistenceAsync(); }
        catch (Exception ex) { AppLog.Warn("StorageWorker", ex.Message); }
        finally { _images.Dispose(); }
    }
}
