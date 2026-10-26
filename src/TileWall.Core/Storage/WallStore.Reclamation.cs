using System.Text.Json;
using TileWall.Core.Wall;

namespace TileWall.Core.Storage;

public sealed partial class WallStore
{
    private readonly object _resourceGate = new();
    private readonly Dictionary<string, int> _resourceLeases = new(StringComparer.Ordinal);
    private string? _lastReclamationState;

    /// <summary>Keep a formal original/source alive until its asynchronous reader has drained.</summary>
    public IDisposable AcquireResourceReadLease(ManagedGalleryReference? gallery = null, ManagedMediaReference? media = null)
    {
        if (gallery is not null && !PreparedManagedGallery.IsValidReference(gallery) ||
            media is not null && !PreparedMediaAsset.IsValidReference(media)) throw new ArgumentException("Invalid resource lease.");
        var keys = new List<string>();
        if (gallery is not null) keys.Add("source:" + gallery.ContentId);
        if (media is not null) keys.Add("media:" + media.Sha256);
        return AcquireResourceLeases(keys);
    }

    private IDisposable AcquireResourceLeases(IEnumerable<string> resources)
    {
        var keys = resources.Distinct(StringComparer.Ordinal).ToArray();
        lock (_resourceGate)
            foreach (var key in keys) _resourceLeases[key] = _resourceLeases.GetValueOrDefault(key) + 1;
        return new ResourceLease(() =>
        {
            lock (_resourceGate)
            {
                foreach (var key in keys)
                    if (_resourceLeases[key] == 1) _resourceLeases.Remove(key); else _resourceLeases[key]--;
                // A prior sweep may have intentionally retained this lease's sole remaining references.
                _lastReclamationState = null;
            }
        });
    }

