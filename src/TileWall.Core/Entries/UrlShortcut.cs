using System.Text;

namespace TileWall.Core.Entries;

/// <summary>
/// .url 互联网快捷方式（INI 风格纯文本）读写：不经 COM [R11]（IShellLink 无 URL 能力，探针 E 实测）。
/// 编辑只替换 URL= 行，其余行（IconFile/IconIndex 等外部字段）逐字节保留（设计 §6.4 语义保真）。
/// 字节解码优先严格 UTF-8，失败回退 Latin-1（重编码字节无损耗）；本类新建内容为无 BOM UTF-8。
/// </summary>
public static class UrlShortcut
{
    public const string UrlEmpty = "URL_EMPTY";
    public const string UrlSchemeUnsupported = "URL_SCHEME_UNSUPPORTED";
    public const string UrlTooLong = "URL_TOO_LONG";
    public const string UrlWhitespace = "URL_WHITESPACE";

    /// <summary>URL 长度上限（任务书 M4 范围：仅 http/https；上限取经典 2048）。</summary>
    public const int MaxUrlLength = 2048;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding WritingUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private const string UrlLinePrefix = "URL=";

    /// <summary>固定错误码列表（空 = 合法）：URL_EMPTY / URL_WHITESPACE / URL_SCHEME_UNSUPPORTED / URL_TOO_LONG。</summary>
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

        var schemeSupported = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        if (!schemeSupported)
        {
            errors.Add(UrlSchemeUnsupported); // ftp:/无 scheme 等一律拒（任务书 M4 范围）
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

    /// <summary>首个 URL= 行的值（大小写不敏感匹配前缀；无 → null）。</summary>
    public static string? ReadUrlLine(byte[] content)
    {
        var text = Decode(content);
        foreach (var line in SplitLines(text))
        {
            if (line.StartsWith(UrlLinePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return line[UrlLinePrefix.Length..];
            }
        }

        return null;
    }

    /// <summary>仅替换首个 URL= 行；其余行连同原换行风格逐字节保留。无 URL= 行则按 \r\n 追加（导入异常文件的兜底）。</summary>
    public static byte[] RewriteUrlLine(byte[] content, string newUrl)
    {
        var encoding = DetectEncoding(content);
        var text = encoding.GetString(content);
        var builder = new StringBuilder(text.Length + newUrl.Length + 16);
        var replaced = false;
        var index = 0;
        while (index < text.Length)
        {
            var lineEnd = index;
            while (lineEnd < text.Length && text[lineEnd] != '\r' && text[lineEnd] != '\n')
            {
                lineEnd++;
            }

            var next = lineEnd;
            if (next < text.Length && text[next] == '\r')
            {
                next++;
            }

            if (next < text.Length && text[next] == '\n')
            {
                next++;
            }

            var line = text[index..lineEnd];
            if (!replaced && line.TrimStart().StartsWith(UrlLinePrefix, StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(UrlLinePrefix).Append(newUrl);
                replaced = true;
            }
            else
            {
                builder.Append(line);
            }

            builder.Append(text[lineEnd..next]); // 原样保留该行换行符（\r\n / \n / \r）
            index = next;
        }

        if (!replaced)
        {
            if (text.Length > 0 && !text.EndsWith('\n'))
            {
                builder.Append("\r\n");
            }

            builder.Append(UrlLinePrefix).Append(newUrl).Append("\r\n");
        }

        return encoding.GetBytes(builder.ToString());
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

    private static string Decode(byte[] content)
    {
        try
        {
            return StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(content); // ANSI/本机码页：字节 1:1 映射，重编码无损耗
        }
    }

    private static Encoding DetectEncoding(byte[] content)
    {
        try
        {
            _ = StrictUtf8.GetString(content);
            return WritingUtf8;
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1;
        }
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            var lineEnd = index;
            while (lineEnd < text.Length && text[lineEnd] != '\r' && text[lineEnd] != '\n')
            {
                lineEnd++;
            }

            yield return text[index..lineEnd];

            var next = lineEnd;
            if (next < text.Length && text[next] == '\r')
            {
                next++;
            }

            if (next < text.Length && text[next] == '\n')
            {
                next++;
            }

            index = next;
        }
    }
}
