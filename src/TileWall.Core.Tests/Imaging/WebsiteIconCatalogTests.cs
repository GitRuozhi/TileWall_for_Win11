using TileWall.Core.Imaging;
using Xunit;

namespace TileWall.Core.Tests.Imaging;

public sealed class WebsiteIconCatalogTests
{
    [Theory]
    [InlineData("EXAMPLE.COM", "example.com")]
    [InlineData("example.com.", "example.com")]
    [InlineData("BÜCHER.example", "xn--bcher-kva.example")]
    [InlineData("XN--BCHER-KVA.EXAMPLE.", "xn--bcher-kva.example")]
    public void NormalizeHost_UsesCanonicalAsciiDnsName(string host, string expected)
    {
        Assert.True(WebsiteIconCatalog.TryNormalizeHost(host, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("example.com..")]
    [InlineData("-bad.example")]
    [InlineData("bad-.example")]
    [InlineData("bad host.example")]
    [InlineData(" example.com")]
    [InlineData("example.com ")]
    [InlineData("example..com")]
    public void NormalizeHost_RejectsInvalidDnsName(string host)
    {
        Assert.False(WebsiteIconCatalog.TryNormalizeHost(host, out var actual));
        Assert.Equal(string.Empty, actual);
    }

    [Theory]
    [InlineData("file:///example.com")]
    [InlineData("ftp://example.com")]
    [InlineData("https://youtube.com.example.org")]
    [InlineData("https://notyoutube.com")]
    [InlineData("https://private.youtube.com")]
    [InlineData("https://www.mail.google.com")]
    [InlineData("https://youtube.com@unlisted.example")]
    [InlineData("https://youtubе.com")]
    [InlineData("https://unlisted.example")]
    public void TryGet_RejectsNonWebAndUnlistedSuffixes(string value)
    {
        Assert.False(WebsiteIconCatalog.TryGet(new Uri(value), out _));
    }

    [Theory]
    [InlineData("https://YOUTUBE.COM./watch?v=1", "youtube")]
    [InlineData("http://youtu.be/1", "youtube")]
    [InlineData("https://www.bilibili.com/video/1", "bilibili")]
    [InlineData("https://music.youtube.com/", "youtube-music")]
    [InlineData("https://mail.google.com/", "gmail")]
    [InlineData("https://drive.google.com/", "google-drive")]
    [InlineData("https://cloud.google.com/", "google-cloud")]
    [InlineData("https://console.cloud.google.com/", "google-cloud-platform")]
    [InlineData("https://copilot.github.com/", "github-copilot")]
    [InlineData("https://github.com/features/copilot", "github")]
    [InlineData("https://amazon.com/prime", "amazon")]
    [InlineData("https://primevideo.com/", "prime-video")]
    [InlineData("https://mail.proton.me/", "proton-mail")]
    [InlineData("https://drive.proton.me/", "proton-drive")]
    public void TryGet_MatchesOnlyTheExplicitWebsiteForTheHost(string value, string expectedId)
    {
        Assert.True(WebsiteIconCatalog.TryGet(new Uri(value), out var match));
        Assert.Equal(expectedId, match.Id);
    }

    [Fact]
    public void TryGet_ExplicitAliasesShareOneAssetAndRelativeUriDoesNotMatch()
    {
        Assert.True(WebsiteIconCatalog.TryGet(new Uri("https://youtube.com"), out var canonical));
        Assert.True(WebsiteIconCatalog.TryGet(new Uri("https://www.youtube.com"), out var alias));
        Assert.Equal(canonical, alias);
        Assert.False(WebsiteIconCatalog.TryGet(new Uri("youtube.com", UriKind.Relative), out _));
    }
}
