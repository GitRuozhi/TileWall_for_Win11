using System.Text.Json;
using System.Text.Json.Serialization;

namespace TileWall.Core.Shell;

/// <summary>Explorer「添加到 TileWall」的一次投递：路径批次 + 命令行截断标记（M9 设计 §4.3/§4.4）。</summary>
public sealed record ExplorerAddMessage(IReadOnlyList<string> Paths, bool Truncated)
{
    public static ExplorerAddMessage Empty { get; } = new([], false);
}

/// <summary>
/// Explorer 添加负载编解码（M9 设计 §4.3）：UTF-8 JSON <c>{"v":1,"truncated":…,"paths":[…]}</c>。
/// 解码先校验 <c>v</c> 再取 <c>paths</c>，每条必须为绝对路径且真实存在（File/Directory），否则丢弃该条——
/// 外部传入路径只用于复制/建链，绝不进入 Shell 执行面（§16.7）。解码失败（非 JSON/版本不符/超限）整体拒收。
/// 纯 BCL（System.Text.Json 既有依赖），单测不碰 Win32。
/// </summary>
public static class ExplorerAddPayload
{
    public const int Version = 1;

    /// <summary>解码上限：WM_COPYDATA 路径列表为 KB 量级；超过 1 MB 一律按协议错误拒收。</summary>
    public const int MaxPayloadBytes = 1 << 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static byte[] Encode(IReadOnlyList<string> paths, bool truncated = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return JsonSerializer.SerializeToUtf8Bytes(new PayloadDto(Version, truncated, [.. paths]), JsonOptions);
    }

    public static bool TryDecode(byte[]? payload, out ExplorerAddMessage message)
    {
        message = ExplorerAddMessage.Empty;
        if (payload is null || payload.Length == 0 || payload.Length > MaxPayloadBytes)
        {
            return false;
        }

        PayloadDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<PayloadDto>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (dto is null || dto.V != Version || dto.Paths is null)
        {
            return false;
        }

        // 逐条过滤（§4.3）：非绝对/不存在 → 丢弃该条；其余（含全空）照常投递——
        // 全空到达主应用后呈现「所选对象不含可添加的文件或文件夹」，不静默吞掉用户手势。
        var paths = dto.Paths.Where(IsAddableExistingPath).ToArray();
        message = new ExplorerAddMessage(paths, dto.Truncated);
        return true;
    }

    /// <summary>接收侧单条准入：绝对路径且真实存在（目录或文件）。</summary>
    public static bool IsAddableExistingPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.IsPathRooted(path)
        && (File.Exists(path) || Directory.Exists(path));

    private sealed record PayloadDto(int V, bool Truncated, IReadOnlyList<string> Paths);
}
