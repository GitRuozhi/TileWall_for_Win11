using TileWall.Core.Import;
using TileWall.Core.Shell;
using TileWall.Core.Wall;

namespace TileWall.ApplicationModel;

internal enum ImportOutcome { Committed, NoChange, Canceled, Rejected, Failed, ResultUnknown, RequiresReopen, RecoveryBlocked }

/// <summary>The aggregate diagnostics for one user confirmation. Reasons are fixed labels or enum values.</summary>
internal sealed class ImportOperation(ImportPageSourceKind source, bool automatic = false)
{
    private readonly string _id = Guid.NewGuid().ToString("N");
    private int _finished;
    private string _stage = "confirmation";
    private string _counts = "";
    private string _layout = "";

    internal void ObserveSource(int acquired, int candidates, int filtered, int selected,
        int duplicates, int missing, int secondary, int readErrors)
    {
        _counts = $" acquired={acquired} candidates={candidates} filtered={filtered} selected={selected}" +
            $" duplicate={duplicates} missing={missing} secondary={secondary} readErrors={readErrors}";
        Stage("source");
    }

    internal void Plan(WallDocument before, WallProposal? proposal, int accepted, string result, bool smallTiles = false)
    {
        var proposed = proposal?.Document;
        _layout = $" plan={result} beforeColumns={before.Columns} beforeRows={before.Rows} accepted={accepted}";
        if (proposed is not null)
            _layout += $" proposedColumns={proposed.Columns} proposedRows={proposed.Rows}" +
                $" grewWall={proposed.Columns > before.Columns || proposed.Rows > before.Rows}" +
                $" smallTiles={smallTiles} enabledExpansion={proposed.Settings.ExpandColumns && !before.Settings.ExpandColumns}";
        Stage("planning");
    }

    internal void Prepared(int count)
    {
        _counts += $" prepared={count}";
        Stage("prepared");
    }

    internal void Stage(string stage)
    {
        _stage = stage;
        AppLog.Info("ImportOperation", Context());
    }

    internal void Finish(ImportOutcome outcome, string reason, WallDocument? committed = null)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        var message = $"{Context()} outcome={outcome} reason={reason}";
        if (committed is not null)
            message += $" finalColumns={committed.Columns} finalRows={committed.Rows}";
        if (outcome is ImportOutcome.Committed or ImportOutcome.NoChange or ImportOutcome.Canceled)
            AppLog.Info("ImportOperation", message);
        else AppLog.Warn("ImportOperation", message);
    }

    private string Context() => $"operation={_id} source={source} automatic={automatic} stage={_stage}{_counts}{_layout}";
}
