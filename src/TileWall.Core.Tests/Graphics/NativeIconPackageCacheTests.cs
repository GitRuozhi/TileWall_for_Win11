using TileWall.Graphics;
using Xunit;

namespace TileWall.Core.Tests.Graphics;

public sealed class NativeIconPackageCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tilewall-icon-budget-" + Guid.NewGuid().ToString("N"));
    private static readonly string First = new('A', 64);
    private static readonly string Second = new('B', 64);
    private static readonly IReadOnlyDictionary<string, byte[]> Payload = new Dictionary<string, byte[]>
    {
        [new string('C', 64) + ".ico"] = new byte[100],
        ["manifest.json"] = new byte[20],
    };

    [Fact]
    public void ALeasedPackageCannotBeSplitOrEvictedToFitAnotherPackage()
    {
        var cache = new ManagedImageCache(_root);
        Assert.True(cache.PublishIconPackage(First, Payload, First, new byte[40], 256, default));
        var firstDirectory = cache.Under("Cache", "Images", "Icons", "Packages", First);
        using (cache.Lease(firstDirectory))
        {
            Assert.False(cache.PublishIconPackage(Second, Payload, Second, new byte[40], 256, default));
            Assert.Equal(2, Directory.GetFiles(firstDirectory).Length);
            Assert.False(Directory.Exists(cache.Under("Cache", "Images", "Icons", "Packages", Second)));
        }
        Assert.True(cache.PublishIconPackage(Second, Payload, Second, new byte[40], 256, default));
        Assert.False(Directory.Exists(firstDirectory));
        Assert.Equal(2, Directory.GetFiles(cache.Under("Cache", "Images", "Icons", "Packages", Second)).Length);
        Assert.True(Directory.GetFiles(cache.Under("Cache", "Images", "Icons"), "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length) <= 256);
    }

    [Fact]
    public void ReusingOnePackageOnlyAddsAMappingAndOversizedPackagesNeverStage()
    {
        var cache = new ManagedImageCache(_root);
        Assert.True(cache.PublishIconPackage(First, Payload, First, new byte[20], 300, default));
        Assert.True(cache.PublishIconPackage(First, Payload, Second, new byte[20], 300, default));
        Assert.Single(Directory.GetDirectories(cache.Under("Cache", "Images", "Icons", "Packages")));
        Assert.False(cache.PublishIconPackage(Second, Payload, Second, new byte[20], 100, default));
        Assert.Empty(Directory.GetDirectories(cache.Under("Cache", "Images", "Icons", "Staging")));
    }

    [Fact]
    public void UnsafePackageNamesAndCancelledPublicationCannotCreateResources()
    {
        var cache = new ManagedImageCache(_root);
        var bad = new Dictionary<string, byte[]>(Payload) { ["../outside.ico"] = [1] };
        Assert.Throws<IOException>(() => cache.PublishIconPackage(First, bad, First, [1], 4096, default));
        Assert.Throws<OperationCanceledException>(() => cache.PublishIconPackage(First, Payload, First, [1], 4096, new(true)));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void ARebuiltPayloadRepairsABadSameKeyPackageOnlyAfterItsLeaseEnds()
    {
        var cache = new ManagedImageCache(_root);
        Assert.True(cache.PublishIconPackage(First, Payload, First, new byte[40], 4096, default));
        var directory = cache.Under("Cache", "Images", "Icons", "Packages", First);
        var imagePath = Path.Combine(directory, new string('C', 64) + ".ico");
        File.WriteAllBytes(imagePath, [1, 2, 3]);
        using (cache.Lease(directory))
        {
            Assert.False(cache.PublishIconPackage(First, Payload, First, new byte[40], 4096, default));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(imagePath));
        }
        Assert.True(cache.PublishIconPackage(First, Payload, First, new byte[40], 4096, default));
        Assert.Equal(Payload[Path.GetFileName(imagePath)], File.ReadAllBytes(imagePath));
        Assert.Equal(2, Directory.GetFiles(directory).Length);
    }

    [Fact]
    public void ReplacingOneMappingAtTheBudgetDoesNotEvictAnUnrelatedOlderMapping()
    {
        var cache = new ManagedImageCache(_root);
        const long limit = 320;
        Assert.True(cache.PublishIconPackage(First, Payload, First, new byte[40], limit, default));
        Assert.True(cache.PublishIconPackage(Second, Payload, Second, new byte[40], limit, default));
        var firstMap = cache.Under("Cache", "Images", "Icons", "Mappings", First + ".json");
        var secondMap = cache.Under("Cache", "Images", "Icons", "Mappings", Second + ".json");
        File.SetLastAccessTimeUtc(secondMap, DateTime.UtcNow.AddDays(-10));
        File.SetLastAccessTimeUtc(firstMap, DateTime.UtcNow.AddDays(-5));
        Assert.True(cache.PublishIconPackage(First, Payload, First, new byte[40], limit, default));
        Assert.True(File.Exists(firstMap)); Assert.True(File.Exists(secondMap));
        Assert.Equal(limit, Directory.GetFiles(cache.Under("Cache", "Images", "Icons"), "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length));
    }

    [Fact]
    public void RepeatedColdMissesAndNewPackagesNeedOnlyOneCapacityCensus()
    {
        var cache = new ManagedImageCache(_root);
        for (var index = 0; index < 12; index++)
        {
            var package = ManagedImageCache.Key("package-" + index);
            var mapping = ManagedImageCache.Key("mapping-" + index);
            Assert.Throws<IOException>(() => cache.Read(cache.Under("Cache", "Images", "Icons", "Mappings", mapping + ".json"), 128));
            Assert.Throws<IOException>(() => cache.Read(cache.Under("Cache", "Images", "Icons", "Packages", package, "manifest.json"), 128));
            cache.DiscardIconPackage(package); // Production's ordinary absent-manifest path.
            Assert.True(cache.PublishIconPackage(package, Payload, mapping, new byte[40], 4096, default));
            Assert.Equal((index + 1) * 160L, IconBytes(cache));
        }
        Assert.Equal(1L, cache.IconBudgetCensusCount);
    }

    [Fact]
    public void RebuildingRetainedEmptyPackageDirectoriesNeedsOnlyOneCapacityCensus()
    {
        var cache = new ManagedImageCache(_root);
        var packages = Enumerable.Range(0, 12).Select(index => ManagedImageCache.Key("package-" + index)).ToArray();
        foreach (var key in packages)
        {
            var directory = cache.Under("Cache", "Images", "Icons", "Packages", key);
            cache.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "manifest.json"), [1]);
        }
        // Match leaf-only cache maintenance: remove files and retain every directory.
        foreach (var path in Directory.GetFiles(cache.Under("Cache", "Images", "Icons"), "*", SearchOption.AllDirectories))
            File.Delete(path);
        for (var index = 0; index < packages.Length; index++)
        {
            var package = packages[index];
            var directory = cache.Under("Cache", "Images", "Icons", "Packages", package);
            Assert.True(Directory.Exists(directory)); Assert.Empty(Directory.GetFileSystemEntries(directory));
            if (index == 1)
                using (cache.Lease(directory))
                {
                    cache.DiscardIconPackage(package);
                    Assert.True(Directory.Exists(directory));
                    Assert.False(cache.PublishIconPackage(package, Payload, package, new byte[40], 4096, default));
                    Assert.Equal(1L, cache.IconBudgetCensusCount);
                }
            if (index % 2 == 0) cache.DiscardIconPackage(package);
            Assert.True(cache.PublishIconPackage(package, Payload, package, new byte[40], 4096, default));
            Assert.Equal((index + 1) * 160L, IconBytes(cache));
        }
        Assert.Equal(1L, cache.IconBudgetCensusCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingAccountedPackageFilesStillRequireAFreshCapacityCensus(bool discardFirst)
    {
        var cache = new ManagedImageCache(_root);
        Assert.True(cache.PublishIconPackage(First, Payload, First, new byte[40], 4096, default));
        var directory = cache.Under("Cache", "Images", "Icons", "Packages", First);
        foreach (var path in Directory.GetFiles(directory)) File.Delete(path);
        Assert.True(Directory.Exists(directory));
        if (discardFirst) cache.DiscardIconPackage(First);
        Assert.True(cache.PublishIconPackage(First, Payload, First, new byte[40], 4096, default));
        Assert.Equal(160L, IconBytes(cache));
        Assert.Equal(2L, cache.IconBudgetCensusCount);
        Assert.True(cache.PublishIconPackage(Second, Payload, Second, new byte[40], 4096, default));
        Assert.Equal(320L, IconBytes(cache));
        Assert.Equal(2L, cache.IconBudgetCensusCount);
    }

    [Fact]
    public void MappingGrowthAndShrinkAreChargedAsNetBytes()
    {
        var cache = new ManagedImageCache(_root);
        Assert.True(cache.PublishIconPackage(First, Payload, First, new byte[20], 512, default));
        Assert.True(cache.PublishIconPackage(First, Payload, Second, new byte[20], 512, default));
        Assert.True(cache.PublishIconPackage(First, Payload, First, new byte[120], 512, default));
        Assert.Equal(260L, IconBytes(cache));
        Assert.True(cache.PublishIconPackage(First, Payload, First, new byte[20], 512, default));
        Assert.Equal(160L, IconBytes(cache));
        Assert.Equal(1L, cache.IconBudgetCensusCount);
        using (cache.Lease(cache.Under("Cache", "Images", "Icons", "Packages", First)))
            Assert.True(cache.PublishIconPackage(Second, Payload, ManagedImageCache.Key("third-map"), new byte[40], 320, default));
        Assert.Equal(320L, IconBytes(cache));
        Assert.True(File.Exists(cache.Under("Cache", "Images", "Icons", "Mappings", First + ".json")));
        Assert.True(File.Exists(cache.Under("Cache", "Images", "Icons", "Mappings", Second + ".json")));
    }

    [Fact]
    public void AFailedMappingPublishRecountsTheAlreadyMovedPackage()
    {
        var cache = new ManagedImageCache(_root);
        var blockedMapping = cache.Under("Cache", "Images", "Icons", "Mappings", First + ".json");
        cache.CreateDirectory(blockedMapping); // A real filesystem failure after the package move.
        var failure = Record.Exception(() => cache.PublishIconPackage(First, Payload, First, new byte[40], 256, default));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        var orphan = cache.Under("Cache", "Images", "Icons", "Packages", First);
        Assert.True(Directory.Exists(orphan));
        Assert.Equal(120L, IconBytes(cache));
        Directory.Delete(blockedMapping);
        Assert.True(cache.PublishIconPackage(Second, Payload, Second, new byte[40], 256, default));
        Assert.False(Directory.Exists(orphan));
        Assert.Equal(160L, IconBytes(cache));
        Assert.True(cache.IconBudgetCensusCount > 1);
    }

    [Fact]
    public void NearCapacityRecountsForeignBytesAndStillProtectsLeasedPackages()
    {
        var cache = new ManagedImageCache(_root);
        Assert.True(cache.PublishIconPackage(First, Payload, First, new byte[40], 512, default));
        var foreign = cache.Under("Cache", "Images", "Icons", "foreign.bin");
        File.WriteAllBytes(foreign, new byte[100]);
        Assert.True(cache.PublishIconPackage(Second, Payload, Second, new byte[40], 512, default));
        var firstDirectory = cache.Under("Cache", "Images", "Icons", "Packages", First);
        File.SetLastAccessTimeUtc(cache.Under("Cache", "Images", "Icons", "Mappings", First + ".json"), DateTime.UtcNow.AddDays(-10));
        Directory.SetLastAccessTimeUtc(firstDirectory, DateTime.UtcNow.AddDays(-9));
        var leased = cache.Under("Cache", "Images", "Icons", "Packages", Second);
        var third = ManagedImageCache.Key("third");
        var thirdMapping = ManagedImageCache.Key("third-map");
        using (cache.Lease(leased))
            Assert.True(cache.PublishIconPackage(third, Payload, thirdMapping, new byte[40], 512, default));
        foreach (var (name, expected) in Payload)
        {
            Assert.Equal(expected, File.ReadAllBytes(Path.Combine(leased, name)));
            Assert.Equal(expected, File.ReadAllBytes(cache.Under("Cache", "Images", "Icons", "Packages", third, name)));
        }
        Assert.Equal(new byte[40], File.ReadAllBytes(cache.Under("Cache", "Images", "Icons", "Mappings", thirdMapping + ".json")));
        Assert.Equal(new byte[100], File.ReadAllBytes(foreign));
        Assert.True(IconBytes(cache) <= 512);
        // A mapping can free sufficient capacity without evicting its package.
        // Directory enumeration itself can also refresh directory access times.
        Assert.True(IconBytes(cache) < 580);
        Assert.True(cache.IconBudgetCensusCount > 1);
    }

    [Fact]
    public async Task GenericIconWritesInvalidateCapacityAccounting()
    {
        var cache = new ManagedImageCache(_root);
        Assert.True(cache.PublishIconPackage(First, Payload, First, new byte[40], 512, default));
        var staged = await cache.StageAsync(new byte[200], default);
        var extra = cache.Under("Cache", "Images", "Icons", "extra.pixels");
        cache.Publish(staged, extra);
        Assert.True(cache.PublishIconPackage(Second, Payload, Second, new byte[40], 512, default));
        Assert.True(IconBytes(cache) <= 512);
        var afterPublish = cache.IconBudgetCensusCount;
        cache.Delete(extra);
        Assert.True(cache.PublishIconPackage(Second, Payload, ManagedImageCache.Key("another-map"), new byte[1], 512, default));
        Assert.True(cache.IconBudgetCensusCount > afterPublish);
    }

    [Fact]
    public void CapacityAccountingNeverAuthorizesChangedFileContents()
    {
        var cache = new ManagedImageCache(_root);
        Assert.True(cache.PublishIconPackage(First, Payload, First, new byte[40], 4096, default));
        var directory = cache.Under("Cache", "Images", "Icons", "Packages", First);
        var image = Path.Combine(directory, new string('C', 64) + ".ico");
        File.WriteAllBytes(image, new byte[200]);
        Assert.Throws<IOException>(() => cache.Read(image, 100));
        using (cache.Lease(directory))
            Assert.False(cache.PublishIconPackage(First, Payload, First, new byte[40], 4096, default));
        Assert.True(cache.PublishIconPackage(First, Payload, First, new byte[40], 4096, default));
        Assert.Equal(Payload[Path.GetFileName(image)], cache.Read(image, 100));
        Assert.Equal(160L, IconBytes(cache));
    }

    private static long IconBytes(ManagedImageCache cache) => Directory.GetFiles(cache.Under("Cache", "Images", "Icons"),
        "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);

    public void Dispose()
    {
        var expected = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_root).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid test root.");
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
