using System.Security.Cryptography;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Storage;

public sealed partial class WallStoreTests
{
    private static WallDocument SeedPreconditionWall(InMemoryFileStore files, string root)
    {
        var a = Tile("a", "A", new("a", "Objects/a/A.url", ManagedEntryKind.Url));
        var b = Tile("b", "B", new("b", "Objects/b/B.url", ManagedEntryKind.Url)) with { LocalBounds = new GridRect(1, 0, 1, 1) };
        var document = new WallDocument { Columns = 1, Rows = 6, Generation = 1,
            Lanes = [new WallLane { Groups = [new StartGroup { Id = "g", Title = "Group", Members = [a, b] }] }] };
        files.WriteAllBytes(Path.Combine(root, "config.json"), WallJson.Serialize(document));
        files.WriteAllBytes(EntryPath(root, a.Entry!), UrlShortcut.CreateContent("https://a.example/"));
        files.WriteAllBytes(EntryPath(root, b.Entry!), UrlShortcut.CreateContent("https://b.example/"));
        return document;
    }

    private static Dictionary<string, string> EntryPreconditions(InMemoryFileStore files, string root, WallDocument current) =>
        current.EnumerateAllMembers().Where(member => member.Entry is not null).ToDictionary(member => member.Id,
            member => Convert.ToHexString(SHA256.HashData(files.ReadAllBytes(EntryPath(root, member.Entry!)))), StringComparer.Ordinal);

    [Theory]
    [InlineData("replacement")]
    [InlineData("unchanged")]
    [InlineData("missing")]
    public void ChangedDestinationEntryWithoutGenerationChangeRejectsBeforeWriting(string change)
    {
        var (store, files, _, root) = Harness();
        var current = SeedPreconditionWall(files, root);
        Assert.Equal(WallOpenStatus.Loaded, store.Open().Status);
        var expected = EntryPreconditions(files, root, current);
        var id = change == "unchanged" ? "b" : "a";
        var entry = current.EnumerateAllMembers().Single(member => member.Id == id).Entry!;
        var path = EntryPath(root, entry);
        if (change == "missing") files.Delete(path);
        else files.WriteAllBytes(path, UrlShortcut.CreateContent("https://external-edit.example/"));
        var before = files.SnapshotFiles();
        var request = new WallCommitRequest(1, current with { Generation = 2 },
            [new EntryChange.CreateUrl("a", "https://imported.example/")], ExpectedEntrySha256: expected);

        var result = store.Commit(current, request);

        Assert.Equal(WallCommitStatus.Rejected, result.Status);
        Assert.Contains(WallStore.ImportDestinationChanged, result.Diagnostics);
        Assert.Null(result.CommittedDocument);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), files.FilePaths);
        foreach (var (file, bytes) in before) Assert.Equal(bytes, files.ReadAllBytes(file));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DestinationEntryChangedDuringStagingRejectsBeforeIntentAndPreservesExternalEdit(bool replaced)
    {
        var inner = new InMemoryFileStore();
        var files = new FaultingFiles(inner);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wall-preconditions-" + Guid.NewGuid().ToString("N")));
        var store = new WallStore(files, new FakeLinkFileService(inner), root);
        var current = SeedPreconditionWall(inner, root);
        var expected = EntryPreconditions(inner, root, current);
        var target = EntryPath(root, current.EnumerateAllMembers().Single(member => member.Id == (replaced ? "a" : "b")).Entry!);
        var external = UrlShortcut.CreateContent("https://changed-during-stage.example/");
        var config = inner.ReadAllBytes(Path.Combine(root, "config.json"));
        var changed = false;
        files.Rules.Add((kind, _, destination) =>
        {
            if (!changed && kind == "write" && destination!.EndsWith("config.tmp", StringComparison.Ordinal))
            { changed = true; inner.WriteAllBytes(target, external); }
            return false;
        });

        var result = store.Commit(current, new(1, current with { Generation = 2 },
            [new EntryChange.CreateUrl("a", "https://imported.example/")], ExpectedEntrySha256: expected));

        Assert.True(changed);
        Assert.Equal(WallCommitStatus.Rejected, result.Status);
        Assert.Contains(WallStore.ImportDestinationChanged, result.Diagnostics);
        Assert.Equal(config, inner.ReadAllBytes(Path.Combine(root, "config.json")));
        Assert.Equal(external, inner.ReadAllBytes(target));
        Assert.DoesNotContain(inner.FilePaths, path => path.Contains(Path.DirectorySeparatorChar + "Staging" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.DoesNotContain(inner.FilePaths, path => path.Contains(Path.DirectorySeparatorChar + "Recovery" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    [Fact]
    public void MatchingDestinationEntryPreconditionsAllowOneJointCommit()
    {
        var (store, files, _, root) = Harness();
        var current = SeedPreconditionWall(files, root);
        var expected = EntryPreconditions(files, root, current);
        var result = store.Commit(current, new(1, current with { Generation = 2 },
            [new EntryChange.CreateUrl("a", "https://imported.example/")], ExpectedEntrySha256: expected));
        Assert.Equal(WallCommitStatus.Committed, result.Status);
        Assert.Equal(2, result.CommittedDocument!.Generation);
        Assert.Equal("https://imported.example/", UrlShortcut.ReadUrlLine(files.ReadAllBytes(
            EntryPath(root, result.CommittedDocument.EnumerateAllMembers().Single(member => member.Id == "a").Entry!))));
        Assert.Equal(expected["b"], Convert.ToHexString(SHA256.HashData(files.ReadAllBytes(
            EntryPath(root, result.CommittedDocument.EnumerateAllMembers().Single(member => member.Id == "b").Entry!)))));
    }
}
