using TileWall.Core.Configuration;
using TileWall.Core.Entries;

namespace TileWall.Core.Import;

/// <summary>候选来源（M8 设计 §5.1：当前用户 / 所有用户两个已知文件夹根）。</summary>
public enum SourceKind
{
    /// <summary>FOLDERID_Programs（当前用户）。</summary>
    User,

    /// <summary>FOLDERID_CommonPrograms（所有用户）。</summary>
    Common,
}

/// <summary>
/// 开始菜单候选（M8 设计 §5.2）：只承载文件名主体 + 来源 + 完整路径——
/// 未读目标内容（流水线零 ReadAllBytes、不执行候选），列表不显示「目标是什么」。
/// </summary>
public sealed record StartMenuCandidate(
    string FullPath,
    string DisplayName,
    EntryKind Kind,
    SourceKind Source,
    string RelativeDir)
{
    /// <summary>名称过不了 EntryNames.Validate 的原因列表；空 = 可导入。</summary>
    public IReadOnlyList<string> NotImportableReasons { get; init; } = [];

    /// <summary>false = 保留显示、禁止勾选、就地说明（不静默剔除）。</summary>
    public bool IsImportable => NotImportableReasons.Count == 0;
}

/// <param name="SkippedDirectories">跳过的不可读、reparse、越界或重复目录计数；缺失来源根不计。</param>
public sealed record StartMenuScanResult(IReadOnlyList<StartMenuCandidate> Candidates, int SkippedDirectories);

/// <summary>
/// 开始菜单候选枚举流水线（M8 设计 §5.2；纯 Core 逻辑，经 IFileStore 可测）：
/// 逐根 BFS 递归（GetPathInfo 检查根界与 reparse；ListDirectories/ListFiles 枚举）→ 仅收 .lnk/.url（EntryNames.KindOfRelativePath，OrdinalIgnoreCase）
/// → 去重键 = 归一化完整路径（OrdinalIgnoreCase；不按名称/EXE 合并——C06 的实现即「去重键与目标无关」）
/// → 名称不过 EntryNames.Validate 的候选标记 NotImportable（保留显示、禁止勾选）。
/// 目录不可读（IOException | UnauthorizedAccessException）→ 该目录整体跳过、计数、其余继续。
/// 不读任何候选文件内容、不执行（IFileStore 只检查路径元信息和枚举目录/文件名）。
/// </summary>
public static class StartMenuScanner
{
    public static StartMenuScanResult Enumerate(IFileStore files, IReadOnlyList<(string Path, SourceKind Source)> roots)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(roots);

        var candidates = new List<StartMenuCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // 去重键：归一化完整路径
        var skipped = 0;

        foreach (var (rootPath, source) in roots)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                continue; // 来源查询失败（IKnownFolderPaths 返回 null）→ 来源整体跳过
            }

            var rootFullPath = NormalizePath(rootPath);
            if (string.IsNullOrEmpty(rootFullPath))
            {
                continue;
            }

            var pending = new Queue<string>();
            var scheduled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Enqueue(rootFullPath);
            scheduled.Add(rootFullPath);
            while (pending.Count > 0)
            {
                var directory = pending.Dequeue();
                if (!IsWithinRoot(rootFullPath, directory))
                {
                    skipped++;
                    continue;
                }

                IReadOnlyList<string> subDirectories;
                IReadOnlyList<string> directoryFiles;
                try
                {
                    var pathInfo = files.GetPathInfo(directory);
                    if (!pathInfo.Exists)
                    {
                        if (!string.Equals(directory, rootFullPath, StringComparison.OrdinalIgnoreCase))
                        {
                            skipped++; // 子目录在枚举后消失
                        }

                        continue;
                    }

                    if (!pathInfo.IsDirectory || pathInfo.IsReparsePoint)
                    {
                        skipped++;
                        continue;
                    }

                    subDirectories = files.ListDirectories(directory);
                    directoryFiles = files.ListFiles(directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++; // 读取失败 ≠ 候选丢失的沉默：计数后该目录整体跳过，其余继续
                    continue;
                }

                foreach (var subDirectory in subDirectories)
                {
                    var normalizedSubDirectory = NormalizePath(subDirectory);
                    if (!IsWithinRoot(rootFullPath, normalizedSubDirectory))
                    {
                        skipped++; // 不跟随从 Programs 根外返回的路径
                        continue;
                    }

                    try
                    {
                        var pathInfo = files.GetPathInfo(normalizedSubDirectory);
                        if (!pathInfo.Exists || !pathInfo.IsDirectory || pathInfo.IsReparsePoint)
                        {
                            skipped++;
                            continue;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        skipped++;
                        continue;
                    }

                    if (!scheduled.Add(normalizedSubDirectory))
                    {
                        skipped++; // 包括回指祖先目录的循环边
                        continue;
                    }

                    pending.Enqueue(normalizedSubDirectory);
                }

                foreach (var file in directoryFiles)
                {
                    var kind = EntryNames.KindOfRelativePath(file);
                    if (kind == EntryKind.None)
                    {
                        continue; // 仅 .lnk/.url
                    }

                    var normalized = NormalizePath(file);
                    if (!IsWithinRoot(rootFullPath, normalized))
                    {
                        continue; // 防御异常文件枚举器返回根外路径
                    }

                    if (!seen.Add(normalized))
                    {
                        continue; // 同一原快捷方式只出现一次（全路径去重，不按显示名/EXE 合并）
                    }

                    var relativeDirectory = RelativeDirectoryOf(rootFullPath, Path.GetDirectoryName(normalized) ?? normalized);
                    candidates.Add(new StartMenuCandidate(
                        normalized,
                        Path.GetFileNameWithoutExtension(normalized),
                        kind,
                        source,
                        relativeDirectory)
                    {
                        NotImportableReasons = EntryNames.Validate(Path.GetFileNameWithoutExtension(normalized)),
                    });
                }
            }
        }

        return new StartMenuScanResult(candidates, skipped);
    }

    /// <summary>
    /// 归一化完整路径（绝对化 + 分隔符统一）。防御：Path.GetFullPath 会把含 DOS 设备名
    /// （CON/NUL…，如「CON.lnk」）的路径改写为 "\\.\CON" 形态、丢失文件名与目录信息——
    /// 检出该改写即回退原样字符串（IFileStore 枚举本就返回绝对路径；保留名候选的标记依赖原文件名）。
    /// </summary>
    private static string NormalizePath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var fileName = Path.GetFileName(path);
            return fileName.Length > 0 && full.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)
                ? full
                : path;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>只接受根本身或根目录分隔符边界内的路径，避免把 C:\Programs2 当作 C:\Programs 子树。</summary>
    private static bool IsWithinRoot(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        var normalizedPath = Path.TrimEndingDirectorySeparator(path);
        if (string.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            || normalizedRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>来源根下的相对目录（根本身 → 空串；根外 → 原样，防御）。</summary>
    private static string RelativeDirectoryOf(string rootFullPath, string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return string.Empty;
        }

        var trimmedRoot = rootFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = StringComparison.OrdinalIgnoreCase;
        if (!directory.StartsWith(trimmedRoot, comparison))
        {
            return directory;
        }

        var relative = directory[trimmedRoot.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return relative;
    }
}
