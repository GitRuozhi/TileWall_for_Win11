using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Storage;

public sealed partial class WallStoreTests
{
    [Fact]
    public void FutureAdditiveSnapshotOpensKnownTargetsImagesAndNativeIconSeriesWithoutRewritingBytes()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall(); store.InitializeFresh(initial);
        var image = OriginalImage(); var icons = IconSeries();
        var candidate = Wall(SingleImage("image", image) with { TargetInput = "https://future.example" },
            Tile("icons") with { Images = new() { IconSeries = icons.Series } })
            with { Generation = 1, InactiveMembers = [Tile("retained")] };
        var seeded = store.Commit(initial, new(0, candidate,
            [new EntryChange.CreateUrl("image", "https://future.example")],
            icons.Assets.Append(image).DistinctBy(asset => asset.Reference).ToArray()));
        Assert.Equal(WallCommitStatus.Committed, seeded.Status);
        InstallFixture(files, root, FutureSnapshot(UserDataSnapshotJson.Prepare(seeded.CommittedDocument!)));
        var before = files.SnapshotFiles();

        var opened = store.Open();
        Assert.Equal(WallOpenStatus.Loaded, opened.Status);
        var members = opened.Document!.EnumerateAllMembers().ToDictionary(member => member.Id);
        Assert.Equal("https://future.example", members["image"].TargetInput);
        Assert.Equal(image.Reference, members["image"].Images.Media);
        var restoredSeries = members["icons"].Images.IconSeries!;
        Assert.Equal(icons.Series.Identity, restoredSeries.Identity);
        Assert.Equal(icons.Series.SelectedFrame, restoredSeries.SelectedFrame);
        Assert.Equal(icons.Series.Frames, restoredSeries.Frames);
        Assert.Equal(image.CopyBytes(), files.ReadAllBytes(store.ManagedMediaPath(members["image"].Images.Media!)));
        Assert.Empty(opened.Document.ActiveView().InactiveMembers);
        Assert.Single(opened.Document.InactiveMembers);
        AssertSameFileBytes(before, files.SnapshotFiles());
    }

    [Fact]
    public void FutureFieldsFollowSurvivingEntitiesAcrossUiReconstructionUnpinRestoreAndActiveProjection()
    {
        var (store, files, _, root) = Harness();
        var a = Tile("a", "old"); var b = Tile("b") with { LocalBounds = new GridRect(1, 0, 1, 1) };
        var initial = Wall(a) with { Columns = 2, InactiveMembers = [Tile("retained")],
            Lanes = [new() { Groups = [new() { Id = "stable-group", Members = [a, b] }] }, new() { Groups = [] }] };
        InstallFixture(files, root, FutureSnapshot(UserDataSnapshotJson.Prepare(initial)));
        var current = store.Open().Document!.ActiveView();
        var untouched = files.SnapshotFiles();
        // Merely preparing and cancelling an edit must not mutate formal storage.
        _ = current with { Settings = new() { ShowGrid = true } };
        AssertSameFileBytes(untouched, files.SnapshotFiles());

        var editedA = Tile("a", "changed") with { Images = new(), ImageDisplay = new() };
        var edited = current with { Generation = 1, Settings = new() { ShowGrid = true },
            Lanes = [new() { Groups = [new() { Id = "stable-group", Members = [editedA, b] }] }, new() { Groups = [] }] };
        var first = store.Commit(current, new(0, edited, []) { PreserveStoredRecycleBin = true });
        Assert.Equal(WallCommitStatus.Committed, first.Status);
        Assert.Empty(first.CommittedDocument!.InactiveMembers);
        AssertFutureEnvelopeFields(files, root);
        var disk = FutureDiskJson(files, root, "wall.json");
        Assert.Equal("lane-0", (string?)disk["lanes"]![0]!["futureLane"]);
        Assert.Equal("stable-group", (string?)disk["lanes"]![0]!["groups"]![0]!["futureGroup"]);
        AssertFutureMemberFields(disk["lanes"]![0]!["groups"]![0]!["members"]![0]!, "a");
        AssertFutureMemberFields(FutureDiskJson(files, root, "recycle-bin.json")["members"]![0]!, "retained");

        current = store.Open().Document!;
        Assert.Single(current.InactiveMembers);
        a = (TileMember)current.EnumerateAllMembers().Single(member => member.Id == "a");
        b = (TileMember)current.EnumerateAllMembers().Single(member => member.Id == "b");
        var unpinned = current with { Generation = 2,
            Lanes = [new() { Groups = [new() { Id = "stable-group", Members = [b] }] }, new() { Groups = [] }],
            InactiveMembers = current.InactiveMembers.Append(a).ToArray() };
        Assert.Equal(WallCommitStatus.Committed, store.Commit(current, new(1, unpinned, [])).Status);
        AssertFutureMemberFields(FutureDiskJson(files, root, "recycle-bin.json")["members"]!.AsArray()
            .Single(member => (string?)member!["id"] == "a")!, "a");

        current = store.Open().Document!;
        // The editor reconstructs the same member, while a new member must not inherit its data.
        var restored = Tile("a", "changed") with { LocalBounds = new GridRect(0, 1, 1, 1) };
        var final = current with { Generation = 3,
            Lanes = [new() { Groups = [new() { Id = "stable-group", Members = [Tile("new")] }] },
                new() { Groups = [new() { Id = "new-group", Members = [restored] }] }],
            InactiveMembers = current.InactiveMembers.Where(member => member.Id != "a").ToArray() };
        Assert.Equal(WallCommitStatus.Committed, store.Commit(current, new(2, final, [])).Status);
        AssertFutureEnvelopeFields(files, root);
        disk = FutureDiskJson(files, root, "wall.json");
        var moved = disk["lanes"]![1]!["groups"]![0]!["members"]![0]!;
        AssertFutureMemberFields(moved, "a");
        Assert.Equal("changed", (string?)moved["visual"]!["title"]);
        Assert.Null(disk["lanes"]![0]!["groups"]![0]!["members"]![0]!["futureMember"]);
        Assert.Null(disk["lanes"]![1]!["groups"]![0]!["futureGroup"]);
        Assert.Equal("lane-1", (string?)disk["lanes"]![1]!["futureLane"]);
        Assert.Equal("stable-group", (string?)disk["lanes"]![0]!["groups"]![0]!["futureGroup"]);
        Assert.DoesNotContain(store.Open().Document!.EnumerateAllMembers(), member => member.Id == "b");
    }

    [Fact]
    public void UnknownOnlyMediaAndSourcesSurviveRepeatedSavesOpenGcAndFailedPublication()
    {
        var inner = new InMemoryFileStore(); var files = new FaultingFiles(inner);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "future-resources-" + Guid.NewGuid().ToString("N")));
        var store = new WallStore(files, new FakeLinkFileService(inner), root);
        var image = OriginalImage(); var gallery = Gallery(); var initial = Wall(); store.InitializeFresh(initial);
        var populated = Wall(SingleImage("image", image), Tile("gallery") with
            { Images = new() { Kind = MemberImageSourceKind.Folder, ManagedGallery = gallery.Reference } }) with { Generation = 1 };
        Assert.Equal(WallCommitStatus.Committed,
            store.Commit(initial, new(0, populated, [], [image], ManagedGalleries: [gallery])).Status);
        var future = FutureSnapshot(UserDataSnapshotJson.Prepare(Wall(Tile("known")) with { Generation = 1 }), nodes =>
        {
            nodes["wall.json"]["futureResources"] = JsonSerializer.SerializeToNode(new { image = image.Reference, source = gallery.Reference });
        });
        InstallFixture(inner, root, future);
        var mediaPath = store.ManagedMediaPath(image.Reference);
        var sourcePath = Path.Combine(store.ManagedGalleryPath(gallery.Reference), "season.png");
        var mediaBytes = inner.ReadAllBytes(mediaPath); var sourceBytes = inner.ReadAllBytes(sourcePath);
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var current = store.Open().Document!;
            var saved = store.Commit(current, new(current.Generation, current with
                { Generation = current.Generation + 1, Settings = current.Settings with { ShowGrid = !current.Settings.ShowGrid } }, []));
            Assert.Equal(WallCommitStatus.Committed, saved.Status);
            Assert.Equal(mediaBytes, inner.ReadAllBytes(mediaPath));
            Assert.Equal(sourceBytes, inner.ReadAllBytes(sourcePath));
        }
        var before = inner.ReadAllBytes(Path.Combine(root, "state.json"));
        var loaded = store.Open().Document!;
        files.Rules.Add((kind, _, destination) => kind == "move" && destination == Path.Combine(root, "state.json"));
        var failed = store.Commit(loaded, new(loaded.Generation, loaded with { Generation = loaded.Generation + 1 }, []));
        Assert.Equal(WallCommitStatus.Rejected, failed.Status);
        Assert.Equal(before, inner.ReadAllBytes(Path.Combine(root, "state.json")));
        Assert.Equal(WallOpenStatus.Loaded, store.Open().Status);
        Assert.Equal(mediaBytes, inner.ReadAllBytes(mediaPath));
        Assert.Equal(sourceBytes, inner.ReadAllBytes(sourcePath));
    }

    [Fact]
    public void HigherVersionAloneDoesNotDisableNormalResourceGc()
    {
        var (store, files, _, root) = Harness(); var initial = Wall(); store.InitializeFresh(initial);
        var image = OriginalImage();
        var first = store.Commit(initial, new(0, Wall(SingleImage("image", image)) with { Generation = 1 }, [], [image])).CommittedDocument!;
        InstallFixture(files, root, FutureSnapshot(UserDataSnapshotJson.Prepare(first), addUnknownFields: false));
        var current = store.Open().Document!;
        var removed = store.Commit(current, new(1, current with { Generation = 2, Lanes = [new() { Groups = [] }] }, []));
        Assert.Equal(WallCommitStatus.Committed, removed.Status);
        current = removed.CommittedDocument!;
        Assert.Equal(WallCommitStatus.Committed, store.Commit(current, new(2, current with { Generation = 3 }, [])).Status);
        Assert.Equal(WallOpenStatus.Loaded, store.Open().Status);
        Assert.False(files.Exists(store.ManagedMediaPath(image.Reference)));
    }

    [Fact]
    public void FutureUnknownOwnershipAlsoProtectsNewlyInstalledMediaAndSourcesDuringRollback()
    {
        var inner = new InMemoryFileStore(); var files = new FaultingFiles(inner);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "future-rollback-" + Guid.NewGuid().ToString("N")));
        var store = new WallStore(files, new FakeLinkFileService(inner), root);
        var image = OriginalImage(); var gallery = Gallery();
        // An old reader cannot determine whether unknown references already exist,
        // including when their files arrive during this attempted transaction.
        InstallFixture(inner, root, FutureSnapshot(UserDataSnapshotJson.Prepare(Wall()), nodes =>
            nodes["wall.json"]["futureResources"] = JsonSerializer.SerializeToNode(new { image = image.Reference, source = gallery.Reference })));
        var current = store.Open().Document!;
        var proposed = current with { Generation = 1, Lanes = Wall(SingleImage("image", image), Tile("gallery") with
            { Images = new() { Kind = MemberImageSourceKind.Folder, ManagedGallery = gallery.Reference } }).Lanes };
        var before = inner.ReadAllBytes(Path.Combine(root, "state.json"));
        files.Rules.Add((kind, _, destination) => kind == "move" && destination == Path.Combine(root, "state.json"));
        var failed = store.Commit(current, new(0, proposed, [], [image], ManagedGalleries: [gallery]));
        Assert.Equal(WallCommitStatus.Rejected, failed.Status);
        Assert.Equal(before, inner.ReadAllBytes(Path.Combine(root, "state.json")));
        Assert.Equal(image.CopyBytes(), inner.ReadAllBytes(store.ManagedMediaPath(image.Reference)));
        Assert.Equal(new byte[] { 0, 2, 3 }, inner.ReadAllBytes(Path.Combine(store.ManagedGalleryPath(gallery.Reference), "season.png")));
        Assert.Equal(WallOpenStatus.Loaded, store.Open().Status);
    }

    [Fact]
    public void SameGenerationFutureDiskChangeRejectsAnOlderSession()
    {
        var (store, files, _, root) = Harness();
        var initial = FutureSnapshot(UserDataSnapshotJson.Prepare(Wall(Tile("a"))));
        InstallFixture(files, root, initial);
        var stale = store.Open().Document!;
        var changed = FutureSnapshot(UserDataSnapshotJson.Prepare(Wall(Tile("a"))), nodes =>
            nodes["settings.json"]["value"]!["futureSetting"] = "externally-changed");
        InstallFixture(files, root, changed);
        var before = files.ReadAllBytes(Path.Combine(root, "state.json"));
        var rejected = store.Commit(stale, new(0, stale with { Generation = 1 }, []));
        Assert.Equal(WallCommitStatus.Rejected, rejected.Status);
        Assert.Equal(before, files.ReadAllBytes(Path.Combine(root, "state.json")));
        Assert.Equal("externally-changed", (string?)FutureDiskJson(files, root, "settings.json")["value"]!["futureSetting"]);
    }

    [Theory]
    [InlineData("manifest-field")]
    [InlineData("settings.json")]
    [InlineData("wall.json")]
    [InlineData("recycle-bin.json")]
    [InlineData("manifest-version")]
    public void SameGenerationEnvelopeOrVersionChangeRejectsDirectStoreCommitWithoutExplicitFingerprint(string change)
    {
        var (store, files, _, root) = Harness();
        var original = FutureSnapshot(UserDataSnapshotJson.Prepare(Wall(Tile("a"))));
        InstallFixture(files, root, original);
        var stale = store.Open().Document!;
        var manifest = JsonNode.Parse(original.ManifestBytes)!;
        var bytes = original.Files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (change == "manifest-field") manifest["futureManifest"] = JsonNode.Parse("{\"external\":true}");
        else if (change == "manifest-version") manifest["schemaVersion"] = 106;
        else
        {
            var envelope = JsonNode.Parse(bytes[change])!;
            envelope["futureEnvelope"] = JsonNode.Parse("{\"external\":true}");
            bytes[change] = Encoding.UTF8.GetBytes(envelope.ToJsonString(UserDataSnapshotJson.Options));
        }
        var changed = RebuildFutureSnapshot(original.Manifest, manifest, bytes);
        InstallFixture(files, root, changed);
        var before = files.ReadAllBytes(Path.Combine(root, "state.json"));

        var rejected = store.Commit(stale, new(0, stale with { Generation = 1 }, []));

        Assert.Equal(WallCommitStatus.Rejected, rejected.Status);
        Assert.Equal(before, files.ReadAllBytes(Path.Combine(root, "state.json")));
        Assert.Equal(WallOpenStatus.Loaded, store.Open().Status);
    }

    [Fact]
    public void NestedFutureExtensionsRemainEquivalentAcrossCompactInputAndTwoIndentedSaves()
    {
        var (store, files, _, root) = Harness();
        var future = FutureSnapshot(UserDataSnapshotJson.Prepare(Wall(Tile("a"))));
        var compactFiles = future.Files.ToDictionary(pair => pair.Key,
            pair => Encoding.UTF8.GetBytes(JsonNode.Parse(pair.Value)!.ToJsonString()), StringComparer.Ordinal);
        future = RebuildFutureSnapshot(future.Manifest, JsonNode.Parse(future.ManifestBytes)!, compactFiles);
        future = future with { ManifestBytes = Encoding.UTF8.GetBytes(JsonNode.Parse(future.ManifestBytes)!.ToJsonString()) };
        InstallFixture(files, root, future);
        var current = store.Open().Document!;

        for (var generation = 1; generation <= 2; generation++)
        {
            var saved = store.Commit(current, new(current.Generation, current with
                { Generation = generation, Settings = current.Settings with { ShowGrid = !current.Settings.ShowGrid } }, []));
            Assert.Equal(WallCommitStatus.Committed, saved.Status);
            // Reuse the returned state without reopening; its JsonElements may retain
            // the compact input spelling while the formal files are now indented.
            current = saved.CommittedDocument!;
            AssertFutureEnvelopeFields(files, root);
            AssertFutureMemberFields(FutureDiskJson(files, root, "wall.json")["lanes"]![0]!["groups"]![0]!["members"]![0]!, "a");
        }
        var reopened = store.Open();
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.Equal(2, reopened.Document!.Generation);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("snapshot-id")]
    [InlineData("missing")]
    [InlineData("duplicate-envelope")]
    [InlineData("duplicate-member")]
    public void FutureExtensionsDoNotWeakenSnapshotIntegrityChecks(string damage)
    {
        var future = FutureSnapshot(UserDataSnapshotJson.Prepare(Wall(Tile("a"))));
        if (damage == "digest")
        {
            var changed = future.Files.ToDictionary(pair => pair.Key, pair => pair.Value);
            changed["wall.json"] = [.. changed["wall.json"], (byte)' '];
            future = future with { Files = changed };
        }
        else if (damage == "snapshot-id") future = future with { Manifest = future.Manifest with { SnapshotId = new string('A', 64) } };
        else
        {
            var changed = future.Files.ToDictionary(pair => pair.Key, pair => pair.Value);
            var json = Encoding.UTF8.GetString(changed["wall.json"]);
            if (damage == "missing")
            {
                var node = JsonNode.Parse(json)!; node.AsObject().Remove("lanes"); json = node.ToJsonString();
            }
            else if (damage == "duplicate-envelope") json = json.Insert(json.IndexOf('{') + 1, "\"columns\":1,");
            else json = json.Replace("\"id\": \"a\"", "\"id\": \"a\", \"id\": \"a\"", StringComparison.Ordinal);
            changed["wall.json"] = Encoding.UTF8.GetBytes(json);
            future = RebuildFutureSnapshot(future.Manifest, JsonNode.Parse(future.ManifestBytes)!, changed);
        }
        void Read() => UserDataSnapshotJson.Read(future.Manifest,
            name => future.Files[name], _ => throw new InvalidOperationException());
        if (damage == "missing")
            Assert.ThrowsAny<IOException>(Read); // A newer structure cannot be interpreted safely.
        else
            Assert.Throws<InvalidDataException>(Read); // Digests, identity and duplicate fields remain corruption.
    }

    [Fact]
    public void UninterpretableFutureMemberCannotBeReplacedByTrustedOlderSnapshotOrSwept()
    {
        var (store, files, _, root) = Harness(); var initial = Wall(); store.InitializeFresh(initial);
        var olderManifest = files.ReadAllBytes(Path.Combine(root, "state.json"));
        var future = FutureSnapshot(UserDataSnapshotJson.Prepare(Wall(Tile("future")) with { Generation = 1 }), nodes =>
            nodes["wall.json"]["lanes"]![0]!["groups"]![0]!["members"]![0]!["$kind"] = "future-widget");
        InstallFixture(files, root, future);
        files.CreateDirectory(Path.Combine(root, "Recovery"));
        files.WriteAllBytes(Path.Combine(root, "Recovery", "state.prev.json"), olderManifest);
        var stage = Path.Combine(root, "Staging", new string('b', 32)); files.CreateDirectory(stage);
        files.WriteAllBytes(Path.Combine(stage, "future-material.bin"), [1, 2, 3]);
        var before = files.SnapshotFiles();
        var blocked = store.Open();
        Assert.Equal(WallOpenStatus.UnsupportedFormatBlocked, blocked.Status);
        Assert.Null(blocked.Document);
        AssertSameFileBytes(before, files.SnapshotFiles());
    }

    [Fact]
    public void UnknownRecoveryProtocolKeepsItsJournalAndMaterialsUntouched()
    {
        var (store, files, root, _, journalPath, _) = CreateObservedJournalScenario(leaveStagingJournal: true);
        var journal = JsonNode.Parse(files.ReadAllBytes(journalPath))!;
        journal["schemaVersion"] = 105;
        journal["futureOperations"] = JsonNode.Parse("[{\"kind\":\"future-resource\",\"payload\":1}]");
        files.WriteAllBytes(journalPath, Encoding.UTF8.GetBytes(journal.ToJsonString()));
        var before = files.SnapshotFiles();
        Assert.Equal(WallOpenStatus.RecoveryBlocked, store.Open().Status);
        AssertSameFileBytes(before, files.SnapshotFiles());
    }

    private static PreparedUserDataSnapshot FutureSnapshot(PreparedUserDataSnapshot source,
        Action<Dictionary<string, JsonNode>>? mutate = null, bool addUnknownFields = true)
    {
        var nodes = source.Files.ToDictionary(pair => pair.Key, pair => JsonNode.Parse(pair.Value)!, StringComparer.Ordinal);
        var manifest = JsonNode.Parse(source.ManifestBytes)!;
        manifest["schemaVersion"] = 104;
        nodes["settings.json"]["schemaVersion"] = 105;
        nodes["wall.json"]["schemaVersion"] = 101;
        nodes["recycle-bin.json"]["schemaVersion"] = 102;
        if (addUnknownFields)
        {
            manifest["futureManifest"] = JsonNode.Parse("{\"flags\":[1,2],\"optional\":null}");
            manifest["wall"]!["futureDigest"] = "digest-extension";
            foreach (var (name, node) in nodes) node["futureEnvelope"] = name;
            nodes["settings.json"]["value"]!["futureSetting"] = "setting-extension";
            var lanes = nodes["wall.json"]["lanes"]!.AsArray();
            for (var index = 0; index < lanes.Count; index++)
            {
                lanes[index]!["futureLane"] = "lane-" + index;
                foreach (var group in lanes[index]!["groups"]!.AsArray())
                {
                    group!["futureGroup"] = (string?)group["id"];
                    foreach (var member in group["members"]!.AsArray()) AddFutureMemberFields(member!);
                }
            }
            foreach (var member in nodes["recycle-bin.json"]["members"]!.AsArray()) AddFutureMemberFields(member!);
        }
        mutate?.Invoke(nodes);
        var bytes = nodes.ToDictionary(pair => pair.Key,
            pair => Encoding.UTF8.GetBytes(pair.Value.ToJsonString(UserDataSnapshotJson.Options)), StringComparer.Ordinal);
        return RebuildFutureSnapshot(source.Manifest, manifest, bytes);
    }

    private static void AddFutureMemberFields(JsonNode member)
    {
        member["futureMember"] = (string?)member["id"];
        member["visual"]!["futureVisual"] = "visual-extension";
        member["images"]!["futureImage"] = JsonNode.Parse("{\"mode\":\"future\",\"values\":[null,3]}");
        member["imageDisplay"]!["futureDisplay"] = "display-extension";
    }

    private static PreparedUserDataSnapshot RebuildFutureSnapshot(UserDataManifest source, JsonNode manifest,
        IReadOnlyDictionary<string, byte[]> files)
    {
        foreach (var (name, property) in new[] { ("settings.json", "settings"), ("wall.json", "wall"), ("recycle-bin.json", "recycleBin") })
        {
            manifest[property]!["length"] = files[name].LongLength;
            manifest[property]!["sha256"] = UserDataSnapshotJson.Hash(files[name]);
        }
        var identity = Encoding.UTF8.GetBytes($"TileWall.Snapshot/1\n{source.Generation}\n{(string?)manifest["settings"]!["sha256"]}\n{(string?)manifest["wall"]!["sha256"]}\n{(string?)manifest["recycleBin"]!["sha256"]}\n");
        manifest["snapshotId"] = UserDataSnapshotJson.Hash(identity);
        var bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString(UserDataSnapshotJson.Options));
        // Deserialize directly so fixtures do not depend on production acceptance checks.
        return new(JsonSerializer.Deserialize<UserDataManifest>(bytes, UserDataSnapshotJson.Options)!, bytes, files);
    }

    private static JsonNode FutureDiskJson(InMemoryFileStore files, string root, string name)
    {
        var manifest = JsonNode.Parse(files.ReadAllBytes(Path.Combine(root, "state.json")))!;
        return JsonNode.Parse(files.ReadAllBytes(Path.Combine(root, "Snapshots", (string)manifest["snapshotId"]!, name)))!;
    }

    private static void AssertFutureEnvelopeFields(InMemoryFileStore files, string root)
    {
        var manifest = JsonNode.Parse(files.ReadAllBytes(Path.Combine(root, "state.json")))!;
        Assert.Equal(104, (int)manifest["schemaVersion"]!);
        Assert.Equal("{\"flags\":[1,2],\"optional\":null}", manifest["futureManifest"]!.ToJsonString());
        Assert.Equal("digest-extension", (string?)manifest["wall"]!["futureDigest"]);
        foreach (var (name, version) in new[] { ("settings.json", 105), ("wall.json", 101), ("recycle-bin.json", 102) })
        {
            var json = FutureDiskJson(files, root, name);
            Assert.Equal(version, (int)json["schemaVersion"]!);
            Assert.Equal(name, (string?)json["futureEnvelope"]);
        }
        Assert.Equal("setting-extension", (string?)FutureDiskJson(files, root, "settings.json")["value"]!["futureSetting"]);
    }

    private static void AssertFutureMemberFields(JsonNode member, string id)
    {
        Assert.Equal(id, (string?)member["futureMember"]);
        Assert.Equal("visual-extension", (string?)member["visual"]!["futureVisual"]);
        Assert.Equal("{\"mode\":\"future\",\"values\":[null,3]}", member["images"]!["futureImage"]!.ToJsonString());
        Assert.Equal("display-extension", (string?)member["imageDisplay"]!["futureDisplay"]);
    }
}
