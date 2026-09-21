#if TILEWALL_PACKAGED
using TileWall.Core.Configuration;
using Windows.Storage;

namespace TileWall.Shell;

/// <summary>
/// packaged 数据目录（M9 设计 §3.1）：ApplicationData.Current.LocalFolder.Path → 既有 FileSystemDataDirectory，
/// 目录内布局（config.json、Objects/&lt;稳定标识&gt;/、Staging/、Recovery/）与 unpackaged 完全一致；
/// 系统落盘 %LOCALAPPDATA%\Packages\&lt;PFN&gt;\LocalState（程序不硬编码该路径，§16.1 [R9]）。
/// 仅 TILEWALL_PACKAGED 编译分支引用（unpackaged 下 ApplicationData.Current 无包标识会抛）。
/// </summary>
public sealed class PackagedDataDirectoryProvider : IDataDirectoryProvider
{
    public IDataDirectory GetDefault() => new FileSystemDataDirectory(ApplicationData.Current.LocalFolder.Path);
}
#endif
