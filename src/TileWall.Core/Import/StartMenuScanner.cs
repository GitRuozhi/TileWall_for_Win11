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

/// <param name="SkippedDirectories">逐目录 try/catch 跳过的不可读目录计数（含不可读根；「无声失败禁令」提示依据）。</param>
public sealed record StartMenuScanResult(IReadOnlyList<StartMenuCandidate> Candidates, int SkippedDirectories);

/// <summary>
/// 开始菜单候选枚举流水线（M8 设计 §5.2；纯 Core 逻辑，经 IFileStore 可测）：
/// 逐根 BFS 递归（ListDirectories/ListFiles）→ 仅收 .lnk/.url（EntryNames.KindOfRelativePath，OrdinalIgnoreCase）
/// → 去重键 = 归一化完整路径（OrdinalIgnoreCase；不按名称/EXE 合并——C06 的实现即「去重键与目标无关」）
/// → 名称不过 EntryNames.Validate 的候选标记 NotImportable（保留显示、禁止勾选）。
/// 目录不可读（IOException | UnauthorizedAccessException）→ 该目录整体跳过、计数、其余继续。
/// 不读任何候选文件内容、不执行（IFileStore 只调目录/文件名枚举）。
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
            pending.Enqueue(rootFullPath);
            while (pending.Count > 0)
            {
                var directory = pending.Dequeue();
                IReadOnlyList<string> subDirectories;
                IReadOnlyList<string> directoryFiles;
                try
                {
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
                    pending.Enqueue(NormalizePath(subDirectory));
                }

                foreach (var file in directoryFiles)
                {
                    var kind = EntryNames.KindOfRelativePath(file);
                    if (kind == EntryKind.None)
                    {
                        continue; // 仅 .lnk/.url
                    }

                    var normalized = NormalizePath(file);
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
