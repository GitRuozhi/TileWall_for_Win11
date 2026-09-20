using TileWall.Core.Configuration;

namespace TileWall.Core.Carousel;

/// <summary>
/// 图片候选枚举纯逻辑（M6 设计 §6.1；§11.1「准备读取或切换时更新可用候选」的唯一入口）。
/// 不监视文件系统、无后台任务（§11.1 明文）；每次调用即时枚举（60 s 级频率，IO 可忽略）。
/// 候选清单不持久化——文件夹内容是派生事实（§6.1）；用户移动/删除图片由重枚举天然消化。
/// </summary>
public static class ImageCatalog
{
    /// <summary>
    /// 静态图片格式白名单（发布前支持清单；不含 .gif/.webp——§11.1 不扩展为 GIF，保守首发）。
    /// 匹配按大小写不敏感（Windows 路径不区分大小写语义；OrdinalIgnoreCase）。
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedExtensions = [".png", ".jpg", ".jpeg", ".bmp"];

    /// <summary>
    /// Single/Multiple → ImagePaths 逐条「存在 + 白名单」过滤，保持清单序；
    /// Folder → ListFiles 顶层（不递归）、白名单、Ordinal 确定性排序；
    /// None / 全部失效 → 空清单（引擎 Evaluate 步骤 1 → None，天然不轮播，B18）。
    /// </summary>
    public static IReadOnlyList<string> EnumerateCandidates(GroupImages source, IFileStore files)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(files);

        switch (source.Kind)
        {
            case GroupImageSourceKind.Single:
            case GroupImageSourceKind.Multiple:
                return [.. source.ImagePaths.Where(IsSupported).Where(files.Exists)];
            case GroupImageSourceKind.Folder:
                if (string.IsNullOrWhiteSpace(source.FolderPath))
                {
                    return [];
                }

                return [.. files
                    .ListFiles(source.FolderPath)
                    .Where(IsSupported)
                    .Order(StringComparer.Ordinal)];
            default:
                return [];
        }
    }

    /// <summary>扩展名白名单判定（大小写不敏感；无扩展名 → false）。</summary>
    public static bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Length > 0
            && SupportedExtensions.Any(ext => string.Equals(extension, ext, StringComparison.OrdinalIgnoreCase));
    }
}
