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

    public void Dispose()
    {
        var expected = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_root).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid test root.");
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
