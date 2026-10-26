using TileWall.Core.Configuration;

namespace TileWall.Core.Tests.Fixtures;

/// <summary>Current WallStore fault points. Each staged-entry fault fires once so rollback can run.</summary>
public enum EntryFaultPoint { None, BeforeEntrySwap, BeforeEntryBackup, BeforeStagingCleanup }
public enum FaultPoint { None, BeforeMove }

public sealed class InMemoryFileStore : IFileStore
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private EntryFaultPoint _entryFault;
    private static readonly string Staging = Path.DirectorySeparatorChar + "Staging" + Path.DirectorySeparatorChar;
    private static readonly string Objects = Path.DirectorySeparatorChar + "Objects" + Path.DirectorySeparatorChar;
    private static readonly string RecoveryEntries = Path.DirectorySeparatorChar + "Recovery" +
        Path.DirectorySeparatorChar + "Entries" + Path.DirectorySeparatorChar;

    public HashSet<string> ReparsePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public FaultPoint InjectedFault { get; set; }
    public EntryFaultPoint EntryFault { get => _entryFault; set => _entryFault = value; }
    public IReadOnlyList<string> FilePaths => [.. _files.Keys.Order(StringComparer.Ordinal)];

    public FileStorePathInfo GetPathInfo(string path) => new(
        _files.ContainsKey(path) || _directories.Contains(path) || ReparsePaths.Contains(path),
        _directories.Contains(path) || ReparsePaths.Contains(path), ReparsePaths.Contains(path));

    public bool Exists(string path) => _files.ContainsKey(path);
    public byte[] ReadAllBytes(string path) => _files.TryGetValue(path, out var bytes)
        ? [.. bytes] : throw new FileNotFoundException($"[memory store] Missing file: {path}", path);
    public void WriteAllBytes(string path, byte[] bytes) => _files[path] = [.. bytes];

    public void Move(string src, string dst, bool overwrite)
    {
        if (InjectedFault == FaultPoint.BeforeMove && dst.EndsWith(Path.DirectorySeparatorChar + "state.json", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Injected config replacement failure.");
        Trip(EntryFaultPoint.BeforeEntrySwap,
            src.Contains(Staging, StringComparison.OrdinalIgnoreCase) && dst.Contains(Objects, StringComparison.OrdinalIgnoreCase));
        if (!_files.TryGetValue(src, out var bytes)) throw new FileNotFoundException($"[memory store] Missing source: {src}", src);
        if (!overwrite && _files.ContainsKey(dst)) throw new IOException($"[memory store] Existing destination: {dst}");
        _files.Remove(src);
        _files[dst] = bytes;
    }

    public void Copy(string src, string dst, bool overwrite)
    {
        Trip(EntryFaultPoint.BeforeEntryBackup, dst.Contains(RecoveryEntries, StringComparison.OrdinalIgnoreCase));
        if (!_files.TryGetValue(src, out var bytes)) throw new FileNotFoundException($"[memory store] Missing source: {src}", src);
        if (!overwrite && _files.ContainsKey(dst)) throw new IOException($"[memory store] Existing destination: {dst}");
        _files[dst] = [.. bytes];
    }

    public void MoveDirectory(string source, string destination)
    {
        if (GetPathInfo(destination).Exists || _files.Keys.Any(path => path.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            throw new IOException("Existing directory destination.");
        if (!GetPathInfo(source).Exists) throw new DirectoryNotFoundException(source);
        var prefix = source + Path.DirectorySeparatorChar;
        foreach (var path in _files.Keys.Where(path => path.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            _files[destination + path[source.Length..]] = _files[path]; _files.Remove(path);
        }
        foreach (var path in _directories.Where(path => path == source || path.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            _directories.Remove(path); _directories.Add(destination + path[source.Length..]);
        }
    }

    public void Delete(string path) => _files.Remove(path);
    public void CreateDirectory(string path) => _directories.Add(path);

    public IReadOnlyList<string> ListDirectories(string directoryPath)
    {
        var prefix = directoryPath + Path.DirectorySeparatorChar;
        return [.. _directories.Where(d => d.StartsWith(prefix, StringComparison.Ordinal))
            .Where(d => !d[prefix.Length..].Contains(Path.DirectorySeparatorChar)).Order(StringComparer.Ordinal)];
    }

    public IReadOnlyList<string> ListFiles(string directoryPath)
    {
        var prefix = directoryPath + Path.DirectorySeparatorChar;
        return [.. _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Where(k => !k[prefix.Length..].Contains(Path.DirectorySeparatorChar)).Order(StringComparer.Ordinal)];
    }

    public void DeleteDirectory(string directoryPath)
    {
        Trip(EntryFaultPoint.BeforeStagingCleanup, directoryPath.Contains(Staging, StringComparison.OrdinalIgnoreCase));
        var prefix = directoryPath + Path.DirectorySeparatorChar;
        foreach (var path in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray()) _files.Remove(path);
        _directories.Remove(directoryPath);
        _directories.RemoveWhere(d => d.StartsWith(prefix, StringComparison.Ordinal));
    }

    public bool DirectoryCreated(string path) => _directories.Contains(path);
    public Dictionary<string, byte[]> SnapshotFiles() =>
        _files.ToDictionary(pair => pair.Key, pair => (byte[])pair.Value.Clone(), StringComparer.Ordinal);

    private void Trip(EntryFaultPoint point, bool condition)
    {
        if (!condition || _entryFault != point) return;
        _entryFault = EntryFaultPoint.None;
        throw new IOException($"Injected {point} failure.");
    }
}

/// <summary>Explicit complete-snapshot fixtures; never emulates the removed config.json format.</summary>
internal static class SnapshotTestData
{
    public static byte[] Install(IFileStore files, string root, TileWall.Core.Wall.WallDocument document,
        string? manifestPath = null)
    {
        var snapshot = TileWall.Core.Storage.UserDataSnapshotJson.Prepare(document);
        var directory = Path.Combine(root, "Snapshots", snapshot.Manifest.SnapshotId);
        files.CreateDirectory(root);
        files.CreateDirectory(Path.Combine(root, "Snapshots"));
        files.CreateDirectory(directory);
        foreach (var (name, bytes) in snapshot.Files) files.WriteAllBytes(Path.Combine(directory, name), bytes);
        files.WriteAllBytes(manifestPath ?? Path.Combine(root, "state.json"), snapshot.ManifestBytes);
        return snapshot.ManifestBytes;
    }

    public static TileWall.Core.Wall.WallDocument Read(IFileStore files, string root, byte[] manifestBytes) =>
        TileWall.Core.Storage.UserDataSnapshotJson.Read(TileWall.Core.Storage.UserDataSnapshotJson.ReadManifest(manifestBytes),
            name => files.ReadAllBytes(Path.Combine(root, "Snapshots",
                TileWall.Core.Storage.UserDataSnapshotJson.ReadManifest(manifestBytes).SnapshotId, name)),
            reference => Path.Combine(root, "Sources", reference.ContentId));
}
