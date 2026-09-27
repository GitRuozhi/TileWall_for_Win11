using TileWall.Core.Configuration;
using TileWall.Core.Import;
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
        return StartMenuScanner.Enumerate(new ScanFiles(token), roots);
    }, token);

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
