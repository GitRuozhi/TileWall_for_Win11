namespace TileWall.Core.Configuration;

/// <summary>
/// 文件操作抽象（设计 §5.2）：Core 内除 <see cref="LocalFileStore"/> 外一律面向此接口——
/// 腾位引擎与序列化完全不碰文件；测试用 InMemoryFileStore 做失败注入。
/// </summary>
public interface IFileStore
{
    bool Exists(string path);

    byte[] ReadAllBytes(string path);

    void WriteAllBytes(string path, byte[] bytes);

    /// <summary>同卷原子替换（唯一提交点）。</summary>
    void Move(string src, string dst, bool overwrite);

    void Copy(string src, string dst, bool overwrite);

    /// <summary>尽力删除；目标不存在时静默（File.Delete 语义）。</summary>
    void Delete(string path);

    void CreateDirectory(string path);

    /// <summary>
    /// 子目录全路径列表（不含自身，确定性排序；目录不存在 → 空列表）。
    /// M4：崩溃清扫枚举 Staging/ 与 Recovery/Entries/。
    /// </summary>
    IReadOnlyList<string> ListDirectories(string directoryPath);

    /// <summary>尽力递归删除目录；不存在时静默（Directory.Delete 语义）。M4：暂存区/撤销材料清理。</summary>
    void DeleteDirectory(string directoryPath);
}
