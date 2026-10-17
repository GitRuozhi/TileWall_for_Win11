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
            var dialog = new ImportDialog(DialogOwner, _images, PrepareStartMenuCandidates, SaveStartMenuImport,
                Document.Settings.VisualStyle);
            ActiveDialog = dialog;
            await dialog.ShowAsync();
        }
        catch (Exception ex) { Status(string.Format(ProductStrings.Get("Import.Failed"), ex.Message)); }
        finally { EndModal(); }
    }

    private IReadOnlyList<StartMenuImportCandidate> PrepareStartMenuCandidates(StartMenuScanResult scan)
    {
        var identities = ImportIdentities;
        var pruned = StartMenuReimport.Prune(Document);
        var retained = IdentitySet(pruned.Document, identities);
        var replacing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in pruned.Removed)
            if (TryIdentity(member, identities) is { } identity) replacing.Add(identity.Key);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var companies = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var output = new List<StartMenuImportCandidate>();
        foreach (var candidate in scan.Candidates.OrderBy(c => c.Source)
                     .ThenBy(c => c.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            LaunchIdentity? identity = null;
            string? error = null;
            var readFailed = false;
            try
            {
                identity = identities.ResolveInput(candidate.FullPath);
                if (identity is null) { error = ProductStrings.Get("Import.InvalidTarget"); readFailed = true; }
                else if (identity.IsUrl && !RegisteredUrlProtocols.IsSupported(identity.Target))
                    error = ProductStrings.Get("Import.UnsupportedProtocol");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
            { error = ex.Message; readFailed = true; }
            var duplicate = identity is not null && !seen.Add(identity.Key);
            output.Add(new(candidate, new StartMenuBucket("pending", string.Empty, 0), identity,
                identity is null ? StartMenuExclusion.None : StartMenuSelectionRules.Classify(candidate.DisplayName, identity),
                duplicate, identity is not null && retained.Contains(identity.Key),
                identity is not null && replacing.Contains(identity.Key), error, readFailed, Company(identity)));
        }
        return output;

        string? Company(LaunchIdentity? identity)
        {
            if (identity is null || identity.IsUrl || !Path.IsPathFullyQualified(identity.Target)) return null;
            if (companies.TryGetValue(identity.Target, out var cached)) return cached;
            string? company = null;
            try
            {
                if (File.Exists(identity.Target)) company = System.Diagnostics.FileVersionInfo.GetVersionInfo(identity.Target).CompanyName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) { }
            companies[identity.Target] = company;
            return company;
        }
    }

    private HashSet<string> IdentitySet(WallDocument snapshot, LaunchIdentityService identities)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in snapshot.Lanes.SelectMany(l => l.Groups).SelectMany(g => g.Members))
            if (TryIdentity(member, identities) is { } identity) result.Add(identity.Key);
        return result;
    }

    private LaunchIdentity? TryIdentity(WallMember member, LaunchIdentityService identities)
    {
        try { return identities.ResolveMember(member, _session.ManagedEntryPath(member)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Warn("LaunchIdentity", $"member={member.Id} identity unreadable: {ex.Message}");
            return null;
        }
    }

    private IReadOnlyList<ImportBatchItem> DeduplicateItems(IReadOnlyList<ImportBatchItem> items,
        WallDocument against, out int skipped)
    {
        var identities = ImportIdentities;
        var seen = IdentitySet(against, identities);
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
