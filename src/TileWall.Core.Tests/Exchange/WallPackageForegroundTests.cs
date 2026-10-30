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
    public WallPackageForegroundTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
    private static WallPackageReader Reader() => new((_, _, _) => throw new InvalidOperationException("No entry is expected."),
        (bytes, extension, _) => Task.FromResult(extension == ".png" && bytes.Span.SequenceEqual(Png)));
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
    public async Task AutomaticForegroundBecomesAnOrdinaryOriginalAndNoneWithoutChangingTheSource()
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
        snapshot.CompleteAutomaticImages(new Dictionary<string, PreparedMediaAsset?> { ["image"] = asset, ["empty"] = null });
        Assert.Equal(WallPackageWriteStatus.Saved, (await new WallPackageWriter(Reader()).WriteAsync(snapshot, path, "test")).Status);
        Assert.Equal(before, files.ReadAllBytes(Path.Combine(root, "state.json")));
        Assert.All(source.EnumerateAllMembers(), member => Assert.Equal(MemberImageSourceKind.Automatic, member.Images.Kind));

        var preview = (await Reader().ReadPreviewAsync(path)).Preview!;
        var imported = preview.Content.ToDocument();
        var image = imported.EnumerateAllMembers().Single(member => member.Id == "image");
        Assert.Equal(MemberImageSourceKind.Single, image.Images.Kind);
        Assert.Equal(asset.Reference, image.Images.Media); Assert.Null(image.Images.Path);
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
        var media = await batch.PrepareMediaAsync([asset.Reference]);
        var importedRoot = Path.Combine(_directory, "imported");
        var importedStore = new WallStore(new LocalFileStore(), new FakeLinkFileService(new LocalFileStore()), importedRoot);
        var empty = WallDocument.Empty(1, 8);
        Assert.Equal(WallOpenStatus.Loaded, importedStore.InitializeFresh(empty).Status);
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
        Assert.Throws<InvalidDataException>(() => snapshot.CompleteAutomaticImages(new Dictionary<string, PreparedMediaAsset?> { ["image"] = null }));
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
    public async Task HistoricalFormatIsValidatedInItsOriginalShapeBeforeItsAutomaticImageIsMadeEmpty(int formatVersion, bool corruptFingerprint)
    {
        var content = WallPackageContent.FromSelection(Source(), ["image", "empty"]);
        var bytes = WallPackageJson.Serialize(content, formatVersion);
        if (formatVersion == 2) Assert.DoesNotContain("targetDisplayName", System.Text.Encoding.UTF8.GetString(bytes));
        var files = new[] { new WallPackageFile("wall.json", bytes.Length, WallPackageJson.Hash(bytes), "wall") };
        var manifest = new WallPackageManifest { FormatVersion = formatVersion, WallSchemaVersion = 4, AppVersion = "1.4.1", CreatedUtc = DateTimeOffset.UtcNow,
            ContentFingerprint = corruptFingerprint ? new string('A', 64) : WallPackageJson.Fingerprint(content, files, formatVersion), Files = files };
        var path = Path.Combine(_directory, "legacy.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var wall = zip.CreateEntry("wall.json").Open()) wall.Write(bytes);
            using (var description = zip.CreateEntry("manifest.json").Open()) description.Write(WallPackageJson.Serialize(manifest));
        }
        var read = await Reader().ReadPreviewAsync(path);
        Assert.Equal(corruptFingerprint ? WallPackageReadStatus.InvalidPackage : WallPackageReadStatus.Valid, read.Status);
        if (corruptFingerprint) return;
        Assert.All(read.Preview!.Content.Members, member => Assert.Equal(MemberImageSourceKind.Automatic, member.Images.Kind));
        Assert.All(read.Preview.Content.ToDocument().EnumerateAllMembers(), member =>
        { Assert.Equal(MemberResourceOrigin.Package, member.ResourceOrigin); Assert.Equal(MemberImageSourceKind.None, member.Images.Kind); });
    }

    [Fact]
    public async Task ExportingAStoredSeriesProjectsOnlyItsPngAndNeverLeaksOtherNativeFrames()
    {
        var files = new LocalFileStore(); var root = Path.Combine(_directory, "series");
        var store = new WallStore(files, new FakeLinkFileService(files), root);
        var empty = WallDocument.Empty(1, 8); store.InitializeFresh(empty);
        var assets = new[] { PreparedMediaAsset.FromVerifiedImage(Png, ".png"),
            PreparedMediaAsset.FromVerifiedImage(new byte[] { 1 }, ".png"), PreparedMediaAsset.FromVerifiedImage(new byte[] { 2 }, ".png") };
        var series = new AutomaticIconSeries("confirmed", assets.Select((asset, index) =>
            new AutomaticIconFrame(asset.Reference, 1 << index, 1 << index)).ToArray(), 0, 2, 2);
        var source = Source(); source = source with { Generation = 1, Lanes = source.Lanes.Select(lane => lane with
            { Groups = lane.Groups.Select(group => group with { Members = group.Members.Select(member => member.Id == "image"
                ? member with { Images = new() { IconSeries = series } } : member).ToArray() }).ToArray() }).ToArray() };
        var saved = store.Commit(empty, new(0, source, [], assets));
        Assert.Equal(WallCommitStatus.Committed, saved.Status);
        using var snapshot = store.CapturePackageSnapshot(saved.CommittedDocument!, 1, ["image", "empty"]);
        snapshot.CompleteAutomaticImages(new Dictionary<string, PreparedMediaAsset?> { ["image"] = assets[0], ["empty"] = null });
        var preview = await WriteAndReadAsync(snapshot, Path.Combine(_directory, "series.zip"));
        var exportedMedia = Assert.Single(preview.Manifest.Files, file => file.Purpose == "media");
        Assert.Equal(assets[0].Reference.Sha256, exportedMedia.Sha256);
        Assert.All(preview.Content.ToDocument().EnumerateAllMembers(), member => Assert.Null(member.Images.IconSeries));
        Assert.Equal(3, store.Open().Document!.EnumerateAllMembers().First().Images.IconSeries!.Frames.Count);
    }
}
