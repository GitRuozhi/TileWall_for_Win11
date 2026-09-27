using System.Security.Cryptography;
using System.Text.Json;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Wall;

namespace TileWall.Core.Storage;

/// <summary>Schema-two wall and managed entries, committed at one config replacement point.</summary>
public sealed class WallStore
{
    private readonly IFileStore _files;
    private readonly ILnkFileService _links;
    private readonly WallStoragePaths _paths;
    private static readonly JsonSerializerOptions JournalJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private const string JournalName = "commit.json";
    private const string IntentName = "install.intent";

    public WallStore(IFileStore files, ILnkFileService links, IDataDirectory directory)
    {
        _paths = new WallStoragePaths(files, directory);
        _files = new GuardedWallFiles(files, _paths);
        _links = new GuardedWallLinks(links, _paths);
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
            if (primary.Schema > 2 || previous.Schema > 2)
                return OpenResult(WallOpenStatus.FutureVersionBlocked, null, "Future schema remains untouched.");
            if (primary.Schema == 1)
                return OpenResult(WallOpenStatus.LegacyResetRequired, null, "Explicit schema-one state requires reset.");
            if (primary.Present && primary.Schema != 2)
                return OpenResult(WallOpenStatus.CorruptBlocked, null, "Primary schema is missing or ambiguous.");
            if (previous.Present && previous.Schema is not 2 and not 1)
                return OpenResult(WallOpenStatus.CorruptBlocked, null, "Previous schema is missing or ambiguous.");
            var initTemp = _paths.Under("config.init.tmp");
            _paths.Guard(initTemp);
            if (_files.Exists(initTemp))
                return OpenResult(WallOpenStatus.RecoveryBlocked, null,
                    $"Unjournaled initialization file requires inspection: {initTemp}");

            var recover = Sweep(primary, previous);
            if (recover is not null) return recover;
            AuditRecoveryMaterials();
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
                    CheckActiveEntries(document!);
                    return OpenResult(WallOpenStatus.Loaded, document);
                }
                if (previous.Schema != 2 || !TryReadDocument(_paths.Previous, out var prior, out _))
                    return OpenResult(WallOpenStatus.CorruptBlocked, null, $"Invalid primary: {error}");
                return RecoverPrevious(prior!);
            }
            if (previous.Schema == 2 && TryReadDocument(_paths.Previous, out var backup, out _))
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
        string? temp = null;
        try
        {
            if (initial.Generation != 0 || Validate(initial).Count > 0)
                return OpenResult(WallOpenStatus.CorruptBlocked, null, "Initial document is invalid or generation is not zero.");
            var status = Open();
            if (status.Status != WallOpenStatus.Fresh) return status;
            _paths.Guard(_paths.Root);
            _files.CreateDirectory(_paths.Root);
            temp = _paths.Under("config.init.tmp");
            _paths.Guard(temp);
            _files.WriteAllBytes(temp, WallJson.Serialize(initial));
            _files.Move(temp, _paths.Config, overwrite: false);
            return OpenResult(WallOpenStatus.Loaded, WallJson.Freeze(initial));
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            if (temp is not null)
                try { _paths.Guard(temp); _files.Delete(temp); }
                catch (Exception cleanup) when (IsStorageError(cleanup))
                { return OpenResult(WallOpenStatus.RecoveryBlocked, null, $"{ex.Message}; cleanup: {cleanup.Message}"); }
            return OpenResult(WallOpenStatus.RecoveryBlocked, null, ex.Message);
        }
    }

    public WallOpenResult ResetLegacyState()
    {
        var open = Open();
        if (open.Status != WallOpenStatus.LegacyResetRequired) return open;
        try
        {
            var targets = new[] { _paths.Config, _paths.Objects, _paths.Staging,
                _paths.Under("Recovery"), _paths.Under("Cache") };
            foreach (var path in targets) _paths.GuardTreeOrFile(path);
            var initTemp = _paths.Under("config.init.tmp");
            _paths.Guard(initTemp);
            if (_files.Exists(initTemp))
                throw new IOException($"Legacy reset blocked by unjournaled initialization: {initTemp}");
            foreach (var stage in _files.ListDirectories(_paths.Staging))
                if (_files.Exists(Path.Combine(stage, IntentName)))
                    throw new IOException($"Legacy reset blocked by undecidable install intent: {stage}");
            foreach (var path in targets)
            {
                if (_files.GetPathInfo(path).IsDirectory) _files.DeleteDirectory(path);
                else _files.Delete(path);
            }
            return Open();
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            return OpenResult(WallOpenStatus.RecoveryBlocked, null, ex.Message);
        }
    }

    public WallPlanResult PlanEntries(WallDocument current, WallDocument candidate,
        IReadOnlyList<EntryChange> changes)
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
            var old = Members(current);
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
                if (ops.Count(o => o is EntryChange.CopyShortcut or EntryChange.CreateLink or EntryChange.CreateUrl or EntryChange.Remove) > 1 ||
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
                var replacement = ops.SingleOrDefault(o => o is EntryChange.CopyShortcut or EntryChange.CreateLink or EntryChange.CreateUrl);
                if (replacement is not null)
                {
                    var kind = replacement switch
                    {
                        EntryChange.CopyShortcut copy => SourceKind(copy.SourcePath),
                        EntryChange.CreateLink link when Path.IsPathFullyQualified(link.TargetPath) => ManagedEntryKind.Lnk,
                        EntryChange.CreateUrl url when UrlShortcut.ValidateUrl(url.Url).Count == 0 => ManagedEntryKind.Url,
                        _ => throw new InvalidDataException($"Invalid entry source: {id}")
                    };
                    var title = after.Visual.Title;
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
                if (!string.IsNullOrEmpty(after.Visual.Title) && NeedsRename(entry, after.Visual.Title))
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
                if (desired is not null && !string.IsNullOrEmpty(after.Visual.Title) &&
                    NeedsRename(desired, after.Visual.Title))
                    throw new InvalidDataException($"Title differs from managed entry filename: {id}");
                planned[id] = desired;
            }
            var normalized = ReplaceEntries(candidate, planned);
            errors = Validate(normalized).ToList();
            return errors.Count == 0
                ? new WallPlanResult(new WallCommitRequest(current.Generation, WallJson.Freeze(normalized), changes.ToArray()), [])
                : new WallPlanResult(null, errors);
        }
        catch (Exception ex) when (IsStorageError(ex) || ex is InvalidDataException or ArgumentException)
        {
            return new WallPlanResult(null, [ex.Message]);
        }
    }

    public WallCommitResult Commit(WallDocument current, WallCommitRequest request, bool recordUndo)
    {
        try
        {
            if (current is null || request is null || request.Proposed is null || request.EntryChanges is null)
                return CommitResult(WallCommitStatus.Rejected, null, null, "Invalid commit request.");
            if (!CurrentMatchesDisk(current, request.ExpectedGeneration, out var expectedHash))
                return CommitResult(WallCommitStatus.Rejected, null, null, "Current generation or config bytes differ from disk.");
            var plan = PlanEntries(current, request.Proposed, request.EntryChanges);
            if (plan.Request is null || !DocumentEqual(plan.Request.Proposed, request.Proposed))
                return CommitResult(WallCommitStatus.Rejected, null, null,
                    plan.Diagnostics.Count == 0 ? "Entry references differ from canonical plan." : string.Join("; ", plan.Diagnostics));
            if (!recordUndo && !NonUndoOnly(current, request.Proposed, request.EntryChanges))
                return CommitResult(WallCommitStatus.Rejected, null, null, "Non-undo commit may only change settings or carousel state.");
            var opened = Open();
            if (opened.Status != WallOpenStatus.Loaded || opened.Document is null)
                return CommitResult(WallCommitStatus.RecoveryBlocked, null, null, $"Store is {opened.Status}.");
            return CommitCore(current, plan.Request.Proposed, plan.Request.EntryChanges,
                recordUndo, false, null, expectedHash);
        }
        catch (Exception ex) when (IsStorageError(ex) || ex is InvalidDataException or ArgumentException)
        {
            return CommitResult(WallCommitStatus.RecoveryBlocked, null, null, ex.Message);
        }
    }

    public WallCommitResult Undo(WallDocument current, WallDocument previous,
        WallUndoToken token, long expectedGeneration)
    {
        try
        {
            if (current.Generation == long.MaxValue || expectedGeneration != current.Generation)
                return CommitResult(WallCommitStatus.Rejected, null, null, "Undo generation overflow or mismatch.");
            if (!CurrentMatchesDisk(current, expectedGeneration, out var expectedHash))
                return CommitResult(WallCommitStatus.Rejected, null, null, "Current generation or config bytes differ from disk.");
            var opened = Open();
            if (opened.Status != WallOpenStatus.Loaded)
                return CommitResult(WallCommitStatus.RecoveryBlocked, null, null, $"Store is {opened.Status}.");
            if (!ValidCommitId(token.CommitId) || token.CommittedGeneration > current.Generation)
                return CommitResult(WallCommitStatus.Rejected, null, null, "Invalid undo token.");
            var journalPath = _paths.Under("Recovery", "Entries", token.CommitId, "journal.json");
            _paths.Guard(journalPath);
            if (!_files.Exists(journalPath))
                return CommitResult(WallCommitStatus.Rejected, null, null, "Undo token is no longer active.");
            var journal = ReadJournal(journalPath, token.CommitId);
            if (!journal.RecordUndo || !_files.Exists(_paths.Under("Recovery", "Entries", token.CommitId, "undo.marker")))
                return CommitResult(WallCommitStatus.Rejected, null, null, "Undo token has no active undo material.");
            var previousSnapshotPath = _paths.Under("Recovery", "Entries", token.CommitId, "old.snapshot.json");
            _paths.Guard(previousSnapshotPath);
            if (journal.NewGeneration != token.CommittedGeneration ||
                !_files.Exists(previousSnapshotPath) ||
                Hash(_files.ReadAllBytes(previousSnapshotPath)) != journal.OldHash ||
                !TryReadDocument(previousSnapshotPath, out var savedPrevious, out _) ||
                !DocumentEqual(savedPrevious!, previous))
                return CommitResult(WallCommitStatus.Rejected, null, null, "Undo snapshot does not match material.");
            var committedSnapshotPath = _paths.Under("Recovery", "Entries", token.CommitId, "new.snapshot.json");
            _paths.Guard(committedSnapshotPath);
            if (!_files.Exists(committedSnapshotPath) ||
                Hash(_files.ReadAllBytes(committedSnapshotPath)) != journal.NewHash ||
                !TryReadDocument(committedSnapshotPath, out var committedSnapshot, out _))
                return CommitResult(WallCommitStatus.RecoveryBlocked, null, null, "Undo committed snapshot is incomplete.");
            if (!SameLayoutAfterNonUndo(committedSnapshot!, current))
                return CommitResult(WallCommitStatus.Rejected, null, null, "Current layout moved beyond this undo token.");
            var merged = MergeForUndo(previous, committedSnapshot!, current) with { Generation = current.Generation + 1 };
            var overrides = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var op in journal.Ops)
            {
                if (op.OldPath is null) continue;
                var backup = BackupPath(token.CommitId, op.MemberId);
                _paths.Guard(backup);
                var bytes = _files.ReadAllBytes(backup);
                if (Hash(bytes) != op.OldHash || bytes.Length != op.OldLength)
                    return CommitResult(WallCommitStatus.RecoveryBlocked, null, null, "Undo entry backup is incomplete.");
                overrides[op.MemberId] = bytes;
            }
            if (Validate(merged).Count > 0) return CommitResult(WallCommitStatus.Rejected, null, null, "Undo document is invalid.");
            return CommitCore(current, merged, [], false, true, overrides, expectedHash);
        }
        catch (Exception ex) when (IsStorageError(ex) || ex is InvalidDataException or ArgumentException)
        {
            return CommitResult(WallCommitStatus.RecoveryBlocked, null, null, ex.Message);
        }
    }

    private WallCommitResult CommitCore(WallDocument current, WallDocument proposed,
        IReadOnlyList<EntryChange> changes, bool recordUndo, bool invalidateUndo,
        Dictionary<string, byte[]>? overrideBytes,
        string expectedDiskHash)
    {
        var commitId = Guid.NewGuid().ToString("N");
        var stageDir = _paths.Under("Staging", commitId);
        var materialDir = _paths.Under("Recovery", "Entries", commitId);
        var oldBytes = _files.ReadAllBytes(_paths.Config);
        if (Hash(oldBytes) != expectedDiskHash)
            return CommitResult(WallCommitStatus.Rejected, null, null, "Config changed during commit preflight.");
        var newBytes = WallJson.Serialize(proposed);
        var old = Members(current);
        var next = Members(proposed);
        var ops = new List<WallJournalOp>();
        foreach (var id in old.Keys.Union(next.Keys, StringComparer.Ordinal))
        {
            old.TryGetValue(id, out var before);
            next.TryGetValue(id, out var after);
            var oldRef = before?.Entry;
            var newRef = after?.Entry;
            var changed = changes.Where(c => c.MemberId == id).ToArray();
            if (oldRef == newRef && (changed.Length == 0 || changed.All(c => c is EntryChange.Keep or EntryChange.Rename)) && overrideBytes is null) continue;
            if (oldRef is null && newRef is null) continue;
            if (overrideBytes is not null && oldRef == newRef && !overrideBytes.ContainsKey(id)) continue;
            var oldPath = oldRef?.RelativePath;
            var newPath = newRef?.RelativePath;
            var oldHash = oldRef is null ? null : Hash(_files.ReadAllBytes(_paths.Entry(oldRef)));
            var oldLength = oldRef is null ? 0 : _files.ReadAllBytes(_paths.Entry(oldRef)).LongLength;
            ops.Add(new WallJournalOp(id, oldPath, newPath, oldHash, oldLength, null, 0));
        }
        var observed = ObserveUnchanged(current, ops);
        var journal = new WallJournal(2, commitId, current.Generation, proposed.Generation,
            Hash(oldBytes), Hash(newBytes), recordUndo, invalidateUndo, ops, observed);
        GuardCommitPaths(journal);
        _paths.Guard(stageDir);
        _paths.Guard(materialDir);
        var intentPublished = false;
        var committed = false;
        try
        {
            _files.CreateDirectory(_paths.Staging);
            _files.CreateDirectory(stageDir);
            _files.CreateDirectory(_paths.Under("Recovery"));
            _files.CreateDirectory(_paths.RecoveryEntries);
            _files.CreateDirectory(materialDir);
            var configTemp = _paths.Under("Staging", commitId, "config.tmp");
            _files.WriteAllBytes(configTemp, newBytes);
            for (var i = 0; i < ops.Count; i++)
            {
                var op = ops[i];
                if (op.NewPath is null) continue;
                var staged = StageEntryPath(commitId, op.MemberId);
                _paths.Guard(staged);
                var bytes = ProduceEntry(op.MemberId, old.GetValueOrDefault(op.MemberId)?.Entry,
                    next[op.MemberId].Entry!, changes.Where(c => c.MemberId == op.MemberId).ToArray(),
                    staged, overrideBytes);
                ops[i] = op with { NewHash = Hash(bytes), NewLength = bytes.LongLength };
            }
            var journalTemp = _paths.Under("Staging", commitId, "commit.tmp");
            var journalPath = _paths.Under("Staging", commitId, JournalName);
            _files.WriteAllBytes(journalTemp, JsonSerializer.SerializeToUtf8Bytes(journal, JournalJson));
            _files.Move(journalTemp, journalPath, false);
            foreach (var op in ops.Where(o => o.OldPath is not null))
            {
                var backupDir = _paths.Under("Recovery", "Entries", commitId, op.MemberId);
                _files.CreateDirectory(backupDir);
                var backup = BackupPath(commitId, op.MemberId);
                _paths.Guard(backup);
                _files.Copy(_paths.Entry(old[op.MemberId].Entry!), backup, false);
                if (!Verify(backup, op.OldHash, op.OldLength)) throw new IOException("Entry backup verification failed.");
            }
            var markerTemp = _paths.Under("Staging", commitId, "intent.tmp");
            var marker = _paths.Under("Staging", commitId, IntentName);
            _files.WriteAllBytes(markerTemp, []);
            _files.Move(markerTemp, marker, false);
            intentPublished = true;
            Install(journal);
            if (Hash(_files.ReadAllBytes(_paths.Config)) != journal.OldHash)
                throw new IOException("Config changed during entry installation.");
            _paths.Guard(_paths.Previous);
            var prevTemp = _paths.Under("Recovery", "config.prev.tmp");
            _files.WriteAllBytes(prevTemp, oldBytes);
            _files.Move(prevTemp, _paths.Previous, true);
            _files.Move(configTemp, _paths.Config, true);
            committed = true;
            var cleanupStatus = CleanupAfterCommit(journal);
            return CommitResult(cleanupStatus, WallJson.Freeze(proposed),
                recordUndo && cleanupStatus == WallCommitStatus.Committed
                    ? new WallUndoToken(commitId, proposed.Generation) : null,
                cleanupStatus == WallCommitStatus.CommittedNeedsCleanup ? "Committed; cleanup or undo material needs attention." : null);
        }
        catch (Exception ex) when (IsStorageError(ex) || ex is InvalidDataException or ArgumentException)
        {
            if (committed) return CommitResult(WallCommitStatus.CommittedNeedsCleanup, WallJson.Freeze(proposed),
                null, ex.Message);
            try
            {
                if (intentPublished) Rollback(journal);
                else
                {
                    _paths.GuardTree(stageDir);
                    _paths.GuardTree(materialDir);
                    _files.DeleteDirectory(stageDir);
                    _files.DeleteDirectory(materialDir);
                }
                return CommitResult(WallCommitStatus.Rejected, null, null, ex.Message);
            }
            catch (Exception rollback) when (IsStorageError(rollback))
            {
                return CommitResult(WallCommitStatus.RecoveryBlocked, null, null,
                    $"Commit {commitId}: {ex.Message}; rollback: {rollback.Message}");
            }
        }
    }

    private byte[] ProduceEntry(string id, ManagedEntryReference? oldEntry, ManagedEntryReference newEntry,
        EntryChange[] changes, string staged, Dictionary<string, byte[]>? overrides)
    {
        if (overrides is not null && overrides.TryGetValue(id, out var restored))
        {
            _files.WriteAllBytes(staged, restored);
            return _files.ReadAllBytes(staged);
        }
        var replacement = changes.FirstOrDefault(c => c is EntryChange.CopyShortcut or EntryChange.CreateLink or EntryChange.CreateUrl);
        switch (replacement)
        {
            case EntryChange.CopyShortcut copy:
                var source = Path.GetFullPath(copy.SourcePath);
                _paths.Guard(source, allowExternalRead: true);
                if (!_files.GetPathInfo(source).Exists) throw new FileNotFoundException("Shortcut source missing.", source);
                _files.Copy(source, staged, false);
                break;
            case EntryChange.CreateLink link:
                _links.Create(staged, link.TargetPath, "", "");
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
        return _files.ReadAllBytes(staged);
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
            _paths.GuardTree(stage);
            _files.DeleteDirectory(stage);
            PruneMaterials(journal);
            return WallCommitStatus.Committed;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            return WallCommitStatus.CommittedNeedsCleanup;
        }
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
        if (Hash(_files.ReadAllBytes(snapshot)) != journal.NewHash)
            throw new IOException("Committed snapshot verification failed.");
        var oldSnapshot = _paths.Under("Recovery", "Entries", journal.CommitId, "old.snapshot.json");
        if (journal.OldHash is not null)
        {
            if (!_files.Exists(oldSnapshot) && _files.Exists(_paths.Previous) &&
                Hash(_files.ReadAllBytes(_paths.Previous)) == journal.OldHash)
                _files.Copy(_paths.Previous, oldSnapshot, false);
            var corruptSource = _paths.Under("Recovery", $"corrupt-{journal.CommitId}.json");
            if (!_files.Exists(oldSnapshot) && journal.InvalidateUndo && _files.Exists(corruptSource) &&
                Hash(_files.ReadAllBytes(corruptSource)) == journal.OldHash)
                _files.Copy(corruptSource, oldSnapshot, false);
            if (!_files.Exists(oldSnapshot) || Hash(_files.ReadAllBytes(oldSnapshot)) != journal.OldHash)
                throw new IOException("Previous snapshot verification failed.");
        }
        if (journal.RecordUndo)
        {
            var marker = _paths.Under("Recovery", "Entries", journal.CommitId, "undo.marker");
            if (!_files.Exists(marker)) _files.WriteAllBytes(marker, []);
        }
    }

    private void PruneMaterials(WallJournal newest)
    {
        var materials = new List<(string Id, WallJournal Journal, bool Undo)>();
        foreach (var directory in _files.ListDirectories(_paths.RecoveryEntries))
        {
            var id = Path.GetFileName(directory);
            if (!ValidCommitId(id)) throw new InvalidDataException($"Unknown recovery material: {directory}");
            var path = _paths.Under("Recovery", "Entries", id, "journal.json");
            if (!_files.Exists(path)) throw new InvalidDataException($"Recovery material lacks journal: {id}");
            var journal = ReadJournal(path, id);
            materials.Add((id, journal, _files.Exists(_paths.Under("Recovery", "Entries", id, "undo.marker"))));
        }
        var latestUndo = newest.InvalidateUndo ? null : materials.Where(m => m.Undo)
            .OrderByDescending(m => m.Journal.NewGeneration).Select(m => m.Id).FirstOrDefault();
        foreach (var material in materials)
        {
            if (material.Id == newest.CommitId || material.Id == latestUndo) continue;
            var directory = _paths.Under("Recovery", "Entries", material.Id);
            _paths.GuardTree(directory);
            _files.DeleteDirectory(directory);
        }
    }

    private void AuditRecoveryMaterials()
    {
        _paths.GuardTree(_paths.RecoveryEntries);
        foreach (var directory in _files.ListDirectories(_paths.RecoveryEntries))
        {
            var id = Path.GetFileName(directory);
            if (!ValidCommitId(id)) throw new InvalidDataException($"Unknown recovery material: {directory}");
            var journalPath = _paths.Under("Recovery", "Entries", id, "journal.json");
            if (!_files.Exists(journalPath)) throw new InvalidDataException($"Recovery material lacks journal: {id}");
            var journal = ReadJournal(journalPath, id);
            var newSnapshot = _paths.Under("Recovery", "Entries", id, "new.snapshot.json");
            if (!_files.Exists(newSnapshot) || Hash(_files.ReadAllBytes(newSnapshot)) != journal.NewHash)
                throw new InvalidDataException($"Recovery material lacks committed snapshot: {id}");
            if (journal.OldHash is not null)
            {
                var oldSnapshot = _paths.Under("Recovery", "Entries", id, "old.snapshot.json");
                if (!_files.Exists(oldSnapshot) || Hash(_files.ReadAllBytes(oldSnapshot)) != journal.OldHash)
                    throw new InvalidDataException($"Recovery material lacks previous snapshot: {id}");
            }
            foreach (var op in journal.Ops.Where(o => o.OldPath is not null))
                if (!Verify(BackupPath(id, op.MemberId), op.OldHash, op.OldLength))
                    throw new InvalidDataException($"Recovery entry backup is incomplete: {id}/{op.MemberId}");
            if (journal.RecordUndo && !_files.Exists(_paths.Under("Recovery", "Entries", id, "undo.marker")))
                throw new InvalidDataException($"Undo material lacks marker: {id}");
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
                var material = _paths.Under("Recovery", "Entries", id);
                _paths.GuardTree(material);
                PreserveCommittedMaterial(journal, journalPath);
                _files.DeleteDirectory(stage);
                PruneMaterials(journal);
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
        var newBytes = WallJson.Serialize(recovered);
        var oldBytes = _files.Exists(_paths.Config) ? _files.ReadAllBytes(_paths.Config) : null;
        var commitId = Guid.NewGuid().ToString("N");
        var operations = entries.Select(e => new WallJournalOp(e.MemberId,
                e.CurrentPath, e.PreviousPath, e.CurrentBytes is null ? null : Hash(e.CurrentBytes),
                e.CurrentBytes?.LongLength ?? 0, e.PreviousBytes is null ? null : Hash(e.PreviousBytes),
                e.PreviousBytes?.LongLength ?? 0)).ToList();
        var observed = ObserveUnchanged(previous, operations);
        var journal = new WallJournal(2, commitId, previous.Generation, recovered.Generation,
            oldBytes is null ? null : Hash(oldBytes), Hash(newBytes), false, true, operations, observed);
        var stageDir = _paths.Under("Staging", commitId);
        var materialDir = _paths.Under("Recovery", "Entries", commitId);
        var intent = false;
        var committed = false;
        try
        {
            GuardCommitPaths(journal);
            _files.CreateDirectory(_paths.Staging);
            _files.CreateDirectory(stageDir);
            _files.CreateDirectory(_paths.Under("Recovery"));
            _files.CreateDirectory(_paths.RecoveryEntries);
            _files.CreateDirectory(materialDir);
            var configTemp = _paths.Under("Staging", commitId, "config.tmp");
            _files.WriteAllBytes(configTemp, newBytes);
            foreach (var entry in entries.Where(e => e.PreviousBytes is not null))
                _files.WriteAllBytes(StageEntryPath(commitId, entry.MemberId), entry.PreviousBytes!);
            var journalTemp = _paths.Under("Staging", commitId, "commit.tmp");
            _files.WriteAllBytes(journalTemp, JsonSerializer.SerializeToUtf8Bytes(journal, JournalJson));
            _files.Move(journalTemp, _paths.Under("Staging", commitId, JournalName), false);
            foreach (var entry in entries.Where(e => e.CurrentBytes is not null))
            {
                var backupDir = _paths.Under("Recovery", "Entries", commitId, entry.MemberId);
                _files.CreateDirectory(backupDir);
                _files.Copy(EntryPath(entry.MemberId, entry.CurrentPath!), BackupPath(commitId, entry.MemberId), false);
                var op = journal.Ops.Single(o => o.MemberId == entry.MemberId);
                if (!Verify(BackupPath(commitId, entry.MemberId), op.OldHash, op.OldLength))
                    throw new IOException("Recovery backup verification failed.");
            }
            var markerTemp = _paths.Under("Staging", commitId, "intent.tmp");
            _files.WriteAllBytes(markerTemp, []);
            _files.Move(markerTemp, _paths.Under("Staging", commitId, IntentName), false);
            intent = true;
            Install(journal);
            if (oldBytes is not null)
            {
                var corrupt = _paths.Under("Recovery", $"corrupt-{commitId}.json");
                _paths.Guard(corrupt);
                _files.WriteAllBytes(corrupt, oldBytes);
            }
            var prevTemp = _paths.Under("Recovery", "config.prev.tmp");
            _files.WriteAllBytes(prevTemp, previousBytes);
            _files.Move(prevTemp, _paths.Previous, true);
            _files.Move(configTemp, _paths.Config, true);
            committed = true;
            var cleanup = CleanupAfterCommit(journal);
            return OpenResult(WallOpenStatus.RecoveredPrevious, WallJson.Freeze(recovered),
                cleanup == WallCommitStatus.CommittedNeedsCleanup ? "Recovered; cleanup remains." : null);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            if (committed) return OpenResult(WallOpenStatus.RecoveredPrevious, WallJson.Freeze(recovered),
                $"Recovered; cleanup remains: {ex.Message}");
            try
            {
                if (intent) Rollback(journal);
                else
                {
                    _paths.GuardTree(stageDir);
                    _paths.GuardTree(materialDir);
                    _files.DeleteDirectory(stageDir);
                    _files.DeleteDirectory(materialDir);
                }
            }
            catch (Exception rollback) when (IsStorageError(rollback))
            { return OpenResult(WallOpenStatus.RecoveryBlocked, null, $"{ex.Message}; rollback: {rollback.Message}"); }
            return OpenResult(WallOpenStatus.RecoveryBlocked, null, ex.Message);
        }
    }

    private sealed record RecoveryEntry(string MemberId, string? CurrentPath, string? PreviousPath,
        byte[]? CurrentBytes, byte[]? PreviousBytes);

    private void CheckActiveEntries(WallDocument document)
    {
        foreach (var entry in Members(document).Values.Select(m => m.Entry).OfType<ManagedEntryReference>())
        {
            var path = _paths.Entry(entry);
            _paths.Guard(path);
            if (!_files.GetPathInfo(path).Exists) throw new IOException($"Referenced entry is missing: {path}");
        }
    }

    private List<WallObservedEntry> ObserveUnchanged(WallDocument document, List<WallJournalOp> operations)
    {
        var changed = operations.Select(o => o.MemberId).ToHashSet(StringComparer.Ordinal);
        var result = new List<WallObservedEntry>();
        foreach (var member in Members(document).Values)
        {
            if (member.Entry is null || changed.Contains(member.Id)) continue;
            var path = _paths.Entry(member.Entry);
            var bytes = _files.ReadAllBytes(path);
            result.Add(new WallObservedEntry(member.Id, member.Entry.RelativePath, Hash(bytes), bytes.LongLength));
        }
        return result;
    }

    private void VerifyObserved(WallJournal journal)
    {
        foreach (var observed in journal.Observed)
            if (!Verify(EntryPath(observed.MemberId, observed.RelativePath), observed.Hash, observed.Length))
                throw new IOException($"Unchanged entry differs from journal: {observed.MemberId}");
    }

    private bool HasAnyManagedResidue()
    {
        foreach (var directory in new[] { _paths.Objects, _paths.Staging })
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
            if (!new[] { _paths.Objects, _paths.Staging, _paths.Under("Recovery"),
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
            document = WallJson.Deserialize(_files.ReadAllBytes(path));
            var issues = Validate(document);
            if (issues.Count == 0) { error = ""; return true; }
            error = string.Join("; ", issues);
        }
        catch (Exception ex) when (IsStorageError(ex) || ex is JsonException or ArgumentException)
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
        return TryReadDocument(_paths.Config, out var disk, out _) && disk!.Generation == generation &&
            DocumentEqual(current, disk);
    }

    private static IReadOnlyList<string> Validate(WallDocument document)
    {
        var errors = WallLayoutEngine.Validate(document).ToList();
        if (errors.Count > 0) return errors;
        errors.AddRange(WallLayoutEngine.ValidateGeometry(document));
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lane in document.Lanes)
            foreach (var group in lane.Groups)
            {
                if (!WallStoragePaths.ValidId(group.Id) || !ids.Add(group.Id)) errors.Add("GROUP_ID_PATH");
                foreach (var member in group.Members)
                {
                    if (!WallStoragePaths.ValidId(member.Id) || !ids.Add(member.Id)) errors.Add("MEMBER_ID_PATH");
                    if (member.Entry is not null && !WallStoragePaths.ValidEntry(member.Entry)) errors.Add("ENTRY_PATH");
                    if (member.Entry is not null && !string.IsNullOrEmpty(member.Visual.Title) &&
                        EntryNames.Validate(member.Visual.Title).Count > 0) errors.Add("ENTRY_TITLE");
                }
            }
        return errors;
    }

    private static bool ShapeOkay(WallDocument document) =>
        document.Lanes is not null && document.Lanes.All(l => l?.Groups is not null &&
            l.Groups.All(g => g?.Members is not null && g.Members.All(m => m is not null)));

    private static Dictionary<string, WallMember> Members(WallDocument document) =>
        document.Lanes.SelectMany(l => l.Groups).SelectMany(g => g.Members)
            .ToDictionary(m => m.Id, StringComparer.Ordinal);

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
        }).ToArray()
    };

    private static WallDocument MergeForUndo(WallDocument previous, WallDocument committed, WallDocument current)
    {
        var edited = Members(committed);
        var now = Members(current);
        return previous with
        {
            Settings = current.Settings,
            Lanes = previous.Lanes.Select(l => l with
            {
                Groups = l.Groups.Select(g => g with
                {
                    Members = g.Members.Select(m => m is PuzzleMember p &&
                        edited.GetValueOrDefault(m.Id) is PuzzleMember saved &&
                        now.GetValueOrDefault(m.Id) is PuzzleMember live &&
                        SamePuzzleImageSelection(p, saved)
                        ? p with { Carousel = live.Carousel } : m).ToArray()
                }).ToArray()
            }).ToArray()
        };
    }

    // Only later automatic switches survive undo. A user source/selection edit restores its old carousel too.
    private static bool SamePuzzleImageSelection(PuzzleMember before, PuzzleMember after) =>
        before.Images.Kind == after.Images.Kind &&
        StringComparer.OrdinalIgnoreCase.Equals(before.Images.FolderPath, after.Images.FolderPath) &&
        before.Images.ImagePaths.SequenceEqual(after.Images.ImagePaths, StringComparer.OrdinalIgnoreCase) &&
        StringComparer.OrdinalIgnoreCase.Equals(before.Carousel?.CurrentImageId, after.Carousel?.CurrentImageId);

    private static bool NonUndoOnly(WallDocument current, WallDocument proposed, IReadOnlyList<EntryChange> changes)
    {
        if (changes.Count != 0) return false;
        var now = Members(current);
        var next = Members(proposed);
        if (now.Count != next.Count || now.Keys.Except(next.Keys).Any()) return false;
        var oldNoCarousel = current with { Generation = 0, Settings = new(), Lanes = StripCarousel(current.Lanes) };
        var newNoCarousel = proposed with { Generation = 0, Settings = new(), Lanes = StripCarousel(proposed.Lanes) };
        return DocumentEqual(oldNoCarousel, newNoCarousel);
    }

    private static bool SameLayoutAfterNonUndo(WallDocument committed, WallDocument current)
    {
        var earlier = committed with { Generation = 0, Settings = new(), Lanes = StripCarousel(committed.Lanes) };
        var latest = current with { Generation = 0, Settings = new(), Lanes = StripCarousel(current.Lanes) };
        return DocumentEqual(earlier, latest);
    }

    private static IReadOnlyList<WallLane> StripCarousel(IReadOnlyList<WallLane> lanes) => lanes.Select(l => l with
    {
        Groups = l.Groups.Select(g => g with
        {
            Members = g.Members.Select(m => m is PuzzleMember p ? p with { Carousel = null } : m).ToArray()
        }).ToArray()
    }).ToArray();

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
        var bytes = _files.ReadAllBytes(path);
        using var document = JsonDocument.Parse(bytes);
        RequireFields(document.RootElement,
            ["schemaVersion", "commitId", "oldGeneration", "newGeneration", "oldHash", "newHash",
                "recordUndo", "invalidateUndo", "ops", "observed"]);
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
        if (journal.SchemaVersion != 2 || !ValidCommitId(journal.CommitId) ||
            journal.OldGeneration < 0 || journal.NewGeneration != journal.OldGeneration + 1 ||
            journal.OldHash is not null && !ValidHash(journal.OldHash) ||
            !ValidHash(journal.NewHash) || journal.Ops is null || journal.Observed is null)
            throw new InvalidDataException($"Malformed journal: {journal.CommitId}");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in journal.Ops)
        {
            if (!WallStoragePaths.ValidId(op.MemberId) || !ids.Add(op.MemberId) ||
                op.OldPath is null && op.NewPath is null ||
                op.OldPath is not null && (!ValidHash(op.OldHash) || op.OldLength < 0) ||
                op.NewPath is not null && (!ValidHash(op.NewHash) || op.NewLength < 0))
                throw new InvalidDataException($"Malformed journal operation: {journal.CommitId}");
            if (op.OldPath is not null) _paths.Guard(EntryPath(op.MemberId, op.OldPath));
            if (op.NewPath is not null) _paths.Guard(EntryPath(op.MemberId, op.NewPath));
        }
        foreach (var observed in journal.Observed)
        {
            if (!WallStoragePaths.ValidId(observed.MemberId) || !ids.Add(observed.MemberId) ||
                !ValidHash(observed.Hash) || observed.Length < 0)
                throw new InvalidDataException($"Malformed observed entry: {journal.CommitId}");
            _paths.Guard(EntryPath(observed.MemberId, observed.RelativePath));
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
            ArgumentException or NotSupportedException or System.Security.SecurityException;

    private static WallOpenResult OpenResult(WallOpenStatus status, WallDocument? document, string? diagnostic = null) =>
        new(status, document, diagnostic is null ? [] : [diagnostic]);
    private static WallCommitResult CommitResult(WallCommitStatus status, WallDocument? document,
        WallUndoToken? undo, string? diagnostic = null) =>
        new(status, document, undo, diagnostic is null ? [] : [diagnostic]);

    private sealed record FileClass(bool Present, int? Schema);
    private sealed record WallJournal(int SchemaVersion, string CommitId, long OldGeneration,
        long NewGeneration, string? OldHash, string NewHash, bool RecordUndo, bool InvalidateUndo,
        List<WallJournalOp> Ops, List<WallObservedEntry> Observed);
    private sealed record WallJournalOp(string MemberId, string? OldPath, string? NewPath,
        string? OldHash, long OldLength, string? NewHash, long NewLength);
    private sealed record WallObservedEntry(string MemberId, string RelativePath, string Hash, long Length);
}
