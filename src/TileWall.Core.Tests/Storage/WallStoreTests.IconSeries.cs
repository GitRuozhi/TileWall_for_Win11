using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Storage;

public sealed partial class WallStoreTests
{
    private static PreparedAutomaticIconSeries IconSeries()
    {
        var assets = new[] { OriginalImage(), PreparedMediaAsset.FromVerifiedImage(new byte[] { 1, 2, 3 }, ".png"),
            PreparedMediaAsset.FromVerifiedImage(new byte[] { 4, 5, 6 }, ".png") };
        return new(new("native:confirmed", assets.Select((asset, index) =>
            new AutomaticIconFrame(asset.Reference, 32 << index, 32 << index)).ToArray(), 1, 2, 2), assets);
    }

    [Fact]
    public void ConfirmedSeriesPersistsAllFramesAcrossRetentionAndRestart()
    {
        var (store, files, links, root) = Harness();
        var initial = Wall(); Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
        var prepared = IconSeries();
        var tile = Tile("series") with { Images = new() { IconSeries = prepared.Series } };
        var added = store.Commit(initial, new(0, Wall(tile) with { Generation = 1 }, [], prepared.Assets));
        Assert.Equal(WallCommitStatus.Committed, added.Status);
        var current = added.CommittedDocument!;
        var retained = Assert.Single(current.EnumerateAllMembers());
        var saved = store.Commit(current, new(1, current with
        { Generation = 2, Lanes = [new() { Groups = [] }], InactiveMembers = [retained] }, []));
        Assert.Equal(WallCommitStatus.Committed, saved.Status);
        var reopened = new WallStore(files, links, root).Open();
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        var series = Assert.Single(reopened.Document!.InactiveMembers).Images.IconSeries!;
        Assert.Equal("native:confirmed", series.Identity); Assert.Equal(1, series.SelectedFrame);
        Assert.Equal(2, series.Columns); Assert.Equal(2, series.Rows);
        Assert.Equal(prepared.Series.Frames.ToArray(), series.Frames.ToArray());
        Assert.All(prepared.Assets, asset => Assert.True(files.Exists(store.ManagedMediaPath(asset.Reference))));
    }

    [Fact]
    public void MissingUnselectedFrameRejectsWholeSeriesAndPublicationFailureRollsBackEveryNewFrame()
    {
        var (store, files, root) = FaultedHarness();
        var initial = Wall(); store.InitializeFresh(initial);
        var prepared = IconSeries();
        var next = Wall(Tile("series") with { Images = new() { IconSeries = prepared.Series } }) with { Generation = 1 };
        Assert.Equal(WallCommitStatus.Rejected,
            store.Commit(initial, new(0, next, [], [prepared.Assets[1]])).Status);
        files.Rules.Add((kind, _, destination) => kind == "move" && destination == Path.Combine(root, "state.json"));
        Assert.Equal(WallCommitStatus.Rejected, store.Commit(initial, new(0, next, [], prepared.Assets)).Status);
        Assert.All(prepared.Assets, asset => Assert.False(files.Exists(store.ManagedMediaPath(asset.Reference))));
        Assert.Equal(0, store.Open().Document!.Generation);
    }