    private sealed class ResourceLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private sealed class ProtectedResources : IDisposable
    {
        internal HashSet<string> Objects { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> Media { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Sources { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Snapshots { get; } = new(StringComparer.Ordinal);
        internal List<Stream> Handles { get; } = [];
        internal string CurrentFingerprint { get; set; } = "";
        internal Action? ReleaseGate { get; init; }
        public void Dispose()
        {
            foreach (var stream in Handles) stream.Dispose();
            ReleaseGate?.Invoke();
        }
    }

    private ProtectedResources CaptureProtectedResourceSet(string? excludedCommitId = null)
    {
        Monitor.Enter(_resourceGate);
        var result = new ProtectedResources { ReleaseGate = () => Monitor.Exit(_resourceGate) };
        try
        {
            var snapshots = new Dictionary<string, UserDataManifest>(StringComparer.Ordinal);
            byte[] Read(string path)
            {
                var stream = _files.OpenReadProtected(path); result.Handles.Add(stream);
                if (stream.Length is < 1 or > 64 * 1024 * 1024) throw new InvalidDataException("Invalid protected material size.");
                var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); stream.Position = 0; return bytes;
            }
            void ProtectDocument(WallDocument document)
            {
                foreach (var member in document.EnumerateAllMembers())
                {
                    result.Objects.Add(member.Id);
                    if (member.Entry is { } entry) result.Objects.Add(entry.ObjectId);
                }
                result.Media.UnionWith(MediaById(document).Keys);
                foreach (var gallery in GalleryReferences(document))
                {
                    if (result.Sources.Add(gallery.ContentId))
                        _ = CheckGallery(gallery, protectedHandles: result.Handles);
                }
            }
            void ProtectManifest(string path, bool required = false)
            {
                if (!_files.Exists(path))
                {
                    if (required) throw new InvalidDataException("A necessary protection manifest is missing.");
                    return;
                }
                var bytes = Read(path);
                if (path == _paths.Config) result.CurrentFingerprint = Hash(bytes);
                var manifest = UserDataSnapshotJson.ReadManifest(bytes);
                result.Snapshots.Add(manifest.SnapshotId);
                if (snapshots.TryGetValue(manifest.SnapshotId, out var prior))
                {
                    if (prior != manifest) throw new InvalidDataException("Conflicting manifests for one immutable snapshot.");
                    return;
                }
                var directory = _paths.Under("Snapshots", manifest.SnapshotId);
                var document = ReadSnapshotAt(manifest, directory, Read);
                snapshots.Add(manifest.SnapshotId, manifest);
                ProtectDocument(document);
            }
            ProtectManifest(_paths.Config, required: true);
            ProtectManifest(_paths.Previous);
            foreach (var directory in _files.ListDirectories(_paths.RecoveryEntries))
            {
                var id = Path.GetFileName(directory);
                if (id == excludedCommitId) continue;
                if (!ValidCommitId(id)) throw new InvalidDataException("Unknown recovery material prevents reclamation.");
                var journalPath = Path.Combine(directory, "journal.json");
                var journal = ReadJournal(Read(journalPath), id);
                ProtectManifest(Path.Combine(directory, "new.snapshot.json"), required: true);
                if (journal.OldHash is not null) ProtectManifest(Path.Combine(directory, "old.snapshot.json"), required: true);
                result.Objects.UnionWith(journal.Ops.Select(op => op.MemberId));
                result.Objects.UnionWith(journal.Observed.Select(op => op.MemberId));
                result.Media.UnionWith(journal.Media.Select(op => op.Asset.Sha256));
                result.Sources.UnionWith(journal.Galleries.Select(op => op.Reference.ContentId));
            }
            foreach (var directory in _files.ListDirectories(_paths.Staging))
            {
                var id = Path.GetFileName(directory);
                if (id == excludedCommitId) continue;
                if (!ValidCommitId(id)) throw new InvalidDataException("Unknown pending material prevents reclamation.");
                var journalPath = Path.Combine(directory, JournalName);
                var journal = ReadJournal(Read(journalPath), id);
                result.Objects.UnionWith(journal.Ops.Select(op => op.MemberId));
                result.Objects.UnionWith(journal.Observed.Select(op => op.MemberId));
                result.Media.UnionWith(journal.Media.Select(op => op.Asset.Sha256));
                result.Sources.UnionWith(journal.Galleries.Select(op => op.Reference.ContentId));
                var pendingManifest = Path.Combine(directory, "state.tmp");
                if (_files.Exists(pendingManifest))
                {
                    var manifest = UserDataSnapshotJson.ReadManifest(Read(pendingManifest));
                    result.Snapshots.Add(manifest.SnapshotId);
                    var formal = _paths.Under("Snapshots", manifest.SnapshotId);
                    var snapshotDirectory = _files.GetPathInfo(formal).Exists ? formal : Path.Combine(directory, "snapshot");
                    var document = UserDataSnapshotJson.Read(manifest, name => Read(Path.Combine(snapshotDirectory, name)), _paths.Gallery);
                    // Pending newly staged galleries may not be formally installed yet; their journal roots are protected above.
                    result.Objects.UnionWith(document.EnumerateAllMembers().Select(member => member.Id));
                    result.Media.UnionWith(MediaById(document).Keys);
                    result.Sources.UnionWith(GalleryReferences(document).Select(gallery => gallery.ContentId));
                }
            }
            foreach (var key in _resourceLeases.Keys)
            {
                if (key.StartsWith("source:", StringComparison.Ordinal)) result.Sources.Add(key[7..]);
                else if (key.StartsWith("media:", StringComparison.Ordinal)) result.Media.Add(key[6..]);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    /// <summary>Independent best-effort GC. It can never revoke a successful commit or block a healthy wall.</summary>
    private void TryReclaimUnreferencedResources(string? expectedFingerprint = null)
    {
        try
        {
            lock (_resourceGate)
                if (expectedFingerprint is not null && _lastReclamationState == expectedFingerprint) return;
            using var protection = CaptureProtectedResourceSet();
            var fingerprint = protection.CurrentFingerprint;
            if (_lastReclamationState == fingerprint) return;
            var candidates = new List<(string Kind, string Directory)>();
            void Collect(string root, string kind, HashSet<string> protectedIds)
            {
                if (_files.ListFiles(root).Count != 0) throw new InvalidDataException("Unknown files in a formal resource root.");
                foreach (var directory in _files.ListDirectories(root))
                {
                    var id = Path.GetFileName(directory);
                    if (!protectedIds.Contains(id)) candidates.Add((kind, directory));
                }
            }
            Collect(_paths.Objects, "object", protection.Objects);
            Collect(_paths.MediaRoot, "media", protection.Media);
            Collect(_paths.Sources, "source", protection.Sources);
            Collect(_paths.Snapshots, "snapshot", protection.Snapshots);
            // Establish ownership for the whole batch before deleting anything.
            foreach (var candidate in candidates) ValidateReclamationCandidate(candidate.Kind, candidate.Directory);
            foreach (var candidate in candidates)
            {
                ValidateReclamationCandidate(candidate.Kind, candidate.Directory);
                _files.DeleteDirectory(candidate.Directory);
            }
            _lastReclamationState = fingerprint;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            try { TileWall.Core.Shell.AppLog.Warn("ResourceReclamation", ex.Message); } catch { }
        }
    }

    private void ValidateReclamationCandidate(string kind, string directory)
    {
        _paths.GuardTree(directory);
        var id = Path.GetFileName(directory);
        if (!_files.GetPathInfo(directory).IsDirectory || _files.ListDirectories(directory).Count != 0)
            throw new InvalidDataException("Unknown resource structure prevents reclamation.");
        var files = _files.ListFiles(directory);
        if (kind == "object")
        {
            if (!WallStoragePaths.ValidId(id) || files.Any(path => !Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Unknown object ownership prevents reclamation.");
        }
        else if (kind == "media")
        {
            if (!UserDataSnapshotJson.ValidHash(id) || files.Count != 1 ||
                Path.GetFileNameWithoutExtension(files[0]) != "original") throw new InvalidDataException("Unknown media ownership.");
            var bytes = _files.ReadAllBytes(files[0]);
            var reference = new ManagedMediaReference(id, Path.GetExtension(files[0]), bytes.LongLength);
            if (!PreparedMediaAsset.IsValidReference(reference) || Hash(bytes) != id) throw new InvalidDataException("Invalid orphan media.");
        }
        else if (kind == "source") _ = CheckGallery(new(id, id), directory);
        else
        {
            if (!UserDataSnapshotJson.ValidHash(id) || files.Select(Path.GetFileName).Any(name => !SnapshotFileNames.Contains(name, StringComparer.Ordinal)))
                throw new InvalidDataException("Unknown snapshot ownership.");
            foreach (var file in files)
            {
                using var json = JsonDocument.Parse(_files.ReadAllBytes(file));
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("schemaVersion", out var version) ||
                    version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema) || schema != 1)
                    throw new InvalidDataException("Unsupported orphan snapshot.");
            }
        }
    }
}
