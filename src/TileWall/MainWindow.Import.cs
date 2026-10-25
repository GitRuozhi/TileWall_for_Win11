using System.Security.Cryptography;
using TileWall.ApplicationModel;
using TileWall.Core.Application;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Exchange;
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
    internal bool CanShowExternalImportNotice => !_welcomeInitializationPending && !PersistenceInteractionBlocked && !_modal && !_resizing && _gesture.Phase == GesturePhase.Idle && !Surface.HasActiveTitleEdit;
    internal bool CanProcessExternalImport => _hasDocument && !_viewBlocked && CanShowExternalImportNotice;
    private LaunchIdentityService ImportIdentities => new(new LocalFileStore(), _session.LinkFiles);
    private WallPackageReader CreateWallPackageReader() => new(
        _session.ObservePackageEntryAsync, _images.VerifyPackageImageAsync);
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
            var dialog = new ImportDialog(DialogOwner, _images, StartMenuImports, CreateWallPackageReader(),
                ReadImportContextAsync, ReadImportSourceAsync, SaveImportAsync, IsImportContextCurrent,
                member => _session.ManagedEntryPath(member), Document.Settings.VisualStyle, LoadStartMenuIconAsync);
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

    private bool IsImportContextCurrent(ImportPageContext context) => _hasDocument && !_viewBlocked &&
        !_session.ResultUnknown && !_session.RequiresReopen && !_session.RecoveryBlocked &&
        Document.Generation == context.Document.Generation && CurrentViewportBudget == context.Budget;

    private async Task<ImportPageContext> ReadImportContextAsync(CancellationToken token)
    {
        var current = Document;
        var budget = CurrentViewportBudget;
        var observations = await (_storageExecutor ?? throw new InvalidOperationException()).ExecuteAsync(() =>
        {
            var files = new LocalFileStore();
            var guarded = new GuardedWallFiles(files, new WallStoragePaths(files, _session.DataRoot));
            var identities = ImportIdentities;
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var member in current.Lanes.SelectMany(lane => lane.Groups).SelectMany(group => group.Members))
            {
                token.ThrowIfCancellationRequested();
                if (member.Entry is not { } entry) continue;
                var path = _session.ManagedEntryPath(member) ?? throw new IOException("Invalid formal entry path.");
                using var stream = guarded.OpenReadProtected(path);
                if (stream.Length is < 1 or > EntryObservation.MaximumBytes) throw new InvalidDataException("Invalid formal entry length.");
                var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
                var fields = entry.Kind == ManagedEntryKind.Lnk ? _session.LinkFiles.Read(path) : null;
                result[member.Id] = EntryObservation.Observe(identities, entry.Kind, path, bytes, fields).Key;
                hashes[member.Id] = Convert.ToHexString(SHA256.HashData(bytes));
            }
            return (Identities: result, Hashes: hashes);
        });
        token.ThrowIfCancellationRequested();
        var context = new ImportPageContext(current, budget, observations.Identities, observations.Hashes);
        if (!IsImportContextCurrent(context)) throw new IOException(ProductStrings.Get("Import.SourceChanged"));
        return context;
    }

    private async Task<ImportPageSource> ReadImportSourceAsync(int source, string? path, CancellationToken token)
    {
        if (source == 0)
        {
            await StartMenuImports.ReadCachedAsync().WaitAsync(token);
            var snapshot = StartMenuImports.Snapshot;
            return await Task.Run(() => StartMenuImportProjection.FromStartMenu(snapshot, StartMenuImports.IconsDirectory, token), token);
        }
        if (source != 3 || path is null) return new([], new Dictionary<string, StartMenuImportCandidate>());
        var read = await CreateWallPackageReader().ReadPreviewAsync(path, token: token);
        token.ThrowIfCancellationRequested();
        if (read.Preview is null) throw new IOException(read.Status == WallPackageReadStatus.UnsupportedFormat
            ? ProductStrings.Get("Import.FutureFormat") : string.Format(ProductStrings.Get("Import.PackageInvalid"), read.Diagnostic));
        return await Task.Run(() => StartMenuImportProjection.FromPackage(read.Preview, token), token);
    }

    private async Task<ImportPageResult> SaveImportAsync(ImportPageRequest request)
    {
        var plan = request.Plan;
        bool Current() => request.IsCurrent() && IsImportContextCurrent(request.Context) && !PersistenceInteractionBlocked;
        if (!Current()) return new(ProductStrings.Get("Import.SourceChanged"), true);
        if (plan.Status == ImportPlanStatus.NoChange) return new();
        if (plan.Status != ImportPlanStatus.Ready || plan.Proposal is null) return new(ProductStrings.Get("Import.LayoutRejected"));
        var changes = new List<EntryChange>();
        var icons = new List<ImportedIconCopy>();
        var destinationIds = plan.Accepted.Select(accepted => accepted.GroupId).ToHashSet(StringComparer.Ordinal);
        var expectedEntrySha256 = new Dictionary<string, string>(StringComparer.Ordinal);
        // Matching and append placement used every member in these destination
        // groups. Protect the same observed entries, including unmodified ones,
        // in the Store transaction rather than re-reading and racing a GUI check.
        foreach (var member in request.Context.Document.Lanes.SelectMany(lane => lane.Groups)
                     .Where(group => destinationIds.Contains(group.Id)).SelectMany(group => group.Members))
        {
            if (member.Entry is null) continue;
            if (!request.Context.ExistingEntrySha256.TryGetValue(member.Id, out var hash))
                return new(ProductStrings.Get("Import.SourceChanged"), true);
            expectedEntrySha256.Add(member.Id, hash);
        }
        IReadOnlyList<PreparedMediaAsset> media = [];
        try
        {
            if (request.Source.Package is { } package)
            {
                // One protected confirmation scope rehashes the package once;
                // entry bytes and the accepted media closure are reread within it.
                using var batch = await CreateWallPackageReader().OpenImportAsync(package);
                foreach (var accepted in plan.Accepted)
                {
                    var source = request.Source.Candidates.Single(candidate => candidate.SourceKey == accepted.SourceKey);
                    if (source.Member.Entry is null) continue;
                    var entry = await batch.ReadEntryAsync(source.SourceKey);
                    if (entry.Identity.Key != source.Identity?.Key) return new(ProductStrings.Get("Import.SourceChanged"), true);
                    changes.Add(new EntryChange.FromPackage(accepted.MemberId, entry));
                }
                var reusable = request.Context.Document.EnumerateAllMembers().Select(member => member.Images.Media)
                    .Append(request.Context.Document.Settings.BackgroundMedia).OfType<ManagedMediaReference>();
                media = await batch.PrepareMediaAsync(plan.ReferencedMedia, reusable);
            }
            else
            {
                foreach (var accepted in plan.Accepted)
                {
                    var source = request.Source.StartMenuSources[accepted.SourceKey];
                    var candidate = source.Candidate;
                    var observed = await (_storageExecutor ?? throw new InvalidOperationException()).ExecuteAsync(() =>
                    {
                        if (!StartMenuImportCoordinator.SnapshotRootMatches(candidate, requireAvailable: true) ||
                            !source.CanAdd) throw new IOException(ProductStrings.Get("Import.SourceChanged"));
                        var before = StartMenuImportScan.ReadSource(candidate, CancellationToken.None);
                        var fields = candidate.Kind == EntryKind.Lnk ? _session.LinkFiles.Read(candidate.FullPath) : null;
                        var identity = EntryObservation.Observe(ImportIdentities,
                            candidate.Kind == EntryKind.Lnk ? ManagedEntryKind.Lnk : ManagedEntryKind.Url, candidate.FullPath, before.Bytes, fields);
                        var after = StartMenuImportScan.ReadSource(candidate, CancellationToken.None);
                        if (identity.Key != source.Identity?.Key || before.Sha256 != after.Sha256 ||
                            !before.Sha256.Equals(source.SourceSha256, StringComparison.OrdinalIgnoreCase))
                            throw new IOException(ProductStrings.Get("Import.SourceChanged"));
                        if (identity.IsUrl && !identity.Key.StartsWith("opaque-url:", StringComparison.Ordinal) && !RegisteredUrlProtocols.IsSupported(identity.Target))
                            throw new IOException(ProductStrings.Get("Import.UnsupportedProtocol"));
                        return (Identity: identity, Sha256: before.Sha256, Fields: fields);
                    });
                    changes.Add(new EntryChange.CopyShortcut(accepted.MemberId, candidate.FullPath, observed.Sha256));
                    if (source.IconDirectory is { } directory && source.IconKey is { } key)
                        icons.Add(new(accepted.MemberId, observed.Sha256, observed.Identity.IsUrl ? null : observed.Identity.Target,
                            observed.Fields?.IconPath, observed.Fields?.IconIndex, directory, key));
                }
            }
            if (!Current()) return new(ProductStrings.Get("Import.SourceChanged"), true);
            _pendingImportIconCopies = (plan.Proposal.Document.Generation, icons.ToArray());
            var error = await CommitProposalAsync(plan.Proposal, changes, preparedMedia: media, expectedEntrySha256: expectedEntrySha256);
            if (error?.Contains(WallStore.ImportDestinationChanged, StringComparison.Ordinal) == true)
                return new(ProductStrings.Get("Import.SourceChanged"), true);
            if (error is null) TransientStatus(string.Format(ProductStrings.Get("Import.PlannedChanges"), plan.AddedCount, plan.OverwrittenCount));
            return new(error);
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains(WallStore.ImportDestinationChanged, StringComparison.Ordinal))
                return new(ProductStrings.Get("Import.SourceChanged"), true);
            foreach (var accepted in plan.Accepted)
                if (request.Source.StartMenuSources.TryGetValue(accepted.SourceKey, out var source)) StartMenuImports.InvalidateSource(source.Candidate);
            var changed = ex.Message == ProductStrings.Get("Import.SourceChanged") ||
                ex.Message == "Wall file changed after preview." || ex is FileNotFoundException or DirectoryNotFoundException;
            return new(string.Format(ProductStrings.Get("Import.Failed"), ex.Message), changed &&
                !_session.ResultUnknown && !_session.RequiresReopen && !_session.RecoveryBlocked);
        }
        finally { _pendingImportIconCopies = null; }
    }

    internal async Task<ExternalImportResult> ImportExternalBatchAsync(ExplorerAddMessage message, CancellationToken token)
    {
        if (message.Paths.Count == 0) return new(ProductStrings.Get("Import.NoAddableObjects"), false, 0);
        try
        {
            if (!CanProcessExternalImport || _windowClosing || ShutdownStarted)
                throw new IOException(ProductStrings.Get("Wall.ViewBlocked"));
            var current = Document;
            var budget = CurrentViewportBudget;
            var identities = ImportIdentities;
            var executor = _storageExecutor ?? throw new InvalidOperationException("Storage worker is unavailable.");
            var lastGroup = current.Lanes.SelectMany(lane => lane.Groups).LastOrDefault();
            void RequireCurrent()
            {
                token.ThrowIfCancellationRequested();
                if (!CanProcessExternalImport || _windowClosing || ShutdownStarted || !ReferenceEquals(Document, current) ||
                    Document.Generation != current.Generation || CurrentViewportBudget != budget ||
                    _session.ResultUnknown || _session.RequiresReopen || _session.RecoveryBlocked)
                    throw new IOException(ProductStrings.Get("Import.SourceChanged"));
            }
            RequireCurrent();
            var observed = await executor.ExecuteAsync(() =>
            {
                var incoming = message.Paths.Select(path => ObserveExternalItem(ImportBatchItem.Create(path), identities, token)).ToArray();
                var keys = incoming.ToDictionary(item => item.Member.Id, item => item.ExpectedIdentityKey!, StringComparer.Ordinal);
                var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                var files = new LocalFileStore();
                var guarded = new GuardedWallFiles(files, new WallStoragePaths(files, _session.DataRoot));
                if (lastGroup is not null)
                    foreach (var member in lastGroup.Members)
                    {
                        token.ThrowIfCancellationRequested();
                        if (member.Entry is not { } entry)
                        {
                            if (identities.ResolveMember(member, null) is { } identity) keys[member.Id] = identity.Key;
                            continue;
                        }
                        var path = _session.ManagedEntryPath(member) ?? throw new IOException("Invalid formal entry path.");
                        using var stream = guarded.OpenReadProtected(path);
                        var bytes = ReadExternalEntryBytes(stream);
                        var fields = entry.Kind == ManagedEntryKind.Lnk ? _session.LinkFiles.Read(path) : null;
                        keys[member.Id] = EntryObservation.Observe(identities, entry.Kind, path, bytes, fields).Key;
                        hashes[member.Id] = Convert.ToHexString(SHA256.HashData(bytes));
                    }
                return (Items: incoming, Keys: keys, Hashes: hashes);
            });
            RequireCurrent();
            var items = observed.Items;
            var plan = WallBatchPlanner.TryPlan(current, items.Select(item => item.Member).ToArray(), lastGroup?.Id, null,
                "external-group-" + items[0].Member.Id, WorkWidthDip, WorkHeightDip, _metrics,
                appendOnly: true, launchIdentityKeys: observed.Keys);
            if (plan.Proposal is null) return new(string.Format(ProductStrings.Get("Import.NoSpace"),
                ProductStrings.WallRejectionReason(plan.Rejection)), false, 0);
            var placedIds = plan.Proposal.Document.Lanes.SelectMany(lane => lane.Groups).SelectMany(group => group.Members)
                .Select(member => member.Id).ToHashSet(StringComparer.Ordinal);
            var accepted = items.Where(item => placedIds.Contains(item.Member.Id)).ToArray();
            var skippedCount = items.Length - accepted.Length;
            string? error = null;
            if (accepted.Length != 0)
            {
                var preparedSources = new Dictionary<string, TargetImageSourceResult>(StringComparer.Ordinal);
                var assets = new Dictionary<string, PreparedMediaAsset>(StringComparer.Ordinal);
                var preparedMembers = new Dictionary<string, WallMember>(StringComparer.Ordinal);
                async Task RequireSourcesCurrentAsync()
                {
                    RequireCurrent();
                    var valid = await Task.WhenAll(preparedSources.Values.Select(source => _images.IsTargetSourceCurrentAsync(source, token)));
                    RequireCurrent();
                    if (valid.Any(value => !value)) throw new IOException(ProductStrings.Get("Import.SourceChanged"));
                }
                // Placement and destination-scoped duplicate elimination precede
                // every image read, media preparation and formal entry change.
                foreach (var item in accepted)
                {
                    var source = await _images.ResolveTargetSourceAsync(item.SourcePath, token)
                        ?? throw new OperationCanceledException(token);
                    RequireCurrent();
                    preparedSources.Add(item.Member.Id, source);
                    var placed = plan.Proposal.Document.EnumerateAllMembers().Single(member => member.Id == item.Member.Id);
                    var prepared = source.ApplyTo(placed);
                    if (source.Source.Kind == MemberImageSourceKind.Single)
                    {
                        var asset = await _images.PrepareMediaAsync(item.SourcePath, token);
                        RequireCurrent();
                        var sourceCurrent = await _images.IsTargetSourceCurrentAsync(source, token);
                        RequireCurrent();
                        if (!sourceCurrent) throw new IOException(ProductStrings.Get("Import.SourceChanged"));
                        assets.TryAdd(asset.Reference.Sha256, asset);
                        prepared = prepared with { Images = prepared.Images with { Media = asset.Reference } };
                    }
                    preparedMembers.Add(item.Member.Id, prepared);
                }
                await executor.ExecuteAsync(() =>
                {
                    foreach (var item in accepted)
                    {
                        var latest = ObserveExternalItem(item, identities, token);
                        if (latest.ExpectedIdentityKey != item.ExpectedIdentityKey || latest.ExpectedSourceSha256 != item.ExpectedSourceSha256 ||
                            latest.ExpectedSourceStamp != item.ExpectedSourceStamp)
                            throw new IOException(ProductStrings.Get("Import.SourceChanged"));
                    }
                    return true;
                });
                RequireCurrent();
                await RequireSourcesCurrentAsync();
                RequireCurrent();
                var proposal = plan.Proposal with { Document = plan.Proposal.Document with
                {
                    Lanes = plan.Proposal.Document.Lanes.Select(lane => lane with
                    {
                        Groups = lane.Groups.Select(group => group with
                        { Members = group.Members.Select(member => preparedMembers.GetValueOrDefault(member.Id) ?? member).ToArray() }).ToArray(),
                    }).ToArray(),
                } };
                var changes = accepted.Select(item => item.Kind == ExplorerAddKind.CopyShortcut
                    ? (EntryChange)new EntryChange.CopyShortcut(item.Member.Id, item.SourcePath, item.ExpectedSourceSha256)
                    : new EntryChange.CreateLink(item.Member.Id, item.SourcePath)).ToArray();
                // Admission is the last cancellation boundary. Accepted persistence
                // drains through its own task even if exit cancels further preparation.
                error = await CommitProposalAsync(proposal, changes, preparedMedia: assets.Values.ToArray(), expectedEntrySha256: observed.Hashes);
            }
            var text = error is null ? string.Format(ProductStrings.Get("Import.Added"), accepted.Length)
                : string.Format(ProductStrings.Get("Import.Failed"), error);
            if (skippedCount != 0) text += " " + string.Format(ProductStrings.Get("Import.SkippedDuplicates"), skippedCount);
            if (message.Truncated) text += " " + ProductStrings.Get("Import.Truncated");
            return new(text, error is null, error is null ? accepted.Length : 0);
        }
        catch (OperationCanceledException)
        {
            return new(ProductStrings.Get("Import.SourceChanged"), false, 0);
        }
        catch (Exception ex)
        {
            return new(string.Format(ProductStrings.Get("Import.Failed"), ex.Message)
                + (message.Truncated ? " " + ProductStrings.Get("Import.Truncated") : string.Empty), false, 0);
        }
    }

    private ImportBatchItem ObserveExternalItem(ImportBatchItem item, LaunchIdentityService identities, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!item.StillValid()) throw new IOException(string.Format(ProductStrings.Get("Import.SourceMissing"), item.SourcePath));
        var attributes = File.GetAttributes(item.SourcePath);
        var directory = (attributes & FileAttributes.Directory) != 0;
        FileSystemInfo info = directory ? new DirectoryInfo(item.SourcePath) : new FileInfo(item.SourcePath);
        var stamp = FormattableString.Invariant($"{(directory ? "directory" : "file")}|{info.CreationTimeUtc.Ticks}|{info.LastWriteTimeUtc.Ticks}|{(info is FileInfo file ? file.Length : 0)}");
        LaunchIdentity identity;
        string? hash = null;
        if (item.Kind == ExplorerAddKind.CopyShortcut)
        {
            using var stream = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = ReadExternalEntryBytes(stream);
            var kind = Path.GetExtension(item.SourcePath).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                ? ManagedEntryKind.Lnk : ManagedEntryKind.Url;
            var fields = kind == ManagedEntryKind.Lnk ? _session.LinkFiles.Read(item.SourcePath) : null;
            identity = EntryObservation.Observe(identities, kind, item.SourcePath, bytes, fields);
            hash = Convert.ToHexString(SHA256.HashData(bytes));
        }
        else identity = LaunchIdentityService.ForPath(item.SourcePath);
        token.ThrowIfCancellationRequested();
        if (identity.IsUrl && (identity.Key.StartsWith("opaque-url:", StringComparison.Ordinal) || !RegisteredUrlProtocols.IsSupported(identity.Target)))
            throw new IOException(ProductStrings.Get("Import.UnsupportedProtocol"));
        return item with { ExpectedIdentityKey = identity.Key, ExpectedSourceSha256 = hash, ExpectedSourceStamp = stamp };
    }

    private static byte[] ReadExternalEntryBytes(Stream stream)
    {
        if (stream.Length is < 1 or > EntryObservation.MaximumBytes) throw new InvalidDataException("Invalid or oversized shortcut.");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
}