    [Fact]
    public void CorruptUnselectedFrameBlocksReopenInsteadOfReacquiringAnExternalSource()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall(); store.InitializeFresh(initial); var prepared = IconSeries();
        var saved = store.Commit(initial, new(0, Wall(Tile("series") with
            { Images = new() { IconSeries = prepared.Series } }) with { Generation = 1 }, [], prepared.Assets));
        Assert.Equal(WallCommitStatus.Committed, saved.Status);
        var before = files.ReadAllBytes(Path.Combine(root, "state.json"));
        files.WriteAllBytes(store.ManagedMediaPath(prepared.Assets[0].Reference), [9]);
        Assert.Equal(WallOpenStatus.RecoveryBlocked, store.Open().Status);
        Assert.Equal(before, files.ReadAllBytes(Path.Combine(root, "state.json")));
    }

    [Theory]
    [InlineData("single")]
    [InlineData("none")]
    [InlineData("package")]
    [InlineData("duplicate")]
    [InlineData("selected")]
    public void InvalidSeriesBindingsAndMetadataCannotBePublished(string invalid)
    {
        var (store, _, _, _) = Harness(); var initial = Wall(); store.InitializeFresh(initial);
        var prepared = IconSeries(); var series = prepared.Series;
        var tile = Tile("series") with { Images = new() { IconSeries = series } };
        tile = invalid switch
        {
            "single" => tile with { Images = tile.Images with { Kind = MemberImageSourceKind.Single, Media = prepared.Assets[0].Reference } },
            "none" => tile with { Images = tile.Images with { Kind = MemberImageSourceKind.None } },
            "package" => tile with { ResourceOrigin = MemberResourceOrigin.Package },
            "duplicate" => tile with { Images = tile.Images with { IconSeries = series with { Frames = [series.Frames[0], series.Frames[0]] } } },
            _ => tile with { Images = tile.Images with { IconSeries = series with { SelectedFrame = series.Frames.Count } } },
        };
        var rejected = store.Commit(initial, new(0, Wall(tile) with { Generation = 1 }, [], prepared.Assets));
        Assert.Equal(WallCommitStatus.Rejected, rejected.Status);
        Assert.Contains("AUTOMATIC_ICON_SERIES", string.Join("; ", rejected.Diagnostics));
    }

    [Fact]
    public void FormatThreeSnapshotRetainsHistoricalBytesAndIdentityWithoutANullSeriesField()
    {
        var document = Wall(Tile("legacy"));
        var prepared = UserDataSnapshotJson.Prepare(document);
        var files = prepared.Files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var settings = JsonNode.Parse(files["settings.json"])!; settings["schemaVersion"] = 3;
        files["settings.json"] = Encoding.UTF8.GetBytes(settings.ToJsonString(UserDataSnapshotJson.Options));
        Assert.DoesNotContain("iconSeries", Encoding.UTF8.GetString(files["wall.json"]));
        UserDataFileDigest Digest(string name) => new(files[name].Length, UserDataSnapshotJson.Hash(files[name]));
        var settingsDigest = Digest("settings.json"); var wall = Digest("wall.json"); var recycle = Digest("recycle-bin.json");
        var identity = UserDataSnapshotJson.Hash(Encoding.UTF8.GetBytes($"TileWall.Snapshot/1\n0\n{settingsDigest.Sha256}\n{wall.Sha256}\n{recycle.Sha256}\n"));
        var manifest = new UserDataManifest(3, 0, identity, settingsDigest, wall, recycle);
        var (store, memory, _, root) = Harness();
        InstallFixture(memory, root, new(manifest, JsonSerializer.SerializeToUtf8Bytes(manifest, UserDataSnapshotJson.Options), files));
        var before = memory.SnapshotFiles();
        var reopened = store.Open(); Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.Null(Assert.Single(reopened.Document!.EnumerateAllMembers()).Images.IconSeries);
        AssertSameFileBytes(before, memory.SnapshotFiles());
    }

    [Fact]
    public void FormalMediaHasNoFormerImageByteQuota()
    {
        var reference = OriginalImage().Reference with { Length = 512L * 1024 * 1024 };
        Assert.True(PreparedMediaAsset.IsValidReference(reference));
        var prepared = PreparedMediaAsset.FromVerifiedImage(new byte[32 * 1024 * 1024 + 1], ".png");
        Assert.Equal(32 * 1024 * 1024 + 1, prepared.Reference.Length);
    }

    [Fact]
    public void ExportLeaseProtectsAllNativeFramesAndReleasesThemAfterTheDetachedSnapshotIsDisposed()
    {
        var (store, files, _, _) = Harness(); var initial = Wall(); store.InitializeFresh(initial);
        var prepared = IconSeries();
        var first = store.Commit(initial, new(0, Wall(Tile("series") with { Images = new() { IconSeries = prepared.Series } })
            with { Generation = 1 }, [], prepared.Assets)).CommittedDocument!;
        using (var snapshot = store.CapturePackageSnapshot(first, 1, ["series"]))
        {
            Assert.NotNull(Assert.Single(snapshot.AutomaticImageMembers).Images.IconSeries);
            var removed = store.Commit(first, new(1, Wall() with { Generation = 2 }, [])).CommittedDocument!;
            var latest = store.Commit(removed, new(2, removed with
                { Generation = 3, Settings = removed.Settings with { AlwaysOnTop = false } }, []));
            Assert.Equal(WallCommitStatus.Committed, latest.Status);
            Assert.All(prepared.Assets, asset => Assert.True(files.Exists(store.ManagedMediaPath(asset.Reference))));
            snapshot.CompleteAutomaticImages(new Dictionary<string, PreparedMediaAsset?> { ["series"] = prepared.Assets[0] });
            Assert.Equal(prepared.Assets[0].Reference, Assert.Single(snapshot.Content.Members).Images.Media);
        }
        Assert.Equal(WallOpenStatus.Loaded, store.Open().Status);
        Assert.All(prepared.Assets, asset => Assert.False(files.Exists(store.ManagedMediaPath(asset.Reference))));
    }
}
