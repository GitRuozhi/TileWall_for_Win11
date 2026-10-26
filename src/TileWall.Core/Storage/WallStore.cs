using System.Security.Cryptography;
using System.Text.Json;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Wall;

namespace TileWall.Core.Storage;

/// <summary>Joint wall/settings/retention and owned assets, committed at one manifest replacement point.</summary>
public sealed partial class WallStore
{
    public const string ImportDestinationChanged = "IMPORT_DESTINATION_CHANGED";
    private readonly IFileStore _files;
    private readonly ILnkFileService _links;
    private readonly WallStoragePaths _paths;
    private readonly LaunchIdentityService _identities;
    private readonly Dictionary<string, LaunchIdentity?> _identityFacts = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JournalJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private const string JournalName = "commit.json";
    private const string IntentName = "install.intent";

    public WallStore(IFileStore files, ILnkFileService links, string dataRoot)
    {
        _paths = new WallStoragePaths(files, dataRoot);
        _files = new GuardedWallFiles(files, _paths);
        _links = new GuardedWallLinks(links, _paths);
        _identities = new LaunchIdentityService(_files, _links);
    }

    public WallOpenResult Open()
    {
        try
        {
            _paths.Guard(_paths.Root);
            _paths.Guard(_paths.Config);
            _paths.Guard(_paths.Previous);
            var primary = Classify(_paths.Config);
            var previous = Classify(_paths.Previous);
            if (primary.Schema is { } primarySchema && primarySchema != UserDataManifest.CurrentFormatVersion ||
                previous.Schema is { } previousSchema && previousSchema != UserDataManifest.CurrentFormatVersion)
                return OpenResult(WallOpenStatus.UnsupportedFormatBlocked, null, "Only the current wall format is supported; data remains untouched.");
            if (primary.Present && primary.Schema != UserDataManifest.CurrentFormatVersion)
                return OpenResult(WallOpenStatus.CorruptBlocked, null, "Primary schema is missing or ambiguous.");
            if (previous.Present && previous.Schema != UserDataManifest.CurrentFormatVersion)
                return OpenResult(WallOpenStatus.CorruptBlocked, null, "Previous schema is missing or ambiguous.");
            var initTemp = _paths.Under("state.init.tmp");
            _paths.Guard(initTemp);
            if (_files.Exists(initTemp))
                return OpenResult(WallOpenStatus.RecoveryBlocked, null,
                    $"Unjournaled initialization file requires inspection: {initTemp}");

            var recover = Sweep(primary, previous);
            if (recover is not null) return recover;
            primary = Classify(_paths.Config);
            previous = Classify(_paths.Previous);
            if (!primary.Present && !previous.Present)
            {
                if (HasAnyManagedResidue()) return OpenResult(WallOpenStatus.RecoveryBlocked, null,
                    "Unattributed files remain in the app data root.");
                return OpenResult(WallOpenStatus.Fresh, null);
            }
            if (primary.Present)
            {
                if (TryReadDocument(_paths.Config, out var document, out var error))
                {
                    AuditRecoveryMaterials();
                    CheckActiveEntries(document!);
                    TryReclaimUnreferencedResources();
                    return OpenResult(WallOpenStatus.Loaded, document);
                }
                if (previous.Schema != UserDataManifest.CurrentFormatVersion || !TryReadDocument(_paths.Previous, out var prior, out _))
                    return OpenResult(WallOpenStatus.CorruptBlocked, null, $"Invalid primary: {error}");
                return RecoverPrevious(prior!);
            }
            if (previous.Schema == UserDataManifest.CurrentFormatVersion && TryReadDocument(_paths.Previous, out var backup, out _))
                return RecoverPrevious(backup!);
            return OpenResult(WallOpenStatus.CorruptBlocked, null, "No valid primary or previous document.");
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            return OpenResult(WallOpenStatus.RecoveryBlocked, null, ex.Message);
        }
    }

