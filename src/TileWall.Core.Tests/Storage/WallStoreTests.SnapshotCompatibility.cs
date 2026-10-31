using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TileWall.Core.Settings;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Storage;

public sealed partial class WallStoreTests
{
    [Fact]
    public void V2MembersRetainTheirExactHistoricalSnapshotIdentityWithoutNewPresentationFields()
    {
        var member = Tile("legacy") with { TargetInput = @"C:\Shortcuts\Legacy.lnk" };
        var document = Wall(member) with { InactiveMembers = [Tile("retained")] };
        var current = UserDataSnapshotJson.Prepare(document);
        var files = current.Files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var settings = JsonNode.Parse(files["settings.json"])!; settings["schemaVersion"] = 2;
        files["settings.json"] = Encoding.UTF8.GetBytes(settings.ToJsonString(UserDataSnapshotJson.Options));
        foreach (var name in new[] { "wall.json", "recycle-bin.json" })
        {
            var json = JsonNode.Parse(files[name])!;
            var members = name == "wall.json"
                ? json["lanes"]!.AsArray().SelectMany(lane => lane!["groups"]!.AsArray()).SelectMany(group => group!["members"]!.AsArray())
                : json["members"]!.AsArray();
            foreach (var value in members)
            foreach (var field in new[] { "resourceOrigin", "sourceShortcutPath", "targetDisplayName" }) value!.AsObject().Remove(field);
            files[name] = Encoding.UTF8.GetBytes(json.ToJsonString(UserDataSnapshotJson.Options));
        }
        UserDataFileDigest Digest(string name) => new(files[name].Length, UserDataSnapshotJson.Hash(files[name]));
        var settingsDigest = Digest("settings.json"); var wallDigest = Digest("wall.json"); var recycleDigest = Digest("recycle-bin.json");
        var id = UserDataSnapshotJson.Hash(Encoding.UTF8.GetBytes($"TileWall.Snapshot/1\n{document.Generation}\n{settingsDigest.Sha256}\n{wallDigest.Sha256}\n{recycleDigest.Sha256}\n"));
        var manifest = new UserDataManifest(2, document.Generation, id, settingsDigest, wallDigest, recycleDigest);
        var restored = UserDataSnapshotJson.Read(manifest, name => files[name], _ => throw new InvalidOperationException());
        Assert.Equal(member.TargetInput, restored.EnumerateAllMembers().First().TargetInput);
        Assert.All(restored.EnumerateAllMembers(), item =>
        { Assert.Equal(MemberResourceOrigin.Local, item.ResourceOrigin); Assert.Null(item.SourceShortcutPath); Assert.Null(item.TargetDisplayName); });
        var (store, memory, _, root) = Harness();
        InstallFixture(memory, root, new(manifest, JsonSerializer.SerializeToUtf8Bytes(manifest, UserDataSnapshotJson.Options), files));
        var before = memory.SnapshotFiles();
        Assert.Equal(WallOpenStatus.Loaded, store.Open().Status);
        AssertSameFileBytes(before, memory.SnapshotFiles());
    }

    [Fact]
    public void CurrentSnapshotPersistsPackageOriginAndPresentationForActiveAndRetainedMembers()
    {
        var member = Tile("package") with { ResourceOrigin = MemberResourceOrigin.Package, TargetInput = @"\\offline\share\app.exe",
            SourceShortcutPath = @"\\offline\share\app.lnk", TargetDisplayName = "Preserved title",
            Visual = new() { TitleMode = MemberTitleMode.Automatic }, Images = new() { Kind = MemberImageSourceKind.None } };
        var document = Wall(member) with { InactiveMembers = [member with { Id = "retained" }] };
        var snapshot = UserDataSnapshotJson.Prepare(document);
        Assert.Equal(UserDataManifest.CurrentFormatVersion, snapshot.Manifest.SchemaVersion);
        var restored = UserDataSnapshotJson.Read(snapshot.Manifest, name => snapshot.Files[name], _ => throw new InvalidOperationException());
        Assert.All(restored.EnumerateAllMembers(), item =>
        {
            Assert.Equal(MemberResourceOrigin.Package, item.ResourceOrigin);
            Assert.Equal(member.SourceShortcutPath, item.SourceShortcutPath);
            Assert.Equal("Preserved title", MemberPresentationRules.ResolveTitle(item));
            Assert.Equal(MemberImageSourceKind.None, item.Images.Kind);
        });
    }

