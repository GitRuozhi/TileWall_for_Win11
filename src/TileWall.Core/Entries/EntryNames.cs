namespace TileWall.Core.Entries;

/// <summary>托管入口类型；扩展名由类型决定、用户不可编辑（设计 §6.3 [R10]）。</summary>
public enum EntryKind
{
    /// <summary>非 .lnk/.url 或无入口。</summary>
    None,

    /// <summary>Windows 快捷方式（IShellLink）。</summary>
    Lnk,

    /// <summary>互联网快捷方式（INI 风格纯文本）。</summary>
    Url,
}

/// <summary>
/// Windows 文件名规则校验（设计 §6.3 [R10]）：纯函数、固定错误码、不静默改字。
/// 校验对象是「主体名」（不含扩展名——扩展名由入口类型决定）。固定次序逐条上报，空列表 = 合法：
/// <c>NAME_EMPTY</c> / <c>NAME_INVALID_CHARS</c> / <c>NAME_RESERVED</c> / <c>NAME_TRAILING_JUNK</c> / <c>NAME_TOO_LONG</c>。
/// </summary>
public static class EntryNames
{
    public const string NameEmpty = "NAME_EMPTY";
    public const string NameInvalidChars = "NAME_INVALID_CHARS";
    public const string NameReserved = "NAME_RESERVED";
    public const string NameTrailingJunk = "NAME_TRAILING_JUNK";
    public const string NameTooLong = "NAME_TOO_LONG";

    /// <summary>主体名长度上限：给 Objects/&lt;稳定标识&gt;/ 目录前缀留出 MAX_PATH 余量（M4 设计 §5.1）。</summary>
    public const int MaxBaseNameLength = 200;

    /// <summary>保留设备名（[R10] 官方规则：作用于首个 '.' 之前的主体名，大小写不敏感；COM1–9/LPT1–9 不含两位数）。</summary>
    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    public static IReadOnlyList<string> Validate(string? baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return [NameEmpty]; // 隐藏标题走 ShowTitle=false，不落到这里（设计 §6.3）
        }

        var errors = new List<string>(2);

        if (baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            errors.Add(NameInvalidChars); // 探针 F：" < > | \0 控制符 : * ? \ /
        }

        var dotIndex = baseName.IndexOf('.');
        var mainPart = dotIndex >= 0 ? baseName[..dotIndex] : baseName;
        if (Array.Exists(ReservedDeviceNames, reserved => reserved.Equals(mainPart, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add(NameReserved); // "CON.txt" 亦拒：保留判定作用于主体名（[R10]）
        }

        if (baseName.EndsWith('.') || baseName.EndsWith(' '))
        {
            errors.Add(NameTrailingJunk);
        }

        if (baseName.Length > MaxBaseNameLength)
        {
            errors.Add(NameTooLong);
        }

        return errors;
    }

    /// <summary>"&lt;base&gt;.lnk|.url"（扩展名由 <paramref name="kind"/> 决定；None 非法）。</summary>
    public static string CombineName(string baseName, EntryKind kind) => kind switch
    {
        EntryKind.Lnk => baseName + ".lnk",
        EntryKind.Url => baseName + ".url",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "入口类型必须为 Lnk 或 Url。"),
    };

    /// <summary>相对路径/文件名 → 入口类型（OrdinalIgnoreCase 比较 .lnk/.url；其余为 None）。</summary>
    public static EntryKind KindOfRelativePath(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        return extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? EntryKind.Lnk
            : extension.Equals(".url", StringComparison.OrdinalIgnoreCase) ? EntryKind.Url
            : EntryKind.None;
    }

    /// <summary>相对路径/文件名 → 主体名（「文件名主体」即名称真值来源，设计 §16.2）。</summary>
    public static string BaseNameOf(string relativePathOrName) => Path.GetFileNameWithoutExtension(relativePathOrName);

    /// <summary>
    /// 默认名推导（仅表单初值/提交兜底，推导失败就地报错由用户改字——不静默替换，设计 §6.3）：
    /// 直接网址 → 主机名；程序/文件 → 文件名主体；文件夹 → 文件夹名。
    /// </summary>
    public static string SuggestBaseName(string targetOrUrl, bool targetIsDirectory)
    {
        if (Uri.TryCreate(targetOrUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return uri.Host; // §5.1「不能把含冒号和斜线的整个 URL 当作文件名」
        }

        var trimmed = targetOrUrl.TrimEnd('\\', '/');
        return targetIsDirectory ? Path.GetFileName(trimmed) : Path.GetFileNameWithoutExtension(trimmed);
    }
}
