using TileWall.Core.Imaging;
using Xunit;

namespace TileWall.Core.Tests.Imaging;

public sealed class SystemIconCatalogTests
{
    private static readonly SystemIconPaths Paths = new(@"C:\RealWindows", @"C:\RealWindows\System32",
        new Dictionary<string, string?>
        {
            ["documents"] = @"E:\Redirected\WorkDocs", ["downloads"] = @"D:\Actual Downloads",
            ["profile"] = @"C:\Users\CurrentAccount",
        });

    [Theory]
    [InlineData("shell:MyComputerFolder", "computer")]
    [InlineData("SHELL:RecycleBinFolder", "recycle")]
    [InlineData("shell:ControlPanelFolder", "control-panel")]
    [InlineData("shell:My Pictures", "pictures")]
    [InlineData("shell:ConnectionsFolder", "network")]
    [InlineData("shell:Common Administrative Tools", "tools")]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "computer")]
    [InlineData("shell:::{645ff040-5081-101b-9f08-00aa002f954e}", "recycle")]
    [InlineData("shell:::{2559A1F3-21D7-11D4-BDAF-00C04F60B9F0}", "run")]
    [InlineData("ms-settings:", "settings")]
    [InlineData("MS-SETTINGS:sound", "sound")]
    [InlineData("ms-settings:network-wifi", "network")]
    [InlineData("ms-settings:storagesense", "storage")]
    [InlineData("ms-settings:powersleep", "power")]
    [InlineData("ms-settings:windowsdefender", "security")]
    [InlineData("ms-settings:a-page-with-no-specific-artwork", "settings")]
    [InlineData("ms-search://search/", "search")]
    [InlineData("MS-SEARCH:", "search")]
    public void NativeNamesAndExactProtocolSchemesUseTheirSemanticAsset(string target, string expected)
    {
        Assert.True(SystemIconCatalog.TryGet(target, null, out var asset));
        Assert.Equal(expected, asset.Id);
    }

    [Theory]
    [InlineData(@"C:\RealWindows\System32\Taskmgr.exe", "tools")]
    [InlineData(@"c:\realwindows\SYSTEM32\diskmgmt.msc", "storage")]
    [InlineData(@"C:\RealWindows\System32\WindowsPowerShell\v1.0\powershell.exe", "terminal")]
    [InlineData(@"C:\RealWindows\ImmersiveControlPanel\SystemSettings.exe", "settings")]
    [InlineData(@"C:\RealWindows\regedit.exe", "tools")]
    [InlineData(@"E:\Redirected\WorkDocs\", "documents")]
    [InlineData(@"D:\Actual Downloads", "downloads")]
    [InlineData("file:///C:/RealWindows/System32/notepad.exe", "notepad")]
    public void PathsMustMatchTheActualHostDirectories(string target, string expected)
    {
        Assert.True(SystemIconCatalog.TryGet(target, Paths, out var asset));
        Assert.Equal(expected, asset.Id);
    }

    [Theory]
    [InlineData("shell:UnknownSystemFeature")]
    [InlineData("shell:MyComputerFolder/child")]
    [InlineData("shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\\child")]
    [InlineData("::{00000000-0000-0000-0000-000000000001}")]
    [InlineData("ms-settings-fake:sound")]
    [InlineData("ms-searcher://search/")]
    [InlineData("https://example.org/ms-settings:sound")]
    [InlineData(@"C:\Apps\Taskmgr.exe")]
    [InlineData(@"C:\RealWindowsFake\System32\Taskmgr.exe")]
    [InlineData(@"C:\RealWindows\System32\Unknown.exe")]
    [InlineData(@"C:\RealWindows\System32\subfolder\Taskmgr.exe")]
    [InlineData(@"C:\RealWindows\System32\Taskmgr.exe /anything")]
    [InlineData(@"C:\Users\AnotherAccount\Documents")]
    [InlineData(@"E:\Redirected\WorkDocs\child")]
    [InlineData("Taskmgr.exe")]
    public void LookalikesAndUnknownTargetsDoNotClaimASystemAsset(string target)
    {
        Assert.False(SystemIconCatalog.TryGet(target, Paths, out _));
    }

    [Fact]
    public void PhysicalPathsRequireHostFactsAndUnrecognizedFolderRolesAreIgnored()
    {
        Assert.False(SystemIconCatalog.TryGet(@"C:\RealWindows\System32\Taskmgr.exe", null, out _));
        var unrecognized = Paths with { KnownFolders = new Dictionary<string, string?> { ["unregistered-role"] = @"D:\Unknown" } };
        Assert.False(SystemIconCatalog.TryGet(@"D:\Unknown", unrecognized, out _));
    }
}
