namespace TileWall.Core.Entries;

/// <summary>
/// 托管入口路径守卫（SC-08 + F-04，评审批 A7）：字符串前缀 ≠ 目录边界。
/// 相对路径必须恰为三段 "Objects/&lt;对象Id&gt;/&lt;名&gt;.lnk|.url"（段级结构判定，纯字符串可判定），
/// 再以数据根为基做 <see cref="Path.GetFullPath"/> 规范化后的完整目录边界比较
/// （结果必须仍在 &lt;root&gt;/Objects/&lt;id&gt;/ 内）。拒绝绝对路径、父目录段（".."）、
/// 跨对象引用、混合分隔符逃逸、子目录嵌套与非法文件名字符；任何规范化异常一律拒绝（fail closed）。
/// 边界比较两侧都以同一 root 拼接后规范化：即使 root 是盘符相对形式（测试虚拟根），
/// 两侧共享同一解析前缀，比较仍然自洽（真实 Windows 语义下与 Path.GetFullPath 行为一致）。
/// </summary>
public static class EntryPathGuard
{
    /// <summary>无数据根上下文（配置校验器等纯字符串场景）使用的哨兵虚拟根：边界比较两侧同根同解析，结论与根无关。</summary>
    public const string VirtualRoot = "tilewall-vroot";

    /// <summary>对象 Id 合法性：非空、非 "."/".."、不含路径分隔符与文件名非法字符、长度给对象目录留出余量。</summary>
    public static bool IsObjectIdValid(string? objectId)
    {
        if (string.IsNullOrWhiteSpace(objectId) || objectId.Length > EntryNames.MaxBaseNameLength)
        {
            return false;
        }

        return objectId is not ("." or "..")
            && objectId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && objectId.IndexOf('/') < 0
            && objectId.IndexOf('\\') < 0;
    }

    /// <summary>
    /// 入口相对路径的段级结构判定（不依赖数据根）：恰为
    /// "Objects/&lt;合法Id&gt;/&lt;合法文件名.lnk|.url&gt;" 三段；非 rooted、无 ".." 段、无空段。
    /// </summary>
    public static bool IsEntryRelativePathStructureValid(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false; // 绝对路径 / 盘符路径 /  UNC、根反斜杠开头一律拒绝
        }

        var segments = relativePath.Split('/', '\\');
        if (segments.Length != 3
            || !segments[0].Equals(EntryPaths.ObjectsDirName, StringComparison.OrdinalIgnoreCase)
            || !IsObjectIdValid(segments[1])
            || !IsEntryFileNameValid(segments[2]))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 相对路径是否确指 &lt;root&gt;/Objects/&lt;objectId&gt;/ 下的一个入口文件（配置校验与日志回放共用的最终边界）：
    /// 段级结构 + Id 与所属对象一致 + GetFullPath 规范化后仍在对象目录内。
    /// </summary>
    public static bool IsEntryRelativePathAllowed(string rootPath, string objectId, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)
            || !IsObjectIdValid(objectId)
            || !IsEntryRelativePathStructureValid(relativePath))
        {
            return false;
        }

        var segments = relativePath!.Split('/', '\\');
        if (!segments[1].Equals(objectId, StringComparison.OrdinalIgnoreCase))
        {
            return false; // 跨对象引用（Objects/&lt;别的Id&gt;/…）
        }

        return IsWithinObjectDirectory(rootPath, objectId, relativePath);
    }

    /// <summary>
    /// GetFullPath 目录边界比较（fail closed）：规范化后的完整路径必须仍位于
    /// 规范化后的 &lt;root&gt;/Objects/&lt;objectId&gt;/ 目录内。任何异常（非法字符、过长、
    /// 段内冒号等）按越界处理。
    /// </summary>
    public static bool IsWithinObjectDirectory(string rootPath, string objectId, string relativePath)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var objectDirectory = Path.GetFullPath(EntryPaths.ObjectsDir(rootPath, objectId));
            var boundary = objectDirectory.EndsWith(Path.DirectorySeparatorChar)
                ? objectDirectory
                : objectDirectory + Path.DirectorySeparatorChar;
            return full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)
                && full.Length > boundary.Length; // 目录本身不是合法入口文件
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or System.Security.SecurityException)
        {
            return false; // 规范化失败 = 不可判定 = 拒绝
        }
    }

    /// <summary>
    /// 完整路径形态的托管入口判定（EntryLauncher 接线，F-04）：必须已 rooted、已规范化
    /// （GetFullPath 幂等——混合分隔符/父段在此暴露）、末三段恰为 Objects/&lt;Id&gt;/&lt;入口文件&gt;。
    /// </summary>
    public static bool IsManagedEntryFullPath(string? entryFullPath)
    {
        if (string.IsNullOrWhiteSpace(entryFullPath) || !Path.IsPathRooted(entryFullPath))
        {
            return false;
        }

        var segments = entryFullPath.Split('/', '\\');
        if (segments.Length < 3
            || !segments[^3].Equals(EntryPaths.ObjectsDirName, StringComparison.OrdinalIgnoreCase)
            || !IsObjectIdValid(segments[^2])
            || !IsEntryFileNameValid(segments[^1]))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(entryFullPath), entryFullPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>入口文件名（含扩展名）：非 "."/".."、含 .lnk/.url 扩展名、无文件名非法字符。</summary>
    private static bool IsEntryFileNameValid(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && fileName is not ("." or "..")
        && EntryNames.KindOfRelativePath(fileName) != EntryKind.None
        && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
