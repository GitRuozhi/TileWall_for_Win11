using TileWall.Core.Settings;
using Xunit;

namespace TileWall.Core.Tests.Settings;

/// <summary>假注册表 seam：捕获写入内容（T-LOGIN 断言面）。</summary>
internal sealed class FakeRunKeyStore : IRunKeyValueStore
{
    public string? Value { get; private set; }

    /// <summary>测试预置既有值（模拟注册表已存在 Run 条目）。</summary>
    public void Seed(string? value) => Value = value;

    public List<string> Calls { get; } = [];

    public bool ThrowOnWrite { get; set; }

    public string? Read()
    {
        Calls.Add("Read");
        return Value;
    }

    public void Write(string value)
    {
        Calls.Add("Write");
        if (ThrowOnWrite)
        {
            throw new IOException("注册表写入被拒绝");
        }

        Value = value;
    }

    public void Delete()
    {
        Calls.Add("Delete");
        Value = null;
    }
}

/// <summary>
/// T-LOGIN（M7 设计 §12.1 / §8.3）：开 = 写 Run 值且命令行含 --background（引号包裹 exe 全路径）；
/// 关 = 删值；读 = 不存在 → 关；写失败 → false + 原因（UI 保持原态、不假装生效）。
/// 真值源 = 注册表；config.Settings.RunAtLogin 字段已停止消费（§14 风险 5 报备项）。
/// </summary>
public sealed class RegistryLoginStartupTests
{
    private const string ExePath = @"C:\Apps\TileWall\TileWall.exe";

    private static RegistryLoginStartup New(FakeRunKeyStore store) => new(store, ExePath);

    [Fact]
    public void RunValueName_IsTileWall()
    {
        Assert.Equal("TileWall", RegistryLoginStartup.RunValueName);
    }

    [Fact]
    public void SetEnabled_True_WritesValueWithBackgroundArgumentAndQuotedExe()
    {
        var store = new FakeRunKeyStore();
        Assert.True(New(store).SetEnabled(true, out var error));
        Assert.Null(error);
        Assert.Equal($"\"{ExePath}\" --background", store.Value);
        Assert.Contains("--background", store.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void SetEnabled_False_DeletesValue()
    {
        var store = new FakeRunKeyStore();
        store.Seed($"\"{ExePath}\" --background");
        Assert.True(New(store).SetEnabled(false, out var error));
        Assert.Null(error);
        Assert.Null(store.Value);
        Assert.Equal(["Delete"], store.Calls);
    }

    [Fact]
    public void SetEnabled_False_WhenValueMissing_IsSilentlySuccessful()
    {
        var store = new FakeRunKeyStore();
        Assert.True(New(store).SetEnabled(false, out _));
        Assert.Null(store.Value);
    }

    [Fact]
    public void IsEnabled_ReflectsRegistryTruth()
    {
        var store = new FakeRunKeyStore();
        var login = New(store);
        Assert.False(login.IsEnabled); // 不存在 → 关

        _ = login.SetEnabled(true, out _);
        Assert.True(login.IsEnabled); // 有值 → 开（经 seam 读回）
    }

    [Fact]
    public void IsEnabled_ReadFailure_TreatedAsDisabled()
    {
        var store = new ThrowingReadStore();
        Assert.False(new RegistryLoginStartup(store, ExePath).IsEnabled); // 读失败按未开启呈现，不假装成功
    }

    [Fact]
    public void SetEnabled_WriteFailure_ReturnsFalseWithError()
    {
        var store = new FakeRunKeyStore { ThrowOnWrite = true };
        Assert.False(New(store).SetEnabled(true, out var error));
        Assert.NotNull(error);
        Assert.Contains("注册表写入被拒绝", error, StringComparison.Ordinal);
        Assert.Null(store.Value); // 磁盘/注册表零改动
    }

    [Fact]
    public void ToggleRoundTrip_TrueThenFalse_LeavesNoResidue()
    {
        var store = new FakeRunKeyStore();
        var login = New(store);
        _ = login.SetEnabled(true, out _);
        _ = login.SetEnabled(false, out _);
        Assert.Null(store.Value);
        Assert.False(login.IsEnabled);
    }

    private sealed class ThrowingReadStore : IRunKeyValueStore
    {
        public string? Read() => throw new UnauthorizedAccessException("key access denied");

        public void Write(string value) => throw new NotSupportedException();

        public void Delete() => throw new NotSupportedException();
    }
}
