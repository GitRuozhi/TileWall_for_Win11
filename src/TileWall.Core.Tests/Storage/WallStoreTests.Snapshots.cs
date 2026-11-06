using System.Text;
using TileWall.Core.Configuration;
using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Storage;

public sealed partial class WallStoreTests
{
    [Fact]
    public void JointSnapshotSeparatesSettingsWallAndRecycleBinAndHasOneManifestGeneration()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall(); Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
        var candidate = Wall(Tile("active")) with
        {
            Generation = 1, RecycleBinEnabled = true, InactiveMembers = [Tile("retained")],
            Settings = initial.Settings with { AlwaysOnTop = false },
        };
        var committed = store.Commit(initial, new(0, candidate, []));
        Assert.Equal(WallCommitStatus.Committed, committed.Status);
        var manifest = UserDataSnapshotJson.ReadManifest(files.ReadAllBytes(Path.Combine(root, "state.json")));
        Assert.Equal(1, manifest.Generation);
        var directory = Path.Combine(root, "Snapshots", manifest.SnapshotId);
        Assert.Equal(new[] { "recycle-bin.json", "settings.json", "wall.json" }, files.ListFiles(directory).Select(Path.GetFileName));
        var settings = Encoding.UTF8.GetString(files.ReadAllBytes(Path.Combine(directory, "settings.json")));
        var wall = Encoding.UTF8.GetString(files.ReadAllBytes(Path.Combine(directory, "wall.json")));
        var recycle = Encoding.UTF8.GetString(files.ReadAllBytes(Path.Combine(directory, "recycle-bin.json")));
        Assert.DoesNotContain("active", settings); Assert.DoesNotContain("retained", settings);
        Assert.Contains("active", wall); Assert.DoesNotContain("retained", wall); Assert.DoesNotContain("alwaysOnTop", wall);
        Assert.Contains("retained", recycle); Assert.DoesNotContain("alwaysOnTop", recycle);
        Assert.DoesNotContain(files.FilePaths, path => Path.GetFileName(path) is "config.json" or "config.prev.json");
        var opened = store.Open(); Assert.Equal(WallOpenStatus.Loaded, opened.Status);
        Assert.True(opened.Document!.RecycleBinEnabled); Assert.False(opened.Document.Settings.AlwaysOnTop);
        Assert.Equal("retained", Assert.Single(opened.Document.InactiveMembers).Id);
    }

    [Theory]
    [InlineData("settings.json")]
    [InlineData("wall.json")]
    [InlineData("recycle-bin.json")]
    public void BrokenCompleteSnapshotRecoversOnlyTheTrustedPreviousWholeSetAndReopens(string missingFile)
    {
        var (store, files, _, root) = Harness(); var initial = Wall(); store.InitializeFresh(initial);
        var first = store.Commit(initial, new(0, Wall(Tile("previous")) with
            { Generation = 1, RecycleBinEnabled = false, Settings = initial.Settings with { AlwaysOnTop = false } }, [])).CommittedDocument!;
        var second = store.Commit(first, new(1, Wall(Tile("latest")) with
            { Generation = 2, RecycleBinEnabled = true, Settings = initial.Settings }, [])).CommittedDocument!;
        var manifest = UserDataSnapshotJson.ReadManifest(files.ReadAllBytes(Path.Combine(root, "state.json")));
        files.Delete(Path.Combine(root, "Snapshots", manifest.SnapshotId, missingFile));
        var recovered = store.Open();
        Assert.Equal(WallOpenStatus.RecoveredPrevious, recovered.Status);
        Assert.Equal("previous", Assert.Single(recovered.Document!.EnumerateAllMembers()).Id);
        Assert.False(recovered.Document.RecycleBinEnabled); Assert.False(recovered.Document.Settings.AlwaysOnTop);
        Assert.True(recovered.Document.Generation >= second.Generation);
        var reopened = store.Open(); Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.Equal(WallJson.Serialize(recovered.Document), WallJson.Serialize(reopened.Document!));
    }

    [Fact]
    public void SnapshotWriteFailureBeforePublicationKeepsTheEntirePreviousGeneration()
    {
        var inner = new InMemoryFileStore(); var files = new FaultingFiles(inner);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "snapshot-fault-" + Guid.NewGuid().ToString("N")));
        var store = new WallStore(files, new FakeLinkFileService(inner), root); var initial = Wall(); store.InitializeFresh(initial);
        var before = inner.ReadAllBytes(Path.Combine(root, "state.json"));
        files.Rules.Add((kind, _, destination) => kind == "write" && destination!.EndsWith(Path.DirectorySeparatorChar + "wall.json", StringComparison.Ordinal));
        var result = store.Commit(initial, new(0, initial with { Generation = 1, RecycleBinEnabled = true }, []));
        Assert.Equal(WallCommitStatus.Rejected, result.Status);
        Assert.Equal(before, inner.ReadAllBytes(Path.Combine(root, "state.json")));
        Assert.Equal(0, store.Open().Document!.Generation);
    }

    [Fact]
    public void StorageDtosIndependentlyRejectDisabledRetentionAndMoreThanTwoHundredItems()
    {
        var disabled = Wall() with { RecycleBinEnabled = false, InactiveMembers = [Tile("retained")] };
        Assert.Throws<InvalidDataException>(() => UserDataSnapshotJson.Prepare(disabled));
        var overflow = disabled with { RecycleBinEnabled = true, InactiveMembers = Enumerable.Range(0, 201).Select(index => (WallMember)Tile("item" + index)).ToArray() };
        Assert.Throws<InvalidDataException>(() => UserDataSnapshotJson.Prepare(overflow));
    }

    private static PreparedManagedGallery Gallery(byte variant = 0) => PreparedManagedGallery.FromVerifiedFiles(
        new[] { new PreparedManagedGalleryFile("season.png", PreparedMediaAsset.FromVerifiedImage(new byte[] { variant, 2, 3 }, ".png")) });

    private static WallDocument WithGallery(WallDocument current, PreparedManagedGallery gallery) => Wall(Tile("gallery") with
    { Images = new MemberImageSource { Kind = MemberImageSourceKind.Folder, ManagedGallery = gallery.Reference } }) with { Generation = current.Generation + 1 };

    [Fact]
    public void GalleryPublicationIsJointAndDiskDtoContainsOnlyItsReference()
    {
        var (store, files, _, root) = Harness(); var initial = Wall(); store.InitializeFresh(initial); var gallery = Gallery();
        var committed = store.Commit(initial, new(0, WithGallery(initial, gallery), [], ManagedGalleries: [gallery]));
        Assert.Equal(WallCommitStatus.Committed, committed.Status);
        var member = Assert.Single(committed.CommittedDocument!.EnumerateAllMembers());
        var source = store.ManagedGalleryPath(gallery.Reference);
        Assert.Equal(source, member.Images.Path);
        Assert.Equal(new byte[] { 0, 2, 3 }, files.ReadAllBytes(Path.Combine(source, "season.png")));
        var manifest = UserDataSnapshotJson.ReadManifest(files.ReadAllBytes(Path.Combine(root, "state.json")));
        var stored = Encoding.UTF8.GetString(files.ReadAllBytes(Path.Combine(root, "Snapshots", manifest.SnapshotId, "wall.json")));
        Assert.Contains(gallery.Reference.ContentId, stored); Assert.DoesNotContain(root.Replace("\\", "\\\\"), stored);
        Assert.Equal(source, Assert.Single(store.Open().Document!.EnumerateAllMembers()).Images.Path);
        var current = committed.CommittedDocument!;
        var reused = store.Commit(current, new(1,
            current with { Generation = 2, Settings = initial.Settings with { AlwaysOnTop = false } }, []));
        Assert.Equal(WallCommitStatus.Committed, reused.Status);
    }

    [Fact]
    public void GalleryPublishFailureRollsBackOnlyNewSourcesAndPreservesAReusedSource()
    {
        var inner = new InMemoryFileStore(); var files = new FaultingFiles(inner);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "gallery-fault-" + Guid.NewGuid().ToString("N")));
        var store = new WallStore(files, new FakeLinkFileService(inner), root); var initial = Wall(); store.InitializeFresh(initial);
        var gallery = Gallery(); var first = store.Commit(initial, new(0, WithGallery(initial, gallery), [], ManagedGalleries: [gallery])).CommittedDocument!;
        var nextGallery = Gallery(1);
        files.Rules.Add((kind, _, destination) => kind == "move" && destination == Path.Combine(root, "state.json"));
        var rejected = store.Commit(first, new(1, WithGallery(first, nextGallery), [], ManagedGalleries: [nextGallery]));
        Assert.Equal(WallCommitStatus.Rejected, rejected.Status);
        Assert.True(inner.GetPathInfo(store.ManagedGalleryPath(gallery.Reference)).Exists);
        Assert.False(inner.GetPathInfo(store.ManagedGalleryPath(nextGallery.Reference)).Exists);
        Assert.Equal(gallery.Reference, Assert.Single(store.Open().Document!.EnumerateAllMembers()).Images.ManagedGallery);
    }

    [Fact]
    public void GalleryAndSnapshotGcKeepPreviousAndLeasedSourcesThenReleaseThem()
    {
        var (store, files, _, root) = Harness(); var initial = Wall(); store.InitializeFresh(initial); var gallery = Gallery();
        var first = store.Commit(initial, new(0, WithGallery(initial, gallery), [], ManagedGalleries: [gallery])).CommittedDocument!;
        var source = store.ManagedGalleryPath(gallery.Reference);
        var removed = store.Commit(first, new(1, Wall() with { Generation = 2 }, [])).CommittedDocument!;
        Assert.True(files.GetPathInfo(source).Exists); // Previous complete snapshot still owns it.
        using (store.AcquireResourceReadLease(gallery.Reference))
        {
            var latest = store.Commit(removed, new(2, removed with { Generation = 3, Settings = removed.Settings with { AlwaysOnTop = false } }, []));
            Assert.Equal(WallCommitStatus.Committed, latest.Status);
            Assert.True(files.GetPathInfo(source).Exists);
            Assert.Equal(2, files.ListDirectories(Path.Combine(root, "Snapshots")).Count);
        }
        Assert.Equal(WallOpenStatus.Loaded, store.Open().Status);
        Assert.False(files.GetPathInfo(source).Exists);
    }

    [Fact]
    public void GcDeletionFailureDoesNotRevokeCommitOrBlockOpenAndRetriesLater()
    {
        var inner = new InMemoryFileStore(); var files = new FaultingFiles(inner);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "gc-fault-" + Guid.NewGuid().ToString("N")));
        var store = new WallStore(files, new FakeLinkFileService(inner), root); var initial = Wall(); store.InitializeFresh(initial); var gallery = Gallery();
        var first = store.Commit(initial, new(0, WithGallery(initial, gallery), [], ManagedGalleries: [gallery])).CommittedDocument!;
        var removed = store.Commit(first, new(1, Wall() with { Generation = 2 }, [])).CommittedDocument!;
        var source = store.ManagedGalleryPath(gallery.Reference);
        files.Sticky.Add((kind, path, _) => kind == "rmdir" && path == source);
        var latest = store.Commit(removed, new(2, removed with { Generation = 3, Settings = removed.Settings with { AlwaysOnTop = false } }, []));
        Assert.Equal(WallCommitStatus.Committed, latest.Status);
        Assert.Equal(WallOpenStatus.Loaded, store.Open().Status); Assert.True(inner.GetPathInfo(source).Exists);
        files.Sticky.Clear(); Assert.Equal(WallOpenStatus.Loaded, store.Open().Status); Assert.False(inner.GetPathInfo(source).Exists);
    }
}
