using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Shell;
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

    [Theory]
    [InlineData(".lnk")]
    [InlineData(".url")]
    public void ExistingDirectoriesWithShortcutExtensionsKeepPathIdentity(string extension)
    {
        var root = Directory.CreateTempSubdirectory("tilewall-directory-identity-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, "Folder" + extension)).FullName;
            var files = new LocalFileStore();
            var links = new FakeLinkFileService(files) { Fault = LnkFaultPoint.BeforeRead };
            var service = new LaunchIdentityService(files, links);

            Assert.Equal(ExplorerAddKind.CreateLink, ExplorerAddPathClassifier.Classify(directory));
            var identity = service.ResolveInput(directory);
            Assert.Equal(LaunchIdentityService.ForPath(directory), identity);
            Assert.Equal(LnkFaultPoint.BeforeRead, links.Fault);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally { root.Delete(recursive: true); }
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

    [Fact]
    public void RealPidlDigestWinsOverMissingLocalizedOrConflictingParsingDescriptions()
    {
        var files = new InMemoryFileStore();
        var service = new LaunchIdentityService(files, new FakeLinkFileService(files));
        var hash = new string('A', 64);
        var expected = LaunchIdentityService.ForShellIdList(hash);
        foreach (var description in new string?[] { null, "", "Computer", "此电脑", "shell:Different.PropertyValue" })
        {
            var fields = new LinkFields(null, "", "", null, 0, 1, true)
            { ShellIdListHash = hash, ShellParsingName = description };
            var observed = service.ResolveObservedInput(EntryKind.Lnk, @"C:\Start\Native.lnk", [1, 2, 3], fields)!;
            Assert.Equal(expected.Key, observed.Key);
            Assert.StartsWith("shell:pidl:", observed.Key);
            Assert.False(observed.IsUrl);
            Assert.Equal(string.IsNullOrWhiteSpace(description) ? "pidl:" + hash : description, observed.Target);
        }
        Assert.NotEqual(LaunchIdentityService.ForShellIdList(hash, "Same label").Key,
            LaunchIdentityService.ForShellIdList(new string('B', 64), "Same label").Key);
    }

    [Fact]
    public void PidlIdentityPreservesArgumentsAndWorkingDirectoryWithoutDependingOnLabel()
    {
        var hash = new string('C', 64);
        var baseline = LaunchIdentityService.ForShellIdList(hash, "Computer", "--profile A", @"C:\Work");
        Assert.Equal(baseline.Key, LaunchIdentityService.ForShellIdList(hash.ToLowerInvariant(), "此电脑", "--profile A", @"c:\work").Key);
        Assert.NotEqual(baseline.Key, LaunchIdentityService.ForShellIdList(hash, "Computer", "--profile B", @"C:\Work").Key);
        Assert.NotEqual(baseline.Key, LaunchIdentityService.ForShellIdList(hash, "Computer", "--profile A", @"C:\Other").Key);
    }

    [Fact]
    public void OrdinaryPathStillWinsWhenShortcutAlsoContainsPidlMetadata()
    {
        var files = new InMemoryFileStore();
        var service = new LaunchIdentityService(files, new FakeLinkFileService(files));
        var fields = new LinkFields(@"C:\Apps\Example.exe", "--keep", @"C:\Work", null, 0, 1, true)
        { ShellIdListHash = new string('D', 64), ShellParsingName = "Computer" };
        var observed = service.ResolveObservedInput(EntryKind.Lnk, @"C:\Start\File.lnk", [1], fields)!;
        Assert.Equal(LaunchIdentityService.ForPath(fields.TargetPath!, fields.Arguments, fields.WorkingDirectory).Key, observed.Key);
    }
}
