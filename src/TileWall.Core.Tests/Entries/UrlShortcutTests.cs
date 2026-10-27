using System.Text;
using TileWall.Core.Entries;
using Xunit;

namespace TileWall.Core.Tests.Entries;

/// <summary>T-URL（M4 设计 §10.2）：ValidateUrl 矩阵 + CreateContent/RewriteUrlLine（其余行保留）/ReadUrlLine。</summary>
public class UrlShortcutTests
{
    [Fact]
    public void TargetAndIconFieldsUseOnlyTheInternetShortcutSectionAndPreserveOtherBytes()
    {
        var input = "[Other]\nURL=https://decoy.invalid/\nIconFile=wrong.ico\n[InternetShortcut]\r\n  URL = https://actual.invalid/  \r\nIconFile=actual.ico\r\nIconIndex=3\r\n[After]\nURL=https://after.invalid/\n";
        var bytes = Encoding.UTF8.GetBytes(input);
        var metadata = UrlShortcut.ReadMetadata(bytes);
        Assert.Equal("https://actual.invalid/", metadata.Url);
        Assert.Equal("actual.ico", metadata.IconFile);
        Assert.Equal("3", metadata.IconIndex);
        Assert.Equal(Encoding.UTF8.GetBytes(input.Replace("https://actual.invalid/", "https://new.invalid/", StringComparison.Ordinal)),
            UrlShortcut.RewriteUrlLine(bytes, "https://new.invalid/"));
    }

    [Theory]
    [InlineData("[InternetShortcut]\nURL=https://one.invalid/\nURL=https://two.invalid/")]
    [InlineData("[InternetShortcut]\nURL=https://one.invalid/\n[InternetShortcut]\nIconFile=icon.ico")]
    [InlineData("[InternetShortcut]\nIconFile=one.ico\nIconFile=two.ico")]
    public void AmbiguousFieldsCannotBecomeAnOpaqueIdentityOrBeEdited(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var files = new TileWall.Core.Tests.Fixtures.InMemoryFileStore();
        var identities = new LaunchIdentityService(files, new TileWall.Core.Tests.Fixtures.FakeLinkFileService(files));
        Assert.Throws<InvalidDataException>(() => UrlShortcut.ReadMetadata(bytes));
        Assert.Throws<InvalidDataException>(() => EntryObservation.Observe(identities, TileWall.Core.Wall.ManagedEntryKind.Url, "ambiguous.url", bytes));
        Assert.Throws<InvalidDataException>(() => UrlShortcut.RewriteUrlLine(bytes, "https://new.invalid/"));
    }

    [Fact]
    public void Utf16LittleEndianUsesAndPreservesItsBomEncodingAndUnrelatedFields()
    {
        const string text = "[InternetShortcut]\r\nURL=https://old.invalid/\r\nIconFile=C:\\图标\\图.ico\r\n";
        byte[] bytes = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)];
        Assert.Equal("https://old.invalid/", UrlShortcut.ReadUrlLine(bytes));
        Assert.Equal(new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(text.Replace("old.invalid", "new.invalid", StringComparison.Ordinal))),
            UrlShortcut.RewriteUrlLine(bytes, "https://new.invalid/"));
    }

    [Fact]
    public void UnsupportedShellTargetEncodingsAreRejectedButOpaqueBomMetadataRemainsReadable()
    {
        const string text = "[InternetShortcut]\r\nURL=https://actual.invalid/\r\n";
        Assert.Throws<InvalidDataException>(() => UrlShortcut.ReadUrlLine([.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(text)]));
        Assert.Throws<InvalidDataException>(() => UrlShortcut.ReadUrlLine([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)]));
        Assert.True(UrlShortcut.ReadMetadata([0xEF, 0xBB, 0xBF, .. "[InternetShortcut]\nIconFile=icon.ico"u8.ToArray()]).HasInternetShortcut);
    }

    [Fact]
    public void AddingMissingTargetStaysInTheCorrectSection()
    {
        var bytes = "[InternetShortcut]\nIconIndex=0\n[Other]\nURL=https://decoy.invalid/\n"u8.ToArray();
        var rewritten = UrlShortcut.RewriteUrlLine(bytes, "https://actual.invalid/");
        Assert.Equal("https://actual.invalid/", UrlShortcut.ReadUrlLine(rewritten));
        Assert.Contains("[Other]\nURL=https://decoy.invalid/\n", Encoding.UTF8.GetString(rewritten));
    }

    [Theory]
    [InlineData("https://example.com/path?q=1")]
    [InlineData("http://example.com")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    public void ValidateUrl_http与https_合法(string url)
    {
        Assert.Empty(UrlShortcut.ValidateUrl(url));
    }

    [Theory]
    [InlineData("example.com/no-scheme")]
    [InlineData("file:///C:/x")]
    [InlineData("C:\\folder\\app.exe")]
    [InlineData("http:///missing-host")]
    public void ValidateUrl_相对地址文件地址或无主机HTTP_SCHEME_UNSUPPORTED(string url)
    {
        Assert.Equal([UrlShortcut.UrlSchemeUnsupported], UrlShortcut.ValidateUrl(url));
    }

    [Theory]
    [InlineData("steam://rungameid/570")]
    [InlineData("steam:run/570")]
    [InlineData("ftp://example.com/file")]
    public void ValidateUrl_语法有效的非文件绝对协议合法(string url)
    {
        Assert.Empty(UrlShortcut.ValidateUrl(url));
        Assert.Equal(url, UrlShortcut.ReadUrlLine(UrlShortcut.CreateContent(url)));
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

    [Fact]
    public void RewriteUrlLine_Latin1拒绝不可表示目标而UTF8仍接受()
    {
        const string target = "https://example.com/\u20AC";
        Assert.Empty(UrlShortcut.ValidateUrl(target));

        var latin1 = Encoding.Latin1.GetBytes(
            "[InternetShortcut]\r\nURL=https://old.example/\r\nIconFile=C:\\icons\\caf\u00E9.ico\r\n");
        const string latin1Target = "https://example.com/caf\u00E9";
        Assert.Empty(UrlShortcut.ValidateUrl(latin1Target));
        Assert.Equal(
            Encoding.Latin1.GetBytes(
                "[InternetShortcut]\r\nURL=" + latin1Target + "\r\nIconFile=C:\\icons\\caf\u00E9.ico\r\n"),
            UrlShortcut.RewriteUrlLine(latin1, latin1Target));
        Assert.Throws<EncoderFallbackException>(() => UrlShortcut.RewriteUrlLine(latin1, target));

        var utf8 = Encoding.UTF8.GetBytes(
            "[InternetShortcut]\r\nURL=https://old.example/\r\nIconFile=C:\\icons\\page.ico\r\n");
        var rewritten = UrlShortcut.RewriteUrlLine(utf8, target);
        Assert.Equal(
            "[InternetShortcut]\r\nURL=" + target + "\r\nIconFile=C:\\icons\\page.ico\r\n",
            Encoding.UTF8.GetString(rewritten));
    }
}
