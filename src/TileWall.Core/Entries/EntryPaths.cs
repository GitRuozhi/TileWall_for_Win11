namespace TileWall.Core.Entries;

/// <summary>
/// 数据目录布局常量与路径拼装（M4 设计 §4.1）：
/// <code>
/// &lt;root&gt;/config.json                     正式配置（唯一权威）
/// &lt;root&gt;/Objects/&lt;稳定标识&gt;/&lt;名&gt;.lnk|.url   每对象独立目录、至多一个活动入口（C20）
/// &lt;root&gt;/Staging/&lt;commitId&gt;/             联合提交暂存区 + 提交日志 commit.json
/// &lt;root&gt;/Recovery/Entries/&lt;commitId&gt;/    回滚与单槽撤销材料（journal.json + 备份 + removed/）
/// </code>
/// 相对路径（config 中的 EntryReference.RelativePath 与提交日志内）一律 "Objects/&lt;id&gt;/&lt;名&gt;" 正斜杠形式；
/// 文件系统路径经 <see cref="Full"/> 用 Path.Combine 还原（Windows 接受混合分隔符，BCL 语义一致）。
/// </summary>
public static class EntryPaths
{
    public const string ObjectsDirName = "Objects";
    public const string StagingDirName = "Staging";
    public const string RecoveryDirName = "Recovery";
    public const string RecoveryEntriesDirName = "Entries";
    public const string RemovedDirName = "removed";

    /// <summary>"Objects/&lt;id&gt;/&lt;fileName&gt;"（config 与提交日志内的规范形式）。</summary>
    public static string EntryRelativePath(string objectId, string fileName) => $"{ObjectsDirName}/{objectId}/{fileName}";

    /// <summary>相对路径 → 文件系统全路径（正斜杠统一为本机分隔符——内存存储按字符串键值，必须全键一致）。</summary>
    public static string Full(string rootPath, string relativePath) =>
        Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public static string ObjectsDir(string rootPath, string objectId) => Path.Combine(rootPath, ObjectsDirName, objectId);

    public static string StagingRoot(string rootPath) => Path.Combine(rootPath, StagingDirName);

    public static string StagingDir(string rootPath, string commitId) => Path.Combine(rootPath, StagingDirName, commitId);

    public static string RecoveryEntriesRoot(string rootPath) =>
        Path.Combine(rootPath, RecoveryDirName, RecoveryEntriesDirName);

    public static string RecoveryEntriesDir(string rootPath, string commitId) =>
        Path.Combine(rootPath, RecoveryDirName, RecoveryEntriesDirName, commitId);

    public static string FileNameOf(string relativePathOrName) => Path.GetFileName(relativePathOrName);
}
