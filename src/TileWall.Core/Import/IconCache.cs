using System.Security.Cryptography;
using TileWall.Core.Configuration;

namespace TileWall.Core.Import;

/// <summary>
/// 候选图标磁盘缓存（M8 设计 §5.4）：&lt;root&gt;/Cache/Icons/&lt;sha256(大小写归一 fullPath)&gt;.png。
/// 命中跳过提取；写入失败静默跳过（缓存尽力而为、永不权威——目录可整体清理重建）。
/// 纯 IFileStore 逻辑（可测：InMemoryFileStore 直接单测）。
/// </summary>
public sealed class IconCache
{
    private readonly IFileStore _files;
    private readonly string _cacheDirectory;

    public IconCache(IFileStore files, string dataRootPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrEmpty(dataRootPath);
        _files = files;
        _cacheDirectory = Path.Combine(dataRootPath, "Cache", "Icons");
    }

    /// <summary>缓存命中 → PNG 字节；未命中/读取失败 → null。</summary>
    public byte[]? TryRead(string fullPath)
    {
        try
        {
            var path = CachePathOf(fullPath);
            return _files.Exists(path) ? _files.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // 缓存读取失败不影响主流程
        }
    }

    /// <summary>尽力写入；失败静默（缓存不权威）。</summary>
    public void Write(string fullPath, byte[] pngBytes)
    {
        try
        {
            _files.CreateDirectory(_cacheDirectory);
            _files.WriteAllBytes(CachePathOf(fullPath), pngBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 尽力而为：缓存永不成为真值来源
        }
    }

    /// <summary>启动清理入口（App 尽力调用；目录不存在时静默）。</summary>
    public void Clear()
    {
        try
        {
            _files.DeleteDirectory(_cacheDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 尽力而为
        }
    }

    private string CachePathOf(string fullPath)
    {
        var normalized = fullPath.ToUpperInvariant(); // 大小写归一（Windows 路径不区分大小写）
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return Path.Combine(_cacheDirectory, Convert.ToHexString(hash) + ".png");
    }
}
