namespace TileWall.Core.Configuration;

/// <summary>System.IO 直通包装（设计 §5.2）；WriteAllBytes 落盘刷新后才算写入成功（§5.3 原子保存第 3 步）。</summary>
public sealed class LocalFileStore : IFileStore
{
    public bool Exists(string path) => File.Exists(path);

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public void WriteAllBytes(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    public void Move(string src, string dst, bool overwrite) => File.Move(src, dst, overwrite);

    public void Copy(string src, string dst, bool overwrite) => File.Copy(src, dst, overwrite);

    public void Delete(string path) => File.Delete(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public IReadOnlyList<string> ListDirectories(string directoryPath) =>
        Directory.Exists(directoryPath)
            ? [.. Directory.EnumerateDirectories(directoryPath).Order(StringComparer.Ordinal)]
            : [];

    public void DeleteDirectory(string directoryPath)
    {
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }
}
