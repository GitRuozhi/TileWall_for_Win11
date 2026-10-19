using System.Reflection;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Storage;

public sealed class JournalPathGuardTests
{
    private sealed class CountingFiles : IFileStore
    {
        internal readonly InMemoryFileStore Memory = new();
        internal readonly List<string> Inspected = [];
        internal int Reads;
        public FileStorePathInfo GetPathInfo(string path) { Inspected.Add(path); return Memory.GetPathInfo(path); }
        public bool Exists(string path) => Memory.Exists(path);
        public byte[] ReadAllBytes(string path) { Reads++; return Memory.ReadAllBytes(path); }
        public void WriteAllBytes(string path, byte[] bytes) => Memory.WriteAllBytes(path, bytes);
        public void Move(string src, string dst, bool overwrite) => Memory.Move(src, dst, overwrite);
        public void Copy(string src, string dst, bool overwrite) => Memory.Copy(src, dst, overwrite);
        public void Delete(string path) => Memory.Delete(path);
        public void CreateDirectory(string path) => Memory.CreateDirectory(path);
        public IReadOnlyList<string> ListDirectories(string path) => Memory.ListDirectories(path);
        public IReadOnlyList<string> ListFiles(string path) => Memory.ListFiles(path);
        public void DeleteDirectory(string path) => Memory.DeleteDirectory(path);
    }

    private static (CountingFiles Files, object Paths, string Root) Harness()
    {
        var files = new CountingFiles();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "journal-guard-memory"));
        var paths = Activator.CreateInstance(typeof(WallStore).Assembly.GetType("TileWall.Core.Storage.WallStoragePaths")!,
            files, root)!;
        return (files, paths, root);
    }

    private static Action<string> Batch(object paths) => (Action<string>)paths.GetType()
        .GetMethod("CreateValidationGuard", BindingFlags.Public | BindingFlags.Instance)!.Invoke(paths, null)!;

    [Fact]
    public void SharedParentsAreInspectedOnceAndBothDistinctLeavesAreInspectedInOrder()
    {
        var (files, paths, root) = Harness();
        var first = Path.Combine(root, "Objects", "a", "a.url");
        var second = Path.Combine(root, "Objects", "b", "b.url");
        var guard = Batch(paths);
        guard(first); guard(second);
        Assert.Equal(1, files.Inspected.Count(path => path == Path.Combine(root, "Objects")));
        Assert.Equal(1, files.Inspected.Count(path => path == first));
        Assert.Equal(1, files.Inspected.Count(path => path == second));
        Assert.True(files.Inspected.IndexOf(first) < files.Inspected.IndexOf(Path.GetDirectoryName(second)!));
        Assert.Equal(files.Inspected.Count, files.Inspected.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void DistinctLeafOrParentReparseAndFileParentsRemainRejected(bool leaf, bool fileParent)
    {
        var (files, paths, root) = Harness();
        var second = Path.Combine(root, "Objects", "b", "b.url");
        var blocked = leaf ? second : Path.GetDirectoryName(second)!;
        if (fileParent) files.Memory.WriteAllBytes(blocked, [1]);
        else files.Memory.ReparsePaths.Add(blocked);
        var guard = Batch(paths);
        guard(Path.Combine(root, "Objects", "a", "a.url"));
        Assert.Throws<IOException>(() => guard(second));
        Assert.Contains(blocked, files.Inspected);
    }

    [Fact]
    public void ANewValidationRechecksSharedParents()
    {
        var (files, paths, root) = Harness();
        var entry = Path.Combine(root, "Objects", "a", "a.url");
        Batch(paths)(entry);
        var parent = Path.Combine(root, "Objects");
        files.Memory.ReparsePaths.Add(parent);
        Assert.Throws<IOException>(() => Batch(paths)(entry));
        Assert.Equal(2, files.Inspected.Count(path => path == parent));
    }

    [Fact]
    public void CachedLeafThatLaterBecomesAParentStillRejectsAFile()
    {
        var (files, paths, root) = Harness();
        var file = Path.Combine(root, "Objects", "a");
        files.Memory.WriteAllBytes(file, [1]);
        var guard = Batch(paths);
        guard(file);
        Assert.Throws<IOException>(() => guard(Path.Combine(file, "a.url")));
        Assert.Equal(1, files.Inspected.Count(path => path == file));
    }

    [Fact]
    public void FileAndLinkIoRecheckAfterBatchEvenWhenItsParentWasCached()
    {
        var (files, paths, root) = Harness();
        var entry = Path.Combine(root, "Objects", "a", "a.url");
        files.Memory.WriteAllBytes(entry, [1]);
        Batch(paths)(entry);
        files.Memory.ReparsePaths.Add(Path.Combine(root, "Objects"));
        var assembly = typeof(WallStore).Assembly;
        var guardedFiles = (IFileStore)Activator.CreateInstance(assembly.GetType("TileWall.Core.Storage.GuardedWallFiles")!, files, paths)!;
        var guardedLinks = (ILnkFileService)Activator.CreateInstance(assembly.GetType("TileWall.Core.Storage.GuardedWallLinks")!,
            new FakeLinkFileService(files), paths)!;
        Assert.Throws<IOException>(() => guardedFiles.ReadAllBytes(entry));
        Assert.Throws<IOException>(() => guardedLinks.Read(entry));
        Assert.Equal(0, files.Reads);
    }
}
