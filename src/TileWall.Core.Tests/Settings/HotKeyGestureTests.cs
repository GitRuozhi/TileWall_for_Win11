using TileWall.Core.Settings;
using Xunit;

namespace TileWall.Core.Tests.Settings;

/// <summary>
/// T-HK（M7 设计 §12.1）：解析 / 规范化重排 / 往返 / 非法值全规则（§5.1）。
/// 主键须非修饰键本身；修饰集须含 Win/Ctrl/Alt 至少其一（仅 Shift 拒绝）；Core 零 Windows 依赖（原始 VK 码）。
/// </summary>
public sealed class HotKeyGestureTests
{
    private const uint ModAlt = HotKeyGesture.ModAlt;
    private const uint ModCtrl = HotKeyGesture.ModCtrl;
    private const uint ModShift = HotKeyGesture.ModShift;
    private const uint ModWin = HotKeyGesture.ModWin;

    // ————————————————————————————— 解析 —————————————————————————————

    [Theory]
    [InlineData("Win+Oem3", ModWin, 0xC0u)] // 默认组合：Win + 反引号（TileWallConfig 既有值）
    [InlineData("Ctrl+Shift+F5", ModCtrl | ModShift, 0x74u)]
    [InlineData("Alt+1", ModAlt, 0x31u)] // 数字别名 "1" = 0x31
    [InlineData("Ctrl+D1", ModCtrl, 0x31u)] // "D1" 与 "1" 同指
    [InlineData("Control+A", ModCtrl, 0x41u)] // "Control" 别名
    [InlineData("Win+Return", ModWin, 0x0Du)] // "Return" 别名 → VK_RETURN
    public void TryParse_ValidTexts_YieldsModifiersAndRawVk(string text, uint modifiers, uint vk)
    {
        var ok = HotKeyGesture.TryParse(text, out var gesture, out var failure);
        Assert.True(ok, failure);
        Assert.Equal(modifiers, gesture!.Modifiers);
        Assert.Equal(vk, gesture.VirtualKey);
        Assert.True(HotKeyGesture.IsValid(gesture));
        Assert.Null(HotKeyGesture.Validate(gesture));
    }

    [Fact]
    public void TryParse_IsCaseInsensitive()
    {
        Assert.True(HotKeyGesture.TryParse("win+oem3", out var lower));
        Assert.True(HotKeyGesture.TryParse("WIN+OEM3", out var upper));
        Assert.Equal(lower, upper); // record 值相等
    }

    [Fact]
    public void TryParse_UnknownBitsAreStripped()
    {
        var ok = HotKeyGesture.TryParse("Win+Oem3", out var gesture);
        Assert.True(ok);
        var polluted = gesture! with { Modifiers = gesture.Modifiers | 0xFFFF0000 };
        Assert.Equal(gesture, polluted with { Modifiers = polluted.Modifiers & HotKeyGesture.KnownModifierMask });
        Assert.Equal(gesture.Modifiers, polluted.Modifiers & HotKeyGesture.KnownModifierMask);
    }

    // ————————————————————————————— 规范化重排与往返 —————————————————————————————

    [Fact]
    public void ToConfigString_RearrangesToCanonicalOrder_WinCtrlAltShift()
    {
        Assert.True(HotKeyGesture.TryParse("Ctrl+Win+Shift+Alt+Oem7", out var gesture));
        Assert.Equal("Win+Ctrl+Alt+Shift+Oem7", gesture!.ToConfigString());
    }

    [Fact]
    public void ParseFormatRoundTrip_HoldsForBreadthOfCombos()
    {
        var texts = new[]
        {
            "Win+Oem3",
            "Ctrl+Alt+Delete",
            "Win+Shift+S",
            "Ctrl+Shift+F12",
            "Alt+NumPad5",
            "Win+Ctrl+Space",
            "Ctrl+Alt+OemMinus",
        };
        foreach (var text in texts)
        {
            Assert.True(HotKeyGesture.TryParse(text, out var gesture, out var failure), $"{text}: {failure}");
            var formatted = gesture!.ToConfigString();
            Assert.True(HotKeyGesture.TryParse(formatted, out var reparsed, out var failure2), $"{formatted}: {failure2}");
            Assert.Equal(gesture, reparsed); // Parse(Format(g)) == g
            Assert.Equal(formatted, reparsed!.ToConfigString()); // 格式化是定点
        }
    }

    [Fact]
    public void ToDisplayString_UsesFriendlyKeycap()
    {
        Assert.True(HotKeyGesture.TryParse("Win+Oem3", out var gesture));
        Assert.Equal("Win+`", gesture!.ToDisplayString());
        Assert.True(HotKeyGesture.TryParse("Ctrl+OemPlus", out var plus));
        Assert.Equal("Ctrl++", plus!.ToDisplayString());
    }

    // ————————————————————————————— 非法值全规则 —————————————————————————————

    [Theory]
    [InlineData(null, "快捷键为空")]
    [InlineData("", "快捷键为空")]
    [InlineData("   ", "快捷键为空")]
    [InlineData("Win+", "快捷键含空键名")]
    [InlineData("Win++A", "快捷键含空键名")]
    [InlineData("A", "须包含")] // 无修饰键
    [InlineData("Shift+A", "须包含")] // 仅 Shift 拒绝：会吞掉大写输入
    [InlineData("Shift+F5", "须包含")]
    [InlineData("Win+Win", "重复的修饰键")] // 两个修饰键、无主键
    [InlineData("Alt+Win", "缺少主键")]
    [InlineData("Ctrl+Shift", "缺少主键")]
    [InlineData("Win+LWin", "主键不能是修饰键本身")] // 0x5B 在修饰键集合内
    [InlineData("Win+Bogus", "无法识别的键名")] // 未知键名
    [InlineData("Win+A+B", "只能有一个主键")]
    [InlineData("Win+Win+A", "重复的修饰键")]
    public void TryParse_InvalidTexts_FailsWithReason(string? text, string reasonPart)
    {
        var ok = HotKeyGesture.TryParse(text, out var gesture, out var failure);
        Assert.False(ok);
        Assert.Null(gesture);
        Assert.NotNull(failure);
        Assert.Contains(reasonPart, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsModifierAsMainKeyAndUnknownKey()
    {
        Assert.Equal("主键不能是修饰键本身", HotKeyGesture.Validate(new HotKeyGesture(ModWin, 0x11))); // VK_CONTROL
        Assert.Contains("无法识别", HotKeyGesture.Validate(new HotKeyGesture(ModWin, 0xFF)));
        Assert.Contains("须包含", HotKeyGesture.Validate(new HotKeyGesture(0, 0x41)));
        Assert.Null(HotKeyGesture.Validate(new HotKeyGesture(ModWin, 0xC0)));
    }

    [Fact]
    public void IsValid_MatchesValidate()
    {
        Assert.True(HotKeyGesture.IsValid(new HotKeyGesture(ModCtrl | ModAlt, 0x51))); // Ctrl+Alt+Q
        Assert.False(HotKeyGesture.IsValid(new HotKeyGesture(ModShift, 0x51)));
        Assert.False(HotKeyGesture.IsValid(new HotKeyGesture(ModWin, 0x5B))); // Win+LWin
    }
}
