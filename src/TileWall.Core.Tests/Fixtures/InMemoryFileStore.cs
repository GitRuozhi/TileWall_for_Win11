using TileWall.Core.Configuration;

namespace TileWall.Core.Tests.Fixtures;

/// <summary>失败注入点（设计 §5.2 / §9.1 T-STORE：原子保存三处失败注入）。</summary>
public enum FaultPoint
{
    None,

    /// <summary>写 config.json.tmp 之前（Save 第 3 步）。</summary>
    BeforeWriteTemp,

    /// <summary>复制 Recovery/config.prev.json 之前（Save 第 4 步）。</summary>
    BeforeBackup,

    /// <summary>Move 原子替换之前（Save 第 5 步，唯一提交点）。</summary>
    BeforeMove,
}

/// <summary>
/// 内存文件存储：路径字符串 → 字节内容的字典（路径不做文件系统语义解释，仅作键）。
/// <see cref="InjectedFault"/> 指定在哪一步注入 IOException；目录仅记录存在性。
/// </summary>
public sealed class InMemoryFileStore : IFileStore
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);

    public FaultPoint InjectedFault { get; set; } = FaultPoint.None;

    public bool Exists(string path) => _files.ContainsKey(path);

    public byte[] ReadAllBytes(string path)
    {
        if (!_files.TryGetValue(path, out var bytes))
        {
            throw new FileNotFoundException($"[内存存储] 文件不存在：{path}", path);
        }

        return bytes;
    }

    public void WriteAllBytes(string path, byte[] bytes)
    {
        if (InjectedFault == FaultPoint.BeforeWriteTemp)
        {
            throw new IOException("注入的 IO 失败：BeforeWriteTemp");
        }

        _files[path] = [.. bytes];
    }

    public void Move(string src, string dst, bool overwrite)
    {
        if (InjectedFault == FaultPoint.BeforeMove)
        {
            throw new IOException("注入的 IO 失败：BeforeMove");
        }

        if (!_files.TryGetValue(src, out var bytes))
        {
            throw new FileNotFoundException($"[内存存储] 源文件不存在：{src}", src);
        }

        if (!overwrite && _files.ContainsKey(dst))
        {
            throw new IOException($"[内存存储] 目标已存在：{dst}");
        }

        _files.Remove(src);
        _files[dst] = bytes;
    }

    public void Copy(string src, string dst, bool overwrite)
    {
        if (InjectedFault == FaultPoint.BeforeBackup)
        {
            throw new IOException("注入的 IO 失败：BeforeBackup");
        }

        if (!_files.TryGetValue(src, out var bytes))
        {
            throw new FileNotFoundException($"[内存存储] 源文件不存在：{src}", src);
        }

        if (!overwrite && _files.ContainsKey(dst))
        {
            throw new IOException($"[内存存储] 目标已存在：{dst}");
        }

        _files[dst] = [.. bytes];
    }

    public void Delete(string path) => _files.Remove(path);

    public void CreateDirectory(string path) => _directories.Add(path);

    /// <summary>测试断言辅助：目录是否被创建过。</summary>
    public bool DirectoryCreated(string path) => _directories.Contains(path);
}

/// <summary>测试用数据目录提供者：以任意字符串为根（无文件系统语义）。</summary>
public sealed class FakeDataDirectoryProvider(string root) : IDataDirectoryProvider
{
    public IDataDirectory GetDefault() => new FileSystemDataDirectory(root);
}
