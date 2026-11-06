using System.IO.Compression;
using TileWall.Core.Entries;
using TileWall.Core.Configuration;
using TileWall.Core.Exchange;
using TileWall.Core.Imaging;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Exchange;

public sealed class WallPackageForegroundTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wall-foreground-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aZ1sAAAAASUVORK5CYII=");
    private static readonly byte[] Png2 = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAEUlEQVR4nGP4z8DwH4QZYAwAR8oH+WdZbrcAAAAASUVORK5CYII=");
    public WallPackageForegroundTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
    private static WallPackageReader Reader() => new((_, _, _) => throw new InvalidOperationException("No entry is expected."),
        (bytes, extension, _) => Task.FromResult(extension == ".png" && (bytes.Span.SequenceEqual(Png) || bytes.Span.SequenceEqual(Png2))));
    private static WallDocument Source() => WallDocument.Empty(1, 8) with
    {
        Lanes = [new() { Groups = [new() { Id = "g", Members =
        [
            new TileMember { Id = "image", LocalBounds = new(0, 0, 2, 2), TargetInput = @"C:\TileWall-Missing-Foreground-Test\missing.exe",
                TargetDisplayName = "Original name", SourceShortcutPath = @"C:\Shortcuts\Original name.lnk",
                ImageDisplay = new() { Mode = ImageDisplayMode.Custom, CustomScale = 1.75 },
                Visual = new() { BackgroundMode = MemberBackgroundMode.Blur } },
            new TileMember { Id = "empty", LocalBounds = new(2, 0, 2, 2) },
        ] }] }],
    };

    [Fact]
    public async Task AutomaticSeriesRoundTripsAllOriginalBytesAndBaselineWithoutChangingTheSource()
    {
        var root = Path.Combine(_directory, "source");
        var files = new LocalFileStore(); var source = Source();
        SnapshotTestData.Install(files, root, source);
        var before = files.ReadAllBytes(Path.Combine(root, "state.json"));
        var store = new WallStore(files, new FakeLinkFileService(files), root);
        using var snapshot = store.CapturePackageSnapshot(source, 0, ["image", "empty"]);
        var path = Path.Combine(_directory, "foreground.zip");
        Assert.Equal(WallPackageWriteStatus.InvalidPackage, (await new WallPackageWriter(Reader()).WriteAsync(snapshot, path, "test")).Status);
        Assert.False(File.Exists(path));
        var asset = PreparedMediaAsset.FromVerifiedImage(Png, ".png");
        var second = PreparedMediaAsset.FromVerifiedImage(Png2, ".png");
        var series = new AutomaticIconSeries("confirmed-export", [new(asset.Reference, 1, 1), new(second.Reference, 2, 2)], 1, 2, 2);
        snapshot.CompleteAutomaticImages(new Dictionary<string, PreparedAutomaticIconSeries?>
            { ["image"] = new(series, [asset, second]), ["empty"] = null });
        Assert.Equal(WallPackageWriteStatus.Saved, (await new WallPackageWriter(Reader()).WriteAsync(snapshot, path, "test")).Status);
        Assert.Equal(before, files.ReadAllBytes(Path.Combine(root, "state.json")));
        Assert.All(source.EnumerateAllMembers(), member => Assert.Equal(MemberImageSourceKind.Automatic, member.Images.Kind));

        var preview = (await Reader().ReadPreviewAsync(path)).Preview!;
        var imported = preview.Content.ToDocument();
        var image = imported.EnumerateAllMembers().Single(member => member.Id == "image");
        Assert.Equal(5, preview.Manifest.FormatVersion);
        Assert.Equal(MemberImageSourceKind.Automatic, image.Images.Kind);
        Assert.True(AutomaticIconSeriesRules.SameSeries(series, image.Images.IconSeries));
        Assert.Null(image.Images.Media); Assert.Null(image.Images.Path);
        Assert.Equal(2, preview.Manifest.Files.Count(file => file.Purpose == "media"));
        Assert.Equal(source.EnumerateAllMembers().First().ImageDisplay, image.ImageDisplay);
        Assert.Equal(MemberBackgroundMode.Blur, image.Visual.BackgroundMode);
        Assert.Equal("Original name", MemberPresentationRules.ResolveTitle(image));
        Assert.Equal(MemberResourceOrigin.Package, image.ResourceOrigin);
        Assert.Equal(MemberImageSourceKind.None, imported.EnumerateAllMembers().Single(member => member.Id == "empty").Images.Kind);
        using (var zip = ZipFile.OpenRead(path))
        using (var payload = zip.GetEntry(WallPackageJson.MediaPath(asset.Reference))!.Open())
        using (var bytes = new MemoryStream())
        { payload.CopyTo(bytes); Assert.Equal(Png, bytes.ToArray()); }

        using var batch = await Reader().OpenImportAsync(preview);
        var media = (await batch.PrepareResourcesAsync(series.Frames.Select(frame => frame.Media), [])).MediaAssets;
        Assert.Equal(asset.CopyBytes(), media.Single(item => item.Reference == asset.Reference).CopyBytes());
        Assert.Equal(second.CopyBytes(), media.Single(item => item.Reference == second.Reference).CopyBytes());
        var importedRoot = Path.Combine(_directory, "imported");
        var importedStore = new WallStore(new LocalFileStore(), new FakeLinkFileService(new LocalFileStore()), importedRoot);
        var empty = WallDocument.Empty(1, 8);
        Assert.Equal(WallOpenStatus.Loaded, importedStore.InitializeFresh(empty).Status);
        var destinationBefore = File.ReadAllBytes(Path.Combine(importedRoot, "state.json"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch.PrepareResourcesAsync(
            series.Frames.Select(frame => frame.Media), [], token: cancellation.Token));
        Assert.Equal(destinationBefore, File.ReadAllBytes(Path.Combine(importedRoot, "state.json")));
        Assert.Empty(importedStore.Open().Document!.EnumerateAllMembers());
        var saved = importedStore.Commit(empty, new(0, imported with { Generation = 1 }, [], media));
        Assert.Equal(WallCommitStatus.Committed, saved.Status);
        var reopened = importedStore.Open(); Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.All(reopened.Document!.EnumerateAllMembers(), member => Assert.Equal(MemberResourceOrigin.Package, member.ResourceOrigin));
        using var again = importedStore.CapturePackageSnapshot(reopened.Document, 1, ["image", "empty"]);
        Assert.Empty(again.AutomaticImageMembers);
        Assert.Equal(preview.Manifest.ContentFingerprint,
            (await WriteAndReadAsync(again, Path.Combine(_directory, "again.zip"))).Manifest.ContentFingerprint);
    }

    private static async Task<WallPackagePreview> WriteAndReadAsync(WallPackageSnapshot snapshot, string path)
    {
        Assert.Equal(WallPackageWriteStatus.Saved, (await new WallPackageWriter(Reader()).WriteAsync(snapshot, path, "test")).Status);
        return (await Reader().ReadPreviewAsync(path)).Preview!;
    }

    [Fact]
    public void WrongSelectionOrCancellationCannotPartiallyCompleteAnExportProjection()
    {
        var root = Path.Combine(_directory, "source"); var files = new LocalFileStore(); var source = Source();
        SnapshotTestData.Install(files, root, source);
        using var snapshot = new WallStore(files, new FakeLinkFileService(files), root).CapturePackageSnapshot(source, 0, ["image", "empty"]);
        Assert.Throws<InvalidDataException>(() => snapshot.CompleteAutomaticImages(new Dictionary<string, PreparedAutomaticIconSeries?> { ["image"] = null }));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => snapshot.CompleteAutomaticImages(token: cancel.Token));
        Assert.All(snapshot.Content.Members, member => Assert.Equal(MemberImageSourceKind.Automatic, member.Images.Kind));
        snapshot.CompleteAutomaticImages();
        Assert.All(snapshot.Content.Members, member => Assert.Equal(MemberImageSourceKind.None, member.Images.Kind));
    }

    [Fact]
    public async Task RestrictedExternalFolderDescriptionSurvivesSaveReopenAndExportWithoutBecomingAMediaReference()
    {
        var external = @"\\unavailable\share\pictures";
        var member = Source().EnumerateAllMembers().First() with
        { ResourceOrigin = MemberResourceOrigin.Package, Images = new() { Kind = MemberImageSourceKind.Folder, Path = external } };
        var source = WallDocument.Empty(1, 8) with { Lanes = [new() { Groups = [new() { Id = "g", Members = [member] }] }] };
        var root = Path.Combine(_directory, "restricted"); var files = new LocalFileStore();
        var store = new WallStore(files, new FakeLinkFileService(files), root);
        var empty = WallDocument.Empty(1, 8); Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(empty).Status);
        Assert.Equal(WallCommitStatus.Committed, store.Commit(empty, new(0, source with { Generation = 1 }, [])).Status);
        var reopened = store.Open().Document!;
        Assert.Equal(external, Assert.Single(reopened.EnumerateAllMembers()).Images.Path);
        using var snapshot = store.CapturePackageSnapshot(reopened, 1, [member.Id]);
        Assert.Empty(snapshot.AutomaticImageMembers);
        var preview = await WriteAndReadAsync(snapshot, Path.Combine(_directory, "restricted.zip"));
        Assert.DoesNotContain(preview.Manifest.Files, file => file.Purpose == "media");
        var restored = Assert.Single(preview.Content.ToDocument().EnumerateAllMembers());
        Assert.Equal(external, restored.Images.Path); Assert.Equal(MemberResourceOrigin.Package, restored.ResourceOrigin);
    }

    [Fact]
    public void PackageAutomaticAndNoneWithResourceOrCarouselCannotBeCommitted()
    {
        var files = new InMemoryFileStore(); var root = Path.Combine(_directory, "validation");
        var store = new WallStore(files, new FakeLinkFileService(files), root);
        var empty = WallDocument.Empty(1, 8); Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(empty).Status);
        WallDocument Candidate(WallMember member) => empty with
        { Generation = 1, Lanes = [new() { Groups = [new() { Id = "g", Members = [member] }] }] };
        var invalid = Source().EnumerateAllMembers().First() with { ResourceOrigin = MemberResourceOrigin.Package };
        Assert.Equal(WallCommitStatus.Rejected, store.Commit(empty, new(0, Candidate(invalid), [])).Status);
        invalid = invalid with { Images = new() { Kind = MemberImageSourceKind.None, Path = @"C:\not-an-empty-image.png" } };
        Assert.Equal(WallCommitStatus.Rejected, store.Commit(empty, new(0, Candidate(invalid), [])).Status);
        invalid = invalid with { Images = new() { Kind = MemberImageSourceKind.None }, Carousel = new() };
        Assert.Equal(WallCommitStatus.Rejected, store.Commit(empty, new(0, Candidate(invalid), [])).Status);
        Assert.Empty(store.Open().Document!.EnumerateAllMembers());
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task HistoricalFormatIsValidatedInItsOriginalShapeBeforeItsAutomaticImageIsMadeEmpty(int formatVersion, bool corruptFingerprint)
    {
        var content = WallPackageContent.FromSelection(Source(), ["image", "empty"]);
        var legacySingle = PreparedMediaAsset.FromVerifiedImage(Png, ".png");
        if (formatVersion == 4)
            content = content with { Members = content.Members.Select(member => member.Id == "image"
                ? member with { Images = new() { Kind = MemberImageSourceKind.Single, Media = legacySingle.Reference } } : member).ToArray() };
        var bytes = WallPackageJson.Serialize(content, formatVersion);
        if (formatVersion == 2) Assert.DoesNotContain("targetDisplayName", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("iconSeries", System.Text.Encoding.UTF8.GetString(bytes));
        var files = new List<WallPackageFile> { new("wall.json", bytes.Length, WallPackageJson.Hash(bytes), "wall") };
        if (formatVersion == 4) files.Add(new(WallPackageJson.MediaPath(legacySingle.Reference), Png.Length, legacySingle.Reference.Sha256, "media"));
        var manifest = new WallPackageManifest { FormatVersion = formatVersion, WallSchemaVersion = formatVersion < 4 ? 4 : WallDocument.CurrentSchemaVersion, AppVersion = "1.4.1", CreatedUtc = DateTimeOffset.UtcNow,
            ContentFingerprint = corruptFingerprint ? new string('A', 64) : WallPackageJson.Fingerprint(content, files, formatVersion), Files = files };
        var path = Path.Combine(_directory, "legacy.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var wall = zip.CreateEntry("wall.json").Open()) wall.Write(bytes);
            using (var description = zip.CreateEntry("manifest.json").Open()) description.Write(WallPackageJson.Serialize(manifest));
            if (formatVersion == 4)
            { using var image = zip.CreateEntry(WallPackageJson.MediaPath(legacySingle.Reference)).Open(); image.Write(Png); }
        }
        var read = await Reader().ReadPreviewAsync(path);
        Assert.Equal(corruptFingerprint ? WallPackageReadStatus.InvalidPackage : WallPackageReadStatus.Valid, read.Status);
        if (corruptFingerprint) return;
        Assert.All(read.Preview!.Content.Members.Where(member => formatVersion < 4 || member.Id == "empty"),
            member => Assert.Equal(MemberImageSourceKind.Automatic, member.Images.Kind));
        Assert.All(read.Preview.Content.ToDocument().EnumerateAllMembers().Where(member => formatVersion < 4 || member.Id == "empty"), member =>
        { Assert.Equal(MemberResourceOrigin.Package, member.ResourceOrigin); Assert.Equal(MemberImageSourceKind.None, member.Images.Kind); });
        if (formatVersion == 4)
        {
            var restored = read.Preview.Content.ToDocument().EnumerateAllMembers().Single(member => member.Id == "image");
            Assert.Equal(MemberImageSourceKind.Single, restored.Images.Kind); Assert.Null(restored.Images.IconSeries);
            Assert.Equal(legacySingle.Reference, restored.Images.Media);
        }
    }

    [Fact]
    public async Task ExportingAStoredSeriesPreservesEveryNativeFrameAndItsBaseline()
    {
        var files = new LocalFileStore(); var root = Path.Combine(_directory, "series");
        var store = new WallStore(files, new FakeLinkFileService(files), root);
        var empty = WallDocument.Empty(1, 8); store.InitializeFresh(empty);
        var assets = new[] { PreparedMediaAsset.FromVerifiedImage(Png, ".png"),
            PreparedMediaAsset.FromVerifiedImage(Png2, ".png") };
        var series = new AutomaticIconSeries("confirmed", assets.Select((asset, index) =>
            new AutomaticIconFrame(asset.Reference, 1 << index, 1 << index)).ToArray(), 1, 2, 2);
        var source = Source(); source = source with { Generation = 1, Lanes = source.Lanes.Select(lane => lane with
            { Groups = lane.Groups.Select(group => group with { Members = group.Members.Select(member => member.Id == "image"
                ? member with { Images = new() { IconSeries = series } } : member).ToArray() }).ToArray() }).ToArray() };
        var saved = store.Commit(empty, new(0, source, [], assets));
        Assert.Equal(WallCommitStatus.Committed, saved.Status);
        using var snapshot = store.CapturePackageSnapshot(saved.CommittedDocument!, 1, ["image", "empty"]);
        Assert.Equal("empty", Assert.Single(snapshot.AutomaticImageMembers).Id);
        snapshot.CompleteAutomaticImages(new Dictionary<string, PreparedAutomaticIconSeries?> { ["empty"] = null });
        var preview = await WriteAndReadAsync(snapshot, Path.Combine(_directory, "series.zip"));
        Assert.Equal(2, preview.Manifest.Files.Count(file => file.Purpose == "media"));
        Assert.True(AutomaticIconSeriesRules.SameSeries(series,
            preview.Content.ToDocument().EnumerateAllMembers().First().Images.IconSeries));
        Assert.Equal(2, store.Open().Document!.EnumerateAllMembers().First().Images.IconSeries!.Frames.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("digest")]
    [InlineData("dimensions")]
    [InlineData("index")]
    public async Task IncompleteOrInvalidAutomaticSeriesRejectsTheWholePackage(string fault)
    {
        var first = PreparedMediaAsset.FromVerifiedImage(Png, ".png");
        var second = PreparedMediaAsset.FromVerifiedImage(Png2, ".png");
        var series = new AutomaticIconSeries("confirmed", [new(first.Reference, 1, 1),
            new(second.Reference, fault == "dimensions" ? 3 : 2, 2)], fault == "index" ? 2 : 1, 2, 2);
        var content = WallPackageContent.FromSelection(Source(), ["image"]);
        content = content with { Members = [content.Members[0] with { Images = new() { IconSeries = series } }] };
        var bytes = WallPackageJson.Serialize(content);
        var files = new List<WallPackageFile> { new("wall.json", bytes.Length, WallPackageJson.Hash(bytes), "wall") };
        foreach (var asset in new[] { first, second })
            if (fault != "missing" || asset != second)
                files.Add(new(WallPackageJson.MediaPath(asset.Reference), asset.Reference.Length, asset.Reference.Sha256, "media"));
        var manifest = new WallPackageManifest { AppVersion = "test", CreatedUtc = DateTimeOffset.UtcNow,
            ContentFingerprint = WallPackageJson.Fingerprint(content, files), Files = files };
        var path = Path.Combine(_directory, "bad-series.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var wall = zip.CreateEntry("wall.json").Open()) wall.Write(bytes);
            using (var description = zip.CreateEntry("manifest.json").Open()) description.Write(WallPackageJson.Serialize(manifest));
            foreach (var asset in new[] { first, second })
            {
                if (fault == "missing" && asset == second) continue;
                var payload = asset.CopyBytes();
                if (fault == "digest" && asset == second) payload[^1] ^= 1;
                using var stream = zip.CreateEntry(WallPackageJson.MediaPath(asset.Reference)).Open(); stream.Write(payload);
            }
        }
        var source = Source(); var root = Path.Combine(_directory, "unchanged"); var filesStore = new LocalFileStore();
        SnapshotTestData.Install(filesStore, root, source);
        var before = filesStore.ReadAllBytes(Path.Combine(root, "state.json"));
        Assert.Equal(WallPackageReadStatus.InvalidPackage, (await Reader().ReadPreviewAsync(path)).Status);
        Assert.Equal(before, filesStore.ReadAllBytes(Path.Combine(root, "state.json")));
    }
}
