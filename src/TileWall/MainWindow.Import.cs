using System.Text;
using TileWall.ApplicationModel;
using TileWall.Core.Shell;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using TileWall.Resources;
using TileWall.Views;

namespace TileWall;

public sealed partial class MainWindow
{
    internal event Action? ImportReadinessChanged;
    internal bool CanShowExternalImportNotice => !_modal && !_resizing && _gesture.Phase == GesturePhase.Idle && !Surface.HasActiveTitleEdit;
    internal bool CanProcessExternalImport => _hasDocument && !_viewBlocked && !_adaptationBlocked && CanShowExternalImportNotice;

    private async Task OpenImportAsync(string? groupId, int lane)
    {
        if (_modal || _viewBlocked || _adaptationBlocked || !_hasDocument) return;
        BeginModal();
        try
        {
            var dialog = new ImportDialog(this, _images,
                items => PlanImport(items, groupId, lane), items => SaveImport(items, groupId, lane));
            ActiveDialog = dialog;
            await dialog.ShowAsync();
        }
        catch (Exception ex) { Status(string.Format(ProductStrings.Get("Import.Failed"), ex.Message)); }
        finally { EndModal(); }
    }

    private ImportPreview PlanImport(IReadOnlyList<ImportBatchItem> items, string? groupId, int lane)
    {
        if (_viewBlocked || _adaptationBlocked || !_hasDocument)
            return new(null, string.Empty, ProductStrings.Get("Import.Blocked"));
        if (items.Count == 0) return new(null, string.Empty, ProductStrings.Get("Import.SelectFirst"));
        foreach (var item in items)
            if (!item.StillValid()) return new(null, string.Empty, string.Format(ProductStrings.Get("Import.SourceMissing"), item.SourcePath));
        if (!SystemParametersInfoW(SpiGetWorkArea, 0, out _workArea, 0))
            return new(null, string.Empty, ProductStrings.Get("Wall.ReadWorkAreaFailed"));
        RefreshNativeFrame();
        var result = WallBatchPlanner.TryPlan(Document, items.Select(i => i.Member).ToArray(), groupId, lane,
            "import-group-" + items[0].Member.Id, WorkWidthDip, WorkHeightDip, _metrics);
        if (result.Proposal is null)
        {
            AppLog.Warn("WallImport", $"rejection={result.Rejection} detail={result.Detail}");
            return new(null, string.Empty,
                string.Format(ProductStrings.Get("Import.NoSpace"), ProductStrings.WallRejectionReason(result.Rejection)));
        }
        var proposal = result.Proposal;
        var sources = items.ToDictionary(item => item.Member.Id, item => item.SourcePath, StringComparer.Ordinal);
        var text = new StringBuilder(string.Format(ProductStrings.Get("Import.LayoutSize"), proposal.Document.Columns, proposal.Document.Rows));
        // Include all members: reviewers can see any displacement of existing objects as well as the new batch.
        for (var laneIndex = 0; laneIndex < proposal.Document.Lanes.Count; laneIndex++)
        for (var groupIndex = 0; groupIndex < proposal.Document.Lanes[laneIndex].Groups.Count; groupIndex++)
        {
            var group = proposal.Document.Lanes[laneIndex].Groups[groupIndex];
            var name = string.IsNullOrWhiteSpace(group.Title)
                ? $"{ProductStrings.Get("Wall.UntitledGroup")} {groupIndex + 1}" : group.Title;
            foreach (var member in group.Members)
            {
                var memberName = member.Visual.Title ?? ProductStrings.Get(member switch
                { PuzzleMember => "Wall.Puzzle", ClockMember => "Wall.Clock", _ => "Wall.Tile" });
                if (sources.TryGetValue(member.Id, out var sourcePath))
                    memberName = string.Format(ProductStrings.Get("Import.NewMember"), memberName, sourcePath);
                text.AppendLine().Append(string.Format(ProductStrings.Get("Import.Placement"),
                    memberName, laneIndex + 1, name,
                    member.LocalBounds.Column + 1, member.LocalBounds.Row + 1, member.LocalBounds.Width, member.LocalBounds.Height));
            }
        }
        return new(proposal, text.ToString(), null);
    }

    private string? SaveImport(IReadOnlyList<ImportBatchItem> items, string? groupId, int lane)
    {
        var latest = PlanImport(items, groupId, lane);
        if (latest.Proposal is null) return latest.Error;
        var changes = items.Select(i => i.Kind == ExplorerAddKind.CopyShortcut
            ? (EntryChange)new EntryChange.CopyShortcut(i.Member.Id, i.SourcePath)
            : new EntryChange.CreateLink(i.Member.Id, i.SourcePath)).ToArray();
        return CommitProposal(latest.Proposal, changes);
    }

    internal ExternalImportResult ImportExternalBatch(ExplorerAddMessage message)
    {
        if (message.Paths.Count == 0)
            return new(ProductStrings.Get("Import.NoAddableObjects"), false, 0);
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = message.Paths.Select(path => ImportBatchItem.Create(path))
                .Where(item => seen.Add(item.SourcePath)).ToArray();
            var error = SaveImport(items, null, 0);
            var result = error is null ? string.Format(ProductStrings.Get("Import.Added"), items.Length)
                : string.Format(ProductStrings.Get("Import.Failed"), error);
            return new(message.Truncated ? result + " " + ProductStrings.Get("Import.Truncated") : result,
                error is null, error is null ? items.Length : 0);
        }
        catch (Exception ex)
        {
            return new(string.Format(ProductStrings.Get("Import.Failed"), ex.Message)
                + (message.Truncated ? " " + ProductStrings.Get("Import.Truncated") : string.Empty), false, 0);
        }
    }
}
