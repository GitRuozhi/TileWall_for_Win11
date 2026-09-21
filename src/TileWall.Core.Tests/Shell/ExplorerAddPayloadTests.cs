using TileWall.Core.Shell;
using Xunit;

namespace TileWall.Core.Tests.Shell;

/// <summary>
/// M9 测试 T1（设计 §6.1）：ExplorerAddPayload 编解码——JSON 往返；v 不符/非 JSON/超限 → 整体拒收；
/// 路径非绝对/不存在/空 → 丢弃该条（不整体失败）；截断标记随载荷往返。
/// </summary>
public sealed class ExplorerAddPayloadTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("tilewall-m9-payload").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string ExistingFile()
    {
        var path = Path.Combine(_tempDir, "存在 目录", "示例.lnk");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "stub");
        return path;
    }

    [Fact]
    public void EncodeDecode_RoundTrips_PathsAndTruncatedFlag()
    {
        var file = ExistingFile();

        var payload = ExplorerAddPayload.Encode([file, @"C:\不存在_单测\never.bin"], truncated: true);

        Assert.True(ExplorerAddPayload.TryDecode(payload, out var message));
        Assert.True(message.Truncated);
        Assert.Equal([file], [.. message.Paths]); // 不存在条目被逐条丢弃，存在条目保序保留
    }

    [Fact]
    public void EncodeDecode_EmptyPaths_ProducesEmptyAcceptedMessage()
    {
        var payload = ExplorerAddPayload.Encode([], truncated: false);

        Assert.True(ExplorerAddPayload.TryDecode(payload, out var message));
        Assert.False(message.Truncated);
        Assert.Empty(message.Paths);
    }

    [Fact]
    public void TryDecode_WrongVersion_RejectsEntirePayload()
    {
        var json = """{"v":99,"truncated":false,"paths":[]}"""u8.ToArray();

        Assert.False(ExplorerAddPayload.TryDecode(json, out var message));
        Assert.Empty(message.Paths);
    }

    [Fact]
    public void TryDecode_NonJson_Rejects()
    {
        Assert.False(ExplorerAddPayload.TryDecode("不是 JSON"u8.ToArray(), out _));
    }

    [Fact]
    public void TryDecode_NullEmptyOrOversize_Rejects()
    {
        Assert.False(ExplorerAddPayload.TryDecode(null, out _));
        Assert.False(ExplorerAddPayload.TryDecode([], out _));
        Assert.False(ExplorerAddPayload.TryDecode(new byte[ExplorerAddPayload.MaxPayloadBytes + 1], out _));
    }

    [Fact]
    public void TryDecode_DropsRelativeAndBlankEntries()
    {
        var file = ExistingFile();
        // 用序列化器构造（手工拼 JSON 需转义反斜杠，易错）；混入相对/空白条目验证逐条丢弃
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new { v = 1, truncated = false, paths = new[] { file, "相对/路径.txt", "   ", "" } });

        Assert.True(ExplorerAddPayload.TryDecode(json, out var message));
        Assert.Equal([file], [.. message.Paths]);
    }

    [Fact]
    public void IsAddableExistingPath_RequiresAbsoluteAndExisting()
    {
        var file = ExistingFile();

        Assert.True(ExplorerAddPayload.IsAddableExistingPath(file));
        Assert.True(ExplorerAddPayload.IsAddableExistingPath(_tempDir));
        Assert.False(ExplorerAddPayload.IsAddableExistingPath(@"C:\不存在_单测\never.bin"));
        Assert.False(ExplorerAddPayload.IsAddableExistingPath("相对/路径.txt"));
        Assert.False(ExplorerAddPayload.IsAddableExistingPath(""));
        Assert.False(ExplorerAddPayload.IsAddableExistingPath(null));
    }
}
