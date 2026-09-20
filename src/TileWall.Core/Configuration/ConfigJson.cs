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
/// 序列化唯一出口：引擎与存储层不得自建 JsonSerializerOptions（设计 §4.3；本类的选项即唯一来源）。
/// 序列化走源生成上下文（写盘字节确定性）；反序列化走同配置的运行时反射——源生成元数据反序列化
/// 会把 JSON 缺省成员写成 null（覆盖属性初始化默认值），破坏 §12「新字段带默认值 → 旧配置逐字段兼容」
/// （M4 时代文件无 images/backdrop，M5 加载必须得到 GroupImages 默认值而非 null）；
/// 两种路径输出字节完全一致（T-SER 断言钉住）。
/// 版本策略：schemaVersion &gt; 1 → Load 判 FutureVersion，不自动迁移、不覆盖文件（设计 §16.6）。
/// 未知 $kind / 非法 JSON → 异常 → ConfigStore 恢复链（§5.3）。
/// </summary>
public static class ConfigJson
{
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.Strict,
    };

    public static byte[] Serialize(TileWallConfig config) =>
        JsonSerializer.SerializeToUtf8Bytes(config, ConfigJsonContext.Default.TileWallConfig);

    public static TileWallConfig Deserialize(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize(utf8, typeof(TileWallConfig), DeserializeOptions) as TileWallConfig
        ?? throw new JsonException("配置载荷为空，无法反序列化。");
}
