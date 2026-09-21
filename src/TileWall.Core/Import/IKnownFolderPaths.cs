namespace TileWall.Core.Import;

/// <summary>
/// 已知文件夹查询面（M8 设计 §5.1、[R6]）：导入窗两类来源目录根的唯一定位口。
/// Core 内无 Win32 依赖；Shell 实现经 SHGetKnownFolderPath 查询
/// FOLDERID_Programs / FOLDERID_CommonPrograms。
/// 任一查询失败（HRESULT != 0，如重定向被组策略禁用）→ 对应属性为 null，
/// 导入窗隐藏该来源——不回退硬编码路径（R-5：只认 API 返回值）。
/// </summary>
public interface IKnownFolderPaths
{
    /// <summary>当前用户「开始菜单\程序」目录；查询失败 → null。</summary>
    string? UserPrograms { get; }

    /// <summary>所有用户「开始菜单\程序」目录；查询失败 → null。</summary>
    string? CommonPrograms { get; }
}
