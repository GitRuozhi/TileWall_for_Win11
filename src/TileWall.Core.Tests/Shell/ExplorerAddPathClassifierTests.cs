using TileWall.Core.Shell;
using Xunit;

namespace TileWall.Core.Tests.Shell;

/// <summary>
/// M9 测试 T2（设计 §6.1）：路径分类器——.lnk/.url → 复制通道；其余存在路径 → 新建 .lnk；
/// 不存在/非绝对/空 → 剔除；投递侧剔除裸盘符根（本机盘符，§4.2 约束 2）。
/// </summary>
public sealed class ExplorerAddPathClassifierTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("tilewall-m9-classify").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string Touch(string fileName)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, "stub");
        return path;
    }

    [Theory]
    [InlineData(".lnk")]
    [InlineData(".url")]
    public void Classify_ShortcutExtensions_CopyChannel(string extension)
    {
        var path = Touch("工具" + extension.ToUpperInvariant()); // 大写扩展名同样命中（大小写不敏感）

        Assert.Equal(ExplorerAddKind.CopyShortcut, ExplorerAddPathClassifier.Classify(path));
    }

    [Fact]
    public void Classify_ExistingFileAndDirectory_CreateLinkChannel()
    {
        var file = Touch("readme.md");
        var dir = Path.Combine(_tempDir, "子目录");
        Directory.CreateDirectory(dir);

        Assert.Equal(ExplorerAddKind.CreateLink, ExplorerAddPathClassifier.Classify(file));
        Assert.Equal(ExplorerAddKind.CreateLink, ExplorerAddPathClassifier.Classify(dir));
        Assert.Equal(ExplorerAddKind.CreateLink, ExplorerAddPathClassifier.Classify(Touch("工具.exe")));
    }

    [Theory]
    [InlineData(@"C:\不存在_单测\never.bin")]
    [InlineData("相对/路径.txt")]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_MissingOrNotAbsolute_Drop(string path)
    {
        Assert.Equal(ExplorerAddKind.Drop, ExplorerAddPathClassifier.Classify(path));
    }

    [Fact]
    public void Classify_Null_Drop()
    {
        Assert.Equal(ExplorerAddKind.Drop, ExplorerAddPathClassifier.Classify(null));
    }

    [Fact]
    public void FilterExisting_KeepsOnlyAddable_InOrder()
    {
        var lnk = Touch("A.lnk");
        var file = Touch("B.txt");
        var dir = Path.Combine(_tempDir, "C目录");
        Directory.CreateDirectory(dir);

        var kept = ExplorerAddPathClassifier.FilterExisting(
            [lnk, file, dir, @"C:\不存在_单测\never.bin", "相对.txt", ""]);

        Assert.Equal([lnk, file, dir], [.. kept]);
    }

    [Fact]
    public void PrepareForRouting_DropsBareDriveRoots_KeepsSubPaths()
    {
        // 纯逻辑（零 IO）：裸本地盘符根在投递侧剔除（本机盘符不可添加，§4.2）；子路径与 UNC 共享根照常保留
        var kept = ExplorerAddPathClassifier.PrepareForRouting([@"C:\", @"C:\Windows", @"\\server\share", "relative.txt", null]);

        Assert.Equal([@"C:\Windows", @"\\server\share"], [.. kept]);
        Assert.False(ExplorerAddPathClassifier.IsRoutablePath(@"C:\"));
        Assert.False(ExplorerAddPathClassifier.IsRoutablePath("Q:"));
        Assert.True(ExplorerAddPathClassifier.IsRoutablePath(@"C:\Windows"));
        Assert.True(ExplorerAddPathClassifier.IsRoutablePath(@"\\server\share"));
    }
}
