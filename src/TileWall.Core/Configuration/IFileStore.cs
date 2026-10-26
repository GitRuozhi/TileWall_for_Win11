namespace TileWall.Core.Configuration;

/// <summary>
/// 文件操作抽象（设计 §5.2）：Core 内除 <see cref="LocalFileStore"/> 外一律面向此接口——
/// 腾位引擎与序列化完全不碰文件；测试用 InMemoryFileStore 做失败注入。
/// </summary>
public interface IFileStore
{
    /// <summary>Inspect an existing path without following a reparse point. Missing paths return Exists=false.</summary>
    FileStorePathInfo GetPathInfo(string path);
    bool Exists(string path);

    byte[] ReadAllBytes(string path);

    /// <summary>Hold an immutable original for one wall-file export. Local storage denies writes/deletion;
    /// the default supports existing in-memory fault fixtures.</summary>
    Stream OpenReadProtected(string path) => new MemoryStream(ReadAllBytes(path), writable: false);

    void WriteAllBytes(string path, byte[] bytes);

    /// <summary>同卷原子替换（唯一提交点）。</summary>
    void Move(string src, string dst, bool overwrite);

    /// <summary>Publish a complete immutable directory on the same volume; destination must not exist.</summary>
    void MoveDirectory(string source, string destination) => throw new NotSupportedException("Directory publication is unsupported by this file store.");

    void Copy(string src, string dst, bool overwrite);

    /// <summary>尽力删除；目标不存在时静默（File.Delete 语义）。</summary>
    void Delete(string path);

    void CreateDirectory(string path);

    /// <summary>
    /// 子目录全路径列表（不含自身，确定性排序；目录不存在 → 空列表）。
    /// M4：崩溃清扫枚举 Staging/ 与 Recovery/Entries/。
    /// </summary>
    IReadOnlyList<string> ListDirectories(string directoryPath);

    /// <summary>
    /// 目录下文件全路径列表（不含子目录内容——顶层、不递归，M6 设计 §6.1；确定性排序；目录不存在 → 空列表，
    /// 与 <see cref="ListDirectories"/> 语义对齐）。M6：ImageCatalog 文件夹候选枚举的唯一 IO 面。
    /// </summary>
    IReadOnlyList<string> ListFiles(string directoryPath);

    /// <summary>尽力递归删除目录；不存在时静默（Directory.Delete 语义）。用于暂存区和事务恢复材料清理。</summary>
    void DeleteDirectory(string directoryPath);
}

public sealed record FileStorePathInfo(bool Exists, bool IsDirectory, bool IsReparsePoint);
