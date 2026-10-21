using TileWall.ApplicationModel;
using TileWall.Core.Application;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Import;
using TileWall.Core.Shell;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using TileWall.Graphics;
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
    private StartMenuImportCoordinator? _startMenuImports;
    private sealed record ImportedIconCopy(string MemberId, string Sha256, string? TargetPath,
        string? IconPath, int? IconIndex, string Directory, string Key);
    private (long Generation, ImportedIconCopy[] Copies)? _pendingImportIconCopies;

    private void PrepareCommittedImportIcons(WallDocument document)
    {
        if (_pendingImportIconCopies is not { } pending || pending.Generation != document.Generation) return;
        _pendingImportIconCopies = null;
        var members = document.Lanes.SelectMany(lane => lane.Groups).SelectMany(group => group.Members)
            .ToDictionary(member => member.Id, StringComparer.Ordinal);
        foreach (var copy in pending.Copies)
            if (members.TryGetValue(copy.MemberId, out var member) && _session.ManagedEntryPath(member) is { } path)
                _ = _images.CopyCachedIconsToMemberAsync(copy.MemberId, path, copy.Sha256,
                    copy.TargetPath, copy.IconPath, copy.IconIndex, copy.Directory, copy.Key);
    }

    private StartMenuImportCoordinator StartMenuImports => _startMenuImports ??= new(_session.DataRoot,
        _storageExecutor ?? throw new InvalidOperationException("Storage worker is unavailable."), ImportIdentities, _session.LinkFiles)
    {
        IconPrepare = (source, directory, token) => _images.PrewarmPeIconsAsync(source.Candidate.FullPath,
            source.Identity?.IsUrl == false ? source.Identity.Target : null, source.LinkFields?.IconPath ?? source.UrlIconPath,
            source.LinkFields?.IconIndex ?? source.UrlIconIndex, source.SourceSha256, directory, token),
    };

    internal void ScheduleStartMenuStartupRefresh()
    {
        if (_hasDocument && !_viewBlocked && !PersistenceInteractionBlocked) StartMenuImports.ScheduleStartupRefresh();
    }

    internal Task StopStartMenuRefreshAsync() => _startMenuImports?.StopAsync() ?? Task.CompletedTask;

    private async Task OpenImportAsync(string? groupId, int lane)
    {
        if (PersistenceInteractionBlocked || _modal || _viewBlocked || !_hasDocument) return;
        try
        {
            if (!await BeginModalAsync()) return;
            var dialog = new ImportDialog(DialogOwner, _images, StartMenuImports, SaveStartMenuImport,
                Document.Settings.VisualStyle, LoadStartMenuIconAsync);
            ActiveDialog = dialog;
            await dialog.ShowAsync();
        }
        catch (Exception ex) { Status(string.Format(ProductStrings.Get("Import.Failed"), ex.Message)); }
        finally { EndModal(); }
    }

    private Task<PuzzleFrameResult> LoadStartMenuIconAsync(StartMenuImportCandidate candidate, int edge, CancellationToken token) =>
        candidate.IconDirectory is { } directory && candidate.IconKey is { } key
            ? _images.LoadCachedIconFrameAsync(directory, key, edge, token)
            : Task.FromResult(new PuzzleFrameResult(null, WallImageStatus.None));

    private string? SaveStartMenuImport(IReadOnlyList<ImportBatchItem> items, bool preserveChanges)
    {
        if (_viewBlocked || !_hasDocument) return ProductStrings.Get("Import.Blocked");
        if (items.Count == 0) return ProductStrings.Get("Import.SelectFirst");
        var current = Document;
        var identities = ImportIdentities;
        var verified = new List<(ImportBatchItem Item, LaunchIdentity Identity, string Sha256, LinkFields? Fields)>();
        foreach (var item in items)
        {
            var candidate = item.CandidateSnapshot ?? throw new IOException("Missing selected source snapshot.");
            try
            {
                if (!StartMenuImportCoordinator.SnapshotRootMatches(candidate, requireAvailable: true) ||
                    !candidate.IsImportable || !StartMenuSourceCache.IsHash(item.ExpectedSourceSha256))
                    throw new IOException(ImportText("Import.SourceChanged", "来源已变化，请更新后重新确认"));
                var observed = StartMenuImportScan.ReadSource(candidate, CancellationToken.None);
                var fields = candidate.Kind == EntryKind.Lnk ? _session.LinkFiles.Read(candidate.FullPath) : null;
                var identity = identities.ResolveObservedInput(candidate.Kind, candidate.FullPath, observed.Bytes, fields);
                var after = StartMenuImportScan.ReadSource(candidate, CancellationToken.None);
                if (identity is null || identity.Key != item.ExpectedIdentityKey ||
                    !observed.Sha256.Equals(item.ExpectedSourceSha256, StringComparison.OrdinalIgnoreCase) ||
                    observed.Sha256 != after.Sha256)
                    throw new IOException(ImportText("Import.SourceChanged", "来源已变化，请更新后重新确认"));
                if (identity.IsUrl && !RegisteredUrlProtocols.IsSupported(identity.Target))
                    throw new IOException(ProductStrings.Get("Import.UnsupportedProtocol"));
                verified.Add((item, identity, observed.Sha256, fields));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                StartMenuImports.InvalidateSource(candidate);
                return string.Format(ProductStrings.Get("Import.Failed"), ex.Message);
            }
        }
        // Duplicate scope is the final normalized display group, never the complete wall.
        var accepted = verified.OrderBy(value => value.Item.GroupOrder)
            .ThenBy(value => value.Item.GroupTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Item.CandidateSnapshot!.Source)
            .ThenBy(value => value.Item.SourcePath, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(value => (StartMenuSourceCache.GroupNameKey(value.Item.GroupTitle), value.Identity.Key)).ToArray();
        var batchId = "start-" + Guid.NewGuid().ToString("N");
        var groups = accepted.GroupBy(value => StartMenuSourceCache.GroupNameKey(value.Item.GroupTitle), StringComparer.Ordinal)
            .Select(bucket =>
            {
                var id = "import-group-" + Guid.NewGuid().ToString("N");
                var source = new ImportSourceMetadata { BatchId = batchId, GroupBucketKey = bucket.First().Item.GroupBucketKey!, OriginGroupId = id };
                var members = bucket.Select(value => new StartMenuMergeMember(value.Item.Member with
                {
                    ImportSource = source with { SourceRoot = value.Item.SourceRoot, RelativePath = value.Item.RelativePath },
                }, value.Identity.Key)).ToArray();
                return new StartMenuMergeGroup(new StartGroup
                {
                    Id = id, Title = bucket.First().Item.GroupTitle?.Trim(), ImportSource = source,
                    Members = members.Select(member => member.Member).ToArray(),
                }, members);
            }).ToArray();
        var merged = StartMenuReimport.Merge(current, groups,
            member => StartMenuImportPreparation.TryIdentity(member, _session.DataRoot, identities)?.Key, preserveChanges);
        var result = StartMenuImportPlanner.TryPlan(current, current.Generation, merged.Groups, _metrics, CurrentViewportBudget);
        if (result.Proposal is null) return ViewportFailure(result);
        var changes = accepted.Select(value => (EntryChange)new EntryChange.CopyShortcut(
            merged.MemberIds[value.Item.Member.Id], value.Item.SourcePath, value.Sha256)).ToArray();
        _pendingImportIconCopies = (result.Proposal.Document.Generation,
            accepted.Where(value => value.Item.IconDirectory is not null && value.Item.IconKey is not null)
                .Select(value => new ImportedIconCopy(merged.MemberIds[value.Item.Member.Id], value.Sha256,
                    value.Identity.IsUrl ? null : value.Identity.Target, value.Fields?.IconPath, value.Fields?.IconIndex,
                    value.Item.IconDirectory!, value.Item.IconKey!)).ToArray());
        string? error;
        try { error = CommitProposal(result.Proposal, changes); }
        finally { _pendingImportIconCopies = null; }
            if (error is null) TransientStatus(string.Format(ProductStrings.Get("Import.Added"), accepted.Length));
        return error;
    }

    internal ExternalImportResult ImportExternalBatch(ExplorerAddMessage message)
    {
        if (message.Paths.Count == 0) return new(ProductStrings.Get("Import.NoAddableObjects"), false, 0);
        try
        {
            var identities = ImportIdentities;
            var items = message.Paths.Select(path =>
            {
                var item = ImportBatchItem.Create(path);
                if (!item.StillValid()) throw new IOException(string.Format(ProductStrings.Get("Import.SourceMissing"), path));
                var identity = identities.ResolveInput(item.SourcePath) ?? throw new IOException(item.SourcePath);
                if (identity.IsUrl && !RegisteredUrlProtocols.IsSupported(identity.Target))
                    throw new IOException(ProductStrings.Get("Import.UnsupportedProtocol"));
                return item with { ExpectedIdentityKey = identity.Key };
            }).ToArray();
            var plan = WallBatchPlanner.TryPlan(Document, items.Select(item => item.Member).ToArray(), null, 0,
                "external-group-" + items[0].Member.Id, WorkWidthDip, WorkHeightDip, _metrics);
            if (plan.Proposal is null) return new(string.Format(ProductStrings.Get("Import.NoSpace"),
                ProductStrings.WallRejectionReason(plan.Rejection)), false, 0);
            var incoming = items.ToDictionary(item => item.Member.Id, StringComparer.Ordinal);
            var skippedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in plan.Proposal.Document.Lanes.SelectMany(lane => lane.Groups))
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in group.Members)
                    if (incoming.TryGetValue(member.Id, out var item) && !seen.Add(item.ExpectedIdentityKey!)) skippedIds.Add(member.Id);
            }
            var proposal = plan.Proposal;
            if (skippedIds.Count != 0)
            {
                var document = proposal.Document with
                {
                    Lanes = proposal.Document.Lanes.Select(lane => lane with
                    {
                        Groups = lane.Groups.Select(group => group with
                        {
                            Members = group.Members.Where(member => !skippedIds.Contains(member.Id)).ToArray(),
                        }).Where(group => group.Members.Count != 0).ToArray(),
                    }).ToArray(),
                };
                proposal = WallLayoutEngine.NormalizeExpanded(document, _metrics).Proposal ?? throw new IOException("Invalid external import layout.");
            }
            var accepted = items.Where(item => !skippedIds.Contains(item.Member.Id)).ToArray();
            var error = CommitProposal(proposal, accepted.Select(item => item.Kind == ExplorerAddKind.CopyShortcut
                ? (EntryChange)new EntryChange.CopyShortcut(item.Member.Id, item.SourcePath)
                : new EntryChange.CreateLink(item.Member.Id, item.SourcePath)).ToArray());
            var text = error is null ? string.Format(ProductStrings.Get("Import.Added"), accepted.Length)
                : string.Format(ProductStrings.Get("Import.Failed"), error);
            if (skippedIds.Count != 0) text += " " + string.Format(ProductStrings.Get("Import.SkippedDuplicates"), skippedIds.Count);
            if (message.Truncated) text += " " + ProductStrings.Get("Import.Truncated");
            return new(text, error is null, error is null ? accepted.Length : 0);
        }
        catch (Exception ex)
        {
            return new(string.Format(ProductStrings.Get("Import.Failed"), ex.Message)
                + (message.Truncated ? " " + ProductStrings.Get("Import.Truncated") : string.Empty), false, 0);
        }
    }

    private static string ImportText(string key, string fallback)
    {
        var text = ProductStrings.Get(key);
        return text == key ? fallback : text;
    }
}
