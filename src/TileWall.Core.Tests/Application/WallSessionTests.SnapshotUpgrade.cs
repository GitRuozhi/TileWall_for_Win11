using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TileWall.Core.Application;
using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Application;

public sealed partial class WallSessionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void HistoricalInitialOpenUpgradesBeforePublishingAndAllowsRepeatedSaves(int version)
    {
        var (session, files, root) = Harness();
        var original = InstallHistoricalSnapshot(files, root, version);
        var store = new WallStore(files, new FakeLinkFileService(files), root);
        var expected = store.Open().Document!;
        var opened = session.Open(8, 8);

        Assert.Equal(WallOpenStatus.Loaded, opened.Status);
        Assert.False(opened.InitializedFresh);
        Assert.True(opened.WasUpgraded);
        Assert.Equal(1, session.Current.Generation);
        Assert.Equal(UserDataManifest.CurrentFormatVersion, session.Current.StorageMetadata!.ManifestVersion);
        Assert.Equal(UserDataManifest.CurrentFormatVersion, session.Current.StorageMetadata.SettingsVersion);
        Assert.Equal(WallJson.Serialize(expected.ActiveView() with { Generation = 1 }), WallJson.Serialize(session.Current));
        Assert.Equal(original, files.ReadAllBytes(Path.Combine(root, "Recovery", "state.prev.json")));
        Assert.Equal(expected.InactiveMembers, store.Open().Document!.InactiveMembers);

        for (var save = 0; save < 2; save++)
        {
            var candidate = session.Current with { Generation = session.Current.Generation + 1,
                Settings = session.Current.Settings with { ShowGrid = !session.Current.Settings.ShowGrid } };
            Assert.True(session.Commit(new(candidate, WallLayoutEngine.Measure(candidate))).Committed);
        }
        var beforeReopen = files.ReadAllBytes(Path.Combine(root, "state.json"));
        var reopened = new WallSession(root, files, new FakeLinkFileService(files));
        Assert.Equal(WallOpenStatus.Loaded, reopened.Open(8, 8).Status);
        Assert.Equal(3, reopened.Current.Generation);
        Assert.Equal(beforeReopen, files.ReadAllBytes(Path.Combine(root, "state.json")));
        Assert.Equal(expected.InactiveMembers, store.Open().Document!.InactiveMembers);
    }

    [Fact]
    public void StartupCombinesFormatUpgradeAndLegacyRecycleEnableInOneGeneration()
    {
        var (session, files, root) = Harness();
        InstallHistoricalSnapshot(files, root, 4, recycleEnabled: false);
        var opened = session.Open(1, 4);
        Assert.True(opened.WasUpgraded);
        Assert.True(session.Current.RecycleBinEnabled);
        Assert.Equal(1, session.Current.Generation);
        var restarted = new WallSession(root, files, new FakeLinkFileService(files));
        Assert.False(restarted.Open(1, 4).WasUpgraded);
        Assert.Equal(1, restarted.Current.Generation);
    }

    [Fact]
    public void UpgradePublicationFailurePreservesHistoricalStateAndRetryCompletesUpgrade()
    {
        var (session, files, root) = Harness();
        var original = InstallHistoricalSnapshot(files, root, 1);
        var before = files.SnapshotFiles();
        files.InjectedFault = FaultPoint.BeforeMove;

        Assert.Equal(WallOpenStatus.OpenFailed, session.Open(1, 4).Status);
        Assert.Throws<InvalidOperationException>(() => session.Current);
        Assert.Equal(original, files.ReadAllBytes(Path.Combine(root, "state.json")));
        foreach (var (path, bytes) in before) Assert.Equal(bytes, files.ReadAllBytes(path));

        files.InjectedFault = FaultPoint.None;
        Assert.Equal(WallOpenStatus.Loaded, session.Open(1, 4).Status);
        Assert.Equal(1, session.Current.Generation);
        Assert.Equal(UserDataManifest.CurrentFormatVersion, session.Current.StorageMetadata!.ManifestVersion);
    }

    [Fact]
    public void UpgradeCleanupFailurePublishesCurrentMetadataAndNextSaveSucceeds()
    {
        var (session, files, root) = Harness();
        InstallHistoricalSnapshot(files, root, 1);
        files.EntryFault = EntryFaultPoint.BeforeStagingCleanup;
        Assert.Equal(WallOpenStatus.Loaded, session.Open(1, 4).Status);
        Assert.Equal(1, session.Current.Generation);
        var candidate = session.Current with { Generation = 2, Settings = session.Current.Settings with { ShowGrid = true } };
        Assert.True(session.Commit(new(candidate, WallLayoutEngine.Measure(candidate))).Committed);
        Assert.Equal(2, session.Current.Generation);
    }

    private static byte[] InstallHistoricalSnapshot(InMemoryFileStore files, string root, int version,
        bool recycleEnabled = true)
    {
        TileMember Member(string id) => new()
        {
            Id = id, TargetInput = "https://" + id + ".example", LocalBounds = new GridRect(0, 0, 1, 1),
            Visual = new() { Title = id },
        };
        var document = WallDocument.Empty(1, 4) with
        {
            Lanes = [new() { Groups = [new() { Id = "kept-group", Members = [Member("active")] }] }],
            RecycleBinEnabled = recycleEnabled,
            InactiveMembers = recycleEnabled ? [Member("retained")] : [],
        };
        var prepared = UserDataSnapshotJson.Prepare(document);
        var bytes = prepared.Files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var settings = JsonNode.Parse(bytes["settings.json"])!;
        settings["schemaVersion"] = version;
        if (version < 5) settings["value"]!.AsObject().Remove("redirectTaskbarStartButton");
        if (version == 1) settings["value"]!.AsObject().Remove("secondaryHotKey");
        bytes["settings.json"] = Encoding.UTF8.GetBytes(settings.ToJsonString(UserDataSnapshotJson.Options));
        if (version < 3)
            foreach (var name in new[] { "wall.json", "recycle-bin.json" })
            {
                var node = JsonNode.Parse(bytes[name])!;
                var members = name == "wall.json"
                    ? node["lanes"]!.AsArray().SelectMany(lane => lane!["groups"]!.AsArray()).SelectMany(group => group!["members"]!.AsArray())
                    : node["members"]!.AsArray();
                foreach (var member in members)
                    foreach (var field in new[] { "resourceOrigin", "sourceShortcutPath", "targetDisplayName" }) member!.AsObject().Remove(field);
                bytes[name] = Encoding.UTF8.GetBytes(node.ToJsonString(UserDataSnapshotJson.Options));
            }
        UserDataFileDigest Digest(string name) => new(bytes[name].LongLength, UserDataSnapshotJson.Hash(bytes[name]));
        var settingsDigest = Digest("settings.json"); var wall = Digest("wall.json"); var recycle = Digest("recycle-bin.json");
        var identity = UserDataSnapshotJson.Hash(Encoding.UTF8.GetBytes($"TileWall.Snapshot/1\n0\n{settingsDigest.Sha256}\n{wall.Sha256}\n{recycle.Sha256}\n"));
        var manifest = new UserDataManifest(version, 0, identity, settingsDigest, wall, recycle);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, UserDataSnapshotJson.Options);
        files.CreateDirectory(root);
        files.CreateDirectory(Path.Combine(root, "Snapshots"));
        var directory = Path.Combine(root, "Snapshots", identity);
        files.CreateDirectory(directory);
        foreach (var (name, value) in bytes) files.WriteAllBytes(Path.Combine(directory, name), value);
        files.WriteAllBytes(Path.Combine(root, "state.json"), manifestBytes);
        return manifestBytes;
    }
}
