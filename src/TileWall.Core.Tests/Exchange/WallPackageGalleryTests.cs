using System.IO.Compression;
using System.Text;
using TileWall.Core.Configuration;
using TileWall.Core.Exchange;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Exchange;

public sealed class WallPackageGalleryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "package-galleries-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aZ1sAAAAASUVORK5CYII=");
    public WallPackageGalleryTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private (WallStore Store, WallDocument Document, PreparedManagedGallery Gallery, string Root) Seed()
    {
        var root = Path.Combine(_directory, "sender");
        var files = new LocalFileStore(); var store = new WallStore(files, new FakeLinkFileService(files), root);
        var initial = WallDocument.Empty(1, 12); Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
        var first = PreparedMediaAsset.FromVerifiedImage(Png, ".png");
        var second = PreparedMediaAsset.FromVerifiedImage(Png.Concat(new byte[] { 1 }).ToArray(), ".png");
        var gallery = PreparedManagedGallery.FromVerifiedFiles([new("01-spring.png", first), new("04-winter.png", second)]);
        var other = PreparedManagedGallery.FromVerifiedFiles([new("same.png", first)]);
        var source = new MemberImageSource { Kind = MemberImageSourceKind.Folder, ManagedGallery = gallery.Reference };
        var proposed = initial with { Generation = 1, Lanes = [new WallLane { Groups = [new StartGroup
        {
            Id = "group", Title = "Original group", Members =
            [new PuzzleMember { Id = "puzzle", LocalBounds = new(0, 0, 2, 2), Partitions = [new(0, 0, 1, 2), new(1, 0, 1, 2)],
                Images = source, RotationInterval = ImageRotationInterval.Seconds5, Visual = new() { TitleMode = MemberTitleMode.Manual, Title = "Seasons" } },
             new TileMember { Id = "shared", LocalBounds = new(2, 0, 2, 2), Images = source },
             new TileMember { Id = "single", LocalBounds = new(4, 0, 1, 1), Images = new() { Kind = MemberImageSourceKind.Single, Media = first.Reference } },
             new TileMember { Id = "other", LocalBounds = new(5, 0, 2, 2), Images = new() { Kind = MemberImageSourceKind.Folder, ManagedGallery = other.Reference } },
             new TileMember { Id = "external", LocalBounds = new(7, 0, 1, 1), Images = new() { Kind = MemberImageSourceKind.Folder, Path = Path.Combine(_directory, "unscanned-external") } }],
        }] }] };
        var result = store.Commit(initial, new(0, proposed, [], [first], ManagedGalleries: [gallery, other]));
        Assert.True(result.Status is WallCommitStatus.Committed or WallCommitStatus.CommittedNeedsCleanup, string.Join(";", result.Diagnostics));
        return (store, result.CommittedDocument!, gallery, root);
    }

    private static WallPackageReader Reader(Func<ReadOnlyMemory<byte>, string, CancellationToken, Task<bool>>? verify = null) => new(
        (_, _, _) => throw new InvalidDataException("These gallery samples have no shortcuts."),
        verify ?? ((bytes, extension, _) => Task.FromResult(extension == ".png" && bytes.Span.StartsWith(Png))));

    private async Task<(string Path, WallPackageReader Reader, PreparedManagedGallery Gallery, string Sender)> Export(params string[] ids)
    {
        var (store, document, gallery, root) = Seed();
        var reader = Reader(); var path = Path.Combine(_directory, "selected.zip");
        using var snapshot = store.CapturePackageSnapshot(document, document.Generation, ids);
        snapshot.CompleteAutomaticImages();
        var result = await new WallPackageWriter(reader).WriteAsync(snapshot, path, "test");
        Assert.Equal(WallPackageWriteStatus.Saved, result.Status);
        return (path, reader, gallery, root);
    }

    [Fact]
    public async Task SelectedGalleryIsPortableAndKeepsCompleteOriginalsPartitionsAndPresentation()
    {
        var (path, reader, gallery, sender) = await Export("puzzle", "shared", "single");
        Directory.Delete(sender, recursive: true);
        var read = await reader.ReadPreviewAsync(path); Assert.Equal(WallPackageReadStatus.Valid, read.Status);
        var preview = read.Preview!;
        Assert.Equal(WallPackageManifest.CurrentFormatVersion, preview.Manifest.FormatVersion); Assert.Single(preview.Galleries);
        Assert.Equal(2, preview.Manifest.Files.Count(file => file.Purpose == "media"));
        Assert.Single(preview.Manifest.Files, file => file.Purpose == "gallery");
        Assert.DoesNotContain(sender, Encoding.UTF8.GetString(WallPackageJson.Serialize(preview.Content)));
        using var batch = await reader.OpenImportAsync(preview);
        var resources = await batch.PrepareResourcesAsync(preview.Content.Members.Select(member => member.Images.Media).OfType<ManagedMediaReference>(), [gallery.Reference]);
        Assert.Equal(gallery.Reference, Assert.Single(resources.ManagedGalleries).Reference);
        Assert.Equal(2, resources.ManagedGalleries[0].Files.Count);
        var root = Path.Combine(_directory, "receiver"); var files = new LocalFileStore();
        var receiver = new WallStore(files, new FakeLinkFileService(files), root);
        var initial = WallDocument.Empty(1, 12); receiver.InitializeFresh(initial);
        var result = receiver.Commit(initial, new(0, preview.Content.ToDocument() with { Generation = 1 }, [], resources.MediaAssets, ManagedGalleries: resources.ManagedGalleries));
        Assert.True(result.Status is WallCommitStatus.Committed or WallCommitStatus.CommittedNeedsCleanup, string.Join(";", result.Diagnostics));
        var restored = result.CommittedDocument!;
        var puzzle = Assert.IsType<PuzzleMember>(restored.EnumerateAllMembers().Single(member => member.Id == "puzzle"));
        Assert.Equal(2, puzzle.Partitions.Count); Assert.Equal(ImageRotationInterval.Seconds5, puzzle.RotationInterval);
        Assert.Equal("Seasons", puzzle.Visual.Title); Assert.Equal(new TileWall.Core.Grid.GridRect(0, 0, 2, 2), puzzle.LocalBounds);
        Assert.StartsWith(root, puzzle.Images.Path); Assert.DoesNotContain(sender, puzzle.Images.Path!);
        foreach (var file in gallery.Files) Assert.Equal(file.Asset.CopyBytes(), File.ReadAllBytes(Path.Combine(puzzle.Images.Path!, file.RelativePath)));
        var reused = await batch.PrepareResourcesAsync([gallery.Files[0].Asset.Reference], [gallery.Reference],
            receiver.ReusablePackageMedia(restored), [gallery.Reference], new() { MaximumPreparedMediaBytes = 0 });
        Assert.Empty(reused.MediaAssets); Assert.Empty(reused.ManagedGalleries);
        Assert.Equal(WallOpenStatus.Loaded, receiver.Open().Status);
    }

    [Fact]
    public async Task DifferentAndSharedGalleriesDeduplicatePayloadsAndExternalFolderRemainsOnlyAPath()
    {
        var (path, reader, _, _) = await Export("puzzle", "shared", "other", "single", "external");
        var preview = (await reader.ReadPreviewAsync(path)).Preview!;
        Assert.Equal(2, preview.Galleries.Count);
        Assert.Equal(2, preview.Manifest.Files.Count(file => file.Purpose == "media"));
        var external = preview.Content.Members.Single(member => member.Id == "external");
        Assert.Null(external.Images.GalleryId); Assert.Equal(Path.Combine(_directory, "unscanned-external"), external.Images.Path);
        Assert.Equal(6, preview.Manifest.Files.Count + 1); // wall, two gallery manifests, two originals, package manifest.
    }

    [Fact]
    public async Task UnselectedManagedGalleryAndAllExternalContentsAreExcluded()
    {
        var (path, reader, _, _) = await Export("external");
        var preview = (await reader.ReadPreviewAsync(path)).Preview!;
        Assert.Empty(preview.Galleries); Assert.Empty(preview.Content.Galleries);
        Assert.Single(preview.Manifest.Files); Assert.Equal("wall", preview.Manifest.Files[0].Purpose);
    }

    [Theory]
    [InlineData("missing-original")]
    [InlineData("bad-manifest")]
    [InlineData("changed-original")]
    [InlineData("extra-payload")]
    public async Task ReaderRejectsBadWholeGalleryClosureBeforeAnySelection(string damage)
    {
        var (path, reader, _, _) = await Export("puzzle");
        Rewrite(path, payloads =>
        {
            var original = payloads.Keys.First(name => name.StartsWith("media/", StringComparison.Ordinal));
            var manifest = payloads.Keys.Single(name => name.StartsWith("galleries/", StringComparison.Ordinal));
            if (damage == "missing-original") payloads.Remove(original);
            if (damage == "bad-manifest") payloads[manifest][0] ^= 1;
            if (damage == "changed-original") payloads[original][0] ^= 1;
            if (damage == "extra-payload") payloads.Add("media/extra/original", Png);
        });
        Assert.Equal(WallPackageReadStatus.InvalidPackage, (await reader.ReadPreviewAsync(path)).Status);
    }

    [Fact]
    public async Task CombinedPreparationBudgetDeduplicatesSingleAndGalleryAndRejectsBeforeProbing()
    {
        var (path, _, gallery, _) = await Export("puzzle", "single");
        var probes = 0;
        var reader = Reader((bytes, extension, _) => { probes++; return Task.FromResult(bytes.Span.StartsWith(Png)); });
        var preview = (await reader.ReadPreviewAsync(path)).Preview!;
        using var batch = await reader.OpenImportAsync(preview);
        var before = probes; var first = gallery.Files[0].Asset.Reference;
        await Assert.ThrowsAsync<InvalidDataException>(() => batch.PrepareResourcesAsync([first], [gallery.Reference],
            limits: new() { MaximumPreparedMediaBytes = first.Length }));
        Assert.Equal(before, probes);
        var total = gallery.Files.Sum(file => file.Asset.Reference.Length);
        var result = await batch.PrepareResourcesAsync([first], [gallery.Reference], limits: new() { MaximumPreparedMediaBytes = total });
        Assert.Single(result.MediaAssets); Assert.Single(result.ManagedGalleries);
        Assert.Equal(before + 2, probes); // Shared Single original is prepared once.
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch.PrepareResourcesAsync([], [gallery.Reference], token: cancel.Token));
    }

    [Fact]
    public async Task ReceiverConflictRejectsWithoutChangingItsWallOrConflictingSource()
    {
        var (path, reader, gallery, _) = await Export("puzzle");
        var preview = (await reader.ReadPreviewAsync(path)).Preview!;
        using var batch = await reader.OpenImportAsync(preview);
        var resources = await batch.PrepareResourcesAsync([], [gallery.Reference]);
        var root = Path.Combine(_directory, "receiver"); var files = new LocalFileStore();
        var receiver = new WallStore(files, new FakeLinkFileService(files), root);
        var initial = WallDocument.Empty(1, 12); receiver.InitializeFresh(initial);
        var manifestBefore = File.ReadAllBytes(Path.Combine(root, "state.json"));
        var conflicting = Path.Combine(root, "Sources", gallery.Reference.ContentId); Directory.CreateDirectory(conflicting);
        File.WriteAllBytes(Path.Combine(conflicting, "manifest.json"), [1, 2, 3]);
        var result = receiver.Commit(initial, new(0, preview.Content.ToDocument() with { Generation = 1 }, [], ManagedGalleries: resources.ManagedGalleries));
        Assert.Equal(WallCommitStatus.Rejected, result.Status);
        Assert.Equal(manifestBefore, File.ReadAllBytes(Path.Combine(root, "state.json")));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(conflicting, "manifest.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogicalGalleryExpansionCannotBypassFileOrExpandedByteBudgetsThroughDeduplication(bool byteBudget)
    {
        var bytes = byteBudget ? Png.Concat(new byte[1024 * 1024 - Png.Length]).ToArray() : Png;
        var asset = PreparedMediaAsset.FromVerifiedImage(bytes, ".png");
        var count = byteBudget ? 2 : 7;
        var gallery = PreparedManagedGallery.FromVerifiedFiles(Enumerable.Range(0, count)
            .Select(index => new PreparedManagedGalleryFile($"copy-{index}.png", asset)));
        var source = WallDocument.Empty(1, 8) with { Lanes = [new WallLane { Groups = [new StartGroup { Id = "group", Members =
            [new TileMember { Id = "tile", LocalBounds = new(0, 0, 1, 1), Images = new() { Kind = MemberImageSourceKind.Folder, ManagedGallery = gallery.Reference } }] }] }] };
        var content = WallPackageContent.FromSelection(source, ["tile"]);
        using var snapshot = new WallPackageSnapshot(Path.Combine(_directory, "data"), 0, content,
            new Dictionary<string, WallPackageSnapshotEntry>(), new Dictionary<string, Stream> { [asset.Reference.Sha256] = new MemoryStream(bytes, writable: false) }, [],
            new Dictionary<string, WallPackageSnapshotGallery> { [gallery.Reference.ContentId] = new(gallery.Reference, gallery.ManifestBytes) });
        var reader = Reader(); var writer = new WallPackageWriter(reader);
        var path = Path.Combine(_directory, "logical.zip");
        Assert.Equal(WallPackageWriteStatus.Saved, (await writer.WriteAsync(snapshot, path, "test")).Status);
        var limits = byteBudget ? new WallPackageLimits { MaximumExpandedBytes = 1536 * 1024 } : new WallPackageLimits { MaximumEntries = 5 };
        var rejectedPath = Path.Combine(_directory, "rejected.zip");
        Assert.Equal(WallPackageWriteStatus.InvalidPackage, (await writer.WriteAsync(snapshot, rejectedPath, "test", limits: limits)).Status);
        Assert.False(File.Exists(rejectedPath));
        Assert.Equal(WallPackageReadStatus.InvalidPackage, (await reader.ReadPreviewAsync(path, limits)).Status);
    }

    [Fact]
    public async Task SharedDigestAcrossGalleryNamesAndExtensionsStillHasOnePayloadAndOnePreparationBudget()
    {
        var asset = PreparedMediaAsset.FromVerifiedImage(Png, ".png");
        var gallery = PreparedManagedGallery.FromVerifiedFiles([new("one.png", asset), new("two.image", asset.WithExtension(".image"))]);
        var document = WallDocument.Empty(1, 8) with { Lanes = [new WallLane { Groups = [new StartGroup { Id = "group", Members =
        [new TileMember { Id = "gallery", LocalBounds = new(0, 0, 1, 1), Images = new() { Kind = MemberImageSourceKind.Folder, ManagedGallery = gallery.Reference } },
         new TileMember { Id = "single", LocalBounds = new(1, 0, 1, 1), Images = new() { Kind = MemberImageSourceKind.Single, Media = asset.Reference } }] }] }] };
        var content = WallPackageContent.FromSelection(document, ["gallery", "single"]);
        using var snapshot = new WallPackageSnapshot(Path.Combine(_directory, "data"), 0, content,
            new Dictionary<string, WallPackageSnapshotEntry>(), new Dictionary<string, Stream> { [asset.Reference.Sha256] = new MemoryStream(Png, writable: false) }, [],
            new Dictionary<string, WallPackageSnapshotGallery> { [gallery.Reference.ContentId] = new(gallery.Reference, gallery.ManifestBytes) });
        var reader = Reader((bytes, _, _) => Task.FromResult(bytes.Span.SequenceEqual(Png)));
        var path = Path.Combine(_directory, "extensions.zip");
        Assert.Equal(WallPackageWriteStatus.Saved, (await new WallPackageWriter(reader).WriteAsync(snapshot, path, "test")).Status);
        var preview = (await reader.ReadPreviewAsync(path)).Preview!;
        Assert.Single(preview.Manifest.Files, file => file.Purpose == "media");
        using var batch = await reader.OpenImportAsync(preview);
        var prepared = await batch.PrepareResourcesAsync([asset.Reference], [gallery.Reference], limits: new() { MaximumPreparedMediaBytes = Png.Length });
        Assert.Single(prepared.MediaAssets);
        Assert.Equal(gallery.Reference, Assert.Single(prepared.ManagedGalleries).Reference);
        Assert.Equal(new[] { ".png", ".image" }, prepared.ManagedGalleries[0].Files.Select(file => file.Asset.Reference.Extension));
    }

    private static void Rewrite(string path, Action<Dictionary<string, byte[]>> mutate)
    {
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var zip = ZipFile.OpenRead(path)) foreach (var entry in zip.Entries)
        {
            using var source = entry.Open(); using var buffer = new MemoryStream(); source.CopyTo(buffer); payloads.Add(entry.FullName, buffer.ToArray());
        }
        mutate(payloads); File.Delete(path);
        using var output = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in payloads) { using var target = output.CreateEntry(name).Open(); target.Write(bytes); }
    }
}
