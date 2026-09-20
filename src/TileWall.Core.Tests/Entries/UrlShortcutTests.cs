using System.Text;
using TileWall.Core.Entries;
using Xunit;

namespace TileWall.Core.Tests.Entries;

/// <summary>T-URL（M4 设计 §10.2）：ValidateUrl 矩阵 + CreateContent/RewriteUrlLine（其余行保留）/ReadUrlLine。</summary>
public class UrlShortcutTests
{
    [Theory]
    [InlineData("https://example.com/path?q=1")]
    [InlineData("http://example.com")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    public void ValidateUrl_http与https_合法(string url)
    {
        Assert.Empty(UrlShortcut.ValidateUrl(url));
    }

    [Theory]
    [InlineData("ftp://example.com/file")]
    [InlineData("example.com/no-scheme")]
    [InlineData("file:///C:/x")]
    public void ValidateUrl_非http_s_SCHEME_UNSUPPORTED(string url)
    {
        Assert.Equal([UrlShortcut.UrlSchemeUnsupported], UrlShortcut.ValidateUrl(url));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateUrl_空_URL_EMPTY(string url)
    {
        Assert.Equal([UrlShortcut.UrlEmpty], UrlShortcut.ValidateUrl(url));
    }

    [Fact]
    public void ValidateUrl_含空白_URL_WHITESPACE_换行阻断INI注入()
    {
        var errors = UrlShortcut.ValidateUrl("https://example.com/a b");
        Assert.Contains(UrlShortcut.UrlWhitespace, errors);

        Assert.Contains(UrlShortcut.UrlWhitespace, UrlShortcut.ValidateUrl("https://x/\r\nURL=https://evil/"));
    }

    [Fact]
    public void ValidateUrl_超长_URL_TOO_LONG()
    {
        var url = "https://example.com/" + new string('a', UrlShortcut.MaxUrlLength);
        Assert.Equal([UrlShortcut.UrlTooLong], UrlShortcut.ValidateUrl(url));
    }

    [Fact]
    public void CreateContent_探针E格式_无BOM()
    {
        var bytes = UrlShortcut.CreateContent("https://example.com/");
        Assert.Equal("[InternetShortcut]\r\nURL=https://example.com/\r\n", Encoding.UTF8.GetString(bytes));
        Assert.NotEqual(0xEF, bytes[0]); // 无 BOM
    }

    [Fact]
    public void ReadUrlLine_取首个URL行_大小写不敏感_无则null()
    {
        var content = "[InternetShortcut]\r\nurl=https://lower.example/\r\n"u8.ToArray();
        Assert.Equal("https://lower.example/", UrlShortcut.ReadUrlLine(content));
        Assert.Null(UrlShortcut.ReadUrlLine("[InternetShortcut]\r\nIconIndex=0\r\n"u8.ToArray()));
    }

    [Fact]
    public void RewriteUrlLine_只改URL行_IconFile等其余行逐字节保留()
    {
        var newBytes = UrlShortcut.RewriteUrlLine(originalu8(), "https://new.example/home");

        var text = Encoding.UTF8.GetString(newBytes);
        Assert.Contains("URL=https://new.example/home", text);
        Assert.DoesNotContain("old.example", text);
        Assert.Contains("IconFile=C:\\icons\\page.ico\r\n", text); // 其余行原样
        Assert.Contains("IconIndex=3\r\n", text);
        Assert.StartsWith("[InternetShortcut]\r\n", text); // 首行与其换行风格原样
        Assert.EndsWith("\r\n", text);

        static byte[] originalu8() => Encoding.UTF8.GetBytes(
            "[InternetShortcut]\r\nURL=https://old.example/\r\nIconFile=C:\\icons\\page.ico\r\nIconIndex=3\r\n");
    }

    [Fact]
    public void RewriteUrlLine_无URL行_追加不破坏原内容()
    {
        var content = "[MySection]\r\nCustom=1\r\n"u8.ToArray();
        var newBytes = UrlShortcut.RewriteUrlLine(content, "https://example.com/");
        var text = Encoding.UTF8.GetString(newBytes);
        Assert.StartsWith("[MySection]\r\nCustom=1\r\n", text);
        Assert.EndsWith("URL=https://example.com/\r\n", text);
    }

    [Fact]
    public void RewriteUrlLine_Latin1内容_按原编码回写()
    {
        // "[InternetShortcut]\r\n" + Latin-1 'Ü' (0xDC) + "\r\n" —— 非 UTF-8 字节
        var header = "[InternetShortcut]\r\n"u8.ToArray();
        var content = new byte[header.Length + 3];
        header.CopyTo(content, 0);
        content[header.Length] = 0xDC;
        content[^2] = 0x0D;
        content[^1] = 0x0A;

        var rewritten = UrlShortcut.RewriteUrlLine(content, "https://example.com/");
        Assert.Equal(0xDC, rewritten[header.Length]); // 高位字节按 Latin-1 无损保留
        Assert.EndsWith("URL=https://example.com/\r\n", Encoding.UTF8.GetString(rewritten));
    }
}
