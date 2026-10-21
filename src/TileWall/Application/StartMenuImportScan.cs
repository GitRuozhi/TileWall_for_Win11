using TileWall.Core.Configuration;
using TileWall.Core.Import;
using System.Security.Cryptography;
using TileWall.Shell.Interop;

namespace TileWall.ApplicationModel;

internal static class StartMenuImportScan
{
    public static Task<StartMenuScanResult> RunAsync(CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var known = new KnownFolderPaths();
        var roots = new List<(string Path, SourceKind Source)>();
        if (known.UserPrograms is { } user) roots.Add((user, SourceKind.User));
        if (known.CommonPrograms is { } common) roots.Add((common, SourceKind.Common));
        var result = StartMenuScanner.Enumerate(new ScanFiles(token), roots);
        token.ThrowIfCancellationRequested();
        // A failed known-folder lookup must never authorize removing that root's old imported members.
        return result with
        {
            SkippedDirectories = result.SkippedDirectories + 2 - roots.Count,
            Roots = result.Roots.Concat(Enum.GetValues<SourceKind>().Where(source => roots.All(root => root.Source != source))
                .Select(source => new StartMenuRootScan(source, null, false, "RootUnavailable"))).ToArray(),
        };
    }, token);

    internal static (byte[] Bytes, string Sha256) ReadSource(StartMenuCandidate candidate, CancellationToken token)
    {
        ValidateSourcePath(candidate, token);
        using var stream = new FileStream(candidate.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.SequentialScan);
        if (stream.Length > StartMenuSourceCache.MaximumSourceBytes) throw new IOException("SourceTooLarge");
        using var bytes = new MemoryStream((int)stream.Length);
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            token.ThrowIfCancellationRequested();
            if (bytes.Length + read > StartMenuSourceCache.MaximumSourceBytes) throw new IOException("SourceTooLarge");
            bytes.Write(buffer, 0, read);
        }
        token.ThrowIfCancellationRequested();
        ValidateSourcePath(candidate, token);
        var content = bytes.ToArray();
        return (content, Convert.ToHexString(SHA256.HashData(content)));
    }

    internal static void ValidateSourcePath(StartMenuCandidate candidate, CancellationToken token)
    {
        var root = candidate.RootPath ?? throw new IOException("UnsafeSource");
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(candidate.FullPath) ||
            !StartMenuSourceCache.Within(root, candidate.FullPath) ||
            candidate.RelativePath.Split('\\', '/').Any(segment => segment is "" or "." or ".." || segment.Contains(':')))
            throw new IOException("UnsafeSource");
        var current = Path.GetFullPath(root);
        Check(current, directory: true);
        var segments = candidate.RelativePath.Split('\\', '/');
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            Check(current, directory: index != segments.Length - 1);
        }
        return;

        void Check(string path, bool directory)
        {
            token.ThrowIfCancellationRequested();
            var info = new LocalFileStore().GetPathInfo(path);
            if (!info.Exists) throw new FileNotFoundException("SourceMissing", path);
            if (info.IsReparsePoint || info.IsDirectory != directory) throw new IOException("UnsafeSource");
        }
    }

    // Cancellation is checked at every scanner IO boundary. The adapter cannot write or read shortcut contents.
    private sealed class ScanFiles(CancellationToken token) : IFileStore
    {
        private readonly LocalFileStore _files = new();
        public FileStorePathInfo GetPathInfo(string path) { token.ThrowIfCancellationRequested(); return _files.GetPathInfo(path); }
        public IReadOnlyList<string> ListDirectories(string path) { token.ThrowIfCancellationRequested(); return _files.ListDirectories(path); }
        public IReadOnlyList<string> ListFiles(string path) { token.ThrowIfCancellationRequested(); return _files.ListFiles(path); }
        public bool Exists(string path) => throw new NotSupportedException();
        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();
        public void WriteAllBytes(string path, byte[] bytes) => throw new NotSupportedException();
        public void Move(string src, string dst, bool overwrite) => throw new NotSupportedException();
        public void Copy(string src, string dst, bool overwrite) => throw new NotSupportedException();
        public void Delete(string path) => throw new NotSupportedException();
        public void CreateDirectory(string path) => throw new NotSupportedException();
        public void DeleteDirectory(string path) => throw new NotSupportedException();
    }
}
