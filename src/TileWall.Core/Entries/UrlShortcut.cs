using System.Text;

namespace TileWall.Core.Entries;

/// <summary>
/// .url 互联网快捷方式（INI 风格纯文本）读写：不经 COM [R11]（IShellLink 无 URL 能力，探针 E 实测）。
/// 编辑只替换 URL= 行，其余行（IconFile/IconIndex 等外部字段）逐字节保留（设计 §6.4 语义保真）。
/// 按 InternetShortcut 区段解析；支持无 BOM UTF-8、UTF-16 LE BOM 和保真 Latin-1。歧义与 Shell 不支持的目标编码拒绝；新建内容为无 BOM UTF-8。
/// </summary>
public static class UrlShortcut
{
    public const string UrlEmpty = "URL_EMPTY";
    public const string UrlSchemeUnsupported = "URL_SCHEME_UNSUPPORTED";
    public const string UrlTooLong = "URL_TOO_LONG";
    public const string UrlWhitespace = "URL_WHITESPACE";

    /// <summary>URL 长度上限（上限取经典 2048）。</summary>
    public const int MaxUrlLength = 2048;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding WritingUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Encoding StrictLatin1 = CreateStrictLatin1();
    private const string UrlLinePrefix = "URL=";

    /// <summary>固定错误码列表（空 = 语法合法）：URL_EMPTY / URL_WHITESPACE / URL_SCHEME_UNSUPPORTED / URL_TOO_LONG。</summary>
    public static IReadOnlyList<string> ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return [UrlEmpty];
        }

        var errors = new List<string>(1);
        if (HasWhitespace(url))
        {
            errors.Add(UrlWhitespace); // 含换行/制表：同时阻断 INI 注入
        }

        var uriSupported = !LooksLikeWindowsDrivePath(url) &&
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri &&
            !uri.Scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase) &&
            ((!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
              !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
             !string.IsNullOrWhiteSpace(uri.Host));
        if (!uriSupported)
        {
            errors.Add(UrlSchemeUnsupported); // 接受语法有效的非 file 绝对 URI；注册可用性由应用层检查
        }

        if (url.Length > MaxUrlLength)
        {
            errors.Add(UrlTooLong);
        }

        return errors;
    }

    /// <summary>"[InternetShortcut]\r\nURL=&lt;url&gt;\r\n"（探针 E 实测格式，无 BOM UTF-8）。</summary>
    public static byte[] CreateContent(string url) =>
        WritingUtf8.GetBytes($"[InternetShortcut]\r\n{UrlLinePrefix}{url}\r\n");

    public sealed record Metadata(bool HasInternetShortcut, string? Url, string? IconFile, string? IconIndex);

    /// <summary>Only unambiguous fields in InternetShortcut participate in identity and icons.</summary>
    public static Metadata ReadMetadata(byte[] content) => Parse(content).Metadata;

    public static string? ReadUrlLine(byte[] content) => ReadMetadata(content).Url;

    /// <summary>只替换 InternetShortcut 内唯一 URL 值，保留编码、换行与其他字段；缺失时插入正确区段。</summary>
    public static byte[] RewriteUrlLine(byte[] content, string newUrl)
    {
        if (ValidateUrl(newUrl).Count != 0) throw new InvalidDataException("Invalid replacement URL.");
        var parsed = Parse(content);
        if (parsed.Preamble.Length == 3) throw new InvalidDataException("UTF-8 BOM Internet Shortcuts cannot be safely edited for Windows Shell.");
        var text = parsed.Text;
        string rewritten;
        if (parsed.UrlStart >= 0)
            rewritten = text[..parsed.UrlStart] + newUrl + text[parsed.UrlEnd..];
        else
        {
            var insertion = parsed.SectionEnd;
            var prefix = text[..insertion];
            var separator = prefix.Length == 0 || prefix.EndsWith('\r') || prefix.EndsWith('\n') ? "" : "\r\n";
            rewritten = prefix + separator + (parsed.Metadata.HasInternetShortcut ? "" : "[InternetShortcut]\r\n") +
                UrlLinePrefix + newUrl + "\r\n" + text[insertion..];
        }
        return [.. parsed.Preamble, .. parsed.Encoding.GetBytes(rewritten)];
    }

    private static bool HasWhitespace(string url)
    {
        foreach (var c in url)
        {
            if (char.IsWhiteSpace(c))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeWindowsDrivePath(string value) =>
        value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' &&
        (value[2] is '\\' or '/');

    private sealed record Parsed(string Text, Encoding Encoding, byte[] Preamble, Metadata Metadata,
        int UrlStart, int UrlEnd, int SectionEnd);

    private static Parsed Parse(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Encoding encoding; byte[] preamble = []; int offset = 0;
        if (content.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            throw new InvalidDataException("UTF-16 BE Internet Shortcuts are not supported by Windows Shell.");
        if (content.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
        { encoding = new UnicodeEncoding(false, false, true); preamble = [0xFF, 0xFE]; offset = 2; }
        else
        {
            if (content.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            { preamble = [0xEF, 0xBB, 0xBF]; offset = 3; }
            try { _ = StrictUtf8.GetString(content.AsSpan(offset)); encoding = StrictUtf8; }
            catch (DecoderFallbackException) { encoding = StrictLatin1; }
        }
        string text;
        try { text = encoding.GetString(content, offset, content.Length - offset); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid shortcut text encoding.", ex); }
        if (text.Contains('\0')) throw new InvalidDataException("NUL in Internet Shortcut text.");
        var found = false; var inSection = false; var sectionEnd = text.Length;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var urlStart = -1; var urlEnd = -1;
        for (var index = 0; index < text.Length;)
        {
            var end = index;
            while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
            var line = text[index..end]; var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (inSection) sectionEnd = index;
                inSection = trimmed.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase);
                if (inSection)
                {
                    if (found) throw new InvalidDataException("Duplicate InternetShortcut section.");
                    found = true; sectionEnd = text.Length;
                }
            }
            else if (inSection && trimmed.Length > 0 && !trimmed.StartsWith(';') && !trimmed.StartsWith('#'))
            {
                var equals = line.IndexOf('=');
                if (equals >= 0)
                {
                    var key = line[..equals].Trim();
                    if (key.Equals("URL", StringComparison.OrdinalIgnoreCase) || key.Equals("IconFile", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("IconIndex", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!fields.TryAdd(key, line[(equals + 1)..].Trim())) throw new InvalidDataException("Duplicate shortcut field.");
                        if (key.Equals("URL", StringComparison.OrdinalIgnoreCase))
                        {
                            urlStart = index + equals + 1;
                            while (urlStart < end && char.IsWhiteSpace(text[urlStart])) urlStart++;
                            urlEnd = end;
                            while (urlEnd > urlStart && char.IsWhiteSpace(text[urlEnd - 1])) urlEnd--;
                        }
                    }
                }
            }
            index = end;
            if (index < text.Length && text[index] == '\r') index++;
            if (index < text.Length && text[index] == '\n') index++;
        }
        if (preamble.Length == 3 && fields.ContainsKey("URL"))
            throw new InvalidDataException("UTF-8 BOM target shortcuts are not supported by Windows Shell.");
        return new(text, encoding, preamble,
            new(found, fields.GetValueOrDefault("URL"), fields.GetValueOrDefault("IconFile"), fields.GetValueOrDefault("IconIndex")),
            urlStart, urlEnd, sectionEnd);
    }

    private static Encoding CreateStrictLatin1()
    {
        var encoding = (Encoding)Encoding.Latin1.Clone();
        encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        return encoding;
    }

}