    public WallOpenResult InitializeFresh(WallDocument initial)
    {
        string? temp = null, stage = null;
        PreparedUserDataSnapshot? snapshot = null;
        var ownsSnapshot = false;
        try
        {
            if (initial.Generation != 0 || Validate(initial).Count > 0)
                return OpenResult(WallOpenStatus.CorruptBlocked, null, "Initial document is invalid or generation is not zero.");
            var status = Open();
            if (status.Status != WallOpenStatus.Fresh) return status;
            CheckActiveEntries(initial);
            _files.CreateDirectory(_paths.Root);
            temp = _paths.Under("state.init.tmp");
            snapshot = UserDataSnapshotJson.Prepare(initial);
            stage = _paths.Under("Staging", Guid.NewGuid().ToString("N"));
            _files.CreateDirectory(_paths.Staging);
            _files.CreateDirectory(stage);
            StageSnapshot(snapshot, stage);
            // This marker precedes directory publication, so an interrupted first publication cannot look Fresh.
            _files.WriteAllBytes(temp, snapshot.ManifestBytes);
            ownsSnapshot = !_files.GetPathInfo(_paths.Under("Snapshots", snapshot.Manifest.SnapshotId)).Exists;
            InstallSnapshot(snapshot.Manifest, stage);
            _files.Move(temp, _paths.Config, overwrite: false);
            try { _files.DeleteDirectory(stage); }
            catch (Exception cleanup) when (IsStorageError(cleanup))
            { return OpenResult(WallOpenStatus.Loaded, ReadSnapshotDocument(snapshot.ManifestBytes), "Initialized; staging cleanup remains: " + cleanup.Message); }
            return OpenResult(WallOpenStatus.Loaded, ReadSnapshotDocument(snapshot.ManifestBytes));
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            // A replace implementation may report failure after having installed the new manifest.
            if (snapshot is not null && TryConfigHash() == Hash(snapshot.ManifestBytes))
                return OpenResult(WallOpenStatus.Loaded, ReadSnapshotDocument(snapshot.ManifestBytes), "Initialized; cleanup remains: " + ex.Message);
            try
            {
                if (_files.Exists(_paths.Config)) return OpenResult(WallOpenStatus.RecoveryBlocked, null, ex.Message);
                if (stage is not null) _files.DeleteDirectory(stage);
                if (ownsSnapshot && snapshot is not null)
                {
                    var directory = _paths.Under("Snapshots", snapshot.Manifest.SnapshotId);
                    if (_files.GetPathInfo(directory).Exists)
                    {
                        _ = ReadSnapshotAt(snapshot.Manifest, directory);
                        _files.DeleteDirectory(directory);
                    }
                }
                if (temp is not null) _files.Delete(temp);
            }
            catch (Exception cleanup) when (IsStorageError(cleanup))
            { return OpenResult(WallOpenStatus.RecoveryBlocked, null, $"{ex.Message}; cleanup: {cleanup.Message}"); }
            return OpenResult(WallOpenStatus.OpenFailed, null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    public WallPlanResult PlanEntries(WallDocument current, WallDocument candidate,
        IReadOnlyList<EntryChange> changes, IReadOnlyList<PreparedMediaAsset>? mediaAssets = null,
        IReadOnlyDictionary<string, string>? expectedEntrySha256 = null,
        IReadOnlyList<PreparedManagedGallery>? managedGalleries = null)
    {
        try
        {
            if (current is null || candidate is null || !ShapeOkay(candidate))
                return new WallPlanResult(null, ["WALL_STRUCTURE"]);
            if (changes is null) return new WallPlanResult(null, ["ENTRY_CHANGES_NULL"]);
            var currentErrors = Validate(current);
            if (currentErrors.Count > 0) return new WallPlanResult(null, currentErrors);
            if (changes.Any(c => c is null || !WallStoragePaths.ValidId(c.MemberId)))
                return new WallPlanResult(null, ["Invalid entry change identity."]);
            if (changes.OfType<EntryChange.CopyShortcut>().Any(copy =>
                copy.ExpectedSourceSha256 is not null && !ValidHash(copy.ExpectedSourceSha256)))
                return new WallPlanResult(null, ["Invalid shortcut source fingerprint."]);
            if (changes.OfType<EntryChange.FromPackage>().Any(copy => copy.Source is null))
                return new WallPlanResult(null, ["Invalid verified package shortcut."]);
            var old = Members(current);
            if (expectedEntrySha256 is not null && expectedEntrySha256.Any(pair =>
                !WallStoragePaths.ValidId(pair.Key) || !ValidHash(pair.Value) || old.GetValueOrDefault(pair.Key)?.Entry is null))
                return new WallPlanResult(null, ["Invalid entry precondition."]);
            var frozenPreconditions = expectedEntrySha256?.ToDictionary(pair => pair.Key,
                pair => pair.Value.ToUpperInvariant(), StringComparer.Ordinal);
            var provisional = new Dictionary<string, ManagedEntryReference?>(StringComparer.Ordinal);
            foreach (var changed in changes)
                if (changed is not null && changed.MemberId is not null)
                    provisional[changed.MemberId] = changed is EntryChange.Remove
                        ? null : old.GetValueOrDefault(changed.MemberId)?.Entry;
            var sanitizedCandidate = ReplaceEntries(candidate, provisional);
            var errors = Validate(sanitizedCandidate).ToList();
            if (current.Generation == long.MaxValue || candidate.Generation != current.Generation + 1)
                errors.Add("GENERATION");
            if (errors.Count != 0) return new WallPlanResult(null, errors);

            var next = Members(candidate);
            var byId = changes.GroupBy(c => c.MemberId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            var planned = new Dictionary<string, ManagedEntryReference?>(StringComparer.Ordinal);
            foreach (var (id, ops) in byId)
            {
                old.TryGetValue(id, out var before);
                next.TryGetValue(id, out var after);
                if (before is null && after is null) throw new InvalidDataException($"Unknown member: {id}");
                if (ops.Count(o => o is EntryChange.Keep) > 0 && ops.Length != 1)
                    throw new InvalidDataException($"Keep cannot be combined: {id}");
                if (ops.Count(o => o is EntryChange.CopyShortcut or EntryChange.FromPackage or EntryChange.CreateLink or EntryChange.CreateShellLink or EntryChange.CreateUrl or EntryChange.Remove) > 1 ||
                    ops.Count(o => o is EntryChange.ChangeTarget) > 1 || ops.Count(o => o is EntryChange.Rename) > 1)
                    throw new InvalidDataException($"Conflicting entry changes: {id}");
                if (ops.Any(o => o is EntryChange.Keep))
                {
                    if (before?.Entry is null || after is null || after.Entry != before.Entry)
                        throw new InvalidDataException($"Keep requires unchanged existing entry: {id}");
                }
                if (ops.Any(o => o is EntryChange.Remove))
                {
                    if (before?.Entry is null || after is not null && after.Entry is not null || ops.Length != 1)
                        throw new InvalidDataException($"Invalid removal: {id}");
                    planned[id] = null;
                    continue;
                }
                if (after is null || after is ClockMember)
                    throw new InvalidDataException($"Entry operation has no eligible member: {id}");
                var entry = before?.Entry;
                var replacement = ops.SingleOrDefault(o => o is EntryChange.CopyShortcut or EntryChange.FromPackage or EntryChange.CreateLink or EntryChange.CreateShellLink or EntryChange.CreateUrl);
                if (replacement is not null)
                {
                    var kind = replacement switch
                    {
                        EntryChange.CopyShortcut copy => SourceKind(copy.SourcePath),
                        EntryChange.FromPackage copy => copy.Source.Kind,
                        EntryChange.CreateLink link when Path.IsPathFullyQualified(link.TargetPath) => ManagedEntryKind.Lnk,
                        EntryChange.CreateShellLink shell when !string.IsNullOrWhiteSpace(shell.ParsingName) && !string.IsNullOrWhiteSpace(shell.ExpectedIdentityKey) => ManagedEntryKind.Lnk,
                        EntryChange.CreateUrl url when UrlShortcut.ValidateUrl(url.Url).Count == 0 => ManagedEntryKind.Url,
                        _ => throw new InvalidDataException($"Invalid entry source: {id}")
                    };
                    var title = after.Visual.TitleMode == MemberTitleMode.Manual ? after.Visual.Title :
                        MemberPresentationRules.AutomaticTitle(after.TargetInput);
                    var name = string.IsNullOrEmpty(title) ? "entry" : ValidName(title);
                    entry = new ManagedEntryReference(id, $"Objects/{id}/{name}.{(kind == ManagedEntryKind.Lnk ? "lnk" : "url")}", kind);
                }
                if (entry is null) throw new InvalidDataException($"No entry to edit: {id}");
                var targetEdit = ops.OfType<EntryChange.ChangeTarget>().SingleOrDefault();
                if (targetEdit is not null)
                {
                    if (replacement is not null || before?.Entry is null) throw new InvalidDataException($"Invalid target edit: {id}");
                    if (entry.Kind == ManagedEntryKind.Url && UrlShortcut.ValidateUrl(targetEdit.TargetPathOrUrl).Count != 0)
                        throw new InvalidDataException($"Invalid URL: {id}");
                    if (entry.Kind == ManagedEntryKind.Lnk && !Path.IsPathFullyQualified(targetEdit.TargetPathOrUrl))
                        throw new InvalidDataException($"Invalid link target: {id}");
                    if (entry.Kind == ManagedEntryKind.Lnk)
                    {
                        var source = _paths.Entry(before.Entry);
                        _paths.Guard(source);
                        if (_links.Read(source).HasIdList)
                            throw new InvalidDataException("IDList shortcut requires whole replacement.");
                    }
                }
                if (ops.Any(o => o is EntryChange.Rename))
                {
                    if (before?.Entry is null || replacement is not null || string.IsNullOrEmpty(after.Visual.Title))
                        throw new InvalidDataException($"Invalid rename: {id}");
                    var title = ValidName(after.Visual.Title);
                    if (NeedsRename(entry, title))
                        entry = entry with { RelativePath = $"Objects/{id}/{title}.{(entry.Kind == ManagedEntryKind.Lnk ? "lnk" : "url")}" };
                }
                if (after.Visual.TitleMode == MemberTitleMode.Manual &&
                    !string.IsNullOrEmpty(after.Visual.Title) && NeedsRename(entry, after.Visual.Title))
                    throw new InvalidDataException($"Title differs from managed entry filename: {id}");
                planned[id] = entry;
            }
            foreach (var (id, before) in old)
            {
                if (before.Entry is not null && !next.ContainsKey(id) && !byId.ContainsKey(id))
                    throw new InvalidDataException($"Missing remove: {id}");
            }
            foreach (var (id, after) in next)
            {
                old.TryGetValue(id, out var before);
                var desired = planned.TryGetValue(id, out var value) ? value : before?.Entry;
                if (after.Entry != desired && !byId.ContainsKey(id))
                    throw new InvalidDataException($"Unexplained entry difference: {id}");
                if (desired is not null && after.Visual.TitleMode == MemberTitleMode.Manual &&
                    !string.IsNullOrEmpty(after.Visual.Title) &&
                    NeedsRename(desired, after.Visual.Title))
                    throw new InvalidDataException($"Title differs from managed entry filename: {id}");
                planned[id] = desired;
            }
            var normalized = UserDataSnapshotJson.MapGalleryPaths(ReplaceEntries(candidate, planned), _paths.Gallery);
            errors = Validate(normalized).ToList();
            return errors.Count == 0
                ? new WallPlanResult(new WallCommitRequest(current.Generation, WallJson.Freeze(normalized), changes.ToArray(), mediaAssets?.ToArray(), frozenPreconditions, managedGalleries?.ToArray()), [])
                : new WallPlanResult(null, errors);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            return new WallPlanResult(null, [ex.Message]);
        }
    }

    public WallCommitResult Commit(WallDocument current, WallCommitRequest request,
        WallCommitTimings? timings = null)
    {
        using var total = timings?.Measure("store.total");
        try
        {
            if (current is null || request is null || request.Proposed is null || request.EntryChanges is null)
                return CommitResult(WallCommitStatus.Rejected, null, "Invalid commit request.");
            string expectedHash;
            using (timings?.Measure("store.diskMatch"))
                if (!CurrentMatchesDisk(current, request.ExpectedGeneration, out expectedHash))
                    return CommitResult(WallCommitStatus.Rejected, null, "Current generation or config bytes differ from disk.");
            WallPlanResult plan;
            using (timings?.Measure("store.planning"))
                plan = PlanEntries(current, request.Proposed, request.EntryChanges, request.MediaAssets, request.ExpectedEntrySha256, request.ManagedGalleries);
            using (timings?.Measure("store.canonicalValidation"))
            {
                // Commit owns one planning pass. Use the canonical request it produced;
                // the caller supplies the candidate document and entry intents, not a
                // second trusted representation of the same wall.
                if (plan.Request is null)
                    return CommitResult(WallCommitStatus.Rejected, null,
                        plan.Diagnostics.Count == 0 ? "Entry references differ from canonical plan." : string.Join("; ", plan.Diagnostics));
            }
            using (timings?.Measure("store.openPreflight"))
            {
                var blocked = PreflightForCommit(current, expectedHash, timings, plan.Request.ExpectedEntrySha256);
                if (blocked is not null) return blocked;
            }
            return CommitCore(current, plan.Request.Proposed, plan.Request.EntryChanges,
                expectedHash, timings, plan.Request.MediaAssets, plan.Request.ExpectedEntrySha256, plan.Request.ManagedGalleries);
        }
        catch (EntryPreconditionChangedException)
        {
            return CommitResult(WallCommitStatus.Rejected, null, ImportDestinationChanged);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            return CommitResult(WallCommitStatus.RecoveryBlocked, null, ex.Message);
        }
    }

    /// <summary>
    /// Commit preflight after CurrentMatchesDisk. It does not parse the current config again.
    /// Sweep may still change disk; the hash is checked again before any entry is installed.
    /// </summary>
    private WallCommitResult? PreflightForCommit(WallDocument current, string expectedHash,
        WallCommitTimings? timings, IReadOnlyDictionary<string, string>? expectedEntrySha256)
    {
        _paths.Guard(_paths.Root);
        _paths.Guard(_paths.Config);
        _paths.Guard(_paths.Previous);
        var previous = Classify(_paths.Previous);
        if (previous.Present && previous.Schema != UserDataManifest.CurrentFormatVersion)
            return CommitResult(WallCommitStatus.RecoveryBlocked, null, "Previous format is unsupported, missing or ambiguous.");
        var initTemp = _paths.Under("state.init.tmp");
        _paths.Guard(initTemp);
        if (_files.Exists(initTemp))
            return CommitResult(WallCommitStatus.RecoveryBlocked, null,
                $"Unjournaled initialization file requires inspection: {initTemp}");
        var recover = Sweep(new FileClass(true, UserDataManifest.CurrentFormatVersion), previous);
        if (recover is not null)
            return CommitResult(WallCommitStatus.RecoveryBlocked, null,
                $"Store is {recover.Status}: {string.Join("; ", recover.Diagnostics)}");
        AuditRecoveryMaterials(timings, expectedHash);
        if (Hash(_files.ReadAllBytes(_paths.Config)) != expectedHash)
            return CommitResult(WallCommitStatus.Rejected, null, "Config changed during commit preflight.");
        CheckActiveEntries(current, verifyMediaContents: false, observeIdentities: false, expectedEntrySha256);
        return null;
    }

    private WallCommitResult CommitCore(WallDocument current, WallDocument proposed,
        IReadOnlyList<EntryChange> changes,
        string expectedDiskHash, WallCommitTimings? timings = null,
        IReadOnlyList<PreparedMediaAsset>? mediaAssets = null,
        IReadOnlyDictionary<string, string>? expectedEntrySha256 = null,
        IReadOnlyList<PreparedManagedGallery>? managedGalleries = null)
    {
        using var total = timings?.Measure("store.transactionTotal");
        var commitId = Guid.NewGuid().ToString("N");
        var stageDir = _paths.Under("Staging", commitId);
        var materialDir = _paths.Under("Recovery", "Entries", commitId);
        List<WallJournalMedia> media;
        List<WallJournalGallery> galleries;
        try
        {
            using (timings?.Measure("store.media.plan"))
                media = PlanMedia(commitId, current, proposed, mediaAssets ?? []);
            galleries = PlanGalleries(commitId, current, proposed, managedGalleries ?? []);
            ValidatePreparedResourceBudget(current, mediaAssets ?? [], managedGalleries ?? []);
        }
        catch (CurrentMediaInvalidException ex)
        { return CommitResult(WallCommitStatus.RecoveryBlocked, null, ex.Message); }
        catch (Exception ex) when (IsStorageError(ex))
        { return CommitResult(WallCommitStatus.Rejected, null, ex.Message); }
        byte[] oldBytes, newBytes;
        PreparedUserDataSnapshot snapshot;
        using (timings?.Measure("store.prepareConfig"))
        {
            oldBytes = _files.ReadAllBytes(_paths.Config);
            if (Hash(oldBytes) != expectedDiskHash)
                return CommitResult(WallCommitStatus.Rejected, null, "Config changed during commit preflight.");
            snapshot = UserDataSnapshotJson.Prepare(proposed);
            newBytes = snapshot.ManifestBytes;
        }
        var old = Members(current);
        var next = Members(proposed);
        var changesByMember = changes.GroupBy(change => change.MemberId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var ops = new List<WallJournalOp>();
        using (timings?.Measure("store.entryOperations"))
        foreach (var id in old.Keys.Union(next.Keys, StringComparer.Ordinal))
        {
            old.TryGetValue(id, out var before);
            next.TryGetValue(id, out var after);
            var oldRef = before?.Entry;
            var newRef = after?.Entry;
            var changed = changesByMember.GetValueOrDefault(id) ?? [];
            if (oldRef == newRef && (changed.Length == 0 || changed.All(c => c is EntryChange.Keep or EntryChange.Rename))) continue;
            if (oldRef is null && newRef is null) continue;
            var oldPath = oldRef?.RelativePath;
            var newPath = newRef?.RelativePath;
            byte[]? oldEntryBytes = oldRef is null ? null : ReadCommitEntryBytes(id, _paths.Entry(oldRef), expectedEntrySha256);
            var oldHash = oldEntryBytes is null ? null : Hash(oldEntryBytes);
            RequireExpectedEntryHash(id, oldHash, expectedEntrySha256);
            var oldLength = oldEntryBytes?.LongLength ?? 0;
            ops.Add(new WallJournalOp(id, oldPath, newPath, oldHash, oldLength, null, 0));
        }
        List<WallObservedEntry> observed;
        using (timings?.Measure("store.entryObservation")) observed = ObserveUnchanged(current, ops, ActiveMemberIds(proposed), expectedEntrySha256);
        var journal = new WallJournal(WallDocument.CurrentSchemaVersion, commitId, current.Generation, proposed.Generation,
            Hash(oldBytes), Hash(newBytes), false, ops, observed, media, galleries);
        using (timings?.Measure("store.guardCommitPaths"))
        {
            GuardCommitPaths(journal);
            _paths.Guard(stageDir);
            _paths.Guard(materialDir);
        }
        InstallTransactionResult installed;
        using (timings?.Measure("store.transactionWrite"))
            installed = ExecuteInstallTransaction(journal, snapshot, oldBytes, null, () =>
            {
                for (var i = 0; i < ops.Count; i++)
                {
                    var op = ops[i];
                    if (op.NewPath is null) continue;
                    var staged = StageEntryPath(commitId, op.MemberId);
                    _paths.Guard(staged);
                    var bytes = ProduceEntry(op.MemberId, old.GetValueOrDefault(op.MemberId)?.Entry,
                        next[op.MemberId].Entry!, changesByMember.GetValueOrDefault(op.MemberId) ?? [],
                        staged);
                    ops[i] = op with { NewHash = Hash(bytes), NewLength = bytes.LongLength };
                    ObserveIdentity(next[op.MemberId].Entry!.Kind, staged, bytes, ops[i].NewHash!);
                }
                using (timings?.Measure("store.media.stage"))
                    StageMedia(journal, mediaAssets ?? []);
                StageGalleries(journal, managedGalleries ?? []);
                ValidateGroupIdentities(proposed, journal);
            }, () =>
            {
                foreach (var op in ops.Where(o => o.OldPath is not null))
                {
                    var backupDir = _paths.Under("Recovery", "Entries", commitId, op.MemberId);
                    _files.CreateDirectory(backupDir);
                    var backup = BackupPath(commitId, op.MemberId);
                    _paths.Guard(backup);
                    _files.Copy(_paths.Entry(old[op.MemberId].Entry!), backup, false);
                    if (!Verify(backup, op.OldHash, op.OldLength))
                    {
                        if (expectedEntrySha256?.ContainsKey(op.MemberId) == true) throw new EntryPreconditionChangedException();
                        throw new IOException("Entry backup verification failed.");
                    }
                }
            }, timings, () => VerifyExpectedEntryPreconditions(journal, expectedEntrySha256));
        if (installed.Error is not null)
            return FinishFailedInstall(installed, commitId, stageDir, materialDir, journal, timings,
                () => CommitResult(WallCommitStatus.CommittedNeedsCleanup, WallJson.Freeze(proposed), installed.Error.Message));
        WallCommitStatus cleanupStatus;
        using (timings?.Measure("store.transactionCleanup")) cleanupStatus = CleanupAfterCommit(journal);
        WallDocument frozen;
        using (timings?.Measure("store.resultFreeze")) frozen = WallJson.Freeze(proposed);
        return CommitResult(cleanupStatus, frozen,
            cleanupStatus == WallCommitStatus.CommittedNeedsCleanup ? "Committed; cleanup needs attention." : null);
    }

    private readonly record struct InstallTransactionResult(bool IntentPublished, bool Committed, bool Blocked, Exception? Error);

    /// <summary>
    /// Shared staging, intent, install and config replacement. Callers supply entry bytes and which
    /// previous document to publish. The config Move is the commit point.
    /// </summary>
    private InstallTransactionResult ExecuteInstallTransaction(WallJournal journal, PreparedUserDataSnapshot snapshot,
        byte[] previousBytes, byte[]? corruptDiagnostic, Action stageEntries, Action backupEntries,
        WallCommitTimings? timings = null, Action? beforeIntent = null)
    {
        var intentPublished = false;
        try
        {
            var stageDir = _paths.Under("Staging", journal.CommitId);
            var materialDir = _paths.Under("Recovery", "Entries", journal.CommitId);
            GuardCommitPaths(journal);
            _paths.Guard(stageDir);
            _paths.Guard(materialDir);
            _files.CreateDirectory(_paths.Staging);
            _files.CreateDirectory(stageDir);
            _files.CreateDirectory(_paths.Under("Recovery"));
            _files.CreateDirectory(_paths.RecoveryEntries);
            _files.CreateDirectory(materialDir);
            var configTemp = _paths.Under("Staging", journal.CommitId, "state.tmp");
            _files.WriteAllBytes(configTemp, snapshot.ManifestBytes);
            StageSnapshot(snapshot, stageDir);
            stageEntries();
            var journalTemp = _paths.Under("Staging", journal.CommitId, "commit.tmp");
            _files.WriteAllBytes(journalTemp, JsonSerializer.SerializeToUtf8Bytes(journal, JournalJson));
            _files.Move(journalTemp, _paths.Under("Staging", journal.CommitId, JournalName), false);
            backupEntries();
            beforeIntent?.Invoke();
            var markerTemp = _paths.Under("Staging", journal.CommitId, "intent.tmp");
            _files.WriteAllBytes(markerTemp, []);
            _files.Move(markerTemp, _paths.Under("Staging", journal.CommitId, IntentName), false);
            intentPublished = true;
            InstallSnapshot(snapshot.Manifest, stageDir);
            InstallGalleries(journal);
            InstallMedia(journal, timings);
            Install(journal);
            if (journal.OldHash is not null && Hash(_files.ReadAllBytes(_paths.Config)) != journal.OldHash)
                throw new IOException("Config changed during entry installation.");
            if (corruptDiagnostic is not null)
            {
                var corrupt = _paths.Under("Recovery", $"corrupt-{journal.CommitId}.json");
                _paths.Guard(corrupt);
                _files.WriteAllBytes(corrupt, corruptDiagnostic);
            }
            _paths.Guard(_paths.Previous);
            var prevTemp = _paths.Under("Recovery", "state.prev.tmp");
            _files.WriteAllBytes(prevTemp, previousBytes);
            _files.Move(prevTemp, _paths.Previous, true);
            _files.Move(configTemp, _paths.Config, true);
            return new(true, true, false, null);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            var hash = TryConfigHash();
            if (hash == journal.NewHash) return new(true, true, false, ex);
            if (intentPublished && hash != journal.OldHash) return new(true, false, true, ex);
            return new(intentPublished, false, false, ex);
        }
    }

    private string? TryConfigHash()
    {
        try
        {
            return _files.Exists(_paths.Config) ? Hash(_files.ReadAllBytes(_paths.Config)) : null;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            return "unreadable";
        }
    }

    private WallCommitResult FinishFailedInstall(InstallTransactionResult installed, string commitId,
        string stageDir, string materialDir, WallJournal journal, WallCommitTimings? timings,
        Func<WallCommitResult> committedResult)
    {
        if (installed.Committed) return committedResult();
        if (installed.Blocked)
            return CommitResult(WallCommitStatus.RecoveryBlocked, null,
                $"Commit {commitId}: {installed.Error!.Message}");
        try
        {
            using var rollbackTiming = timings?.Measure("store.rollback");
            if (installed.IntentPublished) Rollback(journal);
            else
            {
                _paths.GuardTree(stageDir);
                _paths.GuardTree(materialDir);
                _files.DeleteDirectory(stageDir);
                _files.DeleteDirectory(materialDir);
            }
            return CommitResult(WallCommitStatus.Rejected, null, installed.Error!.Message);
        }
        catch (Exception rollback) when (IsStorageError(rollback))
        {
            return CommitResult(WallCommitStatus.RecoveryBlocked, null,
                $"Commit {commitId}: {installed.Error!.Message}; rollback: {rollback.Message}");
        }
    }

    private byte[] ProduceEntry(string id, ManagedEntryReference? oldEntry, ManagedEntryReference newEntry,
        EntryChange[] changes, string staged)
    {
        var replacement = changes.FirstOrDefault(c => c is EntryChange.CopyShortcut or EntryChange.FromPackage or EntryChange.CreateLink or EntryChange.CreateShellLink or EntryChange.CreateUrl);
        switch (replacement)
        {
            case EntryChange.FromPackage package:
                _files.WriteAllBytes(staged, package.Source.CopyBytes());
                break;
            case EntryChange.CopyShortcut copy:
                var source = Path.GetFullPath(copy.SourcePath);
                _paths.Guard(source, allowExternalRead: true);
                if (!_files.GetPathInfo(source).Exists) throw new FileNotFoundException("Shortcut source missing.", source);
                _files.Copy(source, staged, false);
                break;
            case EntryChange.CreateLink link:
                _links.Create(staged, link.TargetPath, "", "");
                break;
            case EntryChange.CreateShellLink shell:
                _links.CreateShell(staged, shell.ParsingName);
                break;
            case EntryChange.CreateUrl url:
                _files.WriteAllBytes(staged, UrlShortcut.CreateContent(url.Url));
                break;
            default:
                if (oldEntry is null) throw new InvalidDataException($"Missing previous entry: {id}");
                _files.Copy(_paths.Entry(oldEntry), staged, false);
                break;
        }
        var edit = changes.OfType<EntryChange.ChangeTarget>().SingleOrDefault();
        if (edit is not null)
        {
            if (newEntry.Kind == ManagedEntryKind.Url)
                _files.WriteAllBytes(staged, UrlShortcut.RewriteUrlLine(_files.ReadAllBytes(staged), edit.TargetPathOrUrl));
            else
            {
                var fields = _links.Read(staged);
                if (fields.HasIdList) throw new InvalidDataException("IDList shortcut requires whole replacement.");
                _links.EditTargetOnly(staged, edit.TargetPathOrUrl);
            }
        }
        var stagedBytes = _files.ReadAllBytes(staged);
        if (replacement is EntryChange.CopyShortcut { ExpectedSourceSha256: { } expected } &&
            !string.Equals(Hash(stagedBytes), expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Shortcut source changed after confirmation.");
        if (replacement is EntryChange.FromPackage packageSource &&
            (stagedBytes.Length != packageSource.Source.Length || Hash(stagedBytes) != packageSource.Source.Sha256 ||
             EntryObservation.Observe(_identities, newEntry.Kind, staged, stagedBytes).Key != packageSource.Source.Identity.Key))
            throw new IOException("Package shortcut differs from verified entry bytes or identity.");
        if (replacement is EntryChange.CreateShellLink shellSource &&
            EntryObservation.Observe(_identities, newEntry.Kind, staged, stagedBytes).Key != shellSource.ExpectedIdentityKey)
            throw new IOException("The native Shell identity changed during preparation.");
        return stagedBytes;
    }

    private void Install(WallJournal journal)
    {
        foreach (var op in journal.Ops)
        {
            if (op.OldPath is not null && op.OldPath != op.NewPath)
                _files.Delete(EntryPath(op.MemberId, op.OldPath));
            if (op.NewPath is not null)
            {
                var destination = EntryPath(op.MemberId, op.NewPath);
                _files.CreateDirectory(Path.GetDirectoryName(destination)!);
                _files.Move(StageEntryPath(journal.CommitId, op.MemberId), destination, true);
                if (!Verify(destination, op.NewHash, op.NewLength)) throw new IOException("Installed entry differs from stage.");
            }
        }
    }

    private void Rollback(WallJournal journal)
    {
        ValidateJournal(journal);
        var actualHash = _files.Exists(_paths.Config) ? Hash(_files.ReadAllBytes(_paths.Config)) : null;
        if (actualHash != journal.OldHash)
            throw new IOException("Config fingerprint prevents rollback.");
        foreach (var op in journal.Ops.Where(o => o.OldPath is not null))
            if (!Verify(BackupPath(journal.CommitId, op.MemberId), op.OldHash, op.OldLength))
                throw new IOException("Missing or corrupt entry backup.");
        foreach (var op in journal.Ops.AsEnumerable().Reverse())
        {
            if (op.NewPath is not null) _files.Delete(EntryPath(op.MemberId, op.NewPath));
            if (op.OldPath is not null)
            {
                var old = EntryPath(op.MemberId, op.OldPath);
                _files.CreateDirectory(Path.GetDirectoryName(old)!);
                _files.Copy(BackupPath(journal.CommitId, op.MemberId), old, true);
                if (!Verify(old, op.OldHash, op.OldLength)) throw new IOException("Rollback verification failed.");
            }
        }
        RollbackMedia(journal);
        RollbackGalleries(journal);
        var stage = _paths.Under("Staging", journal.CommitId);
        var material = _paths.Under("Recovery", "Entries", journal.CommitId);
        _paths.GuardTree(stage);
        _paths.GuardTree(material);
        _files.DeleteDirectory(stage);
        _files.DeleteDirectory(material);
    }

    private WallCommitStatus CleanupAfterCommit(WallJournal journal)
    {
        try
        {
            var stage = _paths.Under("Staging", journal.CommitId);
            PreserveCommittedMaterial(journal, _paths.Under("Staging", journal.CommitId, JournalName));
            var keepMaterialId = RecoverySourceToKeep(journal);
            PruneMaterials(journal, keepMaterialId);
            PruneCorruptDiagnostics(journal);
            _files.DeleteDirectory(stage);
            TryReclaimUnreferencedResources(journal.NewHash);
            return WallCommitStatus.Committed;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            return WallCommitStatus.CommittedNeedsCleanup;
        }
    }

    /// <summary>
    /// Recovery publishes trusted previous bytes into Recovery/state.prev.json.
    /// The source journal is the one whose hash matches that previous, not the corrupt OldHash.
    /// </summary>
    private string? RecoverySourceToKeep(WallJournal journal)
    {
        if (!journal.RecoveryPublish) return null;
        if (!_files.Exists(_paths.Previous))
            throw new IOException("Cleanup refused: trusted previous is missing.");
        var previousHash = Hash(_files.ReadAllBytes(_paths.Previous));
        if (journal.OldHash is not null && journal.OldHash == previousHash) return null;
        if (!TryReadDocument(_paths.Previous, out var previous, out var error))
            throw new IOException($"Trusted previous cannot be read before cleanup: {error}");
        var match = FindPreviousJournal(previousHash);
        var hasEntries = Members(previous!).Values.Any(member => member.Entry is not null);
        if (match is null)
        {
            if (hasEntries) throw new IOException("Cleanup refused: previous entries have no trusted source journal.");
            return null;
        }
        if (match.PreviousIsNew) VerifyNewStateCoverage(match.Journal, previous!);
        else
        {
            VerifyOldStateCoverage(match.Journal, previous!);
            VerifyObserved(match.Journal);
        }
        foreach (var op in match.Journal.Ops.Where(operation => operation.OldPath is not null))
            if (!Verify(BackupPath(match.Journal.CommitId, op.MemberId), op.OldHash, op.OldLength))
                throw new IOException($"Cleanup refused: source backup is incomplete: {match.Journal.CommitId}/{op.MemberId}");
        var sourceSnapshot = _paths.Under("Recovery", "Entries", match.Journal.CommitId, "new.snapshot.json");
        if (!_files.Exists(sourceSnapshot) || Hash(_files.ReadAllBytes(sourceSnapshot)) != match.Journal.NewHash)
            throw new IOException($"Cleanup refused: source snapshot is incomplete: {match.Journal.CommitId}");
        return match.Journal.CommitId;
    }

    private void PruneCorruptDiagnostics(WallJournal journal)
    {
        // Only a recovery publish writes corrupt-{this commit}.json and prunes diagnostics.
        if (!journal.RecoveryPublish) return;
        var keepName = $"corrupt-{journal.CommitId}.json";
        if (!_files.Exists(_paths.Under("Recovery", keepName))) return;
        foreach (var file in _files.ListFiles(_paths.Under("Recovery")))
        {
            var name = Path.GetFileName(file);
            if (!IsCorruptDiagnosticName(name) || name.Equals(keepName, StringComparison.OrdinalIgnoreCase)) continue;
            _paths.Guard(file);
            _files.Delete(file);
        }
    }

    private static bool IsCorruptDiagnosticName(string name)
    {
        const string prefix = "corrupt-";
        const string suffix = ".json";
        if (name.Length != prefix.Length + 32 + suffix.Length ||
            !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(suffix, StringComparison.Ordinal)) return false;
        foreach (var ch in name.AsSpan(prefix.Length, 32))
            if (!Uri.IsHexDigit(ch)) return false;
        return true;
    }

    private void PreserveCommittedMaterial(WallJournal journal, string stagingJournal)
    {
        var directory = _paths.Under("Recovery", "Entries", journal.CommitId);
        _files.CreateDirectory(directory);
        var dest = _paths.Under("Recovery", "Entries", journal.CommitId, "journal.json");
        if (!_files.Exists(dest)) _files.Copy(stagingJournal, dest, false);
        _ = ReadJournal(dest, journal.CommitId);
        var snapshot = _paths.Under("Recovery", "Entries", journal.CommitId, "new.snapshot.json");
        if (!_files.Exists(snapshot)) _files.Copy(_paths.Config, snapshot, false);
        var committedManifest = _files.ReadAllBytes(snapshot);
        if (Hash(committedManifest) != journal.NewHash || !TryReadDocument(committedManifest, out var committed, out _))
            throw new IOException("Committed complete snapshot verification failed.");
        VerifyMediaCoverage(journal, committed!);
        VerifyGalleryCoverage(journal, committed!);
        var oldSnapshot = _paths.Under("Recovery", "Entries", journal.CommitId, "old.snapshot.json");
        if (journal.OldHash is not null)
        {
            if (!_files.Exists(oldSnapshot) && _files.Exists(_paths.Previous) &&
                Hash(_files.ReadAllBytes(_paths.Previous)) == journal.OldHash)
                _files.Copy(_paths.Previous, oldSnapshot, false);
            var corruptSource = _paths.Under("Recovery", $"corrupt-{journal.CommitId}.json");
            if (!_files.Exists(oldSnapshot) && journal.RecoveryPublish && _files.Exists(corruptSource) &&
                Hash(_files.ReadAllBytes(corruptSource)) == journal.OldHash)
                _files.Copy(corruptSource, oldSnapshot, false);
            if (!_files.Exists(oldSnapshot) || Hash(_files.ReadAllBytes(oldSnapshot)) != journal.OldHash)
                throw new IOException("Previous snapshot verification failed.");
            // Recovery deliberately preserves the damaged former primary as an exact diagnostic.
            if (!journal.RecoveryPublish && !TryReadDocument(oldSnapshot, out _, out _))
                throw new IOException("Previous complete snapshot verification failed.");
        }
    }

    private void PruneMaterials(WallJournal newest, string? keepMaterialId = null)
    {
        // An unfinished install still owns its backups even while a different committed
        // transaction is completing cleanup during Open's staging sweep.
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stage in _files.ListDirectories(_paths.Staging))
        {
            var id = Path.GetFileName(stage);
            if (!ValidCommitId(id)) throw new InvalidDataException($"Unknown staging material: {stage}");
            var journalPath = _paths.Under("Staging", id, JournalName);
            if (_files.Exists(journalPath))
            {
                _ = ReadJournal(journalPath, id);
                pending.Add(id);
            }
        }
        var materials = new List<string>();
        foreach (var directory in _files.ListDirectories(_paths.RecoveryEntries))
        {
            var id = Path.GetFileName(directory);
            if (!ValidCommitId(id)) throw new InvalidDataException($"Unknown recovery material: {directory}");
            var path = _paths.Under("Recovery", "Entries", id, "journal.json");
            if (_files.Exists(path)) _ = ReadJournal(path, id);
            else if (!pending.Contains(id)) throw new InvalidDataException($"Recovery material lacks journal: {id}");
            materials.Add(id);
        }
        foreach (var id in materials)
        {
            if (id == newest.CommitId || id == keepMaterialId || pending.Contains(id)) continue;
            _files.DeleteDirectory(_paths.Under("Recovery", "Entries", id));
        }
    }

    private void AuditRecoveryMaterials(WallCommitTimings? timings = null, string? expectedCurrentHash = null)
    {
        _paths.GuardTree(_paths.RecoveryEntries);
        // A recovery transaction may retain a source journal solely to prove its old side.
        // The other side can be the damaged snapshot that caused recovery; never make that
        // diagnostic side a requirement for opening the now healthy current/previous pair.
        var requiredHashes = new HashSet<string>(StringComparer.Ordinal);
        if (expectedCurrentHash is not null) requiredHashes.Add(expectedCurrentHash);
        else if (_files.Exists(_paths.Config)) requiredHashes.Add(Hash(_files.ReadAllBytes(_paths.Config)));
        if (_files.Exists(_paths.Previous)) requiredHashes.Add(Hash(_files.ReadAllBytes(_paths.Previous)));
        foreach (var directory in _files.ListDirectories(_paths.Staging))
        {
            var id = Path.GetFileName(directory);
            var path = Path.Combine(directory, JournalName);
            if (!_files.Exists(path)) continue;
            var pending = ReadJournal(path, id);
            requiredHashes.Add(pending.NewHash);
            if (pending.OldHash is not null) requiredHashes.Add(pending.OldHash);
        }
        var auditedMedia = new HashSet<ManagedMediaReference>();
        var auditedGalleries = new HashSet<ManagedGalleryReference>();
        foreach (var directory in _files.ListDirectories(_paths.RecoveryEntries))
        {
            var id = Path.GetFileName(directory);
            if (!ValidCommitId(id)) throw new InvalidDataException($"Unknown recovery material: {directory}");
            var journalPath = _paths.Under("Recovery", "Entries", id, "journal.json");
            if (!_files.Exists(journalPath)) throw new InvalidDataException($"Recovery material lacks journal: {id}");
            var journal = ReadJournal(journalPath, id);
            void AuditSnapshot(string name, string expectedHash)
            {
                var path = Path.Combine(directory, name);
                var bytes = _files.Exists(path) ? _files.ReadAllBytes(path) : null;
                if (bytes is null || Hash(bytes) != expectedHash)
                    throw new InvalidDataException($"Recovery material lacks its exact manifest: {id}/{name}");
                if (!requiredHashes.Contains(expectedHash)) return;
                if (!TryReadDocument(bytes, out var document, out _))
                    throw new InvalidDataException($"Necessary recovery snapshot cannot be read: {id}/{name}");
                VerifyMediaCoverage(journal, document!);
                VerifyGalleryCoverage(journal, document!);
                foreach (var media in MediaById(document!).Values)
                    if (auditedMedia.Add(media) && !Verify(_paths.Media(media), media.Sha256, media.Length))
                        throw new IOException("Necessary recovery original is missing or changed.");
                foreach (var gallery in GalleryReferences(document!))
                    if (auditedGalleries.Add(gallery)) _ = CheckGallery(gallery);
            }
            using (timings?.Measure("store.media.audit"))
            {
                AuditSnapshot("new.snapshot.json", journal.NewHash);
                if (journal.OldHash is not null) AuditSnapshot("old.snapshot.json", journal.OldHash);
            }
            foreach (var op in journal.Ops.Where(o => o.OldPath is not null))
                if (!Verify(BackupPath(id, op.MemberId), op.OldHash, op.OldLength))
                    throw new InvalidDataException($"Recovery entry backup is incomplete: {id}/{op.MemberId}");
        }
    }

    private WallOpenResult? Sweep(FileClass primary, FileClass previous)
    {
        _paths.Guard(_paths.Staging);
        _paths.Guard(_paths.RecoveryEntries);
        foreach (var stage in _files.ListDirectories(_paths.Staging))
        {
            _paths.GuardTree(stage);
            var id = Path.GetFileName(stage);
            if (!ValidCommitId(id)) throw new InvalidDataException($"Unrecognized staging directory: {stage}");
            var journalPath = _paths.Under("Staging", id, JournalName);
            var marker = _paths.Under("Staging", id, IntentName);
            if (!_files.Exists(journalPath))
            {
                if (_files.Exists(marker)) throw new InvalidDataException($"Intent lacks journal: {id}");
                var material = _paths.Under("Recovery", "Entries", id);
                _paths.GuardTree(material);
                _files.DeleteDirectory(stage);
                _files.DeleteDirectory(material);
                continue;
            }
            var journal = ReadJournal(journalPath, id);
            var hash = primary.Present ? Hash(_files.ReadAllBytes(_paths.Config)) : null;
            if (hash == journal.NewHash)
            {
                if (CleanupAfterCommit(journal) != WallCommitStatus.Committed)
                    throw new IOException($"Committed cleanup remains: {id}");
            }
            else if (hash == journal.OldHash || hash is null && journal.OldHash is null)
            {
                if (_files.Exists(marker)) Rollback(journal);
                else
                {
                    var material = _paths.Under("Recovery", "Entries", id);
                    _paths.GuardTree(material);
                    _files.DeleteDirectory(stage);
                    _files.DeleteDirectory(material);
                }
            }
            else throw new InvalidDataException($"Config fingerprint differs from journal {id}.");
        }
        return null;
    }

    private WallOpenResult RecoverPrevious(WallDocument previous)
    {
        try
        {
            var previousBytes = _files.ReadAllBytes(_paths.Previous);
            var sourceMatch = FindPreviousJournal(Hash(previousBytes));
            if (sourceMatch is null)
            {
                if (Members(previous).Values.Any(m => m.Entry is not null))
                    throw new IOException("Previous entries cannot be verified without trusted journal material.");
                CheckActiveEntries(previous);
                return PublishRecoveredPrevious(previous, previousBytes, []);
            }
            var sourceJournal = sourceMatch.Journal;
            if (sourceMatch.PreviousIsNew)
            {
                VerifyNewStateCoverage(sourceJournal, previous);
                return PublishRecoveredPrevious(previous, previousBytes, []);
            }
            VerifyOldStateCoverage(sourceJournal, previous);
            VerifyObserved(sourceJournal);
            var restore = new List<RecoveryEntry>();
            foreach (var op in sourceJournal.Ops)
            {
                byte[]? oldBytes = null;
                if (op.NewPath is not null)
                {
                    var active = EntryPath(op.MemberId, op.NewPath);
                    if (!Verify(active, op.NewHash, op.NewLength))
                        throw new IOException($"Current entry differs from trusted journal: {op.MemberId}");
                    oldBytes = _files.ReadAllBytes(active);
                }
                byte[]? restored = null;
                if (op.OldPath is not null)
                {
                    var backup = BackupPath(sourceJournal.CommitId, op.MemberId);
                    if (!Verify(backup, op.OldHash, op.OldLength))
                        throw new IOException($"Previous entry material is incomplete: {op.MemberId}");
                    restored = _files.ReadAllBytes(backup);
                }
                restore.Add(new RecoveryEntry(op.MemberId, op.NewPath, op.OldPath, oldBytes, restored));
            }
            return PublishRecoveredPrevious(previous, previousBytes, restore);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            return OpenResult(WallOpenStatus.RecoveryBlocked, null, ex.Message);
        }
    }

    private PreviousJournalMatch? FindPreviousJournal(string previousHash)
    {
        _paths.GuardTree(_paths.RecoveryEntries);
        WallJournal? oldMatch = null;
        WallJournal? newMatch = null;
        foreach (var material in _files.ListDirectories(_paths.RecoveryEntries))
        {
            var id = Path.GetFileName(material);
            if (!ValidCommitId(id)) throw new InvalidDataException($"Unknown recovery material: {material}");
            var journalPath = _paths.Under("Recovery", "Entries", id, "journal.json");
            if (!_files.Exists(journalPath)) continue;
            var candidate = ReadJournal(journalPath, id);
            if (candidate.OldHash == previousHash)
            {
                if (oldMatch is not null) throw new InvalidDataException("Ambiguous previous recovery journal.");
                oldMatch = candidate;
            }
            if (candidate.NewHash == previousHash)
            {
                if (newMatch is not null) throw new InvalidDataException("Ambiguous committed recovery journal.");
                newMatch = candidate;
            }
        }
        if (oldMatch is not null) return new PreviousJournalMatch(oldMatch, false);
        return newMatch is null ? null : new PreviousJournalMatch(newMatch, true);
    }

    private void VerifyOldStateCoverage(WallJournal journal, WallDocument previous)
    {
        VerifyMediaCoverage(journal, previous);
        VerifyGalleryCoverage(journal, previous);
        CheckGalleryReferences(previous);
        CheckMediaReferences(previous);
        var snapshot = _paths.Under("Recovery", "Entries", journal.CommitId, "old.snapshot.json");
        if (!TryReadDocument(snapshot, out var oldDocument, out _) || !DocumentEqual(oldDocument!, previous))
            throw new IOException("Previous document differs from trusted journal.");
        var refs = Members(previous).Values.Where(m => m.Entry is not null)
            .ToDictionary(m => m.Id, m => m.Entry!, StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in journal.Ops)
        {
            if (op.OldPath is null) continue;
            if (!refs.TryGetValue(op.MemberId, out var entry) || entry.RelativePath != op.OldPath)
                throw new IOException("Previous entry reference differs from journal.");
            covered.Add(op.MemberId);
        }
        foreach (var observed in journal.Observed)
        {
            if (!refs.TryGetValue(observed.MemberId, out var entry) || entry.RelativePath != observed.RelativePath)
                throw new IOException("Previous observed entry differs from journal.");
            covered.Add(observed.MemberId);
        }
        if (covered.Count != refs.Count) throw new IOException("Previous entry journal coverage is incomplete.");
    }

    private void VerifyNewStateCoverage(WallJournal journal, WallDocument previous)
    {
        VerifyMediaCoverage(journal, previous);
        VerifyGalleryCoverage(journal, previous);
        CheckGalleryReferences(previous);
        CheckMediaReferences(previous);
        var snapshot = _paths.Under("Recovery", "Entries", journal.CommitId, "new.snapshot.json");
        if (!TryReadDocument(snapshot, out var newDocument, out _) || !DocumentEqual(newDocument!, previous))
            throw new IOException("Committed document differs from trusted journal.");
        var refs = Members(previous).Values.Where(m => m.Entry is not null)
            .ToDictionary(m => m.Id, m => m.Entry!, StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in journal.Ops)
        {
            if (op.NewPath is not null)
            {
                if (!refs.TryGetValue(op.MemberId, out var entry) || entry.RelativePath != op.NewPath ||
                    !Verify(EntryPath(op.MemberId, op.NewPath), op.NewHash, op.NewLength))
                    throw new IOException("Committed entry differs from trusted journal.");
                covered.Add(op.MemberId);
            }
            if (op.OldPath is not null && op.OldPath != op.NewPath &&
                _files.Exists(EntryPath(op.MemberId, op.OldPath)))
                throw new IOException("Removed entry is unexpectedly active.");
        }
        VerifyObserved(journal);
        foreach (var observed in journal.Observed)
        {
            if (!refs.TryGetValue(observed.MemberId, out var entry) || entry.RelativePath != observed.RelativePath)
                throw new IOException("Committed observed entry differs from journal.");
            covered.Add(observed.MemberId);
        }
        if (covered.Count != refs.Count) throw new IOException("Committed entry journal coverage is incomplete.");
    }

    private sealed record PreviousJournalMatch(WallJournal Journal, bool PreviousIsNew);

    private WallOpenResult PublishRecoveredPrevious(WallDocument previous, byte[] previousBytes,
        IReadOnlyList<RecoveryEntry> entries)
    {
        if (previous.Generation == long.MaxValue) throw new InvalidDataException("Recovery generation overflow.");
        var recovered = previous with { Generation = previous.Generation + 1 };
        var snapshot = UserDataSnapshotJson.Prepare(recovered);
        var newBytes = snapshot.ManifestBytes;
        var oldBytes = _files.Exists(_paths.Config) ? _files.ReadAllBytes(_paths.Config) : null;
        var commitId = Guid.NewGuid().ToString("N");
        var operations = entries.Select(e => new WallJournalOp(e.MemberId,
                e.CurrentPath, e.PreviousPath, e.CurrentBytes is null ? null : Hash(e.CurrentBytes),
                e.CurrentBytes?.LongLength ?? 0, e.PreviousBytes is null ? null : Hash(e.PreviousBytes),
                e.PreviousBytes?.LongLength ?? 0)).ToList();
        var observed = ObserveUnchanged(previous, operations);
        var journal = new WallJournal(WallDocument.CurrentSchemaVersion, commitId, previous.Generation, recovered.Generation,
            oldBytes is null ? null : Hash(oldBytes), Hash(newBytes), true, operations, observed,
            PlanMedia(commitId, previous, recovered, []), PlanGalleries(commitId, previous, recovered, []));
        var stageDir = _paths.Under("Staging", commitId);
        var materialDir = _paths.Under("Recovery", "Entries", commitId);
        var installed = ExecuteInstallTransaction(journal, snapshot, previousBytes, oldBytes, () =>
        {
            foreach (var entry in entries.Where(e => e.PreviousBytes is not null))
                _files.WriteAllBytes(StageEntryPath(commitId, entry.MemberId), entry.PreviousBytes!);
        }, () =>
        {
            foreach (var entry in entries.Where(e => e.CurrentBytes is not null))
            {
                var backupDir = _paths.Under("Recovery", "Entries", commitId, entry.MemberId);
                _files.CreateDirectory(backupDir);
                _files.Copy(EntryPath(entry.MemberId, entry.CurrentPath!), BackupPath(commitId, entry.MemberId), false);
                var op = journal.Ops.Single(o => o.MemberId == entry.MemberId);
                if (!Verify(BackupPath(commitId, entry.MemberId), op.OldHash, op.OldLength))
                    throw new IOException("Recovery backup verification failed.");
            }
        });
        if (installed.Error is null)
        {
            var cleanup = CleanupAfterCommit(journal);
            return OpenResult(WallOpenStatus.RecoveredPrevious, WallJson.Freeze(recovered),
                cleanup == WallCommitStatus.CommittedNeedsCleanup ? "Recovered; cleanup remains." : null);
        }
        if (installed.Committed)
            return OpenResult(WallOpenStatus.RecoveredPrevious, WallJson.Freeze(recovered),
                $"Recovered; cleanup remains: {installed.Error.Message}");
        if (!installed.Blocked)
        {
            try
            {
                if (installed.IntentPublished) Rollback(journal);
                else
                {
                    _paths.GuardTree(stageDir);
                    _paths.GuardTree(materialDir);
                    _files.DeleteDirectory(stageDir);
                    _files.DeleteDirectory(materialDir);
                }
            }
            catch (Exception rollback) when (IsStorageError(rollback))
            {
                return OpenResult(WallOpenStatus.RecoveryBlocked, null,
                    $"{installed.Error.Message}; rollback: {rollback.Message}");
            }
        }
        return OpenResult(WallOpenStatus.RecoveryBlocked, null, installed.Error.Message);
    }

    private sealed record RecoveryEntry(string MemberId, string? CurrentPath, string? PreviousPath,
        byte[]? CurrentBytes, byte[]? PreviousBytes);

    private void CheckActiveEntries(WallDocument document, bool verifyMediaContents = true, bool observeIdentities = true,
        IReadOnlyDictionary<string, string>? expectedEntrySha256 = null)
    {
        var identityHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var active = ActiveMemberIds(document);
        foreach (var member in Members(document).Values)
        {
            if (member.Entry is not { } entry) continue;
            var path = _paths.Entry(entry);
            _paths.Guard(path);
            if (!_files.GetPathInfo(path).Exists)
            {
                if (expectedEntrySha256?.ContainsKey(member.Id) == true) throw new EntryPreconditionChangedException();
                throw new IOException($"Referenced entry is missing: {path}");
            }
            if (observeIdentities && active.Contains(member.Id))
            {
                var bytes = _files.ReadAllBytes(path);
                var hash = Hash(bytes);
                ObserveIdentity(entry.Kind, path, bytes, hash);
                identityHashes[member.Id] = hash;
            }
        }
        if (observeIdentities) ValidateGroupIdentities(document, identityHashes);
        CheckGalleryReferences(document);
        CheckMediaReferences(document, verifyMediaContents);
    }

    /// <summary>
    /// Hashes every unchanged managed entry at this transaction. The bytes are not a duplicate of an
    /// earlier read in the same commit, so the full observation stays.
    /// </summary>
    private List<WallObservedEntry> ObserveUnchanged(WallDocument document, List<WallJournalOp> operations,
        HashSet<string>? requiredIdentities = null, IReadOnlyDictionary<string, string>? expectedEntrySha256 = null)
    {
        var changed = operations.Select(o => o.MemberId).ToHashSet(StringComparer.Ordinal);
        var result = new List<WallObservedEntry>();
        var active = requiredIdentities ?? ActiveMemberIds(document);
        foreach (var member in Members(document).Values)
        {
            if (member.Entry is null || changed.Contains(member.Id)) continue;
            var path = _paths.Entry(member.Entry);
            var bytes = ReadCommitEntryBytes(member.Id, path, expectedEntrySha256);
            var hash = Hash(bytes);
            RequireExpectedEntryHash(member.Id, hash, expectedEntrySha256);
            if (active.Contains(member.Id)) ObserveIdentity(member.Entry.Kind, path, bytes, hash);
            result.Add(new WallObservedEntry(member.Id, member.Entry.RelativePath, hash, bytes.LongLength));
        }
        return result;
    }

    private byte[] ReadCommitEntryBytes(string memberId, string path,
        IReadOnlyDictionary<string, string>? expectedEntrySha256)
    {
        try { return _files.ReadAllBytes(path); }
        catch (Exception ex) when ((ex is FileNotFoundException or DirectoryNotFoundException) &&
            expectedEntrySha256?.ContainsKey(memberId) == true)
        { throw new EntryPreconditionChangedException(); }
    }

    private static void RequireExpectedEntryHash(string memberId, string? actual,
        IReadOnlyDictionary<string, string>? expectedEntrySha256)
    {
        if (expectedEntrySha256 is not null && expectedEntrySha256.TryGetValue(memberId, out var expected) && actual != expected)
            throw new EntryPreconditionChangedException();
    }

    private void VerifyExpectedEntryPreconditions(WallJournal journal,
        IReadOnlyDictionary<string, string>? expectedEntrySha256)
    {
        if (expectedEntrySha256 is null || expectedEntrySha256.Count == 0) return;
        // This is a distinct IO boundary after staging and backup, before any install
        // intent or formal replacement. Recheck only the affected destination facts.
        var paths = journal.Ops.Where(op => op.OldPath is not null)
            .ToDictionary(op => op.MemberId, op => op.OldPath!, StringComparer.Ordinal);
        foreach (var observed in journal.Observed) paths.Add(observed.MemberId, observed.RelativePath);
        foreach (var memberId in expectedEntrySha256.Keys)
        {
            if (!paths.TryGetValue(memberId, out var relative)) throw new EntryPreconditionChangedException();
            var bytes = ReadCommitEntryBytes(memberId, EntryPath(memberId, relative), expectedEntrySha256);
            RequireExpectedEntryHash(memberId, Hash(bytes), expectedEntrySha256);
        }
    }

    private sealed class EntryPreconditionChangedException() : IOException(ImportDestinationChanged) { }

    private void VerifyObserved(WallJournal journal)
    {
        foreach (var observed in journal.Observed)
            if (!Verify(EntryPath(observed.MemberId, observed.RelativePath), observed.Hash, observed.Length))
                throw new IOException($"Unchanged entry differs from journal: {observed.MemberId}");
    }

    private bool HasAnyManagedResidue()
    {
        foreach (var directory in new[] { _paths.Objects, _paths.Staging, _paths.MediaRoot, _paths.Snapshots, _paths.Sources })
            if (_files.ListDirectories(directory).Count > 0 || _files.ListFiles(directory).Count > 0)
                return true;
        var recovery = _paths.Under("Recovery");
        if (_files.ListFiles(recovery).Count > 0 ||
            _files.ListDirectories(recovery).Any(d => !d.Equals(_paths.RecoveryEntries, StringComparison.OrdinalIgnoreCase)) ||
            _files.ListFiles(_paths.RecoveryEntries).Count > 0 ||
            _files.ListDirectories(_paths.RecoveryEntries).Count > 0)
            return true;
        foreach (var directory in _files.ListDirectories(_paths.Root))
        {
            _paths.GuardTree(directory);
            if (!new[] { _paths.Objects, _paths.Staging, _paths.MediaRoot, _paths.Snapshots, _paths.Sources, _paths.Under("Recovery"),
                _paths.Under("Cache"), _paths.Under("logs") }.Contains(directory, StringComparer.OrdinalIgnoreCase))
                return true;
        }
        return _files.ListFiles(_paths.Root).Count > 0;
    }

    private static FileClass ParseClass(byte[] bytes)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new FileClass(true, null);
            JsonElement schema = default;
            var matches = 0;
            foreach (var field in root.EnumerateObject())
                if (field.Name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase))
                { schema = field.Value; matches++; }
            return matches == 1 && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var value)
                ? new FileClass(true, value) : new FileClass(true, null);
        }
        catch (JsonException) { return new FileClass(true, null); }
    }

    private FileClass Classify(string path) => _files.Exists(path)
        ? ParseClass(_files.ReadAllBytes(path)) : new FileClass(false, null);

    private bool TryReadDocument(string path, out WallDocument? document, out string error)
    {
        try
        {
            return TryReadDocument(_files.ReadAllBytes(path), out document, out error);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            error = ex.Message;
            document = null;
            return false;
        }
    }

    private bool TryReadDocument(byte[] bytes, out WallDocument? document, out string error)
    {
        try
        {
            document = ReadSnapshotDocument(bytes);
            var issues = Validate(document);
            if (issues.Count == 0) { error = ""; return true; }
            error = string.Join("; ", issues);
        }
        catch (Exception ex) when (IsStorageError(ex))
        { error = ex.Message; }
        document = null;
        return false;
    }

    private bool CurrentMatchesDisk(WallDocument current, long generation, out string diskHash)
    {
        diskHash = "";
        if (current.Generation != generation || !_files.Exists(_paths.Config)) return false;
        _paths.Guard(_paths.Config);
        var bytes = _files.ReadAllBytes(_paths.Config);
        diskHash = Hash(bytes);
        return TryReadDocument(bytes, out var disk, out _) && disk!.Generation == generation &&
            DocumentEqual(current, disk);
    }

    private static IReadOnlyList<string> Validate(WallDocument document)
    {
        var errors = WallLayoutEngine.ValidateGeometry(document).ToList();
        // ValidateGeometry already includes the base checks. Keep their old short-circuit,
        // but still add storage path diagnostics when geometry alone fails.
        if (errors.Any(error => error != "WALL_GEOMETRY")) return errors;
        foreach (var lane in document.Lanes)
            foreach (var group in lane.Groups)
            {
                foreach (var member in group.Members)
                {
                    if (member.Entry is not null && !WallStoragePaths.ValidEntry(member.Entry)) errors.Add("ENTRY_PATH");
                    if (member.Entry is not null && member.Visual.TitleMode == MemberTitleMode.Manual &&
                        !string.IsNullOrEmpty(member.Visual.Title) &&
                        EntryNames.Validate(member.Visual.Title).Count > 0) errors.Add("ENTRY_TITLE");
                }
            }
        foreach (var member in document.InactiveMembers)
        {
            if (member.Entry is not null && !WallStoragePaths.ValidEntry(member.Entry)) errors.Add("ENTRY_PATH");
        }
        errors.AddRange(ValidateMediaReferences(document));
        errors.AddRange(ValidateGalleryReferences(document));
        if (document.Settings.ExpandColumns &&
            (document.Lanes[^1].Groups.Count != 0 || document.Lanes.Take(document.Lanes.Count - 1).Any(lane => lane.Groups.Count == 0)))
            errors.Add("EXPANDED_TAIL_LANE");
        return errors;
    }

    private static bool ShapeOkay(WallDocument document) =>
        document.Lanes is not null && document.InactiveMembers is not null &&
        document.InactiveMembers.All(m => m is not null) && document.Lanes.All(l => l?.Groups is not null &&
            l.Groups.All(g => g?.Members is not null && g.Members.All(m => m is not null)));

    private static Dictionary<string, WallMember> Members(WallDocument document) =>
        document.EnumerateAllMembers().ToDictionary(m => m.Id, StringComparer.Ordinal);

    private static WallDocument ReplaceEntries(WallDocument document,
        Dictionary<string, ManagedEntryReference?> planned) => document with
    {
        Lanes = document.Lanes.Select(l => l with
        {
            Groups = l.Groups.Select(g => g with
            {
                Members = g.Members.Select(m => planned.TryGetValue(m.Id, out var entry)
                    ? m switch
                    {
                        TileMember tile => tile with { Entry = entry },
                        PuzzleMember puzzle => puzzle with { Entry = entry },
                        ClockMember clock => clock with { Entry = entry },
                        _ => throw new InvalidDataException("Unknown member type.")
                    }
                    : m).ToArray()
            }).ToArray()
        }).ToArray(),
        InactiveMembers = document.InactiveMembers.Select(m => planned.TryGetValue(m.Id, out var entry)
            ? m with { Entry = entry } : m).ToArray()
    };

    private void GuardCommitPaths(WallJournal journal)
    {
        _paths.Guard(_paths.Config);
        _paths.Guard(_paths.Previous);
        foreach (var op in journal.Ops)
        {
            if (op.OldPath is not null) _paths.Guard(EntryPath(op.MemberId, op.OldPath));
            if (op.NewPath is not null)
            {
                var path = EntryPath(op.MemberId, op.NewPath);
                _paths.Guard(path);
                if (op.OldPath != op.NewPath && _files.Exists(path))
                    throw new IOException($"Destination already exists: {path}");
            }
        }
    }

    private ManagedEntryKind SourceKind(string source)
    {
        if (!Path.IsPathFullyQualified(source)) throw new InvalidDataException("Shortcut source must be absolute.");
        source = Path.GetFullPath(source);
        _paths.Guard(source, allowExternalRead: true);
        var kind = Path.GetExtension(source).ToLowerInvariant() switch
        {
            ".lnk" => ManagedEntryKind.Lnk,
            ".url" => ManagedEntryKind.Url,
            _ => throw new InvalidDataException("Only .lnk or .url can be copied.")
        };
        if (!_files.GetPathInfo(source).Exists) throw new FileNotFoundException("Shortcut source missing.", source);
        return kind;
    }

    private static string ValidName(string title)
    {
        if (EntryNames.Validate(title).Count != 0) throw new InvalidDataException("Invalid entry title.");
        return title;
    }

    private static bool NeedsRename(ManagedEntryReference entry, string title) =>
        !string.Equals(EntryNames.BaseNameOf(entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
            title, StringComparison.OrdinalIgnoreCase);

    private string EntryPath(string id, string relative)
    {
        var kind = relative.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            ? ManagedEntryKind.Lnk : ManagedEntryKind.Url;
        var reference = new ManagedEntryReference(id, relative, kind);
        return _paths.Entry(reference);
    }

    private string BackupPath(string commitId, string id) =>
        _paths.Under("Recovery", "Entries", commitId, id, "old.entry");
    private string StageEntryPath(string commitId, string id) =>
        _paths.Under("Staging", commitId, $"entry-{id}.tmp");

    private WallJournal ReadJournal(string path, string id)
    {
        _paths.Guard(path);
        return ReadJournal(_files.ReadAllBytes(path), id);
    }

    private WallJournal ReadJournal(byte[] bytes, string id)
    {
        using var document = JsonDocument.Parse(bytes);
        RequireFields(document.RootElement,
            ["schemaVersion", "commitId", "oldGeneration", "newGeneration", "oldHash", "newHash",
                "recoveryPublish", "ops", "observed", "media", "galleries"]);
        var rawOps = document.RootElement.GetProperty("ops");
        if (rawOps.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"Malformed journal ops: {id}");
        foreach (var rawOp in rawOps.EnumerateArray())
            RequireFields(rawOp,
                ["memberId", "oldPath", "newPath", "oldHash", "oldLength", "newHash", "newLength"]);
        var rawObserved = document.RootElement.GetProperty("observed");
        if (rawObserved.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Malformed observed entries: {id}");
        foreach (var raw in rawObserved.EnumerateArray())
            RequireFields(raw, ["memberId", "relativePath", "hash", "length"]);
        var rawMedia = document.RootElement.GetProperty("media");
        if (rawMedia.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"Malformed journal media: {id}");
        foreach (var raw in rawMedia.EnumerateArray())
        {
            RequireFields(raw, ["asset", "newlyInstalled", "stagedPath"]);
            RequireFields(raw.GetProperty("asset"), ["sha256", "extension", "length"]);
        }
        var rawGalleries = document.RootElement.GetProperty("galleries");
        if (rawGalleries.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Malformed journal galleries.");
        foreach (var raw in rawGalleries.EnumerateArray())
        {
            RequireFields(raw, ["reference", "newlyInstalled"]);
            RequireFields(raw.GetProperty("reference"), ["contentId", "manifestSha256"]);
        }
        var journal = JsonSerializer.Deserialize<WallJournal>(bytes, JournalJson)
            ?? throw new InvalidDataException($"Empty journal: {id}");
        if (journal.CommitId != id) throw new InvalidDataException($"Journal ID mismatch: {id}");
        ValidateJournal(journal);
        return journal;
    }

    private static void RequireFields(JsonElement element, string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Journal field is not an object.");
        var names = element.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != expected.Length || names.Distinct(StringComparer.Ordinal).Count() != expected.Length ||
            names.Except(expected, StringComparer.Ordinal).Any() || expected.Except(names, StringComparer.Ordinal).Any())
            throw new InvalidDataException("Journal has missing, duplicate, or unexpected fields.");
    }

    private void ValidateJournal(WallJournal journal)
    {
        // Historical journals share long prefixes. Deduplicate metadata only within
        // this validation; guarded file/link operations still recheck at every IO.
        var guardPath = _paths.CreateValidationGuard();
        if (journal.SchemaVersion != WallDocument.CurrentSchemaVersion || !ValidCommitId(journal.CommitId) ||
            journal.OldGeneration < 0 || journal.NewGeneration != journal.OldGeneration + 1 ||
            journal.OldHash is not null && !ValidHash(journal.OldHash) ||
            !ValidHash(journal.NewHash) || journal.Ops is null || journal.Observed is null || journal.Media is null || journal.Galleries is null)
            throw new InvalidDataException($"Malformed journal: {journal.CommitId}");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in journal.Ops)
        {
            if (!WallStoragePaths.ValidId(op.MemberId) || !ids.Add(op.MemberId) ||
                op.OldPath is null && op.NewPath is null ||
                op.OldPath is not null && (!ValidHash(op.OldHash) || op.OldLength < 0) ||
                op.NewPath is not null && (!ValidHash(op.NewHash) || op.NewLength < 0))
                throw new InvalidDataException($"Malformed journal operation: {journal.CommitId}");
            if (op.OldPath is not null) guardPath(EntryPath(op.MemberId, op.OldPath));
            if (op.NewPath is not null) guardPath(EntryPath(op.MemberId, op.NewPath));
        }
        foreach (var observed in journal.Observed)
        {
            if (string.IsNullOrWhiteSpace(observed.RelativePath))
                throw new InvalidDataException($"Malformed observed entry path: {journal.CommitId}");
            if (!WallStoragePaths.ValidId(observed.MemberId) || !ids.Add(observed.MemberId) ||
                !ValidHash(observed.Hash) || observed.Length < 0)
                throw new InvalidDataException($"Malformed observed entry: {journal.CommitId}");
            guardPath(EntryPath(observed.MemberId, observed.RelativePath));
        }
        var galleryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gallery in journal.Galleries)
        {
            if (gallery is null || !PreparedManagedGallery.IsValidReference(gallery.Reference) || !galleryIds.Add(gallery.Reference.ContentId))
                throw new InvalidDataException("Malformed gallery journal operation.");
            guardPath(_paths.Gallery(gallery.Reference));
            if (gallery.NewlyInstalled) guardPath(GalleryStagePath(journal.CommitId, gallery.Reference.ContentId));
        }
        var mediaIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var media in journal.Media)
        {
            if (media is null || !PreparedMediaAsset.IsValidReference(media.Asset) || !mediaIds.Add(media.Asset.Sha256) ||
                (media.NewlyInstalled ? media.StagedPath != MediaStageRelativePath(journal.CommitId, media.Asset.Sha256) : media.StagedPath is not null))
                throw new InvalidDataException($"Malformed media journal operation: {journal.CommitId}");
            guardPath(_paths.Media(media.Asset));
            if (media.NewlyInstalled) guardPath(MediaStagePath(journal.CommitId, media.Asset.Sha256));
        }
    }

    private bool Verify(string path, string? hash, long length) =>
        _files.Exists(path) && _files.ReadAllBytes(path) is var bytes &&
        bytes.LongLength == length && Hash(bytes) == hash;

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private static bool ValidCommitId(string? id) => id is { Length: 32 } && id.All(Uri.IsHexDigit);
    private static bool DocumentEqual(WallDocument a, WallDocument b) =>
        WallJson.Serialize(a).AsSpan().SequenceEqual(WallJson.Serialize(b));
    private static bool IsStorageError(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
            ArgumentException or NotSupportedException or System.Security.SecurityException or
            System.Runtime.InteropServices.COMException;

    private static WallOpenResult OpenResult(WallOpenStatus status, WallDocument? document, string? diagnostic = null) =>
        new(status, document, diagnostic is null ? [] : [diagnostic]);
    private static WallCommitResult CommitResult(WallCommitStatus status, WallDocument? document,
        string? diagnostic = null) =>
        new(status, document, diagnostic is null ? [] : [diagnostic]);

    private sealed record FileClass(bool Present, int? Schema);
    private sealed record WallJournal(int SchemaVersion, string CommitId, long OldGeneration,
        long NewGeneration, string? OldHash, string NewHash, bool RecoveryPublish,
        List<WallJournalOp> Ops, List<WallObservedEntry> Observed, List<WallJournalMedia> Media, List<WallJournalGallery> Galleries);
    private sealed record WallJournalOp(string MemberId, string? OldPath, string? NewPath,
        string? OldHash, long OldLength, string? NewHash, long NewLength);
    private sealed record WallObservedEntry(string MemberId, string RelativePath, string Hash, long Length);
}
