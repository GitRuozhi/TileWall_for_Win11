using System.Security.Cryptography;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Storage;

public sealed partial class WallStoreTests
{
    private static PreparedMediaAsset OriginalImage() => PreparedMediaAsset.FromVerifiedImage(
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aHZkAAAAASUVORK5CYII="), ".png");

    private static TileMember SingleImage(string id, PreparedMediaAsset asset) => Tile(id) with
    {
        Images = new MemberImageSource { Kind = MemberImageSourceKind.Single, Media = asset.Reference }
    };

    [Fact]
    public void OriginalMediaSurvivesUnpinUndoAndRestartWithoutExternalSource()
    {
        var (store, files, links, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var asset = OriginalImage();
        var original = store.Commit(initial, new(0, Wall(SingleImage("a", asset)) with { Generation = 1 },
            [new EntryChange.CreateUrl("a", "https://example.com")], [asset]), true);
        Assert.Equal(WallCommitStatus.Committed, original.Status);
        var before = original.CommittedDocument!;
        var member = before.Lanes[0].Groups[0].Members[0];
        var mediaPath = store.ManagedMediaPath(asset.Reference);
        var originalBytes = files.ReadAllBytes(mediaPath);
        var entryBytes = files.ReadAllBytes(EntryPath(root, member.Entry!));
        var unpinned = store.Commit(before, new(1, before with
        {
            Generation = 2, Lanes = [new WallLane { Groups = [] }], InactiveMembers = [member]
        }, []), true);
        Assert.Equal(WallCommitStatus.Committed, unpinned.Status);
        Assert.Equal(member, Assert.Single(unpinned.CommittedDocument!.InactiveMembers));
        Assert.Equal(originalBytes, files.ReadAllBytes(mediaPath));
        Assert.Equal(entryBytes, files.ReadAllBytes(EntryPath(root, member.Entry!)));

        var reopened = new WallStore(files, links, root);
        var loaded = reopened.Open();
        Assert.Equal(WallOpenStatus.Loaded, loaded.Status);
        Assert.Empty(loaded.Document!.Lanes[0].Groups);
        Assert.Single(loaded.Document.InactiveMembers);
        var undo = reopened.Undo(loaded.Document, before, unpinned.Undo!, 2);
        Assert.Equal(WallCommitStatus.Committed, undo.Status);
        Assert.Empty(undo.CommittedDocument!.InactiveMembers);
        Assert.Equal(member, Assert.Single(undo.CommittedDocument.Lanes[0].Groups[0].Members));
        Assert.Equal(originalBytes, files.ReadAllBytes(mediaPath));
    }

    [Fact]
    public void ConfigReplacementFailureRollsBackOnlyNewMedia()
    {
        var (store, files, root) = FaultedHarness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var asset = OriginalImage();
        files.Rules.Add((kind, _, destination) => kind == "move" && destination == Path.Combine(root, "config.json"));
        var rejected = store.Commit(initial, new(0, Wall(SingleImage("a", asset)) with { Generation = 1 }, [], [asset]), true);
        Assert.Equal(WallCommitStatus.Rejected, rejected.Status);
        Assert.False(files.Exists(store.ManagedMediaPath(asset.Reference)));
        Assert.Equal(0, store.Open().Document!.Generation);
        Assert.Empty(files.ListDirectories(Path.Combine(root, "Staging")));
    }

    [Fact]
    public void ReusedOriginalIsNeverOverwrittenOrDeletedByRejectedSave()
    {
        var (store, files, root) = FaultedHarness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var asset = OriginalImage();
        var first = store.Commit(initial, new(0, Wall(SingleImage("a", asset)) with { Generation = 1 }, [], [asset]), true);
        var before = first.CommittedDocument!;
        var path = store.ManagedMediaPath(asset.Reference);
        var bytes = files.ReadAllBytes(path);
        var candidate = before with
        {
            Generation = 2,
            Settings = before.Settings with { BackgroundMode = WallBackgroundMode.Custom, BackgroundMedia = asset.Reference }
        };
        files.Rules.Add((kind, _, destination) => kind == "move" && destination == Path.Combine(root, "config.json"));
        var rejected = store.Commit(before, new(1, candidate, [], [asset]), false);
        Assert.Equal(WallCommitStatus.Rejected, rejected.Status);
        Assert.Equal(bytes, files.ReadAllBytes(path));
        Assert.Equal(1, store.Open().Document!.Generation);
    }

    [Fact]
    public void CommittedMediaRemainsAvailableAcrossDeferredCleanup()
    {
        var (store, files, root) = FaultedHarness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var asset = OriginalImage();
        files.Rules.Add((kind, _, destination) => kind == "copy" &&
            destination?.EndsWith(Path.DirectorySeparatorChar + "new.snapshot.json", StringComparison.Ordinal) == true);
        var committed = store.Commit(initial, new(0, Wall(SingleImage("a", asset)) with { Generation = 1 }, [], [asset]), true);
        Assert.Equal(WallCommitStatus.CommittedNeedsCleanup, committed.Status);
        Assert.True(files.Exists(store.ManagedMediaPath(asset.Reference)));
        Assert.NotEmpty(files.ListDirectories(Path.Combine(root, "Staging")));
        var reopened = store.Open();
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.Equal(asset.Reference, reopened.Document!.Lanes[0].Groups[0].Members[0].Images.Media);
        Assert.Empty(files.ListDirectories(Path.Combine(root, "Staging")));
    }

    [Fact]
    public void InterruptedMediaRollbackRetainsItsJournalAndRetriesOnOpen()
    {
        var (store, files, root) = FaultedHarness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var asset = OriginalImage();
        var path = store.ManagedMediaPath(asset.Reference);
        files.Rules.Add((kind, _, destination) => kind == "move" && destination == Path.Combine(root, "config.json"));
        files.Sticky.Add((kind, source, _) => kind == "delete" && source == path);
        var blocked = store.Commit(initial, new(0, Wall(SingleImage("a", asset)) with { Generation = 1 }, [], [asset]), true);
        Assert.Equal(WallCommitStatus.RecoveryBlocked, blocked.Status);
        Assert.True(files.Exists(path));
        Assert.NotEmpty(files.ListDirectories(Path.Combine(root, "Staging")));
        Assert.Equal(0, WallJson.Deserialize(files.ReadAllBytes(Path.Combine(root, "config.json"))).Generation);

        files.Sticky.Clear();
        var opened = store.Open();
        Assert.Equal(WallOpenStatus.Loaded, opened.Status);
        Assert.Equal(0, opened.Document!.Generation);
        Assert.False(files.Exists(path));
        Assert.Empty(files.ListDirectories(Path.Combine(root, "Staging")));
    }

    [Fact]
    public void FormalMediaIsRequiredAndUnattributedMediaBlocksFreshInitialization()
    {
        var (store, files, _, root) = Harness();
        var asset = OriginalImage();
        var path = store.ManagedMediaPath(asset.Reference);
        files.CreateDirectory(Path.GetDirectoryName(path)!);
        files.WriteAllBytes(path, [1]);
        Assert.Equal(WallOpenStatus.RecoveryBlocked, store.Open().Status);
        Assert.Equal(WallOpenStatus.RecoveryBlocked, store.InitializeFresh(Wall()).Status);
        Assert.True(files.Exists(path));

        var (emptyStore, _, _, _) = Harness();
        var initial = Wall();
        emptyStore.InitializeFresh(initial);
        var rejected = emptyStore.Commit(initial, new(0, Wall(SingleImage("a", asset)) with { Generation = 1 }, []), true);
        Assert.Equal(WallCommitStatus.Rejected, rejected.Status);
        Assert.Contains("prepared", string.Join(" ", rejected.Diagnostics), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChangedOriginalReferencedOnlyByInactiveMemberBlocksOpen()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var asset = OriginalImage();
        var committed = store.Commit(initial, new(0, initial with
        { Generation = 1, InactiveMembers = [SingleImage("a", asset)] }, [], [asset]), true);
        Assert.Equal(WallCommitStatus.Committed, committed.Status);
        files.WriteAllBytes(store.ManagedMediaPath(asset.Reference), [5]);
        var config = files.ReadAllBytes(Path.Combine(root, "config.json"));
        Assert.Equal(WallOpenStatus.RecoveryBlocked, store.Open().Status);
        Assert.Equal(config, files.ReadAllBytes(Path.Combine(root, "config.json")));
    }

    [Fact]
    public void MediaPathsAndActualIoRejectReparsePoints()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var asset = OriginalImage();
        files.ReparsePaths.Add(Path.Combine(root, "Media"));
        var result = store.Commit(initial, new(0, Wall(SingleImage("a", asset)) with { Generation = 1 }, [], [asset]), true);
        Assert.False(result.Status is WallCommitStatus.Committed or WallCommitStatus.CommittedNeedsCleanup);
        Assert.Equal(0, WallJson.Deserialize(files.ReadAllBytes(Path.Combine(root, "config.json"))).Generation);
        Assert.Throws<IOException>(() => store.ManagedMediaPath(asset.Reference with { Extension = "/outside.png" }));
    }

    [Fact]
    public void ShortcutExpectedFingerprintIsBoundToActualStagedBytes()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var source = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".url"));
        var confirmed = UrlShortcut.CreateContent("https://confirmed.example");
        var changed = UrlShortcut.CreateContent("https://changed.example");
        files.WriteAllBytes(source, changed);
        var result = store.Commit(initial, new(0, Wall(Tile("a")) with { Generation = 1 },
            [new EntryChange.CopyShortcut("a", source, Convert.ToHexString(SHA256.HashData(confirmed)))]), true);
        Assert.Equal(WallCommitStatus.Rejected, result.Status);
        Assert.Equal(changed, files.ReadAllBytes(source));
        Assert.Equal(0, store.Open().Document!.Generation);
        Assert.Empty(files.ListDirectories(Path.Combine(root, "Staging")));
    }

    [Fact]
    public void FinalStorageRejectsDuplicateActiveGroupIdentityAndAllowsOtherGroupsOrInactive()
    {
        var (store, _, _, _) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var a = Tile("a");
        var b = Tile("b") with { LocalBounds = new GridRect(1, 0, 1, 1) };
        var duplicate = initial with
        {
            Generation = 1,
            Lanes = [new WallLane { Groups = [new StartGroup { Id = "g", Members = [a, b] }] }]
        };
        EntryChange[] sources = [new EntryChange.CreateUrl("a", "https://same.example"), new EntryChange.CreateUrl("b", "https://same.example")];
        Assert.Equal(WallCommitStatus.Rejected, store.Commit(initial, new(0, duplicate, sources), true).Status);
        var separate = Wall(a, b with { LocalBounds = new GridRect(0, 0, 1, 1) }) with { Generation = 1 };
        var allowed = store.Commit(initial, new(0, separate, sources), true);
        Assert.Equal(WallCommitStatus.Committed, allowed.Status);
        var before = allowed.CommittedDocument!;
        var unpinned = before.Lanes[0].Groups[0].Members[0];
        var retained = before with
        {
            Generation = 2, Lanes = [new WallLane { Groups = [before.Lanes[0].Groups[1]] }], InactiveMembers = [unpinned]
        };
        Assert.Equal(WallCommitStatus.Committed, store.Commit(before, new(1, retained, []), true).Status);
    }
}
