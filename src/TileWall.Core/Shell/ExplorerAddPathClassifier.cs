namespace TileWall.Core.Shell;

/// <summary>Explorer 选中对象的处置通道（M9 设计 §4.5 第 3 步）。</summary>
public enum ExplorerAddKind
{
    /// <summary>剔除（不存在/非绝对路径/本机盘符根等不可添加对象）。</summary>
    Drop,

    /// <summary>.lnk/.url → 托管副本完整复制（EntryDraft.CopyFromFile 通道）。</summary>
    CopyShortcut,

    /// <summary>其余文件/程序/文件夹 → 新建 .lnk 指向原对象（EntryDraft.CreateForPath 通道）。</summary>
    CreateLink,
}

/// <summary>
/// Explorer 添加路径的纯分类器（M9 设计 §4.4/§4.5）：
/// 投递前剔除本机盘符根等「无宿主对象」的文件系统路径；接收后按扩展名分派复制/建链通道，
/// 不存在或非绝对路径剔除。分类只读磁盘存在性，零写入。
/// </summary>
public static class ExplorerAddPathClassifier
{
    /// <summary>分类单条路径（IO：存在性检查）。</summary>
    public static ExplorerAddKind Classify(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return ExplorerAddKind.Drop;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return ExplorerAddKind.Drop;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".url", StringComparison.OrdinalIgnoreCase)
            ? ExplorerAddKind.CopyShortcut
            : ExplorerAddKind.CreateLink;
    }

    /// <summary>接收侧过滤（主应用 HandleExplorerAdd 第 1 步）：保留可分类为复制/建链的路径。</summary>
    public static IReadOnlyList<string> FilterExisting(IEnumerable<string?> paths) =>
        paths.Where(p => Classify(p) != ExplorerAddKind.Drop)
            .Cast<string>()
            .ToArray();

    /// <summary>
    /// 投递侧过滤（COM 服务器 Invoke，零 IO）：绝对路径且不是「裸盘符/共享根」。
    /// 本机盘符（C:\）在 Invoke 时剔除（§4.2 约束 2）；回收站等虚拟对象取不到 FILESYSPATH，天然不在此列。
    /// </summary>
    public static IReadOnlyList<string> PrepareForRouting(IEnumerable<string?> paths) =>
        paths.Where(IsRoutablePath).Cast<string>().ToArray();

    public static bool IsRoutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return false;
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || !string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return true; // 非「路径==根」的普通路径
        }

        // 路径即根：仅裸本地盘符（"C:"/"C:\"）剔除；UNC 共享根（\\srv\share）是合法链接目标，保留
        return !(root.Length >= 2 && root[1] == ':');
    }
}
