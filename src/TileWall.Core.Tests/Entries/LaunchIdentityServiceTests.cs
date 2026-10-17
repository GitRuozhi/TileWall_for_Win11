using TileWall.Core.Entries;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Entries;

public sealed class LaunchIdentityServiceTests
{
    [Fact]
    public void ShortcutAndDirectFileShareIdentityButArgumentsAndWorkingDirectoryRemainDistinct()
    {
        var files = new InMemoryFileStore();
        var links = new FakeLinkFileService(files);
        links.Register(@"C:\Start\Example.lnk", @"C:\Apps\Example.exe");
        links.Register(@"C:\Start\Argument.lnk", @"C:\Apps\Example.exe", "--profile A");
        links.Register(@"C:\Start\Working.lnk", @"C:\Apps\Example.exe", "", @"C:\Work");
        var service = new LaunchIdentityService(files, links);
        var direct = service.ResolveInput(@"c:\apps\.\example.exe")!;
        Assert.Equal(direct.Key, service.ResolveInput(@"C:\Start\Example.lnk")!.Key);
        Assert.NotEqual(direct.Key, service.ResolveInput(@"C:\Start\Argument.lnk")!.Key);
        Assert.NotEqual(direct.Key, service.ResolveInput(@"C:\Start\Working.lnk")!.Key);
        Assert.NotEqual(LaunchIdentityService.ForPath(@"C:\Apps\Example.exe", "--Profile A").Key,
            LaunchIdentityService.ForPath(@"C:\Apps\Example.exe", "--profile A").Key);
    }

    [Fact]
    public void UrlShortcutUsesCanonicalUrlWithoutDroppingQueryOrFragment()
    {
        var files = new InMemoryFileStore();
        var links = new FakeLinkFileService(files);
        files.WriteAllBytes(@"C:\Start\Site.url", UrlShortcut.CreateContent("HTTPS://Example.COM:443/path?q=1#one"));
        var service = new LaunchIdentityService(files, links);
        Assert.Equal(service.ResolveInput("https://example.com/path?q=1#one")!.Key, service.ResolveInput(@"C:\Start\Site.url")!.Key);
        Assert.NotEqual(service.ResolveInput("https://example.com/path?q=2#one")!.Key, service.ResolveInput(@"C:\Start\Site.url")!.Key);
        Assert.NotEqual(service.ResolveInput("https://example.com/path?q=1#two")!.Key, service.ResolveInput(@"C:\Start\Site.url")!.Key);
    }

    [Fact]
    public void StableShellParsingNamesDeduplicateDifferentLinkBytes()
    {
        var files = new InMemoryFileStore();
        var links = new FakeLinkFileService(files);
        links.Register(@"C:\Start\User.lnk", null, hasIdList: true, shellParsingName: "shell:AppsFolder\\Example.App!Main");
        links.Register(@"C:\Start\Common.lnk", null, hasIdList: true, shellParsingName: "SHELL:APPSFOLDER\\EXAMPLE.APP!MAIN");
        var service = new LaunchIdentityService(files, links);
        Assert.NotEqual(files.ReadAllBytes(@"C:\Start\User.lnk"), files.ReadAllBytes(@"C:\Start\Common.lnk"));
        Assert.Equal(service.ResolveInput(@"C:\Start\User.lnk")!.Key, service.ResolveInput(@"C:\Start\Common.lnk")!.Key);
    }

    [Fact]
    public void UnknownPidlCopiesAreRecognizedButDifferentOpaqueContentNeverSharesOneUnknownKey()
    {
        var files = new InMemoryFileStore();
        var links = new FakeLinkFileService(files);
        links.Register(@"C:\Start\One.lnk", null, hasIdList: true);
        files.Copy(@"C:\Start\One.lnk", @"C:\Managed\Copy.lnk", false);
        links.Register(@"C:\Start\Two.lnk", null, arguments: "different-pidl", hasIdList: true);
        var service = new LaunchIdentityService(files, links);
        Assert.Equal(service.ResolveInput(@"C:\Start\One.lnk")!.Key, service.ResolveInput(@"C:\Managed\Copy.lnk")!.Key);
        Assert.NotEqual(service.ResolveInput(@"C:\Start\One.lnk")!.Key, service.ResolveInput(@"C:\Start\Two.lnk")!.Key);
    }
}
