using System.Diagnostics;
using System.Text.Json;
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
    private readonly StartMenuImportProjection _importSourceProjection = new();
    private NativeImportCatalog? _nativeImports;
    private NativeImportCatalog NativeImports => _nativeImports ??= new(_session.DataRoot, _session.LinkFiles);
    private readonly Dictionary<ImportPageSourceKind, Task<IReadOnlyList<NativeImportEntry>>> _nativeRefreshes = [];
    private readonly Dictionary<ImportPageSourceKind, Task<IReadOnlyList<NativeImportEntry>>> _nativeCacheReads = [];
    private readonly IWallStorageExecutor _importSourceExecutor = new ImportSourceExecutor();
    private sealed class ImportSourceExecutor : IWallStorageExecutor
    {
        private int _stopped;
        // Short native descriptions own their own MTA; no COM object or long source scan occupies formal persistence.
        public Task<T> ExecuteAsync<T>(Func<T> work)
        {
            ArgumentNullException.ThrowIfNull(work);
            if (Volatile.Read(ref _stopped) != 0) throw new ObjectDisposedException(nameof(ImportSourceExecutor));
            return Task.Run(() => ShellItemCatalog.InApartment(work));
        }
        public Task ShutdownAsync()
        {
            // StopStartMenuRefreshAsync first drains the coordinator, which awaits every returned job.
            // This adapter has no thread/message loop or independent producer to drain a second time.
            Interlocked.Exchange(ref _stopped, 1);
            return Task.CompletedTask;
        }
    }
    private readonly CancellationTokenSource _nativeImportLifetime = new();
    private ImportPageSourceKind? _lastImportSource;
    private readonly HashSet<string> _knownImportPackages = new(StringComparer.OrdinalIgnoreCase);
    private StartMenuImportCoordinator StartMenuImports => _startMenuImports ??= new(_session.DataRoot,
        _importSourceExecutor, ImportIdentities, _session.LinkFiles)
    {
        IconPrepare = (source, directory, token) => _images.PrewarmPeIconsAsync(source.Candidate.FullPath,
            source.Identity?.IsUrl == false ? source.Identity.Target : null, source.LinkFields?.IconPath ?? source.UrlIconPath,
            source.LinkFields?.IconIndex ?? source.UrlIconIndex, source.SourceSha256, directory, token),
    };

    internal void ScheduleStartMenuStartupRefresh()
    {
        if (_hasDocument && !_viewBlocked && !PersistenceInteractionBlocked) StartMenuImports.ScheduleStartupRefresh();
    }

    internal async Task StopStartMenuRefreshAsync()
    {
        _nativeImportLifetime.Cancel();
        await (_startMenuImports?.StopAsync() ?? Task.CompletedTask);
        await _importSourceExecutor.ShutdownAsync();
        try { await Task.WhenAll(_nativeRefreshes.Values.Concat(_nativeCacheReads.Values)); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Runtime.InteropServices.COMException) { }
    }

    private async Task OpenImportAsync(string? groupId, int lane)
    {
        if (PersistenceInteractionBlocked || _modal || _viewBlocked || !_hasDocument) return;
        try
        {
            if (!await BeginModalAsync()) return;
            _importSourceProjection.BeginPage();
            _lastImportSource = null;
            var dialog = new ImportDialog(DialogOwner, _images, StartMenuImports, CreateWallPackageReader(),
                ReadImportContextAsync, (source, path, token) => ReadImportSourceAsync(_importSourceProjection, source, path, token),
                (context, source, projection, token) => PrepareImportPageAsync(_importSourceProjection, context, source, projection, token),
                SaveImportAsync, SaveExportAsync, IsImportContextCurrent, Document.Settings.VisualStyle, LoadImportIconAsync,
                KnownFolderPaths.Desktop, RestoreRecycledMembersAsync);
            ActiveDialog = dialog;
            await dialog.ShowAsync();
        }
        catch (Exception ex) { Status(string.Format(ProductStrings.Get("Import.Failed"), ex.Message)); }
        finally { EndModal(); }
    }

    private Task<PuzzleFrameResult> LoadImportIconAsync(ImportSourceCandidate candidate, int width, int height, CancellationToken token) =>
        _images.LoadMemberFrameAsync(candidate.Member, candidate.SourceKind is ImportPageSourceKind.CurrentWall or ImportPageSourceKind.RecycleBin
                ? _session.ManagedEntryPath(candidate.Member) ?? candidate.Member.TargetInput
                : candidate.SourceKind == ImportPageSourceKind.WallPackage ? null : candidate.Member.SourceShortcutPath ?? candidate.Member.TargetInput ?? candidate.Identity?.Target,
            width, height, ct: token);

    private bool IsImportContextCurrent(ImportPageContext context) => _hasDocument && !_viewBlocked &&
        !_session.ResultUnknown && !_session.RequiresReopen && !_session.RecoveryBlocked &&
        Document.Generation == context.Document.Generation && CurrentViewportBudget == context.Budget;

    private Task<ImportPageContext> ReadImportContextAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var current = Document;
        var budget = CurrentViewportBudget;
        var context = new ImportPageContext(current, budget, new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));
        if (!IsImportContextCurrent(context)) throw new ImportSourceChangedException("The import source changed.");
        return Task.FromResult(context);
    }

    private async Task<ImportPagePreparation> PrepareImportPageAsync(StartMenuImportProjection sourceProjection,
        ImportPageContext context, ImportPageSource source, ImportSourceProjection projection, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsImportContextCurrent(context) || projection.Generation != context.Document.Generation)
            throw new ImportSourceChangedException("The import source changed.");
        // Availability filtering can move surviving candidates to different groups.
        source = await sourceProjection.PrepareAvailabilityAsync(source, token);
        projection = ImportSourcePlanner.Project(context.Document, source.Candidates, projection.RecognizeGroups,
            projection.AutoFilter, context.ExistingIdentities);
        var current = context.Document;
        var destinationIds = projection.Groups.Select(group => group.DestinationGroupId).OfType<string>()
            .Where(id => !context.ObservedDestinationGroups.Contains(id)).ToHashSet(StringComparer.Ordinal);
        // Group assignment is pure data. No entry in an unrelated wall group is
        // opened while presenting or refreshing source candidates.
        if (destinationIds.Count != 0)
        {
            var observations = await (_storageExecutor ?? throw new InvalidOperationException()).ExecuteAsync(() =>
            {
                var files = new LocalFileStore();
                var guarded = new GuardedWallFiles(files, new WallStoragePaths(files, _session.DataRoot));
                var identities = ImportIdentities;
                var result = new Dictionary<string, string>(context.ExistingIdentities, StringComparer.Ordinal);
                var hashes = new Dictionary<string, string>(context.ExistingEntrySha256, StringComparer.Ordinal);
                foreach (var member in current.Lanes.SelectMany(lane => lane.Groups)
                             .Where(group => destinationIds.Contains(group.Id)).SelectMany(group => group.Members))
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
            var observedGroups = context.ObservedDestinationGroups.Concat(destinationIds).ToHashSet(StringComparer.Ordinal);
            context = context with { ExistingIdentities = observations.Identities, ExistingEntrySha256 = observations.Hashes,
                ObservedDestinationGroups = observedGroups };
        }
        token.ThrowIfCancellationRequested();
        if (!IsImportContextCurrent(context)) throw new ImportSourceChangedException("The import source changed.");
        return new(context, source);
    }

    private async Task<ImportPageSource> ReadImportSourceAsync(StartMenuImportProjection projection, ImportPageSourceKind source, string? path, CancellationToken token)
    {
        var switched = _lastImportSource != source;
        _lastImportSource = source;
        if (source == ImportPageSourceKind.StartMenu)
        {
            var clock = Stopwatch.StartNew();
            var cacheRead = StartMenuImports.ReadCachedAsync();
            var memoryReady = cacheRead.IsCompletedSuccessfully;
            await cacheRead.WaitAsync(token);
            var snapshot = StartMenuImports.Snapshot;
            if (switched) AppLog.Warn("ImportSources", JsonSerializer.Serialize(new { source = "StartMenu", stage = "cache",
                cacheMs = clock.Elapsed.TotalMilliseconds, memoryReady, count = snapshot.Sources.Count, refreshing = snapshot.Refreshing }));
            return await Task.Run(() => projection.FromStartMenu(snapshot, StartMenuImports.IconsDirectory, token), token);
        }
        if (source is ImportPageSourceKind.Desktop or ImportPageSourceKind.InstalledApplications or ImportPageSourceKind.SystemFeatures)
        {
            var clock = Stopwatch.StartNew();
            var native = NativeImports;
            var memoryHit = native.TryMemory(source, out var cached);
            if (!memoryHit)
            {
                if (!_nativeCacheReads.TryGetValue(source, out var cacheRead))
                    _nativeCacheReads[source] = cacheRead = native.CachedAsync(source, _nativeImportLifetime.Token);
                cached = await cacheRead.WaitAsync(token);
            }
            token.ThrowIfCancellationRequested();
            var cacheMs = clock.Elapsed.TotalMilliseconds;
            if (!_nativeRefreshes.TryGetValue(source, out var refresh) || switched && refresh.IsCompleted)
            {
                refresh = RefreshNativeSourceAsync(source);
                _nativeRefreshes[source] = refresh;
            }
            if (refresh.IsFaulted) throw refresh.Exception!.GetBaseException();
            if (refresh.IsCanceled) throw new ImportSourceChangedException("The native source scope changed.");
            var visible = native.Snapshot(source);
            var projected = native.Project(source, visible.Count != 0 || refresh.IsCompleted ? visible : cached);
            if (switched) AppLog.Warn("ImportSources", JsonSerializer.Serialize(new { source = source.ToString(),
                stage = "cache", cacheMs, memoryHit, count = projected.Candidates.Count, refreshing = projected.Refreshing }));
            return projected;
        }
        if (source == ImportPageSourceKind.CurrentWall) return await ReadCurrentWallSourceAsync(token);
        if (source == ImportPageSourceKind.RecycleBin)
        {
            var recycle = await _session.ReadRecycleBinAsync(Document.Generation, token);
            var rows = recycle.Members.Select((member, index) =>
            {
                token.ThrowIfCancellationRequested();
                return new ImportSourceCandidate("recycle:" + member.Id, RecycleBinPresentation.Title(member), member)
                {
                    SourceKind = source, Policy = ImportSourcePolicy.Attributed,
                    SourceGroup = new("recycle", ProductStrings.Get("Recycle.Title"), 0), SourceOrder = index,
                    HasRecordedSize = true, HasLaunchTarget = member.Entry is not null,
                    TargetKind = member.Entry is null ? ImportTargetKind.Component : ImportTargetKind.Unknown,
                };
            }).ToArray();
            return new(rows, new Dictionary<string, StartMenuImportCandidate>(), Scanned: rows.Length) { AvailabilityPrepared = true };
        }
        if (source != ImportPageSourceKind.WallPackage || path is null) return new([], new Dictionary<string, StartMenuImportCandidate>());
        _knownImportPackages.Add(Path.GetFullPath(path));
        var read = await CreateWallPackageReader().ReadPreviewAsync(path, token: token);
        token.ThrowIfCancellationRequested();
        if (read.Preview is null) throw new IOException(read.Status == WallPackageReadStatus.UnsupportedFormat
            ? ProductStrings.Get("Import.FutureFormat") : string.Format(ProductStrings.Get("Import.PackageInvalid"), read.Diagnostic));
        return await Task.Run(() => projection.FromPackage(read.Preview, token), token);
    }

    private async Task<IReadOnlyList<NativeImportEntry>> RefreshNativeSourceAsync(ImportPageSourceKind kind)
    {
        void Changed()
        {
            if (!_nativeImportLifetime.IsCancellationRequested)
                DispatcherQueue.TryEnqueue(() => { if (ActiveDialog is ImportDialog dialog) dialog.SourceChanged(kind); });
        }
        try { return await NativeImports.ReadAsync(kind, _nativeImportLifetime.Token, Changed); }
        finally { Changed(); }
    }

    private async Task<ImportPageSource> ReadCurrentWallSourceAsync(CancellationToken token)
    {
        var document = Document;
        return await (_storageExecutor ?? throw new InvalidOperationException()).ExecuteAsync(() =>
        {
            var rows = new List<ImportSourceCandidate>();
            foreach (var (lane, laneIndex) in document.Lanes.Select((lane, index) => (lane, index)))
            foreach (var (group, groupIndex) in lane.Groups.Select((group, index) => (group, index)))
            foreach (var (member, memberIndex) in group.Members.Select((member, index) => (member, index)))
            {
                token.ThrowIfCancellationRequested();
                LaunchIdentity? identity = null;
                if (member.Entry is not null)
                {
                    try { identity = ImportIdentities.ResolveMember(member, _session.ManagedEntryPath(member)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException) { }
                }
                var target = member.ResourceOrigin == MemberResourceOrigin.Package
                    ? new StartMenuImportProjection.TargetObservation(ImportTargetAvailability.Unknown, null)
                    : StartMenuImportProjection.ObserveTarget(identity);
                rows.Add(new(member.Id, member.Visual.Title ?? member.Id, member)
                {
                    SourceKind = ImportPageSourceKind.CurrentWall, Policy = ImportSourcePolicy.Attributed,
                    SourceGroup = new(group.Id, group.Title, groupIndex) { LaneIndex = laneIndex }, SourceOrder = memberIndex,
                    HasRecordedPosition = true, HasRecordedSize = true, Identity = identity,
                    HasLaunchTarget = member.Entry is not null, TargetKind = StartMenuImportProjection.ContentKind(identity, member.Entry is not null, target.IsDirectory),
                    TargetAvailability = target.Availability,
                });
            }
            return new ImportPageSource(rows, new Dictionary<string, StartMenuImportCandidate>(), Scanned: rows.Count) { AvailabilityPrepared = true };
        });
    }

    private async Task<ImportPageResult> SaveExportAsync(ImportPageExportRequest request)
    {
        if (!request.IsCurrent() || !IsImportContextCurrent(request.Context) || PersistenceInteractionBlocked)
            return new(ProductStrings.Get("Import.SourceChanged"), true);
        try
        {
            using var snapshot = await _session.CapturePackageSnapshotAsync(request.Context.Document.Generation,
                request.SelectedMemberIds, CancellationToken.None);
            void RequireCurrent()
            {
                if (!request.IsCurrent() || !IsImportContextCurrent(request.Context) || PersistenceInteractionBlocked || ShutdownStarted)
                    throw new ImportSourceChangedException("The export source changed.");
            }
            async Task RequireEntryCurrentAsync(WallMember member, string? path)
            {
                RequireCurrent();
                if (snapshot.EntrySha256(member.Id) is not { } expected) return;
                await (_storageExecutor ?? throw new InvalidOperationException()).ExecuteAsync(() =>
                {
                    var files = new LocalFileStore();
                    var guarded = new GuardedWallFiles(files, new WallStoragePaths(files, _session.DataRoot));
                    using var stream = guarded.OpenReadProtected(path ?? throw new IOException("Missing formal export entry."));
                    if (stream.Length is < 1 or > EntryObservation.MaximumBytes || Convert.ToHexString(SHA256.HashData(stream)) != expected)
                        throw new ImportSourceChangedException("The export entry changed.");
                    return true;
                });
                RequireCurrent();
            }
            var images = new Dictionary<string, PreparedMediaAsset?>(StringComparer.Ordinal);
            long preparedBytes = 0;
            var preparedHashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in snapshot.AutomaticImageMembers)
            {
                RequireCurrent();
                PreparedMediaAsset? image = null;
                if (member.ResourceOrigin == MemberResourceOrigin.Local)
                {
                    var path = _session.ManagedEntryPath(member) ?? member.SourceShortcutPath ?? member.TargetInput;
                    await RequireEntryCurrentAsync(member, path);
                    var reserve = member.ImageDisplay.Mode == TileWall.Core.Imaging.ImageDisplayMode.Custom
                        ? Math.Max(1, member.ImageDisplay.CustomScale) : 1;
                    image = await _images.PrepareAutomaticExportImageAsync(member, path,
                        (int)Math.Clamp(Math.Ceiling(_metrics.WidthOf(member.LocalBounds.Width) * Scale * reserve), 1, 4096),
                        (int)Math.Clamp(Math.Ceiling(_metrics.HeightOf(member.LocalBounds.Height) * Scale * reserve), 1, 4096), CancellationToken.None);
                    await RequireEntryCurrentAsync(member, path);
                }
                if (image is not null && preparedHashes.Add(image.Reference.Sha256))
                {
                    preparedBytes = checked(preparedBytes + image.Reference.Length);
                    if (preparedBytes > new WallPackageLimits().MaximumPreparedMediaBytes)
                        throw new IOException("Automatic foregrounds exceed the export preparation budget.");
                }
                images.Add(member.Id, image);
            }
            RequireCurrent();
            snapshot.CompleteAutomaticImages(images);
            var result = await new WallPackageWriter(CreateWallPackageReader()).WriteAsync(snapshot, request.OutputPath,
                typeof(ProductVersion).Assembly.GetName().Version!.ToString(), additionalProtectedPaths: _knownImportPackages,
                token: CancellationToken.None);
            if (result.Status == WallPackageWriteStatus.Saved)
            { TransientStatus(string.Format(ProductStrings.Get("Import.ExportSaved"), request.OutputPath)); return new(); }
            if (result.PreservedPath is { } preserved)
                return new(string.Format(ProductStrings.Get("Import.ExportConflict"), preserved));
            return new(result.Status == WallPackageWriteStatus.ResultUnknown ? ProductStrings.Get("Import.ExportUnknown") :
                string.Format(ProductStrings.Get("Import.ExportFailed"), result.Diagnostic ?? result.Status.ToString()));
        }
        catch (Exception ex) { return new(string.Format(ProductStrings.Get("Import.ExportFailed"), ex.Message)); }
    }

    private async Task<ImportPageResult> SaveImportAsync(ImportPageRequest request)
    {
        var plan = request.Plan;
        bool Current() => request.IsCurrent() && IsImportContextCurrent(request.Context) && !PersistenceInteractionBlocked;
        if (!Current() || request.Source.Refreshing || !request.Source.AvailabilityPrepared || !request.Context.Covers(request.Projection))
            return new(ProductStrings.Get("Import.SourceChanged"), true);
        if (plan.Status == ImportPlanStatus.NoChange) return new();
        if (plan.Status != ImportPlanStatus.Ready || plan.Proposal is null) return new(ProductStrings.Get("Import.LayoutRejected"));
        var changes = new List<EntryChange>();
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
        IReadOnlyList<PreparedManagedGallery> galleries = [];
        var proposal = plan.Proposal;
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
                var reusable = await _session.ReusablePackageMediaAsync(request.Context.Document.Generation);
                var acceptedIds = plan.Accepted.Select(accepted => accepted.SourceKey).ToHashSet(StringComparer.Ordinal);
                var requiredGalleries = request.Source.Candidates.Where(candidate => acceptedIds.Contains(candidate.SourceKey))
                    .Select(candidate => candidate.Member.Images.ManagedGallery).OfType<ManagedGalleryReference>().Distinct();
                var reusableGalleries = await _session.ReusablePackageGalleriesAsync(request.Context.Document.Generation);
                var resources = await batch.PrepareResourcesAsync(plan.ReferencedMedia, requiredGalleries, reusable, reusableGalleries);
                media = resources.MediaAssets;
                galleries = resources.ManagedGalleries;
            }
            else if (request.Source.Candidates.FirstOrDefault()?.SourceKind is ImportPageSourceKind.Desktop or ImportPageSourceKind.InstalledApplications or ImportPageSourceKind.SystemFeatures)
            {
                var kind = request.Source.Candidates[0].SourceKind;
                try { await NativeImports.ReadAsync(kind, CancellationToken.None); }
                catch (Exception sourceError) when (sourceError is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                { throw new ImportSourceChangedException("The native source changed.", sourceError); }
                var prepared = await (_storageExecutor ?? throw new InvalidOperationException()).ExecuteAsync(() =>
                {
                    var result = new List<EntryChange>();
                    var confirmed = NativeImports.ConfirmMany(kind, plan.Accepted.Select(item => item.SourceKey).ToArray());
                    foreach (var accepted in plan.Accepted)
                    {
                        var source = confirmed[accepted.SourceKey];
                        if (request.Source.AvailabilityFacts.GetValueOrDefault(source.Key) != NativeImportCatalog.Facts(source))
                            throw new ImportSourceChangedException("The import source changed.");
                        result.Add(source.Kind switch
                        {
                            NativeImportTarget.Shortcut => new EntryChange.CopyShortcut(accepted.MemberId, source.SourcePath!, source.Sha256),
                            NativeImportTarget.Path => new EntryChange.CreateLink(accepted.MemberId, source.Target),
                            NativeImportTarget.Shell => new EntryChange.CreateShellLink(accepted.MemberId, source.Target,
                                (source.Identity ?? LaunchIdentityService.ForShell(source.Target)).Key),
                            NativeImportTarget.Uri => new EntryChange.CreateUrl(accepted.MemberId, source.Target),
                            _ => throw new IOException("Unsupported native source."),
                        });
                    }
                    return result;
                });
                changes.AddRange(prepared);
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
                            !source.CanAdd) throw new ImportSourceChangedException("The import source changed.");
                        var before = StartMenuImportScan.ReadSource(candidate, CancellationToken.None);
                        var fields = candidate.Kind == EntryKind.Lnk ? _session.LinkFiles.Read(candidate.FullPath) : null;
                        var identity = EntryObservation.Observe(ImportIdentities,
                            candidate.Kind == EntryKind.Lnk ? ManagedEntryKind.Lnk : ManagedEntryKind.Url, candidate.FullPath, before.Bytes, fields);
                        var after = StartMenuImportScan.ReadSource(candidate, CancellationToken.None);
                        if (identity.Key != source.Identity?.Key || before.Sha256 != after.Sha256 ||
                            !before.Sha256.Equals(source.SourceSha256, StringComparison.OrdinalIgnoreCase))
                            throw new ImportSourceChangedException("The import source changed.");
                        if (identity.IsUrl && !identity.Key.StartsWith("opaque-url:", StringComparison.Ordinal) && !RegisteredUrlProtocols.IsSupported(identity.Target))
                            throw new IOException(ProductStrings.Get("Import.UnsupportedProtocol"));
                        return (Identity: identity, Sha256: before.Sha256, Fields: fields);
                    });
                    changes.Add(new EntryChange.CopyShortcut(accepted.MemberId, candidate.FullPath, observed.Sha256));
                }
            }
            if (request.Source.Package is null)
            {
                void RequireCurrent()
                {
                    if (!Current()) throw new ImportSourceChangedException("The import source changed.");
                }
                var sourceByKey = request.Source.Candidates.ToDictionary(candidate => candidate.SourceKey, StringComparer.Ordinal);
                var placed = proposal.Document.EnumerateAllMembers().ToDictionary(member => member.Id, StringComparer.Ordinal);
                var inputs = plan.Accepted.Where(accepted => ImportTargetRecognition.AppliesTo(sourceByKey[accepted.SourceKey]))
                    .Select(accepted =>
                    {
                        var source = sourceByKey[accepted.SourceKey];
                        var known = request.RecognizedTargets.GetValueOrDefault(accepted.SourceKey);
                        return (Member: placed[accepted.MemberId], Input: source.Member.SourceShortcutPath ?? source.Member.TargetInput,
                            Inference: known?.Matches(request.Source, source) == true ? known.Result : null);
                    }).ToArray();
                var prepared = await PrepareAcceptedTargetImagesAsync(inputs, request.Context.Document.Generation, RequireCurrent, CancellationToken.None);
                proposal = WithPreparedTargetImages(proposal, prepared.Members);
                media = prepared.Assets;
                // Native direct paths have no copied shortcut bytes for Store to compare.
                // Reconfirm their source stamp after image preparation as well as before it.
                if (request.Source.Candidates.FirstOrDefault()?.SourceKind is ImportPageSourceKind.Desktop or ImportPageSourceKind.InstalledApplications or ImportPageSourceKind.SystemFeatures)
                    await (_storageExecutor ?? throw new InvalidOperationException()).ExecuteAsync(() =>
                    {
                        var kind = request.Source.Candidates[0].SourceKind;
                        var confirmed = NativeImports.ConfirmMany(kind, plan.Accepted.Select(item => item.SourceKey).ToArray());
                        foreach (var accepted in plan.Accepted)
                        {
                            var source = confirmed[accepted.SourceKey];
                            if (request.Source.AvailabilityFacts.GetValueOrDefault(source.Key) != NativeImportCatalog.Facts(source))
                                throw new ImportSourceChangedException("The import source changed.");
                        }
                        return true;
                    });
                await RequireTargetSourcesCurrentAsync(prepared.Sources, RequireCurrent, CancellationToken.None);
            }
            if (!Current()) return new(ProductStrings.Get("Import.SourceChanged"), true);
            var error = await CommitProposalAsync(proposal, changes, preparedMedia: media,
                expectedEntrySha256: expectedEntrySha256, managedGalleries: galleries);
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
            var changed = ex is ImportSourceChangedException || ex.Message == ProductStrings.Get("Import.SourceChanged") ||
                ex.Message == "Wall file changed after preview." || ex is FileNotFoundException or DirectoryNotFoundException;
            if (changed && request.Source.Candidates.FirstOrDefault()?.SourceKind is { } sourceKind &&
                sourceKind is ImportPageSourceKind.Desktop or ImportPageSourceKind.InstalledApplications or ImportPageSourceKind.SystemFeatures)
                _nativeRefreshes.Remove(sourceKind);
            return new(changed ? ProductStrings.Get("Import.SourceChanged") : string.Format(ProductStrings.Get("Import.Failed"), ex.Message), changed &&
                !_session.ResultUnknown && !_session.RequiresReopen && !_session.RecoveryBlocked);
        }
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
                    throw new ImportSourceChangedException("The import source changed.");
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
                // Placement and destination-scoped duplicate elimination precede
                // every image read, media preparation and formal entry change.
                var placed = plan.Proposal.Document.EnumerateAllMembers().ToDictionary(member => member.Id, StringComparer.Ordinal);
                var prepared = await PrepareAcceptedTargetImagesAsync(accepted.Select(item =>
                    (Member: placed[item.Member.Id], Input: (string?)item.SourcePath, Inference: (TargetImageSourceResult?)null)).ToArray(),
                    current.Generation, RequireCurrent, token);
                await executor.ExecuteAsync(() =>
                {
                    foreach (var item in accepted)
                    {
                        var latest = ObserveExternalItem(item, identities, token);
                        if (latest.ExpectedIdentityKey != item.ExpectedIdentityKey || latest.ExpectedSourceSha256 != item.ExpectedSourceSha256 ||
                            latest.ExpectedSourceStamp != item.ExpectedSourceStamp)
                            throw new ImportSourceChangedException("The import source changed.");
                    }
                    return true;
                });
                RequireCurrent();
                await RequireTargetSourcesCurrentAsync(prepared.Sources, RequireCurrent, token);
                RequireCurrent();
                var proposal = WithPreparedTargetImages(plan.Proposal, prepared.Members);
                var changes = accepted.Select(item => item.Kind == ExplorerAddKind.CopyShortcut
                    ? (EntryChange)new EntryChange.CopyShortcut(item.Member.Id, item.SourcePath, item.ExpectedSourceSha256)
                    : new EntryChange.CreateLink(item.Member.Id, item.SourcePath)).ToArray();
                // Admission is the last cancellation boundary. Accepted persistence
                // drains through its own task even if exit cancels further preparation.
                error = await CommitProposalAsync(proposal, changes, preparedMedia: prepared.Assets, expectedEntrySha256: observed.Hashes);
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

    private async Task<(IReadOnlyDictionary<string, WallMember> Members, IReadOnlyList<PreparedMediaAsset> Assets,
        IReadOnlyList<TargetImageSourceResult> Sources)> PrepareAcceptedTargetImagesAsync(
        IReadOnlyList<(WallMember Member, string? Input, TargetImageSourceResult? Inference)> inputs,
        long generation, Action requireCurrent, CancellationToken token)
    {
        var members = new Dictionary<string, WallMember>(StringComparer.Ordinal);
        var assets = new Dictionary<string, PreparedMediaAsset>(StringComparer.Ordinal);
        var sources = new List<TargetImageSourceResult>();
        IReadOnlySet<string>? reusable = null;
        long newBytes = 0;
        foreach (var input in inputs)
        {
            requireCurrent(); token.ThrowIfCancellationRequested();
            var source = input.Inference ?? await _images.ResolveTargetSourceAsync(input.Input, token)
                ?? throw new OperationCanceledException(token);
            requireCurrent();
            if (input.Inference is not null && !await _images.IsTargetSourceCurrentAsync(source, token))
                throw new ImportSourceChangedException("The import source changed.");
            requireCurrent(); sources.Add(source);
            var member = source.ApplyTo(input.Member);
            if (source.Source.Kind == MemberImageSourceKind.Single)
            {
                var asset = await _images.PrepareMediaAsync(source.Source.Path ?? throw new IOException("Missing image source."), token);
                requireCurrent();
                if (!await _images.IsTargetSourceCurrentAsync(source, token))
                    throw new ImportSourceChangedException("The import source changed.");
                requireCurrent();
                // Reuse the Store's verified resource closure for the batch budget.
                // Keep only one frozen copy per digest; the transaction still checks its own budget.
                reusable ??= (await _session.ReusablePackageMediaAsync(generation)).Select(media => media.Sha256).ToHashSet(StringComparer.Ordinal);
                requireCurrent();
                if (assets.TryAdd(asset.Reference.Sha256, asset) && !reusable.Contains(asset.Reference.Sha256))
                {
                    newBytes += asset.Reference.Length;
                    if (newBytes > PreparedManagedGallery.MaximumTotalBytes)
                        throw new IOException("Selected original images exceed the combined preparation budget.");
                }
                member = member with { Images = member.Images with { Media = asset.Reference } };
            }
            members.Add(member.Id, member);
        }
        return (members, assets.Values.ToArray(), sources);
    }

    private async Task RequireTargetSourcesCurrentAsync(IReadOnlyList<TargetImageSourceResult> sources,
        Action requireCurrent, CancellationToken token)
    {
        foreach (var source in sources)
        {
            requireCurrent(); token.ThrowIfCancellationRequested();
            if (!await _images.IsTargetSourceCurrentAsync(source, token))
                throw new ImportSourceChangedException("The import source changed.");
        }
        requireCurrent();
    }

    private static WallProposal WithPreparedTargetImages(WallProposal proposal, IReadOnlyDictionary<string, WallMember> members) =>
        proposal with { Document = proposal.Document with
        {
            Lanes = proposal.Document.Lanes.Select(lane => lane with
            {
                Groups = lane.Groups.Select(group => group with
                { Members = group.Members.Select(member => members.GetValueOrDefault(member.Id) ?? member).ToArray() }).ToArray(),
            }).ToArray(),
        } };

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
        string? shortcutTarget = null;
        if (item.Kind == ExplorerAddKind.CopyShortcut)
        {
            using var stream = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = ReadExternalEntryBytes(stream);
            var kind = Path.GetExtension(item.SourcePath).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                ? ManagedEntryKind.Lnk : ManagedEntryKind.Url;
            var fields = kind == ManagedEntryKind.Lnk ? _session.LinkFiles.Read(item.SourcePath) : null;
            identity = EntryObservation.Observe(identities, kind, item.SourcePath, bytes, fields);
            shortcutTarget = kind == ManagedEntryKind.Url ? UrlShortcut.ReadUrlLine(bytes)
                : fields?.TargetPath ?? fields?.ShellParsingName;
            hash = Convert.ToHexString(SHA256.HashData(bytes));
        }
        else identity = LaunchIdentityService.ForPath(item.SourcePath);
        token.ThrowIfCancellationRequested();
        if (identity.IsUrl && (identity.Key.StartsWith("opaque-url:", StringComparison.Ordinal) || !RegisteredUrlProtocols.IsSupported(identity.Target)))
            throw new IOException(ProductStrings.Get("Import.UnsupportedProtocol"));
        return item with
        {
            Member = item.Member with
            {
                TargetInput = item.Kind == ExplorerAddKind.CopyShortcut
                    ? shortcutTarget ?? (identity.Key.StartsWith("opaque", StringComparison.Ordinal) ? item.Member.TargetInput : identity.Target)
                    : item.Member.TargetInput,
                SourceShortcutPath = item.Kind == ExplorerAddKind.CopyShortcut ? item.SourcePath : null,
                TargetDisplayName = MemberPresentationRules.AutomaticTitle(item.SourcePath, directory),
                ResourceOrigin = MemberResourceOrigin.Local,
            },
            ExpectedIdentityKey = identity.Key, ExpectedSourceSha256 = hash, ExpectedSourceStamp = stamp,
        };
    }

    private static byte[] ReadExternalEntryBytes(Stream stream)
    {
        if (stream.Length is < 1 or > EntryObservation.MaximumBytes) throw new InvalidDataException("Invalid or oversized shortcut.");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
}
