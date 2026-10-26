using TileWall.Core.Application;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Application;

public sealed class WelcomeWallGallerySourceTests
{
    private static string Root() => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "welcome-gallery-" + Guid.NewGuid().ToString("N")));
    private static PreparedMediaAsset[] Originals(byte variant = 0) => Enumerable.Range(1, 4)
        .Select(index => PreparedMediaAsset.FromVerifiedImage(new byte[] { (byte)index, variant, 255 }, ".png")).ToArray();

    [Fact]
    public void PreparationFreezesExactSeasonBytesAndDoesNoFormalOrStagingIo()
    {
        var files = new InMemoryFileStore();
        var originals = Originals();
        var prepared = WelcomeWallGallerySource.Prepare(files, Root(), originals);
        Assert.Empty(files.FilePaths);
        Assert.Equal(new[] { "01-spring.png", "02-summer.png", "03-autumn.png", "04-winter.png" }, prepared.Files.Select(file => file.RelativePath));
        for (var index = 0; index < 4; index++) Assert.Equal(originals[index].CopyBytes(), prepared.Files[index].Asset.CopyBytes());
        var manifest = prepared.ManifestBytes.ToArray();
        manifest[0] = 0;
        Assert.NotEqual(0, prepared.ManifestBytes.Span[0]);
    }

    [Fact]
    public void ContentAndSeasonOrderSelectDistinctContentReferences()
    {
        var files = new InMemoryFileStore(); var root = Root(); var originals = Originals();
        var first = WelcomeWallGallerySource.Prepare(files, root, originals);
        Assert.Equal(first.Reference, WelcomeWallGallerySource.Prepare(files, root, originals).Reference);
        Assert.NotEqual(first.Reference, WelcomeWallGallerySource.Prepare(files, root, originals.Reverse().ToArray()).Reference);
        Assert.NotEqual(first.Reference, WelcomeWallGallerySource.Prepare(files, root, Originals(1)).Reference);
        Assert.Empty(files.FilePaths);
    }

    [Fact]
    public void CancelledPreparationPublishesNothing()
    {
        var files = new InMemoryFileStore(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => WelcomeWallGallerySource.Prepare(files, Root(), Originals(), cancellation.Token));
        Assert.Empty(files.FilePaths);
    }

    [Theory]
    [InlineData(0)] [InlineData(3)] [InlineData(5)]
    public void RequiresExactlyFourImages(int count)
    {
        var files = new InMemoryFileStore();
        Assert.Throws<ArgumentException>(() => WelcomeWallGallerySource.Prepare(files, Root(), Enumerable.Repeat(Originals()[0], count).ToArray()));
        Assert.Empty(files.FilePaths);
    }
}
