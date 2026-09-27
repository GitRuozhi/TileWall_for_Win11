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
        if (InjectedFault == FaultPoint.BeforeMove && dst.EndsWith(Path.DirectorySeparatorChar + "config.json", StringComparison.OrdinalIgnoreCase))
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
