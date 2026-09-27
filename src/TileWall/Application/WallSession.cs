using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Storage;
using TileWall.Core.Wall;

namespace TileWall.ApplicationModel;

/// <summary>The sole owner of the committed, frozen wall snapshot in the UI process.</summary>
internal sealed class WallSession
{
    private readonly WallStore _store;
    private readonly object _gate = new();
    private WallDocument? _current;
    private WallDocument? _undoPrevious;
    private WallUndoToken? _undoToken;
    private Action<WallDocument>? _refreshView;

    public WallSession(string dataRoot)
        : this(dataRoot, new LocalFileStore(), new ShellLinkFileService()) { }

    internal WallSession(string dataRoot, IFileStore files, ILnkFileService links)
    {
        DataRoot = dataRoot;
        LinkFiles = links;
        _store = new WallStore(files, links, new FileSystemDataDirectory(dataRoot));
    }

    public string DataRoot { get; }
    public ILnkFileService LinkFiles { get; }
    public WallDocument Current => _current ?? throw new InvalidOperationException("Wall is not open.");
    public bool CanUndo => _undoToken is not null;
    public bool ViewBlocked { get; private set; }

    public void SetViewRefresh(Action<WallDocument> refreshView)
    {
        _refreshView = refreshView ?? throw new ArgumentNullException(nameof(refreshView));
    }

    public bool RefreshViewFromCurrent()
    {
        lock (_gate) return RefreshViewCore();
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

    public WallOpenResult Open(int initialColumns, int initialRows)
    {
        lock (_gate)
        {
            var result = _store.Open();
            if (result.Status == WallOpenStatus.LegacyResetRequired)
                result = _store.ResetLegacyState();
            if (result.Status == WallOpenStatus.Fresh)
                result = _store.InitializeFresh(WallDocument.Empty(initialColumns, initialRows));
            if (result.Document is not null && result.Status is WallOpenStatus.Loaded or WallOpenStatus.RecoveredPrevious)
                _current = WallJson.Freeze(result.Document);
            return result;
        }
    }

    public WallLayoutResult Propose(WallAction action, double workWidthDip, double workHeightDip,
        WallMetrics? metrics = null)
    {
        lock (_gate)
            return WallLayoutEngine.TryApply(Current, action, workWidthDip, workHeightDip, metrics);
    }

    public WallSessionResult Commit(WallProposal proposal, IReadOnlyList<EntryChange>? changes = null,
        bool recordUndo = true)
    {
        lock (_gate)
        {
            if (ViewBlocked) return new(false, false, ["View is blocked until rebuilt from Current."]);
            var before = Current;
            var plan = _store.PlanEntries(before, proposal.Document, changes ?? []);
            if (plan.Request is null)
                return new(false, false, plan.Diagnostics);
            var result = _store.Commit(before, plan.Request, recordUndo);
            if (result.Status is not (WallCommitStatus.Committed or WallCommitStatus.CommittedNeedsCleanup) ||
                result.CommittedDocument is null)
                return new(false, false, result.Diagnostics);

            // Disk commit is the linearization point. A view failure must never restore before.
            _current = WallJson.Freeze(result.CommittedDocument);
            if (recordUndo)
            {
                _undoPrevious = result.Undo is null ? null : before;
                _undoToken = result.Undo;
            }
            var viewOkay = RefreshViewCore();
            return new(true, result.Status == WallCommitStatus.CommittedNeedsCleanup,
                viewOkay ? result.Diagnostics : [.. result.Diagnostics, "Committed on disk; view refresh failed."],
                !viewOkay);
        }
    }

    public WallSessionResult Undo()
    {
        lock (_gate)
        {
            if (ViewBlocked) return new(false, false, ["View is blocked until rebuilt from Current."]);
            if (_undoPrevious is null || _undoToken is null)
                return new(false, false, ["没有可撤销的操作。"]);
            var result = _store.Undo(Current, _undoPrevious, _undoToken, Current.Generation);
            if (result.Status is not (WallCommitStatus.Committed or WallCommitStatus.CommittedNeedsCleanup) ||
                result.CommittedDocument is null)
                return new(false, false, result.Diagnostics);
            _current = WallJson.Freeze(result.CommittedDocument);
            _undoPrevious = null;
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