    [Fact]
    public void RealV1WriterFixtureKeepsItsCanonicalIdentityAndBytesOnOpen()
    {
        var legacy = ReadV1Fixture();
        Assert.Equal("C031A401EA24603BF99B4BE5E82F121E11C5EECFD879168F57B1E0CAFF070013",
            UserDataSnapshotJson.Hash(legacy.ManifestBytes));
        Assert.Equal("FC098433BF0592D12158BC78EAFA8A9C6706228FE974234A6701BA0476F255A0", legacy.Manifest.SnapshotId);
        var (store, files, _, root) = Harness();
        InstallFixture(files, root, legacy);
        var unchanged = files.SnapshotFiles();

        // Read reconstructs V1 canonical identity. Using the V2 settings shape here
        // would reject these independently frozen bytes before Open could succeed.
        var opened = store.Open();
        Assert.Equal(WallOpenStatus.Loaded, opened.Status);
        Assert.Equal("Win+Oem3", opened.Document!.Settings.HotKey);
        Assert.Equal(HotKeyGesture.DefaultSecondary.ToConfigString(), opened.Document.Settings.SecondaryHotKey);
        AssertSameFileBytes(unchanged, files.SnapshotFiles());
    }

    [Theory]
    [InlineData("Ctrl+Alt+F9")]
    [InlineData("Win+Ctrl+Oem3")]
    public void V1CustomPrimaryIsPreservedEvenWhenItEqualsTheNewSecondDefault(string primary)
    {
        var (store, files, _, root) = Harness();
        InstallFixture(files, root, ReadV1Fixture(primary));
        var unchanged = files.SnapshotFiles();
        var opened = store.Open();
        Assert.Equal(WallOpenStatus.Loaded, opened.Status);
        Assert.Equal(primary, opened.Document!.Settings.HotKey);
        Assert.Equal("Win+Ctrl+Oem3", opened.Document.Settings.SecondaryHotKey);
        AssertSameFileBytes(unchanged, files.SnapshotFiles());
    }

    [Fact]
    public void NextJointCommitUpgradesV1ToCurrentAndReopensBothSavedKeys()
    {
        var (store, files, _, root) = Harness();
        var legacy = ReadV1Fixture("Ctrl+Alt+F9");
        InstallFixture(files, root, legacy);
        var current = store.Open().Document!;
        var candidate = current with
        {
            Generation = current.Generation + 1,
            Settings = current.Settings with { SecondaryHotKey = "Ctrl+Alt+F10" },
        };
        var saved = store.Commit(current, new(current.Generation, candidate, []));
        Assert.Equal(WallCommitStatus.Committed, saved.Status);
        Assert.Equal(legacy.ManifestBytes, files.ReadAllBytes(Path.Combine(root, "Recovery", "state.prev.json")));
        var manifest = UserDataSnapshotJson.ReadManifest(files.ReadAllBytes(Path.Combine(root, "state.json")));
        Assert.Equal(UserDataManifest.CurrentFormatVersion, manifest.SchemaVersion);
        var directory = Path.Combine(root, "Snapshots", manifest.SnapshotId);
        foreach (var (name, version) in new[] { ("settings.json", UserDataManifest.CurrentFormatVersion), ("wall.json", 1), ("recycle-bin.json", 1) })
        {
            using var json = JsonDocument.Parse(files.ReadAllBytes(Path.Combine(directory, name)));
            Assert.Equal(version, json.RootElement.GetProperty("schemaVersion").GetInt32());
        }
        var reopened = store.Open();
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.Equal("Ctrl+Alt+F9", reopened.Document!.Settings.HotKey);
        Assert.Equal("Ctrl+Alt+F10", reopened.Document.Settings.SecondaryHotKey);
        Assert.Equal(legacy.ManifestBytes, files.ReadAllBytes(Path.Combine(root, "Recovery", "state.prev.json")));
    }

