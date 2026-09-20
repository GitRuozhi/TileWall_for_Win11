using TileWall.Core.Carousel;
using TileWall.Core.Configuration;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Carousel;

/// <summary>
/// T-CATALOG（M6 设计 §11.1；§6.1；B18）：
/// 单张/多张直出 + 缺失/白名单外过滤；文件夹枚举（InMemoryFileStore.ListFiles）：
/// 白名单外剔除（.gif/.txt/.webp）、子目录不进、Ordinal 确定性排序、空/不存在目录 → 空；
/// 候选全失效 → 空清单 → 引擎 None。
/// </summary>
public sealed class ImageCatalogTests
{
    private const string Folder = @"C:\imgs";
    private const string Sub = @"C:\imgs\sub";

    private static InMemoryFileStore NewStore()
    {
        var files = new InMemoryFileStore();
        foreach (var path in new[]
                 {
                     $@"{Folder}\b.png", $@"{Folder}\a.jpg", $@"{Folder}\c.jpeg", $@"{Folder}\d.BMP",
                     $@"{Folder}\bad.gif", $@"{Folder}\note.txt", $@"{Folder}\modern.webp", $@"{Folder}\noext",
                     $@"{Sub}\nested.png",
                 })
        {
            files.WriteAllBytes(path, [1, 2, 3]);
        }

        return files;
    }

    [Fact]
    public void Single_ExistingAndWhitelisted_Passes_OthersFiltered()
    {
        var files = NewStore();
        Assert.Equal([$@"{Folder}\a.jpg"], ImageCatalog.EnumerateCandidates(
            new GroupImages { Kind = GroupImageSourceKind.Single, ImagePaths = [$@"{Folder}\a.jpg"] }, files));
        Assert.Equal([$@"{Folder}\d.BMP"], ImageCatalog.EnumerateCandidates(
            new GroupImages { Kind = GroupImageSourceKind.Single, ImagePaths = [$@"{Folder}\d.BMP"] }, files));
        Assert.Empty(ImageCatalog.EnumerateCandidates(
            new GroupImages { Kind = GroupImageSourceKind.Single, ImagePaths = [$@"{Folder}\missing.png"] }, files));
        Assert.Empty(ImageCatalog.EnumerateCandidates(
            new GroupImages { Kind = GroupImageSourceKind.Single, ImagePaths = [$@"{Folder}\bad.gif"] }, files));
    }

    [Fact]
    public void Multiple_KeepsListOrder_And_FiltersMissing()
    {
        var files = NewStore();
        var source = new GroupImages
        {
            Kind = GroupImageSourceKind.Multiple,
            ImagePaths = [$@"{Folder}\c.jpeg", $@"{Folder}\gone.png", $@"{Folder}\a.jpg", $@"{Folder}\bad.gif"],
        };
        Assert.Equal([$@"{Folder}\c.jpeg", $@"{Folder}\a.jpg"], ImageCatalog.EnumerateCandidates(source, files));
    }

    [Fact]
    public void Folder_TopLevelOnly_WhitelistOnly_OrdinalDeterministic()
    {
        var files = NewStore();
        var candidates = ImageCatalog.EnumerateCandidates(
            new GroupImages { Kind = GroupImageSourceKind.Folder, FolderPath = Folder }, files);

        // 大小写不敏感白名单（.BMP 入选）；.gif/.txt/.webp/无扩展名剔除；子目录文件不进；Ordinal 排序
        Assert.Equal([$@"{Folder}\a.jpg", $@"{Folder}\b.png", $@"{Folder}\c.jpeg", $@"{Folder}\d.BMP"], candidates);

        // 确定性：两次枚举逐项相等
        Assert.Equal(candidates, ImageCatalog.EnumerateCandidates(
            new GroupImages { Kind = GroupImageSourceKind.Folder, FolderPath = Folder }, files));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(@"C:\no-such-dir")]
    public void Folder_EmptyOrMissingDirectory_YieldsEmpty(string? folder)
    {
        var files = NewStore();
        Assert.Empty(ImageCatalog.EnumerateCandidates(
            new GroupImages { Kind = GroupImageSourceKind.Folder, FolderPath = folder }, files));
    }

    [Fact]
    public void None_YieldsEmpty_And_EmptyCandidatesMeanEngineNone()
    {
        var files = NewStore();
        Assert.Empty(ImageCatalog.EnumerateCandidates(new GroupImages(), files));

        // 引擎接线：空候选 → Evaluate 步骤 1 → None（B18 天然不轮播）
        var scheduler = new CarouselScheduler(new FixedClock());
        var decision = scheduler.Evaluate([], new CarouselState(), CarouselRuntimeFactory.New(), explicitCheck: false);
        Assert.Equal(CarouselAction.None, decision.Action);
    }

    [Fact]
    public void LocalFileStore_ListFiles_TopLevelOrdinalSorted()
    {
        var root = Path.Combine(Path.GetTempPath(), "tilewall-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        try
        {
            foreach (var name in new[] { "z.png", "a.png", "m.png" })
            {
                File.WriteAllText(Path.Combine(root, name), "x");
            }

            File.WriteAllText(Path.Combine(root, "nested", "deep.png"), "x");
            var store = new LocalFileStore();
            Assert.Equal(
            [
                Path.Combine(root, "a.png"),
                Path.Combine(root, "m.png"),
                Path.Combine(root, "z.png"),
            ], store.ListFiles(root)); // 顶层、不递归、Ordinal
            Assert.Empty(store.ListFiles(@"C:\definitely-missing"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }
}
