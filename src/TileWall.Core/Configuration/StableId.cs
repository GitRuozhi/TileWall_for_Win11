namespace TileWall.Core.Configuration;

/// <summary>
/// 稳定标识（设计 §2.1、[R10]）：创建对象时一次性生成，此后所有保存不再改变；
/// 永不复用、永不随改名/移动/清链接改变。作 JSON 主键与 Objects/&lt;id&gt;/ 托管目录名。
/// </summary>
public static class StableId
{
    /// <summary>GUID "N" 格式：32 位十六进制，文件系统安全。</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");
}