    [Fact]
    public void V2BrokenCurrentRecoversItsCompleteV1PreviousAndPublishesV2()
    {
        var (store, files, _, root) = Harness();
        var legacy = ReadV1Fixture("Ctrl+Alt+F9");
        InstallFixture(files, root, legacy);
        var current = store.Open().Document!;
        var candidate = current with { Generation = 1, Settings = current.Settings with { SecondaryHotKey = "Ctrl+Alt+F10" } };
        Assert.Equal(WallCommitStatus.Committed, store.Commit(current, new(0, candidate, [])).Status);
        var manifest = UserDataSnapshotJson.ReadManifest(files.ReadAllBytes(Path.Combine(root, "state.json")));
        files.Delete(Path.Combine(root, "Snapshots", manifest.SnapshotId, "wall.json"));

        var recovered = store.Open();
        Assert.Equal(WallOpenStatus.RecoveredPrevious, recovered.Status);
        Assert.Equal("Ctrl+Alt+F9", recovered.Document!.Settings.HotKey);
        Assert.Equal("Win+Ctrl+Oem3", recovered.Document.Settings.SecondaryHotKey);
        Assert.Equal(UserDataManifest.CurrentFormatVersion, UserDataSnapshotJson.ReadManifest(files.ReadAllBytes(Path.Combine(root, "state.json"))).SchemaVersion);
        Assert.Equal(legacy.ManifestBytes, files.ReadAllBytes(Path.Combine(root, "Recovery", "state.prev.json")));
        var reopened = store.Open();
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.Equal(recovered.Document.Settings, reopened.Document!.Settings);
    }

