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
/// 不存在、非完全限定路径或裸本机盘符根剔除。分类只读磁盘存在性，零写入。
/// </summary>
public static class ExplorerAddPathClassifier
{
    /// <summary>分类单条路径（只读存在性检查；目录优先于快捷方式扩展名）。</summary>
    public static ExplorerAddKind Classify(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsRoutablePath(path))
        {
            return ExplorerAddKind.Drop;
        }

        var isDirectory = Directory.Exists(path);
        var isFile = File.Exists(path);
        if (!isDirectory && !isFile)
        {
            return ExplorerAddKind.Drop;
        }

        if (isDirectory)
        {
            return ExplorerAddKind.CreateLink;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".url", StringComparison.OrdinalIgnoreCase)
            ? ExplorerAddKind.CopyShortcut
            : ExplorerAddKind.CreateLink;
    }

    /// <summary>
    /// 投递侧过滤（COM 服务器 Invoke，零 IO）：完全限定路径且不是裸本机盘符根。
    /// 本机盘符（C:\）在 Invoke 时剔除（§4.2 约束 2）；回收站等虚拟对象取不到 FILESYSPATH，天然不在此列。
    /// </summary>
    public static IReadOnlyList<string> PrepareForRouting(IEnumerable<string?> paths) =>
        paths.Where(IsRoutablePath).Cast<string>().ToArray();

    public static bool IsRoutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(RemoveExtendedDrivePrefixForNormalization(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root) || !IsSamePathIgnoringTrailingSeparators(fullPath, root))
        {
            return true; // 非「路径==根」的普通路径
        }

        // 路径即根：仅本机盘符根剔除；普通 UNC/扩展 UNC 共享根仍按原规则保留。
        return !IsLocalDriveRoot(root);
    }

    private static bool IsLocalDriveRoot(string root)
    {
        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (IsDriveDesignator(trimmedRoot))
        {
            return true;
        }

        // 扩展长度盘符路径的 Path root 形如 \\?\C:\；普通扩展长度子路径仍会因不等于 root 而放行。
        const string extendedPathPrefix = "\\\\?\\";
        return trimmedRoot.StartsWith(extendedPathPrefix, StringComparison.OrdinalIgnoreCase)
            && IsDriveDesignator(trimmedRoot[extendedPathPrefix.Length..]);
    }

    private static bool IsDriveDesignator(string path) =>
        path.Length == 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';

    private static string RemoveExtendedDrivePrefixForNormalization(string path)
    {
        const string extendedPathPrefix = "\\\\?\\";
        if (!path.StartsWith(extendedPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var drivePath = path[extendedPathPrefix.Length..];
        return drivePath.Length >= 3
            && IsDriveDesignator(drivePath[..2])
            && (drivePath[2] == Path.DirectorySeparatorChar || drivePath[2] == Path.AltDirectorySeparatorChar)
            ? drivePath
            : path; // 扩展 UNC 或其它设备路径不去前缀，保留其既有根语义
    }

    private static bool IsSamePathIgnoringTrailingSeparators(string path, string root)
    {
        var pathWithoutTrailingSeparators = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootWithoutTrailingSeparators = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(pathWithoutTrailingSeparators, rootWithoutTrailingSeparators, StringComparison.OrdinalIgnoreCase);
    }
}
