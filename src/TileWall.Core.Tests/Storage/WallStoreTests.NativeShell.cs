using TileWall.Core.Entries;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Storage;

public sealed partial class WallStoreTests
{
    private const string ComputerShell = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    private const string RecycleShell = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    [Fact]
    public void NativeShellBatchCommitsVerifiedIdentitiesTogetherAndReopens()
    {
        var (store, files, links, root) = Harness();
        var current = Wall();
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(current).Status);
        var candidate = Wall(Tile("computer", "Computer"), Tile("recycle", "Recycle")) with { Generation = 1, Rows = 12 };
        var request = Plan(store, current, candidate,
            new EntryChange.CreateShellLink("computer", ComputerShell, LaunchIdentityService.ForShell(ComputerShell).Key),
            new EntryChange.CreateShellLink("recycle", RecycleShell, LaunchIdentityService.ForShell(RecycleShell).Key));

        var committed = store.Commit(current, request);

        Assert.Equal(WallCommitStatus.Committed, committed.Status);
        Assert.Equal(1, committed.CommittedDocument!.Generation);
        var identities = new LaunchIdentityService(files, links);
        foreach (var (id, expected) in new[] { ("computer", ComputerShell), ("recycle", RecycleShell) })
        {
            var member = committed.CommittedDocument.EnumerateAllMembers().Single(member => member.Id == id);
            Assert.Equal(ManagedEntryKind.Lnk, member.Entry!.Kind);
            var path = EntryPath(root, member.Entry);
            Assert.True(links.StateOf(path).HasIdList);
            Assert.Equal(expected, links.StateOf(path).ShellParsingName);
            Assert.Equal(LaunchIdentityService.ForShell(expected).Key,
                EntryObservation.Observe(identities, ManagedEntryKind.Lnk, path, files.ReadAllBytes(path)).Key);
        }
        var reopened = store.Open();
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.Equal(WallJson.Serialize(committed.CommittedDocument), WallJson.Serialize(reopened.Document!));
        Assert.Equal(2, reopened.Document!.EnumerateAllMembers().Count());
    }

    [Fact]
    public void NativeShellExpectedIdentityMismatchRejectsWithoutFormalChanges()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
        var seeded = store.Commit(initial, Plan(store, initial, Wall(Tile("old", "Old")) with { Generation = 1 },
            new EntryChange.CreateUrl("old", "https://existing.example/")));
        var current = seeded.CommittedDocument!;
        var old = Assert.IsType<TileMember>(Assert.Single(current.EnumerateAllMembers()));
        var before = FormalFiles(files, root);
        var candidate = Wall(old, Tile("native", "Native")) with { Generation = 2, Rows = 12 };
        var request = Plan(store, current, candidate,
            new EntryChange.CreateShellLink("native", ComputerShell, LaunchIdentityService.ForShell(RecycleShell).Key));

        var result = store.Commit(current, request);

        Assert.Equal(WallCommitStatus.Rejected, result.Status);
        Assert.Null(result.CommittedDocument);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("native Shell identity changed", StringComparison.Ordinal));
        AssertSameFileBytes(before, FormalFiles(files, root));
        Assert.Empty(files.ListDirectories(Path.Combine(root, "Staging")));
        Assert.Equal(1, store.Open().Document!.Generation);
    }

    [Fact]
    public void NativeShellCreationFailureAfterEarlierStagedEntryRollsBackCompleteBatch()
    {
        var files = new InMemoryFileStore();
        var links = new FailingShellLinks(new FakeLinkFileService(files)) { FailOnShellCreate = 2 };
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "shell-transaction-" + Guid.NewGuid().ToString("N")));
        var store = new WallStore(files, links, root);
        var initial = Wall();
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
        var seeded = store.Commit(initial, Plan(store, initial, Wall(Tile("old", "Old")) with { Generation = 1 },
            new EntryChange.CreateUrl("old", "https://existing.example/")));
        var current = seeded.CommittedDocument!;
        var old = Assert.IsType<TileMember>(Assert.Single(current.EnumerateAllMembers()));
        var before = FormalFiles(files, root);
        var candidate = Wall(old, Tile("first", "First"), Tile("second", "Second")) with { Generation = 2, Rows = 12 };
        var request = Plan(store, current, candidate,
            new EntryChange.CreateShellLink("first", ComputerShell, LaunchIdentityService.ForShell(ComputerShell).Key),
            new EntryChange.CreateShellLink("second", RecycleShell, LaunchIdentityService.ForShell(RecycleShell).Key));

        var result = store.Commit(current, request);

        Assert.Equal(2, links.ShellCreates);
        Assert.Equal(WallCommitStatus.Rejected, result.Status);
        Assert.Null(result.CommittedDocument);
        AssertSameFileBytes(before, FormalFiles(files, root));
        Assert.Empty(files.ListDirectories(Path.Combine(root, "Staging")));
        var reopened = store.Open();
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.Equal(WallJson.Serialize(current), WallJson.Serialize(reopened.Document!));
    }

    private static Dictionary<string, byte[]> FormalFiles(InMemoryFileStore files, string root) => files.SnapshotFiles()
        .Where(pair => pair.Key.Equals(Path.Combine(root, "state.json"), StringComparison.Ordinal) ||
            pair.Key.Equals(Path.Combine(root, "Recovery", "state.prev.json"), StringComparison.Ordinal) ||
            pair.Key.StartsWith(Path.Combine(root, "Objects") + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            pair.Key.StartsWith(Path.Combine(root, "Media") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private sealed class FailingShellLinks(FakeLinkFileService inner) : ILnkFileService
    {
        public int FailOnShellCreate { get; init; }
        public int ShellCreates { get; private set; }
        public void Create(string path, string target, string arguments, string workingDirectory) => inner.Create(path, target, arguments, workingDirectory);
        public LinkFields Read(string path) => inner.Read(path);
        public void EditTargetOnly(string path, string target) => inner.EditTargetOnly(path, target);
        public void CreateShell(string path, string parsingName)
        {
            inner.CreateShell(path, parsingName);
            if (++ShellCreates == FailOnShellCreate) throw new IOException("Injected native Shell creation failure after stage write.");
        }
    }
}
