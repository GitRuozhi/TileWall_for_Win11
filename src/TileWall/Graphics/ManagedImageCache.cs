using System.Security.Cryptography;
using System.Text;

namespace TileWall.Graphics;

/// <summary>Actual IO guard and bounded eviction for the image cache subtrees only.</summary>
internal sealed class ManagedImageCache(string root)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _leases = new(StringComparer.OrdinalIgnoreCase);
    internal string Root { get; } = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
    internal string Under(params string[] segments)
    {
        if (segments.Any(s => string.IsNullOrWhiteSpace(s) || s is "." or ".." || s.IndexOfAny(['/', '\\', ':', '\0']) >= 0))
            throw new IOException("Invalid image cache path segment.");
        var result = Path.GetFullPath(Path.Combine([Root, .. segments]));
        RequireUnder(result); return result;
    }
    internal void Guard(string path)
    {
        RequireUnder(path);
        var cursor = Path.GetPathRoot(path)!;
        foreach (var part in path[cursor.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, part);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(cursor); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse point in image cache path.");
            if (cursor != path && (attributes & FileAttributes.Directory) == 0) throw new IOException("File in image cache directory path.");
        }
    }
    private void RequireUnder(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(path, Root, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Image cache path escapes its data root.");
    }
    internal void CreateDirectory(string path)
    {
        Guard(path); Directory.CreateDirectory(path); Guard(path);
    }
    internal async Task<string> StageAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var directory = Under("Cache", "Images", "Temp"); CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp"); Guard(path);
        try { await File.WriteAllBytesAsync(path, bytes.ToArray(), ct).ConfigureAwait(false); }
        catch { Delete(path); throw; }
        Guard(path); return path;
    }
    internal void Publish(string staged, string destination)
    {
        Guard(staged); CreateDirectory(Path.GetDirectoryName(destination)!); Guard(destination);
        File.Move(staged, destination, overwrite: true); Guard(destination);
    }
    internal void Delete(string path)
    {
        Guard(path); if (File.Exists(path)) File.Delete(path);
    }
    internal byte[] Read(string path, int maximumBytes)
    {
        Guard(path); var info = new FileInfo(path);
        if (!info.Exists || info.Length < 1 || info.Length > maximumBytes) throw new IOException("Missing or oversized image cache file.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > maximumBytes) throw new IOException("Image cache file grew while being read.");
        return bytes;
    }
    internal IDisposable Lease(string path)
    {
        lock (_gate)
        {
            Guard(path); _leases[path] = _leases.GetValueOrDefault(path) + 1;
            try
            {
                if (File.Exists(path)) File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
                else if (Directory.Exists(path)) Directory.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            }
            catch (IOException) { }
        }
        return new ReadLease(this, path);
    }
    private sealed class ReadLease(ManagedImageCache cache, string path) : IDisposable
    {
        private bool _released;
        public void Dispose()
        {
            lock (cache._gate)
            {
                if (_released) return; _released = true;
                if (cache._leases[path] <= 1) cache._leases.Remove(path); else cache._leases[path]--;
            }
        }
    }
    internal void Evict(string directory, long limit, string pattern)
    {
        lock (_gate)
        {
            var files = SafeFiles(directory).Where(path => path.EndsWith(pattern, StringComparison.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path)).OrderBy(info => info.LastAccessTimeUtc).ToArray();
            var bytes = files.Sum(info => info.Length);
            foreach (var file in files)
            {
                if (bytes <= limit) break;
                if (_leases.ContainsKey(file.FullName)) continue;
                try { Guard(file.FullName); var length = file.Length; File.Delete(file.FullName); bytes -= length; }
                catch (IOException) { }
            }
        }
    }

    // Native icon frames and their manifest are one eviction/publication unit.
    // The single writer reserves the complete package (including its mapping)
    // before staging, while readers lease the package directory.
    internal bool PublishIconPackage(string key, IReadOnlyDictionary<string, byte[]> payload,
        string mappingKey, byte[] mapping, long limit, CancellationToken token)
    {
        if (!IsKey(key) || !IsKey(mappingKey) || payload.Count is < 2 or > 65 ||
            !payload.ContainsKey("manifest.json") || payload.Any(pair =>
                pair.Value.Length == 0 || pair.Key != "manifest.json" &&
                !(pair.Key.EndsWith(".ico", StringComparison.Ordinal) && IsKey(pair.Key[..^4]) ||
                  pair.Key.EndsWith(".pixels", StringComparison.Ordinal) && IsKey(pair.Key[..^7]))))
            throw new IOException("Invalid native icon package.");
        var required = checked(payload.Sum(pair => (long)pair.Value.Length) + mapping.LongLength);
        if (required > limit || required > 33L * 1024 * 1024) return false;
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            var root = Under("Cache", "Images", "Icons");
            var packages = Path.Combine(root, "Packages");
            var mappings = Path.Combine(root, "Mappings");
            var destination = Path.Combine(packages, key);
            var mappingPath = Path.Combine(mappings, mappingKey + ".json");
            Guard(destination); Guard(mappingPath);
            var exists = Directory.Exists(destination);
            // An immutable key is reusable only after its whole payload matches.
            // Otherwise repeatedly publishing the same key would preserve a bad
            // plane forever even after the producer successfully rebuilt it.
            if (exists && !IconPackageMatches(destination, payload, token))
            {
                if (IsLeased(destination)) return false;
                DeleteIconTree(destination); exists = false;
            }
            var previousMappingBytes = File.Exists(mappingPath) ? new FileInfo(mappingPath).Length : 0;
            var additional = checked((exists ? 0 : payload.Sum(pair => (long)pair.Value.Length)) + mapping.LongLength - previousMappingBytes);
            if (!MakeIconSpace(root, packages, mappings, additional, limit, destination, mappingPath)) return false;
            var staging = Path.Combine(root, "Staging", Guid.NewGuid().ToString("N"));
            try
            {
                CreateDirectory(staging);
                if (!exists)
                {
                    var packageStage = Path.Combine(staging, "package"); CreateDirectory(packageStage);
                    foreach (var pair in payload)
                    {
                        token.ThrowIfCancellationRequested();
                        var path = Path.Combine(packageStage, pair.Key); Guard(path);
                        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        output.Write(pair.Value);
                    }
                    token.ThrowIfCancellationRequested();
                    CreateDirectory(packages); Guard(packageStage); Guard(destination);
                    Directory.Move(packageStage, destination);
                }
                token.ThrowIfCancellationRequested();
                var mapStage = Path.Combine(staging, "mapping.json"); Guard(mapStage);
                File.WriteAllBytes(mapStage, mapping); Publish(mapStage, mappingPath);
                Guard(destination); Directory.SetLastAccessTimeUtc(destination, DateTime.UtcNow);
                return true;
            }
            finally { DeleteIconTree(staging); }
        }
    }

    private bool IconPackageMatches(string directory, IReadOnlyDictionary<string, byte[]> payload, CancellationToken token)
    {
        try
        {
            if (Directory.EnumerateDirectories(directory).Any() || SafeFiles(directory).Count() != payload.Count) return false;
            foreach (var (name, expected) in payload)
            {
                token.ThrowIfCancellationRequested();
                if (!Read(Path.Combine(directory, name), expected.Length).AsSpan().SequenceEqual(expected)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal void DiscardIconPackage(string key)
    {
        if (!IsKey(key)) return;
        lock (_gate)
        {
            var directory = Under("Cache", "Images", "Icons", "Packages", key);
            if (!IsLeased(directory)) DeleteIconTree(directory);
        }
    }

    private bool MakeIconSpace(string root, string packages, string mappings, long additional, long limit, string keep, string keepMapping)
    {
        var files = SafeFiles(root).Select(path => new FileInfo(path)).ToArray();
        var bytes = files.Sum(file => file.Length);
        if (bytes + additional <= limit) return true;
        var candidates = new List<(string Path, DateTime Used, bool Directory)>();
        Guard(mappings);
        if (Directory.Exists(mappings))
            foreach (var path in Directory.EnumerateFiles(mappings, "*.json"))
            {
                Guard(path);
                if (!path.Equals(keepMapping, StringComparison.OrdinalIgnoreCase) && IsKey(Path.GetFileNameWithoutExtension(path)))
                    candidates.Add((path, File.GetLastAccessTimeUtc(path), false));
            }
        Guard(packages);
        if (Directory.Exists(packages))
            foreach (var path in Directory.EnumerateDirectories(packages))
            {
                Guard(path);
                if (path != keep && IsKey(Path.GetFileName(path))) candidates.Add((path, Directory.GetLastAccessTimeUtc(path), true));
            }
        var stages = Path.Combine(root, "Staging"); Guard(stages);
        if (Directory.Exists(stages))
            foreach (var path in Directory.EnumerateDirectories(stages))
            {
                Guard(path);
                if (Guid.TryParseExact(Path.GetFileName(path), "N", out _)) candidates.Add((path, DateTime.MinValue, true));
            }
        foreach (var candidate in candidates.OrderBy(item => item.Used))
        {
            if (bytes + additional <= limit) return true;
            if (IsLeased(candidate.Path)) continue;
            try
            {
                Guard(candidate.Path);
                var length = candidate.Directory ? SafeFiles(candidate.Path).Sum(path => new FileInfo(path).Length) : new FileInfo(candidate.Path).Length;
                if (candidate.Directory) DeleteIconTree(candidate.Path); else Delete(candidate.Path);
                bytes -= length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return bytes + additional <= limit;
    }

    private bool IsLeased(string path) => _leases.Keys.Any(lease =>
        lease.Equals(path, StringComparison.OrdinalIgnoreCase) ||
        lease.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(lease + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private void DeleteIconTree(string path)
    {
        Guard(path); if (!Directory.Exists(path)) return;
        foreach (var file in SafeFiles(path).ToArray()) { Guard(file); File.Delete(file); }
        foreach (var child in Directory.EnumerateDirectories(path).ToArray()) DeleteIconTree(child);
        Guard(path); Directory.Delete(path, recursive: false);
    }

    private static bool IsKey(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private IEnumerable<string> SafeFiles(string directory)
    {
        Guard(directory); if (!Directory.Exists(directory)) yield break;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            Guard(entry);
            if (Directory.Exists(entry)) { foreach (var path in SafeFiles(entry)) yield return path; }
            else yield return entry;
        }
    }
    internal static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
