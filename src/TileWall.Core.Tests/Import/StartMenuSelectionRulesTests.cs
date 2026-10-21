using TileWall.Core.Entries;
using TileWall.Core.Import;
using Xunit;

namespace TileWall.Core.Tests.Import;

public sealed class StartMenuSelectionRulesTests
{
    [Fact]
    public void ProjectionKeepsAddableKeysAndCountsRemovedOnes()
    {
        var trimmed = StartMenuSelectionRules.MigrateSelection(["A", "B"], ["B"]);
        Assert.Equal(["B"], trimmed.SelectedKeys);
        Assert.Equal(1, trimmed.RemovedCount);

        var restored = StartMenuSelectionRules.MigrateSelection(trimmed.SelectedKeys, ["A", "B"]);
        Assert.Equal(["B"], restored.SelectedKeys);
        Assert.Equal(0, restored.RemovedCount);

        var cleared = StartMenuSelectionRules.MigrateSelection(["A", "B"], []);
        Assert.Empty(cleared.SelectedKeys);
        Assert.Equal(2, cleared.RemovedCount);
    }

    [Fact]
    public void SameDisplayNameAndDuplicateKeysStayOnSelectionKey()
    {
        var migrated = StartMenuSelectionRules.MigrateSelection(
            ["path-a", "path-a", "path-b"], ["path-b", "other-name"]);
        Assert.Equal(["path-b"], migrated.SelectedKeys);
        Assert.Equal(1, migrated.RemovedCount);
        Assert.Equal(1, StartMenuSelectionRules.MigrateSelection(["A"], ["a"]).RemovedCount);
        Assert.Empty(StartMenuSelectionRules.MigrateSelection(["A"], ["a"]).SelectedKeys);
    }

    [Theory]
    [InlineData("Alpha", 'A')]
    [InlineData("中文", 'Z')]
    [InlineData("微信", 'W')]
    [InlineData("重庆", 'C')]
    [InlineData("7-Zip", '#')]
    [InlineData("😀 App", '#')]
    public void InitialUsesFixedLocalReadingAndFallback(string name, char expected) =>
        Assert.Equal(expected, StartMenuSelectionRules.Initial(name));

    [Fact]
    public void BothRootsAndNestedDirectoriesShareTopLevelFolderBucket()
    {
        var user = new StartMenuCandidate(@"C:\User\Tools\Deep\A.lnk", "A", EntryKind.Lnk, SourceKind.User, @"Tools\Deep");
        var common = new StartMenuCandidate(@"C:\Common\TOOLS\B.lnk", "B", EntryKind.Lnk, SourceKind.Common, "TOOLS");
        var buckets = StartMenuGroupingRules.Assign([
            new(user, LaunchIdentityService.ForPath(@"C:\Apps\A.exe"), true),
            new(common, LaunchIdentityService.ForPath(@"C:\Apps\B.exe"), true),
            new(user with { RelativeDir = "", DisplayName = "Z" }, LaunchIdentityService.ForPath(@"C:\Apps\Z.exe"), true),
        ]);
        Assert.Equal(buckets[0].Key, buckets[1].Key);
        Assert.True(buckets[2].Order < buckets[0].Order); // Letters are displayed before original folders.
    }

    [Theory]
    [InlineData("Uninstall Example", @"C:\Apps\Example\remove.exe", "", StartMenuExclusion.Uninstall)]
    [InlineData("Example", @"C:\Apps\Example\unins000.exe", "", StartMenuExclusion.Uninstall)]
    [InlineData("Remove product", @"C:\Windows\System32\msiexec.exe", "/x{123}", StartMenuExclusion.Uninstall)]
    [InlineData("HelpDesk", @"C:\Apps\HelpDesk\HelpDesk.exe", "", StartMenuExclusion.None)]
    [InlineData("Update Studio", @"C:\Apps\update\studio.exe", "", StartMenuExclusion.None)]
    [InlineData("Example Updater", @"C:\Apps\Example\updater.exe", "", StartMenuExclusion.Maintenance)]
    [InlineData("Readme", @"C:\Apps\Example\readme.pdf", "", StartMenuExclusion.DocumentOrWeb)]
    [InlineData("Third-party Notepad", @"C:\Apps\notepad.exe", "", StartMenuExclusion.None)]
    [InlineData("Microsoft Word", @"C:\Program Files\Microsoft Office\WINWORD.EXE", "", StartMenuExclusion.None)]
    [InlineData("Node.js command prompt", @"C:\Windows\System32\cmd.exe", @"/k C:\Node\nodevars.bat", StartMenuExclusion.None)]
    [InlineData("Developer PowerShell", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", @"-NoExit -Command Import-Module C:\VS\DevShell.psm1", StartMenuExclusion.None)]
    [InlineData("Third-party console", @"C:\Windows\System32\mmc.exe", @"C:\Apps\console.msc", StartMenuExclusion.None)]
    public void SimplificationUsesLaunchPurposeWithoutNameOrPublisherGuessing(string name, string path, string args, StartMenuExclusion expected) =>
        Assert.Equal(expected, StartMenuSelectionRules.Classify(name, LaunchIdentityService.ForPath(path, args)));

    [Fact]
    public void KnownBuiltInsAndPackagedAppsAreExcludedWithoutGroupNameDependence()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal(StartMenuExclusion.WindowsBuiltIn, StartMenuSelectionRules.Classify("任意标题",
            LaunchIdentityService.ForPath(Path.Combine(windows, "System32", "notepad.exe"))));
        Assert.Equal(StartMenuExclusion.WindowsBuiltIn, StartMenuSelectionRules.Classify("任意标题",
            new LaunchIdentity("id", "shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "", "", false)));
        Assert.Equal(StartMenuExclusion.DocumentOrWeb, StartMenuSelectionRules.Classify("Example", LaunchIdentityService.ForUrl("https://example.com")));
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://example.com")]
    public void HttpAndHttpsUrlsAreExcludedAsWebEntries(string url) =>
        Assert.Equal(StartMenuExclusion.DocumentOrWeb,
            StartMenuSelectionRules.Classify("Example", LaunchIdentityService.ForUrl(url)));

    [Fact]
    public void SteamProtocolLauncherIsKeptAsAnApplication()
    {
        Assert.Equal(StartMenuExclusion.None,
            StartMenuSelectionRules.Classify("Steam", LaunchIdentityService.ForUrl("steam://rungameid/570")));
    }

    [Theory]
    [InlineData("ms-settings:privacy")]
    [InlineData("windowsdefender://home")]
    public void KnownWindowsApplicationProtocolsRemainExcluded(string uri) =>
        Assert.Equal(StartMenuExclusion.WindowsBuiltIn,
            StartMenuSelectionRules.Classify("任意标题", LaunchIdentityService.ForUrl(uri)));
}
