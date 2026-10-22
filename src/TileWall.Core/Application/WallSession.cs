using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Shell;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using System.Text.Json;

namespace TileWall.Core.Application;

/// <summary>The sole owner of the committed, frozen wall snapshot in the UI process.</summary>
internal sealed class WallSession
{
    private readonly WallStore _store;
    private readonly object _gate = new();
    private readonly bool _commitPerformanceEnabled = Environment.GetEnvironmentVariable("TILEWALL_PERF") == "1";
    private WallDocument? _current;
    private WallDocument? _undoPrevious;
    private WallDocument? _undoCommitted;
    private WallUndoToken? _undoToken;
    private Action<WallDocument>? _refreshView;
    private IWallStorageExecutor? _executor;
    private Func<Action, Task>? _acceptOnUi;
    private bool _writePending;
    private UiCommitReservation? _activeUiCommit;
    private bool _storeRecoveryBlocked;
    private bool _resultUnknown;

    private sealed class UiCommitReservation { }

    public WallSession(string dataRoot)
        : this(dataRoot, new LocalFileStore(), new ShellLinkFileService()) { }

    internal WallSession(string dataRoot, IFileStore files, ILnkFileService links)
    {
        DataRoot = dataRoot;
        LinkFiles = links;
        _store = new WallStore(files, links, dataRoot);
    }

    public string DataRoot { get; }
    public ILnkFileService LinkFiles { get; }
    public WallDocument Current => _current ?? throw new InvalidOperationException("Wall is not open.");
    public bool CanUndo => _undoToken is not null;
    public bool ViewBlocked { get; private set; }
    public bool RequiresReopen { get; private set; }
    public bool RecoveryBlocked => _storeRecoveryBlocked;
    public bool ResultUnknown => _resultUnknown;
    public bool WritePending { get { lock (_gate) return _writePending; } }

    public void ConfigureAsyncExecution(IWallStorageExecutor executor, Func<Action, Task> acceptOnUi)
    {
        lock (_gate)
        {
            if (_writePending) throw new InvalidOperationException("A storage operation is pending.");
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _acceptOnUi = acceptOnUi ?? throw new ArgumentNullException(nameof(acceptOnUi));
        }
    }

    public void SetViewRefresh(Action<WallDocument> refreshView)
    {
        _refreshView = refreshView ?? throw new ArgumentNullException(nameof(refreshView));
    }

    public bool RefreshViewFromCurrent()
    {
        lock (_gate) return !_writePending && !RequiresReopen && !_storeRecoveryBlocked && RefreshViewCore();
    }

    private bool RefreshViewCore()
    {
        try
        {
            _refreshView?.Invoke(Current);
            ViewBlocked = false;
            return true;
        }
        catch (Exception)
        {
            ViewBlocked = true;
            return false;
        }
    }