    [Fact]
    public void V1ToV2PublicationFailureRollsBackToTheExactV1Manifest()
    {
        var (store, files, _, root) = Harness();
        var legacy = ReadV1Fixture("Ctrl+Alt+F9");
        InstallFixture(files, root, legacy);
        var current = store.Open().Document!;
        files.InjectedFault = FaultPoint.BeforeMove;
        var failed = store.Commit(current, new(0, current with
        {
            Generation = 1, Settings = current.Settings with { SecondaryHotKey = "Ctrl+Alt+F10" },
        }, []));
        Assert.Equal(WallCommitStatus.Rejected, failed.Status);
        Assert.Equal(legacy.ManifestBytes, files.ReadAllBytes(Path.Combine(root, "state.json")));
        files.InjectedFault = FaultPoint.None;
        var reopened = store.Open();
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.Equal(current.Settings, reopened.Document!.Settings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UninterpretableFutureManifestBlocksBeforeStagingSweepOrPreviousRecovery(bool futureIsPrevious)
    {
        var (store, files, _, root) = Harness();
        var legacy = ReadV1Fixture();
        InstallFixture(files, root, legacy);
        files.CreateDirectory(Path.Combine(root, "Recovery"));
        files.WriteAllBytes(Path.Combine(root, "Recovery", "state.prev.json"), legacy.ManifestBytes);
        var futurePath = futureIsPrevious ? Path.Combine(root, "Recovery", "state.prev.json") : Path.Combine(root, "state.json");
        files.WriteAllBytes(futurePath, Encoding.UTF8.GetBytes($"{{\"schemaVersion\":{UserDataManifest.CurrentFormatVersion + 1}}}"));
        // This future manifest has no basic structure. It must remain untouched,
        // even when a valid V1 fallback exists.
        var stage = Path.Combine(root, "Staging", new string('a', 32));
        files.CreateDirectory(Path.Combine(root, "Staging"));
        files.CreateDirectory(stage);
        files.WriteAllBytes(Path.Combine(stage, "future-material.bin"), [1, 2, 3]);
        var unchanged = files.SnapshotFiles();
        Assert.Equal(WallOpenStatus.UnsupportedFormatBlocked, store.Open().Status);
        AssertSameFileBytes(unchanged, files.SnapshotFiles());
        Assert.True(files.GetPathInfo(stage).Exists);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Shift+A")]
    public void V2DamagedSecondaryKeyDoesNotBecomeTheDefault(string? secondary)
    {
        var snapshot = UserDataSnapshotJson.Prepare(WallDocument.Empty(1, 1));
        var value = JsonNode.Parse(snapshot.Files["settings.json"])!;
        value["value"]!["secondaryHotKey"] = secondary;
        var damaged = ReplaceSettings(snapshot, Encoding.UTF8.GetBytes(value.ToJsonString(UserDataSnapshotJson.Options)));
        Assert.Throws<InvalidDataException>(() => UserDataSnapshotJson.Read(damaged.Manifest,
            name => damaged.Files[name], _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void V2MissingSecondKeyAndMismatchedSettingsVersionAreRejected()
    {
        var snapshot = UserDataSnapshotJson.Prepare(WallDocument.Empty(1, 1));
        var value = JsonNode.Parse(snapshot.Files["settings.json"])!;
        value["value"]!.AsObject().Remove("secondaryHotKey");
        var missing = ReplaceSettings(snapshot, Encoding.UTF8.GetBytes(value.ToJsonString(UserDataSnapshotJson.Options)));
        Assert.Throws<InvalidDataException>(() => UserDataSnapshotJson.Read(missing.Manifest,
            name => missing.Files[name], _ => throw new InvalidOperationException()));
        value = JsonNode.Parse(snapshot.Files["settings.json"])!;
        value["schemaVersion"] = 1;
        var mismatch = ReplaceSettings(snapshot, Encoding.UTF8.GetBytes(value.ToJsonString(UserDataSnapshotJson.Options)));
        Assert.Throws<InvalidDataException>(() => UserDataSnapshotJson.Read(mismatch.Manifest,
            name => mismatch.Files[name], _ => throw new InvalidOperationException()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Shift+A")]
    public void SecondKeyDoesNotExpandTheLegacyPrimaryValidationContract(string? primary)
    {
        var document = WallDocument.Empty(1, 1) with { Settings = new() { HotKey = primary } };
        var snapshot = UserDataSnapshotJson.Prepare(document);
        var restored = UserDataSnapshotJson.Read(snapshot.Manifest, name => snapshot.Files[name],
            _ => throw new InvalidOperationException());
        Assert.Equal(primary, restored.Settings.HotKey);
        Assert.Equal("Win+Ctrl+Oem3", restored.Settings.SecondaryHotKey);
    }

    [Fact]
    public void V1SettingsCannotSmuggleInTheNewField()
    {
        var snapshot = ReadV1Fixture();
        var value = JsonNode.Parse(snapshot.Files["settings.json"])!;
        value["value"]!["secondaryHotKey"] = "Ctrl+Alt+F10";
        var incompatible = ReplaceSettings(snapshot, Encoding.UTF8.GetBytes(value.ToJsonString(UserDataSnapshotJson.Options)));
        Assert.Throws<JsonException>(() => UserDataSnapshotJson.Read(incompatible.Manifest,
            name => incompatible.Files[name], _ => throw new InvalidOperationException()));
    }

    private static PreparedUserDataSnapshot ReadV1Fixture(string? primary = null)
    {
        // These four files came from the real previous executable. Keep their
        // original bytes; variants mutate just a value then rebuild its digest.
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Snapshots", "V1");
        var manifestBytes = File.ReadAllBytes(Path.Combine(directory, "state.json"));
        var manifest = UserDataSnapshotJson.ReadManifest(manifestBytes);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in new[] { "settings.json", "wall.json", "recycle-bin.json" })
            files[name] = File.ReadAllBytes(Path.Combine(directory, name));
        var snapshot = new PreparedUserDataSnapshot(manifest, manifestBytes, files);
        if (primary is null) return snapshot;
        var settings = Encoding.UTF8.GetString(files["settings.json"]);
        var oldValue = JsonSerializer.Serialize("Win+Oem3", UserDataSnapshotJson.Options);
        var replacement = JsonSerializer.Serialize(primary, UserDataSnapshotJson.Options);
        return ReplaceSettings(snapshot, Encoding.UTF8.GetBytes(settings.Replace("\"hotKey\": " + oldValue,
            "\"hotKey\": " + replacement, StringComparison.Ordinal)));
    }

    private static PreparedUserDataSnapshot ReplaceSettings(PreparedUserDataSnapshot source, byte[] settings)
    {
        var files = source.Files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        files["settings.json"] = settings;
        var digest = new UserDataFileDigest(settings.LongLength, UserDataSnapshotJson.Hash(settings));
        var identity = Encoding.UTF8.GetBytes($"TileWall.Snapshot/1\n{source.Manifest.Generation}\n{digest.Sha256}\n{source.Manifest.Wall.Sha256}\n{source.Manifest.RecycleBin.Sha256}\n");
        var manifest = source.Manifest with { Settings = digest, SnapshotId = UserDataSnapshotJson.Hash(identity) };
        return new(manifest, JsonSerializer.SerializeToUtf8Bytes(manifest, UserDataSnapshotJson.Options), files);
    }

    private static void InstallFixture(InMemoryFileStore files, string root, PreparedUserDataSnapshot snapshot)
    {
        var directory = Path.Combine(root, "Snapshots", snapshot.Manifest.SnapshotId);
        files.CreateDirectory(root);
        files.CreateDirectory(Path.Combine(root, "Snapshots"));
        files.CreateDirectory(directory);
        foreach (var (name, bytes) in snapshot.Files) files.WriteAllBytes(Path.Combine(directory, name), bytes);
        files.WriteAllBytes(Path.Combine(root, "state.json"), snapshot.ManifestBytes);
    }
}
