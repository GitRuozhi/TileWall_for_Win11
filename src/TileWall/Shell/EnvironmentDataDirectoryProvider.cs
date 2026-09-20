using TileWall.Core.Configuration;

namespace TileWall.Shell;

/// <summary>
/// 环境变量指定的数据目录（仅 DEBUG UIA 测试隔离用：TILEWALL_DATA_DIR=&lt;临时目录&gt;）。
/// 判定逻辑在 App（#if DEBUG）；本类只做「路径 → IDataDirectory」的适配。
/// </summary>
public sealed class EnvironmentDataDirectoryProvider : IDataDirectoryProvider
{
    private readonly string _rootPath;

    public EnvironmentDataDirectoryProvider(string rootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);
        _rootPath = rootPath;
    }

    public IDataDirectory GetDefault() => new FileSystemDataDirectory(_rootPath);
}
