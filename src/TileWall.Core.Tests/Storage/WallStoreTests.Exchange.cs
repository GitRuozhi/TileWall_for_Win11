using System.Text;
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
        files.WriteAllBytes(Path.Combine(root, "config.json"), Encoding.UTF8.GetBytes(json.ToJsonString()));
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
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Compatibility", "wall-package-v1.twpack");
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
