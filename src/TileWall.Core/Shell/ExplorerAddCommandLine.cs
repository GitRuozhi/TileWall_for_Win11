namespace TileWall.Core.Shell;

/// <summary>
/// Explorer 添加入口与启动解析共用的参数名称和负载长度上限。
/// </summary>
public static class ExplorerAddCommandLine
{
    /// <summary>添加流程触发参数（与 --background 同为命令行开关）。</summary>
    public const string AddArgument = "--add";

    /// <summary>批次因命令行长度被截断的标记参数（墙上提示「请分批添加」的依据）。</summary>
    public const string TruncatedMarker = "--add-truncated";

    /// <summary>参数串长度上限（Windows 32767 上限留出安全余量，含 exe 路径）。</summary>
    public const int MaxArgumentsLength = 32000;
}
