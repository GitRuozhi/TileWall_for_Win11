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
    private bool _persistencePending, _persistenceAdmissionClosed, _pendingCarouselCommit;
    private bool _environmentAfterPersistence;
    private readonly ExitDraftSavePermission _exitDraftSave = new();
    private bool PersistenceInteractionBlocked => _persistencePending || _persistenceAdmissionClosed;
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
            id => !_hasDocument || _windowClosing ? null : _session.Current.Lanes.SelectMany(lane => lane.Groups)
                .SelectMany(group => group.Members).Concat(_session.Current.InactiveMembers).FirstOrDefault(member => member.Id == id));
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
        IReadOnlyList<EntryChange>? changes = null, bool recordUndo = true, bool carousel = false,
        Action<string?>? settle = null)
    {
        Task<string?> Reject(string error) { settle?.Invoke(error); return Task.FromResult<string?>(error); }
        if (PersistenceInteractionBlocked) return Reject(ProductStrings.Get("Wall.SettingsSaving"));
        if (_viewBlocked) return Reject(ProductStrings.Get("Wall.ViewBlocked"));
        var fit = WallViewportPolicy.Validate(proposal, ViewportBudget(proposal.Document.Settings), _metrics);
        if (fit.Proposal is null) return Reject(ViewportFailure(fit));
        _persistencePending = true;
        _pendingCarouselCommit = carousel;
        _puzzles.SetPersistenceBusy(true);
        WallRoot.IsHitTestVisible = false;
        Surface.IsHitTestVisible = false;
        UpdateResizeZones();
        var operation = CommitProposalAsyncCore(proposal, changes, recordUndo, settle);
        _pendingPersistence = operation.IsCompleted ? null : operation;
        return operation;
    }

    private async Task<string?> CommitProposalAsyncCore(WallProposal proposal,
        IReadOnlyList<EntryChange>? changes, bool recordUndo, Action<string?>? settle)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        string? error = null;
        try
        {
            var media = await PrepareFormalMediaAsync(proposal);
            var result = await _session.CommitAsync(media.Proposal, changes, recordUndo, media.Assets);
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
                _pendingCarouselCommit = false;
                _pendingPersistence = null;
                if (!_windowClosing)
                {
                    Surface.IsHitTestVisible = !_viewBlocked && !_persistenceAdmissionClosed;
                    WallRoot.IsHitTestVisible = !_persistenceAdmissionClosed;
                    _puzzles.SetPersistenceBusy(_persistenceAdmissionClosed);
                    UpdateResizeZones();
                    if (_environmentAfterPersistence)
                    { _environmentAfterPersistence = false; QueueEnvironmentRefresh(); }
                    if (_appearanceAfterPersistence)
                    { _appearanceAfterPersistence = false; QueueAppearanceRefresh(); }
                    ImportReadinessChanged?.Invoke();
                }
            }
        }
        return error;
    }

    private void SettleReleasePersistence(string? error)
    {
        _performance.End(error is null ? "release" : "commit-rejected", Surface.VisualCreationCount, Surface.GridCreationCount);
        if (error is not null)
        {
            if (!_viewBlocked) { Surface.Render(Document); ApplyViewport(animateMode: false); }
            Status(error);
        }
    }

    internal void SuspendPersistenceAdmission()
    {
        _persistenceAdmissionClosed = true;
        _puzzles.SetPersistenceBusy(true);
        WallRoot.IsHitTestVisible = false;
        Surface.IsHitTestVisible = false;
        UpdateResizeZones();
        CancelTransientState();
    }

    internal void ResumePersistenceAdmission()
    {
        _persistenceAdmissionClosed = false;
        if (_windowClosing) return;
        _puzzles.SetPersistenceBusy(_persistencePending);
        WallRoot.IsHitTestVisible = !_persistencePending;
        Surface.IsHitTestVisible = !_persistencePending && !_viewBlocked;
        UpdateResizeZones();
        ImportReadinessChanged?.Invoke();
    }

    internal async Task DrainPersistenceAsync()
    {
        while (_pendingPersistence is { } pending) await pending;
    }

    internal async Task ShutdownPersistenceAsync()
    {
        TraceInput("Shutdown drain-persistence");
        await DrainPersistenceAsync();
        TraceInput("Shutdown stop-start-menu");
        await StopStartMenuRefreshAsync();
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
