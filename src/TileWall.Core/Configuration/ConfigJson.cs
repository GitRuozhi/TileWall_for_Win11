using System.Text.Json;
using System.Text.Json.Serialization;

namespace TileWall.Core.Configuration;

/// <summary>JSON 源生成上下文：序列化选项唯一来源（技术选型 §六；camelCase / 忽略 null / 缩进 / 严格数字）。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(TileWallConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext
{
}

/// <summary>
/// 序列化唯一出口：引擎与存储层不得自建 JsonSerializerOptions（设计 §4.3）。
/// 版本策略：schemaVersion &gt; 1 → Load 判 FutureVersion，不自动迁移、不覆盖文件（设计 §16.6）。
/// 未知 $kind / 非法 JSON → 异常 → ConfigStore 恢复链（§5.3）。
/// </summary>
public static class ConfigJson
{
    public static byte[] Serialize(TileWallConfig config) =>
        JsonSerializer.SerializeToUtf8Bytes(config, ConfigJsonContext.Default.TileWallConfig);

    public static TileWallConfig Deserialize(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize(utf8, ConfigJsonContext.Default.TileWallConfig)
        ?? throw new JsonException("配置载荷为空，无法反序列化。");
}
