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
            try { if (File.Exists(path)) File.SetLastAccessTimeUtc(path, DateTime.UtcNow); }
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
