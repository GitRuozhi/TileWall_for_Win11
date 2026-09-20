namespace TileWall.Core.Settings;

/// <summary>
/// 快捷键组合（M7 设计 §5.1）：原始 VK 码 + 修饰集，Core 零 Windows 依赖。
/// 修饰位数值恰与 Win32 MOD_* 一致（Shell 层经 GlobalHotKeyRegistrar 直传 RegisterHotKey），
/// 但 Core 只把它们当作自己的标志位：解析/校验/格式化不引用任何 Windows 类型，保证纯单测。
/// </summary>
/// <param name="Modifiers">修饰集（ModWin/ModCtrl/ModAlt/ModShift 的按位或，已规范化掉未知位）。</param>
/// <param name="VirtualKey">主键 Win32 虚拟键码（如 0xC0 = Oem3 反引号）。</param>
public sealed record HotKeyGesture(uint Modifiers, uint VirtualKey)
{
    public const uint ModAlt = 0x0001;
    public const uint ModCtrl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    /// <summary>已知修饰位全集；解析时多余位一律剥除（规范化，往返成立的前提）。</summary>
    public const uint KnownModifierMask = ModAlt | ModCtrl | ModShift | ModWin;

    /// <summary>从文本解析（如 "Win+Oem3"）。失败时 gesture = null 并给出原因文本（就地呈现用）。</summary>
    public static bool TryParse(string? text, out HotKeyGesture? gesture, out string? failureReason)
    {
        gesture = null;
        failureReason = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            failureReason = "快捷键为空";
            return false;
        }

        var tokens = text.Split('+');
        uint modifiers = 0;
        string? mainToken = null;
        foreach (var raw in tokens)
        {
            var token = raw.Trim();
            if (token.Length == 0)
            {
                failureReason = "快捷键含空键名";
                return false;
            }

            if (ModifierFromToken(token) is { } bit)
            {
                if ((modifiers & bit) != 0)
                {
                    failureReason = $"重复的修饰键：{token}";
                    return false;
                }

                modifiers |= bit;
            }
            else if (mainToken is null)
            {
                mainToken = token;
            }
            else
            {
                failureReason = $"快捷键只能有一个主键：{mainToken} 与 {token}";
                return false;
            }
        }

        if (mainToken is null)
        {
            failureReason = "快捷键缺少主键";
            return false;
        }

        if (!HotKeyNames.TryParseKey(mainToken, out var vk))
        {
            failureReason = $"无法识别的键名：{mainToken}";
            return false;
        }

        var candidate = new HotKeyGesture(modifiers & KnownModifierMask, vk);
        failureReason = Validate(candidate);
        if (failureReason is not null)
        {
            return false;
        }

