using TileWall.ApplicationModel;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Import;
using TileWall.Core.Shell;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using TileWall.Resources;
using TileWall.Shell.Interop;
using TileWall.Views;

namespace TileWall;

public sealed partial class MainWindow
{
    internal event Action? ImportReadinessChanged;
    internal bool CanShowExternalImportNotice => !PersistenceInteractionBlocked && !_modal && !_resizing && _gesture.Phase == GesturePhase.Idle && !Surface.HasActiveTitleEdit;
    internal bool CanProcessExternalImport => _hasDocument && !_viewBlocked && CanShowExternalImportNotice;
    private LaunchIdentityService ImportIdentities => new(new LocalFileStore(), _session.LinkFiles);

    private async Task OpenImportAsync(string? groupId, int lane)
    {
        if (PersistenceInteractionBlocked || _modal || _viewBlocked || !_hasDocument) return;
        try
        {
            await BeginModalAsync();
            var dialog = new ImportDialog(DialogOwner, _images, PrepareStartMenuCandidatesAsync, SaveStartMenuImport,
                Document.Settings.VisualStyle);
            ActiveDialog = dialog;
            await dialog.ShowAsync();
        }
        catch (Exception ex) { Status(string.Format(ProductStrings.Get("Import.Failed"), ex.Message)); }
        finally { EndModal(); }
    }

    private Task<IReadOnlyList<StartMenuImportCandidate>> PrepareStartMenuCandidatesAsync(StartMenuScanResult scan,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var executor = _storageExecutor ?? throw new InvalidOperationException("Storage worker is unavailable.");
        // Everything that belongs to the UI/session is captured before queueing.
        var input = StartMenuImportPreparation.Capture(Document, scan, _session.DataRoot, ImportIdentities,
            new(ProductStrings.Get("Import.InvalidTarget"), ProductStrings.Get("Import.UnsupportedProtocol")),
            static target => RegisteredUrlProtocols.IsSupported(target));
        return StartMenuImportPreparation.PrepareAsync(executor, input, token);
    }

    private IReadOnlyList<ImportBatchItem> DeduplicateItems(IReadOnlyList<ImportBatchItem> items,
        WallDocument against, out int skipped)
    {
        var identities = ImportIdentities;
        var seen = StartMenuImportPreparation.IdentitySet(against, _session.DataRoot, identities);
        var accepted = new List<ImportBatchItem>();
        skipped = 0;
        foreach (var item in items)
        {
            if (!item.StillValid()) throw new IOException(string.Format(ProductStrings.Get("Import.SourceMissing"), item.SourcePath));
            var identity = identities.ResolveInput(item.SourcePath) ?? throw new IOException(item.SourcePath);
            if (identity.IsUrl && !RegisteredUrlProtocols.IsSupported(identity.Target))
                throw new IOException(ProductStrings.Get("Import.UnsupportedProtocol"));
            if (!seen.Add(identity.Key)) { skipped++; continue; }
            accepted.Add(item);
        }
        return accepted;
    }

    private string? SaveStartMenuImport(IReadOnlyList<ImportBatchItem> items, bool scanComplete)
    {
        if (_viewBlocked || !_hasDocument) return ProductStrings.Get("Import.Blocked");
        if (items.Count == 0) return ProductStrings.Get("Import.SelectFirst");
        var current = Document;
        var pruned = StartMenuReimport.Prune(current);
        if (!scanComplete && pruned.Removed.Count != 0) return ProductStrings.Get("Import.IncompleteScan");
        var accepted = DeduplicateItems(items, pruned.Document, out var skipped);
        if (accepted.Count == 0) return ProductStrings.Get("Import.NoAddableObjects");
        var batchId = "start-" + Guid.NewGuid().ToString("N");
        var groups = accepted.GroupBy(i => i.GroupBucketKey ?? throw new InvalidOperationException("Missing import group."))
            .OrderBy(g => g.First().GroupOrder).ThenBy(g => g.First().GroupTitle, StringComparer.OrdinalIgnoreCase)
            .Select(bucket =>
            {
                var id = "import-group-" + Guid.NewGuid().ToString("N");
                var source = new ImportSourceMetadata { BatchId = batchId, GroupBucketKey = bucket.Key, OriginGroupId = id };
                return new StartGroup
                {
                    Id = id, Title = bucket.First().GroupTitle, ImportSource = source,
                    Members = bucket.Select(item => (WallMember)(item.Member with
                    {
                        ImportSource = source with { SourceRoot = item.SourceRoot, RelativePath = item.RelativePath },
                    })).ToArray(),
                };
            }).ToArray();
        var result = StartMenuImportPlanner.TryPlan(pruned.Document, current.Generation, groups, _metrics,
            budget: CurrentViewportBudget);
        if (result.Proposal is null)
            return ViewportFailure(result);
        var changes = pruned.Removed.Where(m => m.Entry is not null).Select(m => (EntryChange)new EntryChange.Remove(m.Id))
            .Concat(accepted.Select(i => (EntryChange)new EntryChange.CopyShortcut(i.Member.Id, i.SourcePath))).ToArray();
        var error = CommitProposal(result.Proposal, changes);
        if (error is null) Status(string.Format(ProductStrings.Get("Import.Added"), accepted.Count) +
            (skipped == 0 ? string.Empty : " " + string.Format(ProductStrings.Get("Import.SkippedDuplicates"), skipped)));
        return error;
    }

    internal ExternalImportResult ImportExternalBatch(ExplorerAddMessage message)
    {
        if (message.Paths.Count == 0) return new(ProductStrings.Get("Import.NoAddableObjects"), false, 0);
        try
        {
            var items = DeduplicateItems(message.Paths.Select(path => ImportBatchItem.Create(path)).ToArray(), Document, out var skipped);
            if (items.Count == 0)
                return new(string.Format(ProductStrings.Get("Import.SkippedDuplicates"), skipped), true, 0);
            var plan = WallBatchPlanner.TryPlan(Document, items.Select(i => i.Member).ToArray(), null, 0,
                "external-group-" + items[0].Member.Id, WorkWidthDip, WorkHeightDip, _metrics);
            var error = plan.Proposal is null
                ? string.Format(ProductStrings.Get("Import.NoSpace"), ProductStrings.WallRejectionReason(plan.Rejection))
                : CommitProposal(plan.Proposal, items.Select(i => i.Kind == ExplorerAddKind.CopyShortcut
                    ? (EntryChange)new EntryChange.CopyShortcut(i.Member.Id, i.SourcePath)
                    : new EntryChange.CreateLink(i.Member.Id, i.SourcePath)).ToArray());
            var text = error is null ? string.Format(ProductStrings.Get("Import.Added"), items.Count)
                : string.Format(ProductStrings.Get("Import.Failed"), error);
            if (skipped != 0) text += " " + string.Format(ProductStrings.Get("Import.SkippedDuplicates"), skipped);
            if (message.Truncated) text += " " + ProductStrings.Get("Import.Truncated");
            return new(text, error is null, error is null ? items.Count : 0);
        }
        catch (Exception ex)
        {
            return new(string.Format(ProductStrings.Get("Import.Failed"), ex.Message)
                + (message.Truncated ? " " + ProductStrings.Get("Import.Truncated") : string.Empty), false, 0);
        }
    }
}
