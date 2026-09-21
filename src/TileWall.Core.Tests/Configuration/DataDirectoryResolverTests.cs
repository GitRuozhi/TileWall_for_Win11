using TileWall.Core.Configuration;
using Xunit;

namespace TileWall.Core.Tests.Configuration;

/// <summary>
/// M9 测试（硬性要求 3）：packaged/unpackaged 两分支的数据目录与图标缓存根解析（M9 设计 §3.1/§3.2）。
/// 纯逻辑：packaged 分支由应用工程喂入 ApplicationData 的真实路径，本套件用等价字符串断言结构。
/// </summary>
public sealed class DataDirectoryResolverTests
{
    private const string LocalFolderPath = @"C:\Users\u\AppData\Local\Packages\TileWall_b44kswq522h2p\LocalState";
    private const string LocalAppData = @"C:\Users\u\AppData\Local";
    private const string LocalCachePath = @"C:\Users\u\AppData\Local\Packages\TileWall_b44kswq522h2p\LocalCache";

    [Fact]
    public void Resolve_PackagedBranch_UsesLocalFolder()
    {
        var directory = DataDirectoryResolver.Resolve(
            packaged: true, environmentOverride: null, LocalFolderPath, LocalAppData);

        Assert.Equal(DataDirectoryMode.PackagedLocalFolder, DataDirectoryResolver.SelectMode(true, null));
        Assert.Equal(LocalFolderPath, directory.RootPath);
        Assert.Equal(Path.Combine(LocalFolderPath, "Objects"), directory.ObjectsPath); // 布局与 unpackaged 完全一致
        Assert.Equal(Path.Combine(LocalFolderPath, "Recovery"), directory.RecoveryPath);
    }

    [Fact]
    public void Resolve_UnpackagedBranch_UsesLocalAppDataTileWall()
    {
        var directory = DataDirectoryResolver.Resolve(
            packaged: false, environmentOverride: null, LocalFolderPath, LocalAppData);

        Assert.Equal(DataDirectoryMode.LocalAppData, DataDirectoryResolver.SelectMode(false, null));
        Assert.Equal(Path.Combine(LocalAppData, "TileWall"), directory.RootPath); // 拍板 Q1 既有
    }

    [Fact]
    public void Resolve_EnvironmentOverride_WinsInBothModes()
    {
        var packaged = DataDirectoryResolver.Resolve(
            packaged: true, environmentOverride: @"D:\uia-pkg", LocalFolderPath, LocalAppData);
        var unpackaged = DataDirectoryResolver.Resolve(
            packaged: false, environmentOverride: @"D:\uia-dev ", LocalFolderPath, LocalAppData);

        Assert.Equal(DataDirectoryMode.EnvironmentOverride, DataDirectoryResolver.SelectMode(true, @"D:\uia-pkg"));
        Assert.Equal(@"D:\uia-pkg", packaged.RootPath); // packaged 下 UIA 隔离依旧最高优先（设计 §3.1）
        Assert.Equal(@"D:\uia-dev", unpackaged.RootPath); // 空白边缘 trim
    }

    [Fact]
    public void Resolve_BlankOverride_FallsThroughToMode()
    {
        Assert.Equal(DataDirectoryMode.PackagedLocalFolder, DataDirectoryResolver.SelectMode(true, "   "));
        Assert.Equal(DataDirectoryMode.LocalAppData, DataDirectoryResolver.SelectMode(false, ""));
    }

    [Fact]
    public void IconCacheRoot_PackagedUsesLocalCache_UnpackagedUsesDataRoot()
    {
        Assert.Equal(LocalCachePath, DataDirectoryResolver.SelectIconCacheRoot(true, LocalCachePath, LocalFolderPath));
        Assert.Equal(LocalFolderPath, DataDirectoryResolver.SelectIconCacheRoot(false, null, LocalFolderPath));
        // packaged 但 LocalCacheFolder 不可得 → 退回数据根（不抛、不硬编码）
        Assert.Equal(LocalFolderPath, DataDirectoryResolver.SelectIconCacheRoot(true, "  ", LocalFolderPath));
    }
}
