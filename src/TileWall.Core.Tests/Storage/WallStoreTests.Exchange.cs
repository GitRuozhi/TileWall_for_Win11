using System.Text;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using TileWall.Core.Exchange;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Storage;

public sealed partial class WallStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Schema4OpenPreservesBomOpaqueUrlButStillRejectsInvalidUrl(bool invalidUrl)
    {
        var (store, files, _, root) = Harness();
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Compatibility", "wall-schema4-v035.json");
        var json = JsonNode.Parse(File.ReadAllBytes(fixture))!;
        json["lanes"]![0]!["groups"]![0]!["members"]![0]!["entry"] = JsonNode.Parse("""
            {"objectId":"tile-035","relativePath":"Objects/tile-035/Saved tile.url","kind":1}
            """);
        var document = WallJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())) with { RecycleBinEnabled = true };
        SnapshotTestData.Install(files, root, document);
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.Latin1.GetBytes("[InternetShortcut]\r\nIconFile=C:\\Samples\\caf\u00E9.ico\r\n" +
            (invalidUrl ? "URL=file:///C:/blocked\r\n" : ""))];
        var entryPath = Path.Combine(root, "Objects", "tile-035", "Saved tile.url");
        files.WriteAllBytes(entryPath, bytes);
        var result = store.Open();
        Assert.Equal(invalidUrl ? WallOpenStatus.RecoveryBlocked : WallOpenStatus.Loaded, result.Status);
        Assert.Equal(bytes, files.ReadAllBytes(entryPath));
        if (invalidUrl) Assert.Null(result.Document);
        else
        {
            Assert.Equal(35, result.Document!.Generation);
            Assert.True(result.Document.Settings.ShowGrid);
            Assert.Single(result.Document.InactiveMembers);
            Assert.Equal("opaque-url:" + Convert.ToHexString(SHA256.HashData(bytes)),
                store.ObservePackageEntry(ManagedEntryKind.Url, bytes).Key);
        }
    }

    [Fact]
    public async Task VerifiedPackageEntryPreservesOpaqueBytesInJointTransactionAndRollsBackOnSwapFailure()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall(); store.InitializeFresh(initial);
        var fixture = Path.Combine(Path.GetTempPath(), "current-opaque-" + Guid.NewGuid().ToString("N") + ".twpack");
        CreateCurrentOpaquePackage(fixture);
        try
        {
        var reader = new WallPackageReader((kind, bytes, token) => Task.FromResult(store.ObservePackageEntry(kind, bytes, token)),
            (_, _, _) => Task.FromResult(true));
        var preview = (await reader.ReadPreviewAsync(fixture)).Preview!;
        using var batch = await reader.OpenImportAsync(preview);
        var source = await batch.ReadEntryAsync("tile-v1");
        var candidate = Wall(Tile("local-id", "Local title")) with { Generation = 1 };
        var request = Plan(store, initial, candidate, new EntryChange.FromPackage("local-id", source));
        files.EntryFault = EntryFaultPoint.BeforeEntrySwap;
        Assert.Equal(WallCommitStatus.Rejected, store.Commit(initial, request).Status);
        Assert.Equal(0, store.Open().Document!.Generation);
        var committed = store.Commit(initial, request);
        Assert.Equal(WallCommitStatus.Committed, committed.Status);
        var member = committed.CommittedDocument!.Lanes[0].Groups[0].Members[0];
        Assert.Equal("local-id", member.Entry!.ObjectId);
        Assert.EndsWith("Local title.url", member.Entry.RelativePath);
        Assert.Equal("[InternetShortcut]\r\nIconFile=C:\\Samples\\icon.ico\r\n",
            Encoding.UTF8.GetString(files.ReadAllBytes(EntryPath(root, member.Entry))));
        Assert.Equal(1, store.Open().Document!.Generation);
        }
        finally { File.Delete(fixture); }
    }

    private static void CreateCurrentOpaquePackage(string path)
    {
        var content = new WallPackageContent
        {
            Columns = 1, Rows = 6, Lanes = [new(["group"])], Groups = [new("group", null, ["tile-v1"])],
            Members = [new() { Id = "tile-v1", Kind = "tile", LocalBounds = new(0, 0, 2, 2), EntryKind = ManagedEntryKind.Url }],
        };
        var wall = WallPackageJson.Serialize(content);
        var entry = Encoding.UTF8.GetBytes("[InternetShortcut]\r\nIconFile=C:\\Samples\\icon.ico\r\n");
        var files = new WallPackageFile[]
        {
            new("wall.json", wall.Length, WallPackageJson.Hash(wall), "wall"),
            new("objects/tile-v1/entry.url", entry.Length, WallPackageJson.Hash(entry), "entry"),
        };
        var manifest = new WallPackageManifest { AppVersion = "test", CreatedUtc = DateTimeOffset.UtcNow,
            Files = files, ContentFingerprint = WallPackageJson.Fingerprint(content, files) };
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Write(string name, byte[] bytes) { using var target = zip.CreateEntry(name).Open(); target.Write(bytes); }
        Write("manifest.json", WallPackageJson.Serialize(manifest)); Write("wall.json", wall); Write("objects/tile-v1/entry.url", entry);
    }

    [Fact]
    public void PackageEntryObservationRejectsCacheReparseAndCleansMalformedShortcut()
    {
        var (store, files, _, root) = Harness();
        files.ReparsePaths.Add(Path.Combine(root, "Cache"));
        Assert.Throws<IOException>(() => store.ObservePackageEntry(ManagedEntryKind.Lnk, Encoding.UTF8.GetBytes("bad")));
        Assert.Empty(files.FilePaths);
        files.ReparsePaths.Clear();
        Assert.ThrowsAny<Exception>(() => store.ObservePackageEntry(ManagedEntryKind.Lnk, Encoding.UTF8.GetBytes("bad")));
        Assert.Empty(files.FilePaths);
    }
}
