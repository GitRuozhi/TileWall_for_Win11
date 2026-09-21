namespace TileWall.Core.Configuration;

/// <summary>数据目录解析结论（M9 设计 §3.1 三级判定）。</summary>
public enum DataDirectoryMode
{
    /// <summary>DEBUG 下 TILEWALL_DATA_DIR 覆盖（UIA 测试隔离，最高优先）。</summary>
    EnvironmentOverride,

    /// <summary>packaged → ApplicationData.Current.LocalFolder（落盘 %LOCALAPPDATA%\Packages\&lt;PFN&gt;\LocalState）。</summary>
    PackagedLocalFolder,

    /// <summary>unpackaged → %LOCALAPPDATA%\TileWall（拍板 Q1 既有）。</summary>
    LocalAppData,
}

/// <summary>
/// 数据目录与图标缓存根的模式解析（M9 设计 §3.1/§3.2；纯逻辑，两分支均有单测）：
/// 判定顺序 = 环境覆盖（DEBUG 才会传入非 null）&gt; packaged &gt; unpackaged。
/// packaged 分支由应用工程以 ApplicationData.Current.LocalFolder/LocalCacheFolder 的真实路径喂入——
/// 本类不引用任何 WinRT 类型，Core 零 Windows 依赖的基线不破坏；目录内布局（config.json、Objects/、Recovery/）两模式完全一致。
/// </summary>
public static class DataDirectoryResolver
{
    public static DataDirectoryMode SelectMode(bool packaged, string? environmentOverride) =>
        !string.IsNullOrWhiteSpace(environmentOverride)
            ? DataDirectoryMode.EnvironmentOverride
            : packaged
                ? DataDirectoryMode.PackagedLocalFolder
                : DataDirectoryMode.LocalAppData;

    /// <summary>按模式解析数据目录。packaged → <paramref name="localFolderPath"/>；否则 → <paramref name="localAppDataRoot"/>\TileWall。</summary>
    public static IDataDirectory Resolve(
        bool packaged,
        string? environmentOverride,
        string localFolderPath,
        string localAppDataRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(localFolderPath);
        ArgumentException.ThrowIfNullOrEmpty(localAppDataRoot);
        var overridePath = environmentOverride?.Trim();
        return SelectMode(packaged, environmentOverride) switch
        {
            DataDirectoryMode.EnvironmentOverride when overridePath is not null => new FileSystemDataDirectory(overridePath),
            DataDirectoryMode.PackagedLocalFolder => new FileSystemDataDirectory(localFolderPath),
            _ => new FileSystemDataDirectory(Path.Combine(localAppDataRoot, "TileWall")),
        };
    }

    /// <summary>
    /// 图标缓存根（M9 设计 §3.2）：packaged → LocalCacheFolder（系统管理升级/卸载清理语义，§16.1）；
    /// unpackaged → 数据根（M8 既有 &lt;root&gt;/Cache/Icons 布局由 IconCache 自行拼接）。
    /// 返回值是 IconCache 构造参数（缓存根基准），不是最终的 Cache/Icons 路径。
    /// </summary>
    public static string SelectIconCacheRoot(bool packaged, string? localCacheFolderPath, string dataRootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataRootPath);
        return packaged && !string.IsNullOrWhiteSpace(localCacheFolderPath)
            ? localCacheFolderPath
            : dataRootPath;
    }
}