        gesture = candidate;
        return true;
    }

    /// <summary>解析重载（无原因文本需求时）。</summary>
    public static bool TryParse(string? text, out HotKeyGesture? gesture) =>
        TryParse(text, out gesture, out _);

    /// <summary>校验既有组合：null = 有效；否则为就地呈现的原因文本。</summary>
    public static string? Validate(HotKeyGesture gesture)
    {
        if (HotKeyNames.IsModifierKey(gesture.VirtualKey))
        {
            return "主键不能是修饰键本身";
        }

        var effective = gesture.Modifiers & KnownModifierMask;
        if ((effective & (ModWin | ModCtrl | ModAlt)) == 0)
        {
            return "须包含 Win、Ctrl 或 Alt 修饰键（仅 Shift 的组合会吞掉正常打字输入）";
        }

        if (!HotKeyNames.IsKnownKey(gesture.VirtualKey))
        {
            return $"无法识别的键名：0x{gesture.VirtualKey:X2}";
        }

        return null;
    }

    /// <summary>有效组合判定。</summary>
    public static bool IsValid(HotKeyGesture gesture) => Validate(gesture) is null;

    /// <summary>配置串（规范序 Win &gt; Ctrl &gt; Alt &gt; Shift），Parse(ToConfigString()) == this。</summary>
    public string ToConfigString() => Compose(HotKeyNames.CanonicalNameOf);

    /// <summary>界面显示串（友好键帽名，如 "Win + `"）。</summary>
    public string ToDisplayString() => Compose(HotKeyNames.DisplayNameOf);

    private string Compose(Func<uint, string> nameOf)
    {
        var parts = new List<string>(5);
        if ((Modifiers & ModWin) != 0)
        {
            parts.Add("Win");
        }

        if ((Modifiers & ModCtrl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((Modifiers & ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((Modifiers & ModShift) != 0)
        {
            parts.Add("Shift");
        }

        parts.Add(nameOf(VirtualKey));
        return string.Join("+", parts);
    }

    private static uint? ModifierFromToken(string token) => token.ToUpperInvariant() switch
    {
        "WIN" => ModWin,
        "CTRL" or "CONTROL" => ModCtrl,
        "ALT" => ModAlt,
        "SHIFT" => ModShift,
        _ => null,
    };
}

/// <summary>VK 码与键名对照表（Core 自有名称表，Shell 层不做转换——数值即 Win32 VK 码）。</summary>
internal static class HotKeyNames
{
    /// <summary>修饰键本体作为主键一律拒绝（Win/Ctrl/Alt/Shift 及其左右变体）。</summary>
    private static readonly HashSet<uint> ModifierKeys = new()
    {
        0x10, 0xA0, 0xA1, // Shift / LShift / RShift
        0x11, 0xA2, 0xA3, // Ctrl / LCtrl / RCtrl
        0x12, 0xA4, 0xA5, // Alt / LAlt / RAlt
        0x5B, 0x5C,       // LWin / RWin
    };

    private static readonly Dictionary<uint, string[]> CanonicalNames = BuildNames();

    private static Dictionary<uint, string[]> BuildNames()
    {
        var names = new Dictionary<uint, string[]>();
        for (var i = 0; i < 26; i++)
        {
            names[(uint)(0x41 + i)] = [((char)('A' + i)).ToString()];
        }

        for (var i = 0; i < 10; i++)
        {
            var digit = ((char)('0' + i)).ToString();
            names[(uint)(0x30 + i)] = [digit, "D" + i]; // "1" 与 "D1" 同指
        }

        for (var i = 0; i < 24; i++)
        {
            names[(uint)(0x70 + i)] = ["F" + (i + 1)];
        }

        for (var i = 0; i < 10; i++)
        {
            names[(uint)(0x60 + i)] = ["NumPad" + i];
        }

        void Add(uint vk, params string[] aliases) => names[vk] = aliases;

        Add(0x08, "Back");
        Add(0x09, "Tab");
        Add(0x0D, "Enter", "Return");
        Add(0x13, "Pause");
        Add(0x14, "CapsLock");
        Add(0x1B, "Esc");
        Add(0x20, "Space");
        Add(0x21, "PgUp");
        Add(0x22, "PgDn");
        Add(0x23, "End");
        Add(0x24, "Home");
        Add(0x25, "Left");
        Add(0x26, "Up");
        Add(0x27, "Right");
        Add(0x28, "Down");
        Add(0x2C, "PrintScreen");
        Add(0x2D, "Insert");
        Add(0x2E, "Delete");
        Add(0x5B, "LWin"); // 入表仅为让 "Win+LWin" 这类组合给出「主键不能是修饰键本身」而非「无法识别」
        Add(0x5C, "RWin");
        Add(0x6A, "Multiply");
        Add(0x6B, "Add");
        Add(0x6D, "Subtract");
        Add(0x6E, "Decimal");
        Add(0x6F, "Divide");
        Add(0xBA, "Oem1");
        Add(0xBB, "OemPlus");
        Add(0xBC, "OemComma");
        Add(0xBD, "OemMinus");
        Add(0xBE, "OemPeriod");
        Add(0xBF, "Oem2");
        Add(0xC0, "Oem3"); // 反引号 / 波浪键帽（默认组合 Win+Oem3）
        Add(0xDB, "Oem4");
        Add(0xDC, "Oem5");
        Add(0xDD, "Oem6");
        Add(0xDE, "Oem7");
        return names;
    }

    /// <summary>主键名解析（大小写不敏感，接受别名如 "Return"、"D1"）。</summary>
    public static bool TryParseKey(string token, out uint vk)
    {
        foreach (var pair in CanonicalNames)
        {
            foreach (var name in pair.Value)
            {
                if (string.Equals(name, token, StringComparison.OrdinalIgnoreCase))
                {
                    vk = pair.Key;
                    return true;
                }
            }
        }

        vk = 0;
        return false;
    }

    public static bool IsModifierKey(uint vk) => ModifierKeys.Contains(vk);

    public static bool IsKnownKey(uint vk) => CanonicalNames.ContainsKey(vk) && !ModifierKeys.Contains(vk);

    public static string CanonicalNameOf(uint vk) =>
        CanonicalNames.TryGetValue(vk, out var names) ? names[0] : $"0x{vk:X2}";

    public static string DisplayNameOf(uint vk) => CanonicalNameOf(vk) switch
    {
        "Oem1" => ";",
        "OemPlus" => "+",
        "OemComma" => ",",
        "OemMinus" => "-",
        "OemPeriod" => ".",
        "Oem2" => "/",
        "Oem3" => "`",
        "Oem4" => "[",
        "Oem5" => "\\",
        "Oem6" => "]",
        "Oem7" => "'",
        var name => name,
    };
}
