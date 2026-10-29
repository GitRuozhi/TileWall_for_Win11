using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TileWall.Core.Entries;
using TileWall.Core.Configuration;
using TileWall.Core.Exchange;
using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Exchange;

public sealed class WallPackageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wall-package-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aZ1sAAAAASUVORK5CYII=");
    public WallPackageTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("members")]
    [InlineData("groups")]
    [InlineData("references")]
    [InlineData("metadata")]
    public async Task StructureAndMetadataBudgetsRejectBeforeProducingAPreview(string budget)
    {
        var path = Path.Combine(_directory, "bounded.zip"); CreatePackage(path);
        var limits = budget switch
        {
            "members" => new WallPackageLimits { MaximumMembers = 2 },
            "groups" => new WallPackageLimits { MaximumGroups = 1 },
            "references" => new WallPackageLimits { MaximumReferences = 3 },
            _ => new WallPackageLimits { MaximumMetadataBytes = 10 },
        };
        if (budget == "groups")
            Mutate(path, payloads =>
            {
                var content = WallPackageJson.Deserialize<WallPackageContent>(payloads["wall.json"]);
                content = content with { Lanes = [new(["one", "two"])], Groups =
                    [new("one", "One", ["tile"]), new("two", "Two", ["puzzle", "clock"])] };
                payloads["wall.json"] = WallPackageJson.Serialize(content); RebuildManifest(payloads);
            });
        var result = await Harness().ReadPreviewAsync(path, limits);
        Assert.Equal(WallPackageReadStatus.InvalidPackage, result.Status);
        Assert.Null(result.Preview);
    }

    [Fact]
    public void PreflightAndGeometryRespectAlreadyCancelledOperations()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        using var bytes = new MemoryStream(new byte[22]);
        Assert.Throws<OperationCanceledException>(() => WallPackageZip.Preflight(bytes, new(), cancel.Token));
        Assert.Throws<OperationCanceledException>(() => WallLayoutEngine.ValidateGeometry(WallDocument.Empty(1, 20), token: cancel.Token));
    }

    [Fact]
    public void MaliciousOverlapDoesNotAllocateOneDiagnosticPerMemberPair()
    {
        var members = Enumerable.Range(0, 1000).Select(index => (WallMember)new ClockMember
            { Id = "clock-" + index, LocalBounds = new(0, 0, 1, 1) }).ToArray();
        var document = WallDocument.Empty(1, 20) with { Lanes = [new() { Groups = [new() { Id = "g", Members = members }] }] };
        var errors = WallLayoutEngine.ValidateGeometry(document);
        Assert.InRange(errors.Count, 1, members.Length);
        Assert.Contains("MEMBER_OVERLAP", errors);
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("Manifest.json")]
    [InlineData("MANIFEST.JSON")]
    public void GalleryCannotOverwriteItsOwnManifestOnWindows(string name)
    {
        var image = PreparedMediaAsset.FromVerifiedImage(Png, ".png");
        Assert.Throws<InvalidDataException>(() => PreparedManagedGallery.FromVerifiedFiles([new(name, image)]));
    }

    [Fact]
    public async Task LegacyDevelopmentPackageRequiresASupportedVersionWithoutMigration()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Compatibility");
        var reader = Harness();
        var result = await reader.ReadPreviewAsync(Path.Combine(fixture, "wall-package-v1.twpack"));
        Assert.Equal(WallPackageReadStatus.UnsupportedFormat, result.Status);
        Assert.Null(result.Preview);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReaderPreservesBomOpaqueUrlButStillRejectsInvalidUrl(bool invalidUrl)
    {
        var path = Path.Combine(_directory, "bom-opaque.twpack"); CreatePackage(path);
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("[InternetShortcut]\r\nIconFile=C:\\Samples\\icon.ico\r\n" +
            (invalidUrl ? "URL=file:///C:/blocked\r\n" : ""))];
        Mutate(path, payloads => { payloads["objects/tile/entry.url"] = bytes; RebuildManifest(payloads); });
        var reader = Harness();
        var result = await reader.ReadPreviewAsync(path);
        Assert.Equal(invalidUrl ? WallPackageReadStatus.InvalidPackage : WallPackageReadStatus.Valid, result.Status);
        if (invalidUrl) Assert.Null(result.Preview);
        else
        {
            var preview = result.Preview!;
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            Assert.Equal("opaque-url:" + hash, preview.Identities["tile"].Key);
            using var batch = await reader.OpenImportAsync(preview);
            Assert.Equal(hash, (await batch.ReadEntryAsync("tile")).Sha256);
        }
    }

    [Fact]
    public void LegacyDevelopmentDocumentPreservesMembersWithFixedRetention()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Compatibility", "wall-schema4-v035.json"));
        var document = WallJson.Deserialize(bytes);
        Assert.True(document.RecycleBinEnabled);
        Assert.Empty(WallLayoutEngine.ValidateGeometry(document));
        Assert.Equal(35, document.Generation);
        Assert.Equal("tile-035", document.Lanes[0].Groups[0].Members[0].Id);
        Assert.Equal(new GridRect(1, 1, 2, 2), document.Lanes[0].Groups[0].Members[0].LocalBounds);
        Assert.Equal("inactive-035", Assert.Single(document.InactiveMembers).Id);
        Assert.True(document.Settings.ShowGrid);
        Assert.False(document.Settings.AlwaysOnTop);
        Assert.Equal(WallLanguageMode.English, document.Settings.LanguageMode);
    }

    private WallPackageReader Harness(
        Func<ReadOnlyMemory<byte>, string, CancellationToken, Task<bool>>? verifyImageAsync = null)
    {
        var files = new InMemoryFileStore();
        var root = Path.Combine(_directory, "data");
        var store = new WallStore(files, new FakeLinkFileService(files), root);
        return new WallPackageReader((kind, bytes, token) => Task.FromResult(store.ObservePackageEntry(kind, bytes, token)),
            verifyImageAsync ?? ((bytes, extension, _) => Task.FromResult(extension == ".png" && bytes.Span.SequenceEqual(Png))));
    }

    private Task<(string Path, WallPackageReader Reader)> TestPackageAsync()
    {
        var reader = Harness();
        var path = Path.Combine(_directory, "sample.zip");
        CreatePackage(path);
        return Task.FromResult((path, reader));
    }

    [Theory]
    [InlineData(".zip")]
    [InlineData(".twpack")]
    public async Task ReaderAcceptsCurrentPackageWithZipOrLegacyExtension(string extension)
    {
        var path = Path.Combine(_directory, "compatible" + extension);
        CreatePackage(path);
        var result = await Harness().ReadPreviewAsync(path);
        Assert.Equal(WallPackageReadStatus.Valid, result.Status);
        Assert.Equal(WallPackageManifest.CurrentFormatVersion, result.Preview!.Manifest.FormatVersion);
    }

    [Fact]
    public async Task OrdinaryZipWithoutWallPackageStructureIsRejected()
    {
        var path = Path.Combine(_directory, "ordinary.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var text = new StreamWriter(zip.CreateEntry("notes.txt").Open()))
            await text.WriteAsync("An ordinary ZIP archive.");
        var result = await Harness().ReadPreviewAsync(path);
        Assert.Equal(WallPackageReadStatus.InvalidPackage, result.Status);
        Assert.Null(result.Preview);
    }

    [Fact]
    public async Task ReaderContentMapsPackageFieldsAndDefaultsOtherWallState()
    {
        var (path, reader) = await TestPackageAsync();
        var result = await reader.ReadPreviewAsync(path);
        Assert.Equal(WallPackageReadStatus.Valid, result.Status);
        var content = result.Preview!.Content;
        Assert.Equal(3, content.Members.Count);
        Assert.Equal(new WallPackageBounds(3, 1, 2, 2), content.Members.Single(m => m.Id == "puzzle").LocalBounds);
        Assert.Equal("HH:mm:ss", content.Members.Single(m => m.Id == "clock").TimeFormat);
        var document = content.ToDocument();
        Assert.Empty(document.InactiveMembers);
        Assert.Equal(0, document.Generation);
        Assert.False(document.Settings.ShowGrid);
        Assert.All(document.EnumerateAllMembers(), m => Assert.Null(m.Carousel));
    }

    [Theory]
    [InlineData("missing-group")]
    [InlineData("multi-group")]
    [InlineData("empty-group")]
    [InlineData("extra-file")]
    [InlineData("traversal")]
    [InlineData("normalized-duplicate")]
    [InlineData("hash")]
    [InlineData("invalid-entry")]
    [InlineData("invalid-image")]
    [InlineData("missing-image")]
    [InlineData("invalid-geometry")]
    public async Task CompleteValidationRejectsDamageEvenWithoutSelection(string damage)
    {
        var (path, reader) = await TestPackageAsync();
        Mutate(path, payloads =>
        {
            var content = JsonNode.Parse(payloads["wall.json"])!.AsObject();
            switch (damage)
            {
                case "missing-group": content["groups"]![0]!["memberIds"]!.AsArray().RemoveAt(0); break;
                case "multi-group": content["groups"]![0]!["memberIds"]!.AsArray().Add("tile"); break;
                case "empty-group": content["groups"]![0]!["memberIds"] = new JsonArray(); break;
                case "invalid-geometry": content["members"]![0]!["localBounds"]!["width"] = 0; break;
                case "extra-file": payloads.Add("extra.bin", [1]); return;
                case "traversal": payloads.Add("../escape", [1]); return;
                case "normalized-duplicate": payloads.Add("objects\\tile\\entry.url", payloads["objects/tile/entry.url"]); return;
                case "hash": payloads["objects/tile/entry.url"][0] ^= 1; return;
                case "invalid-entry": payloads["objects/tile/entry.url"] = Encoding.UTF8.GetBytes("[InternetShortcut]\r\nURL=file:///C:/bad\r\n"); RebuildManifest(payloads); return;
                case "invalid-image": payloads[payloads.Keys.Single(k => k.StartsWith("media/"))][0] ^= 1; RebuildManifest(payloads); return;
                case "missing-image": payloads.Remove(payloads.Keys.Single(k => k.StartsWith("media/"))); return;
            }
            payloads["wall.json"] = Encoding.UTF8.GetBytes(content.ToJsonString()); RebuildManifest(payloads);
        });
        Assert.Equal(WallPackageReadStatus.InvalidPackage, (await reader.ReadPreviewAsync(path)).Status);
    }

    [Fact]
    public async Task FutureVersionIsDistinctFromCorruptionAndCancellationIsNotAnError()
    {
        var (path, reader) = await TestPackageAsync();
        Mutate(path, p => { var json = JsonNode.Parse(p["manifest.json"])!; json["formatVersion"] = WallPackageManifest.CurrentFormatVersion + 1; p["manifest.json"] = Encoding.UTF8.GetBytes(json.ToJsonString()); });
        Assert.Equal(WallPackageReadStatus.UnsupportedFormat, (await reader.ReadPreviewAsync(path)).Status);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Equal(WallPackageReadStatus.Cancelled, (await reader.ReadPreviewAsync(path, token: cancel.Token)).Status);
    }

    [Theory]
    [InlineData(2, MemberImageSourceKind.Automatic)]
    [InlineData(3, MemberImageSourceKind.Automatic)]
    [InlineData(3, MemberImageSourceKind.Folder)]
    [InlineData(3, MemberImageSourceKind.None)]
    public async Task AValidMediaPayloadAndFingerprintCannotHideAMediaReferenceOnANonSingleSource(int formatVersion,
        MemberImageSourceKind kind)
    {
        var path = Path.Combine(_directory, "wrong-source-kind.zip");
        CreatePackage(path);
        Mutate(path, payloads =>
        {
            var content = WallPackageJson.Deserialize<WallPackageContent>(payloads["wall.json"]);
            content = content with { Members = content.Members.Select(member => member.Id == "tile"
                ? member with { Images = member.Images with { Kind = kind } } : member).ToArray() };
            payloads["wall.json"] = WallPackageJson.Serialize(content, formatVersion);
            var manifest = WallPackageJson.Deserialize<WallPackageManifest>(payloads["manifest.json"]);
            var files = manifest.Files.Select(file => file.Path == "wall.json"
                ? file with { Length = payloads["wall.json"].Length, Sha256 = WallPackageJson.Hash(payloads["wall.json"]) } : file).ToArray();
            manifest = manifest with { FormatVersion = formatVersion, Files = files,
                ContentFingerprint = WallPackageJson.Fingerprint(content, files, formatVersion) };
            payloads["manifest.json"] = WallPackageJson.Serialize(manifest);
        });
        var probes = 0;
        var read = await Harness((_, _, _) => { probes++; return Task.FromResult(true); }).ReadPreviewAsync(path);
        Assert.Equal(WallPackageReadStatus.InvalidPackage, read.Status);
        Assert.Equal("Invalid formal image reference.", read.Diagnostic);
        Assert.Equal(0, probes);
    }

    [Fact]
    public async Task ContainerBudgetsAndChangedPackageAreRejectedBeforeImportPreparation()
    {
        var (path, reader) = await TestPackageAsync();
        Assert.Equal(WallPackageReadStatus.InvalidPackage,
            (await reader.ReadPreviewAsync(path, new() { MaximumEntries = 1 })).Status);
        Assert.Equal(WallPackageReadStatus.InvalidPackage,
            (await reader.ReadPreviewAsync(path, new() { MaximumExpandedBytes = 1 })).Status);
        Assert.Equal(WallPackageReadStatus.InvalidPackage,
            (await reader.ReadPreviewAsync(path, new() { MaximumFileBytes = 1 })).Status);
        var preview = (await reader.ReadPreviewAsync(path)).Preview!;
        Mutate(path, p => { var json = JsonNode.Parse(p["manifest.json"])!; json["appVersion"] = "later"; p["manifest.json"] = Encoding.UTF8.GetBytes(json.ToJsonString()); });
        Assert.Equal(WallPackageReadStatus.InvalidPackage, (await reader.RevalidateAsync(preview)).Status);
        await Assert.ThrowsAsync<InvalidDataException>(async () => { using var batch = await reader.OpenImportAsync(preview); });
    }

    [Fact]
    public void MediaPreparationBudgetDeduplicatesChecksConflictsAndExcludesReusedAssets()
    {
        static ManagedMediaReference Image(char c, long length = PreparedMediaAsset.MaximumBytes) => new(new string(c, 64), ".png", length);
        var four = new[] { Image('A'), Image('B'), Image('C'), Image('D') };
        Assert.Equal(4, WallPackageMediaBudget.Plan(four.Concat(four), []).Count);
        Assert.Throws<InvalidDataException>(() => WallPackageMediaBudget.Plan(four.Append(Image('E', 1)), []));
        Assert.Single(WallPackageMediaBudget.Plan(four.Append(Image('E', 1)), four));
        Assert.Throws<InvalidDataException>(() => WallPackageMediaBudget.Plan([Image('A', 1)], [Image('A', 2)]));
        Assert.Empty(WallPackageMediaBudget.Plan([], [], 0));
    }

    [Fact]
    public async Task AcceptedBatchPreparesOnlyRequiredMediaAndRejectsBudgetBeforeProbing()
    {
        var probes = 0;
        var reader = Harness((_, _, _) => { probes++; return Task.FromResult(true); });
        var path = Path.Combine(_directory, "budget.twpack");
        CreatePackage(path);
        var preview = (await reader.ReadPreviewAsync(path)).Preview!;
        var reference = preview.Content.Members.Single(m => m.Id == "tile").Images.Media!;
        probes = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.PrepareMediaAsync(preview, [reference], limits: new() { MaximumPreparedMediaBytes = 0 }));
        Assert.Equal(0, probes);
        using var batch = await reader.OpenImportAsync(preview);
        Assert.Empty(await batch.PrepareMediaAsync([reference], [reference], new() { MaximumPreparedMediaBytes = 0 }));
        Assert.Equal(0, probes);
        Assert.Single(await batch.PrepareMediaAsync([reference, reference]));
        Assert.Equal(1, probes);
        Assert.NotNull(await batch.ReadEntryAsync("tile"));
    }

    // Test-only fixture generation keeps Reader tests independent of any production export code.
    private void CreatePackage(string path)
    {
        var media = PreparedMediaAsset.FromVerifiedImage(Png, ".png").Reference;
        var content = new WallPackageContent
        {
            Columns = 1,
            Rows = 20,
            Lanes = [new WallPackageLane(["group"])],
            Groups = [new WallPackageGroup("group", "Mixed", ["tile", "puzzle", "clock"])],
            Members =
            [
                new WallPackageMember
                {
                    Id = "tile", Kind = "tile", LocalBounds = new(0, 0, 2, 2),
                    EntryKind = ManagedEntryKind.Url, TargetInput = "https://example.com/",
                    Images = new() { Kind = MemberImageSourceKind.Single, Media = media },
                },
                new WallPackageMember
                {
                    Id = "puzzle", Kind = "puzzle", LocalBounds = new(3, 1, 2, 2),
                    Partitions = [new(0, 0, 2, 2)],
                },
                new WallPackageMember
                {
                    Id = "clock", Kind = "clock", LocalBounds = new(0, 4, 2, 1), TimeFormat = "HH:mm:ss",
                },
            ],
        };
        WallPackageJson.ValidateContent(content);
        var payloads = new Dictionary<string, (byte[] Bytes, string Purpose)>(StringComparer.Ordinal)
        {
            ["wall.json"] = (WallPackageJson.Serialize(content), "wall"),
            [WallPackageJson.EntryPath(content.Members[0])] =
                (Encoding.UTF8.GetBytes("[InternetShortcut]\r\nIconFile=C:\\Samples\\icon.ico\r\n"), "entry"),
            [WallPackageJson.MediaPath(media)] = (Png, "media"),
        };
        var files = payloads.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
            new WallPackageFile(pair.Key, pair.Value.Bytes.Length, WallPackageJson.Hash(pair.Value.Bytes), pair.Value.Purpose)).ToArray();
        var manifest = new WallPackageManifest
        {
            AppVersion = "test", CreatedUtc = DateTimeOffset.UtcNow,
            ContentFingerprint = WallPackageJson.Fingerprint(content, files), Files = files,
        };
        payloads.Add("manifest.json", (WallPackageJson.Serialize(manifest), "manifest"));

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, payload) in payloads.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            using var output = zip.CreateEntry(name).Open();
            output.Write(payload.Bytes);
        }
    }

    private static void Mutate(string path, Action<Dictionary<string, byte[]>> change)
    {
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var zip = ZipFile.OpenRead(path)) foreach (var entry in zip.Entries)
        { using var source = entry.Open(); using var buffer = new MemoryStream(); source.CopyTo(buffer); payloads.Add(entry.FullName, buffer.ToArray()); }
        change(payloads);
        File.Delete(path);
        using var output = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in payloads) { using var stream = output.CreateEntry(name).Open(); stream.Write(bytes); }
    }

    private static void RebuildManifest(Dictionary<string, byte[]> payloads)
    {
        var manifest = WallPackageJson.Deserialize<WallPackageManifest>(payloads["manifest.json"]);
        var files = manifest.Files.Select(f => payloads.TryGetValue(f.Path, out var bytes)
            ? f with { Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) } : f).ToArray();
        var content = WallPackageJson.Deserialize<WallPackageContent>(payloads["wall.json"]);
        manifest = manifest with { Files = files, ContentFingerprint = WallPackageJson.Fingerprint(content, files) };
        payloads["manifest.json"] = WallPackageJson.Serialize(manifest);
    }
}
