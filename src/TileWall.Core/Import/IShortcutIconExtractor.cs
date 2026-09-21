using TileWall.Core.Entries;

namespace TileWall.Core.Import;

/// <summary>
/// 候选图标提取面（M8 设计 §5.4）：Shell 真身经 SHGetFileInfoW（资源读取，不启动目标进程）→ PNG 字节。
/// 失败（含畸形 .lnk/.url 的 COM 异常，R-3）→ null → UI 占位字形。
/// 图标仅用于导入窗候选列表：导入磁贴前景维持 M4 占位现状，缓存路径绝不写入配置（缓存可清理非真值）。
/// </summary>
public interface IShortcutIconExtractor
{
    /// <summary>提取候选图标为 PNG 字节；不可得 → null（调用方落占位）。</summary>
    byte[]? Extract(string fullPath, EntryKind kind);
}