    public string? ManagedEntryPath(WallMember member)
    {
        if (member.Entry is not { } entry ||
            !EntryPathGuard.IsEntryRelativePathAllowed(DataRoot, member.Id, entry.RelativePath))
            return null;
        return Path.GetFullPath(Path.Combine(DataRoot,
            entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    public string? ManagedMediaPath(ManagedMediaReference? media) =>
        media is null ? null : _store.ManagedMediaPath(media);

    public WallOpenResult Open(int initialColumns, int initialRows)
    {
        lock (_gate)
        {
            if (_writePending) throw new InvalidOperationException("A storage operation is pending.");
            var result = _store.Open();
            if (result.Status == WallOpenStatus.Fresh)
                result = _store.InitializeFresh(WallDocument.Empty(initialColumns, initialRows));
            if (result.Document is not null && result.Status is WallOpenStatus.Loaded or WallOpenStatus.RecoveredPrevious)
            {
                _current = result.Document;
                _undoPrevious = null;
                _undoCommitted = null;
                _undoToken = null;
                RequiresReopen = false;
                _storeRecoveryBlocked = false;
                _resultUnknown = false;
                ViewBlocked = false;
            }
            else if (result.Status == WallOpenStatus.RecoveryBlocked)
            {
                MarkStoreRecoveryBlocked();
            }
            else if (result.Status == WallOpenStatus.OpenFailed)
            {
                // No recovery latch. An existing document or latch stays blocked.
                if (RequiresReopen || _storeRecoveryBlocked || _resultUnknown || _current is not null)
                    ViewBlocked = true;
            }
            else if (RequiresReopen || _storeRecoveryBlocked || _resultUnknown)
            {
                // A failed attempt to read the committed snapshot must not unlock the
                // old in-memory document or make it look current again.
                ViewBlocked = true;
            }
            return result;
        }
    }

    public WallLayoutResult Propose(WallAction action, double workWidthDip, double workHeightDip,
        WallMetrics? metrics = null)
    {
        lock (_gate)
        {
            if (_writePending)
                return new(null, WallRejection.StaleGeneration, "A storage operation is pending.");
            if (ViewBlocked)
                return new(null, WallRejection.StaleGeneration, ViewBlockedDiagnostic());
            return WallLayoutEngine.TryApply(Current, action, workWidthDip, workHeightDip, metrics);
        }
    }

    public WallSessionResult Commit(WallProposal proposal, IReadOnlyList<EntryChange>? changes = null,
        bool recordUndo = true, IReadOnlyList<PreparedMediaAsset>? mediaAssets = null)
    {
        var timings = CreateCommitTimings();
        var total = timings?.Measure("session.total");
        var outcome = "exception";
        long? generation = null;
        try
        {
            lock (_gate)
            {
                if (_writePending) return BusyResult();
                if (ViewBlocked)
                {
                    outcome = RequiresReopen ? "requires-reopen" : "view-blocked";
                    return BlockedViewResult();
                }
                var before = Current;
                generation = before.Generation;
                // The store plans and validates the same request inside Commit; planning here
                // duplicated the full-wall traversal for every save.
                var request = new WallCommitRequest(before.Generation, proposal.Document, changes ?? [], mediaAssets);
                WallCommitResult result;
                try { result = _store.Commit(before, request, recordUndo, timings); }
                catch (Exception)
                {
                    MarkResultUnknown();
                    throw;
                }
                if (timings is not null) outcome = result.Status.ToString();
                if (result.Status is not (WallCommitStatus.Committed or WallCommitStatus.CommittedNeedsCleanup) ||
                    result.CommittedDocument is not { } committedDocument)
                {
                    if (result.Status == WallCommitStatus.RecoveryBlocked)
                        MarkStoreRecoveryBlocked();
                    return new(false, false, result.Diagnostics);
                }

                var accepted = AcceptCommitted(before, result, committedDocument, recordUndo, timings);
                if (timings is not null && accepted.ViewFailed) outcome += ":view-failed";
                return accepted;
            }
        }
        finally
        {
            total?.Dispose();
            if (timings is not null)
            {
                // One aggregate after the operation; logging/serialization cannot affect its outcome.
                try
                {
                    AppLog.Warn("CommitPerformance", JsonSerializer.Serialize(new
                    {
                        schema = 1, measurement = "wall-time", generation, recordUndo,
                        entryChanges = changes?.Count ?? 0, outcome,
                        stages = timings.Snapshot(),
                    }));
                }
                catch { }
            }
        }
    }

    public Task<WallSessionResult> CommitAsync(WallProposal proposal, IReadOnlyList<EntryChange>? changes = null,
        bool recordUndo = true, IReadOnlyList<PreparedMediaAsset>? mediaAssets = null)
    {
        lock (_gate)
        {
            if (_writePending) return Task.FromResult(BusyResult());
            if (ViewBlocked) return Task.FromResult(BlockedViewResult());
            if (_executor is null || _acceptOnUi is null)
                throw new InvalidOperationException("Asynchronous storage has not been configured.");
            var before = Current;
            var frozen = proposal with { Document = WallJson.Freeze(proposal.Document) };
            var entryChanges = changes?.ToArray() ?? [];
            var preparedMedia = mediaAssets?.ToArray() ?? [];
            var reservation = new UiCommitReservation();
            _writePending = true;
            _activeUiCommit = reservation;
            return CommitReservedAsync(before, frozen, entryChanges, recordUndo, preparedMedia, reservation, _executor, _acceptOnUi);
        }
    }

    private async Task<WallSessionResult> CommitReservedAsync(WallDocument before, WallProposal proposal,
        IReadOnlyList<EntryChange> changes, bool recordUndo, IReadOnlyList<PreparedMediaAsset> mediaAssets, UiCommitReservation reservation,
        IWallStorageExecutor executor, Func<Action, Task> acceptOnUi)
    {
        var timings = CreateCommitTimings();
        var total = timings?.Measure("session.total");
        var outcome = "exception";
        var storageThread = 0;
        var acceptanceThread = 0;
        WallCommitResult? stored = null;
        WallSessionResult? accepted = null;
        try
        {
            stored = await executor.ExecuteAsync(() =>
            {
                storageThread = Environment.CurrentManagedThreadId;
                // Commit owns planning; a second PlanEntries here duplicated the full wall.
                return _store.Commit(before,
                    new WallCommitRequest(before.Generation, proposal.Document, changes, mediaAssets),
                    recordUndo, timings);
            }).ConfigureAwait(false);
            outcome = stored.Status.ToString();
            if (stored.Status == WallCommitStatus.RecoveryBlocked)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_activeUiCommit, reservation))
                        MarkStoreRecoveryBlocked();
                }
            }
            // A rejected/recovery-blocked transaction has nothing to publish on the UI
            // thread. Requiring a dispatcher turn here turned a clean rejection into a
            // dispatcher exception during shutdown.
            if (stored.Status is not (WallCommitStatus.Committed or WallCommitStatus.CommittedNeedsCleanup))
            {
                return new(false, false, stored.Diagnostics);
            }
            if (stored.CommittedDocument is not { } committedDocument)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_activeUiCommit, reservation))
                    {
                        MarkRequiresReopen();
                        _activeUiCommit = null;
                    }
                }
                throw new InvalidOperationException("The committed wall snapshot is unavailable for UI acceptance.");
            }

            await acceptOnUi(() =>
            {
                lock (_gate)
                {
                    // A dispatcher may invoke its callback after returning or more than
                    // once. Only the live reservation can publish this disk snapshot.
                    if (!ReferenceEquals(_activeUiCommit, reservation) || accepted is not null) return;
                    acceptanceThread = Environment.CurrentManagedThreadId;
                    accepted = AcceptCommitted(before, stored, committedDocument, recordUndo, timings);
                }
            }).ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(_activeUiCommit, reservation))
                {
                    if (accepted is null) MarkRequiresReopen();
                    _activeUiCommit = null;
                }
            }
            if (accepted is null)
                throw new InvalidOperationException("The UI dispatcher completed without accepting the committed snapshot.");
            if (accepted.ViewFailed) outcome += ":view-failed";
            return accepted;
        }
        catch
        {
            // A committed disk snapshot must never be rolled back merely because its
            // UI dispatcher disappeared. Normal exit drains before closing that dispatcher.
            lock (_gate)
            {
                if (ReferenceEquals(_activeUiCommit, reservation))
                {
                    if (stored is null)
                        MarkResultUnknown();
                    else if (stored.Status == WallCommitStatus.RecoveryBlocked)
                        MarkStoreRecoveryBlocked();
                    else if (stored.Status is WallCommitStatus.Committed or WallCommitStatus.CommittedNeedsCleanup && accepted is null)
                        MarkRequiresReopen();
                    _activeUiCommit = null;
                }
            }
            // Once Current was published, an exception raised by the dispatcher after
            // running the callback does not turn that accepted commit into an unknown one.
            if ((stored?.Status is WallCommitStatus.Committed or WallCommitStatus.CommittedNeedsCleanup) && accepted is not null)
            {
                outcome += ":dispatcher-failed-after-accept";
                return accepted;
            }
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeUiCommit, reservation)) _activeUiCommit = null;
                _writePending = false;
            }
            total?.Dispose();
            if (timings is not null)
            {
                try
                {
                    AppLog.Warn("CommitPerformance", JsonSerializer.Serialize(new
                    {
                        schema = 2, measurement = "wall-time", generation = before.Generation, recordUndo,
                        entryChanges = changes.Count, outcome, asynchronous = true,
                        storageThread, acceptanceThread, stages = timings.Snapshot(),
                    }));
                }
                catch { }
            }
        }
    }

    private WallSessionResult AcceptCommitted(WallDocument before, WallCommitResult result,
        WallDocument committedDocument, bool recordUndo, WallCommitTimings? timings)
    {
        // Called only on the UI acceptance path; disk replacement is the commit point.
        // WallStore returns a frozen snapshot from every accepting path.
        using (timings?.Measure("session.publish")) _current = committedDocument;
        if (recordUndo)
        {
            _undoPrevious = result.Undo is null ? null : before;
            _undoCommitted = result.Undo is null ? null : committedDocument;
            _undoToken = result.Undo;
        }
        bool viewOkay;
        using (timings?.Measure("session.viewRefresh")) viewOkay = RefreshViewCore();
        return new(true, result.Status == WallCommitStatus.CommittedNeedsCleanup,
            viewOkay ? result.Diagnostics : [.. result.Diagnostics, "Committed on disk; view refresh failed."], !viewOkay);
    }

    private static WallSessionResult BusyResult() => new(false, false, ["A storage operation is pending."]);

    private WallSessionResult BlockedViewResult() =>
        new(false, false, [ViewBlockedDiagnostic()]);

    private string ViewBlockedDiagnostic() => _resultUnknown
        ? "The storage result is unknown; reopen the wall before continuing."
        : RequiresReopen
        ? "The committed wall snapshot was not accepted by the UI; reopen the wall from disk before continuing."
        : _storeRecoveryBlocked
            ? "Storage recovery is blocked; reopen the wall after recovery succeeds before continuing."
            : "View is blocked until rebuilt from Current.";

    private void MarkStoreRecoveryBlocked()
    {
        _storeRecoveryBlocked = true;
        ViewBlocked = true;
    }

    private void MarkResultUnknown()
    {
        _resultUnknown = true;
        ViewBlocked = true;
    }

    private void MarkRequiresReopen()
    {
        RequiresReopen = true;
        ViewBlocked = true;
    }

    private WallCommitTimings? CreateCommitTimings()
    {
        if (!_commitPerformanceEnabled) return null;
        try { return new WallCommitTimings(); }
        catch { return null; }
    }

    public WallSessionResult Undo(Func<WallDocument, string?>? preflight = null)
    {
        lock (_gate)
        {
            if (_writePending) return BusyResult();
            if (ViewBlocked) return BlockedViewResult();
            if (_undoPrevious is null || _undoCommitted is null || _undoToken is null)
                return new(false, false, ["没有可撤销的操作。"]);
            // Store undo preserves current settings and can therefore restore a layout
            // created in a different viewport mode. Check that exact geometry under the
            // same lock before touching disk or consuming the active undo material.
            var prepared = WallStore.PrepareUndoCandidate(_undoPrevious, _undoCommitted, Current);
            if (prepared.Proposal is not { } undoProposal)
                return new(false, false, [prepared.Detail ?? prepared.Rejection.ToString()]);
            var candidate = undoProposal.Document;
            if (preflight?.Invoke(candidate) is { } rejection)
                return new(false, false, [rejection]);
            WallCommitResult result;
            try { result = _store.Undo(Current, _undoPrevious, _undoToken, Current.Generation, candidate); }
            catch (Exception)
            {
                MarkResultUnknown();
                throw;
            }
            if (result.Status is not (WallCommitStatus.Committed or WallCommitStatus.CommittedNeedsCleanup) ||
                result.CommittedDocument is null)
            {
                if (result.Status == WallCommitStatus.RecoveryBlocked)
                    MarkStoreRecoveryBlocked();
                return new(false, false, result.Diagnostics);
            }
            _current = result.CommittedDocument;
            _undoPrevious = null;
            _undoCommitted = null;
            _undoToken = null;
            var viewOkay = RefreshViewCore();
            return new(true, result.Status == WallCommitStatus.CommittedNeedsCleanup,
                viewOkay ? result.Diagnostics : [.. result.Diagnostics, "Undo committed on disk; view refresh failed."],
                !viewOkay);
        }
    }
}

internal sealed record WallSessionResult(bool Committed, bool NeedsCleanup,
    IReadOnlyList<string> Diagnostics, bool ViewFailed = false);

internal interface IWallStorageExecutor
{
    Task<T> ExecuteAsync<T>(Func<T> work);
    Task ShutdownAsync();
}
