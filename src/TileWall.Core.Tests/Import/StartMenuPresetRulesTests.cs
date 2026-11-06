using TileWall.Core.Entries;
using TileWall.Core.Import;
using Xunit;

namespace TileWall.Core.Tests.Import;

public sealed class StartMenuPresetRulesTests
{
    [Theory]
    [InlineData("Word", @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", "Microsoft Corporation", "office")]
    [InlineData("Developer PowerShell for VS 2022", @"C:\Windows\System32\powershell.exe", "Microsoft Corporation", "visual-studio")]
    [InlineData("SSMS", @"C:\Program Files\Microsoft SQL Server Management Studio 20\ssms.exe", "Microsoft Corporation", "sql-server")]
    [InlineData("Anaconda Navigator", @"C:\Tools\Anaconda3\pythonw.exe", "", "anaconda")]
    [InlineData("Photoshop", @"C:\Program Files\Adobe\Photoshop 2025\Photoshop.exe", "Adobe Inc.", "adobe")]
    [InlineData("LibreOffice Writer", @"C:\Program Files\LibreOffice\program\swriter.exe", "The Document Foundation", "libreoffice")]
    [InlineData("WPS Office", @"C:\Tools\WPS Office\wps.exe", "", "wps")]
    [InlineData("Visual Studio Code", @"C:\Tools\Microsoft VS Code\Code.exe", "Microsoft Corporation", "microsoft")]
    [InlineData("NVIDIA Control Panel", @"C:\Program Files\NVIDIA Corporation\Control Panel Client\nvcplui.exe", "", "nvidia")]
    [InlineData("微信", @"C:\Program Files\Tencent\WeChat\WeChat.exe", "", "tencent")]
    [InlineData("网易云音乐", @"C:\Apps\CloudMusic\cloudmusic.exe", "", "netease")]
    public void ExplicitFamilyEvidencePrecedesPublisherFallback(string title, string target, string company, string expected)
    {
        var result = StartMenuPresetRules.Match(title, "", LaunchIdentityService.ForPath(target), company);
        Assert.Equal("preset:" + expected, result?.Key);
    }

    [Theory]
    [InlineData("steam://rungameid/570", "steam")]
    [InlineData("com.epicgames.launcher://apps/xyz?action=launch", "epic")]
    [InlineData("origin://launchgame/123", "ea")]
    [InlineData("uplay://launch/123", "ubisoft")]
    [InlineData("battlenet://Pro", "blizzard")]
    public void RecognizedPlatformProtocolIsEvidence(string target, string expected) =>
        Assert.Equal("preset:" + expected, StartMenuPresetRules.Match("Game", "", LaunchIdentityService.ForUrl(target))?.Key);

    [Theory]
    [InlineData("My Adobe Notes", @"C:\Apps\notes.exe", "")]
    [InlineData("NVIDIAish", @"C:\Apps\notes.exe", "")]
    [InlineData("Intel notes", @"C:\Intel\Unrelated\notes.exe", "")]
    [InlineData("Foreign app", @"C:\Windows\explorer.exe", "Microsoft Corporation")]
    [InlineData("Foreign app", @"C:\Windows\rundll32.exe", "Microsoft Corporation")]
    [InlineData("Unrelated", @"C:\Adobe Backup\unrelated.exe", "")]
    public void SimilarNamesAndPublicHostsDoNotInventPublisherGroups(string title, string target, string company) =>
        Assert.Null(StartMenuPresetRules.Match(title, "", LaunchIdentityService.ForPath(target), company));

    [Theory]
    [InlineData("Game")]
    [InlineData("Steam")]
    public void UnknownProtocolDoesNotGuessPlatformFromName(string name) =>
        Assert.Null(StartMenuPresetRules.Match(name, "", LaunchIdentityService.ForUrl("foreign://launch/1")));

    [Fact]
    public void ExplicitHostedPackageIsUsedInsteadOfHostsCompany()
    {
        var identity = LaunchIdentityService.ForPath(@"C:\Windows\explorer.exe", @"shell:AppsFolder\Microsoft.Office.OneNote_8wekyb3d8bbwe!App");
        Assert.Equal("preset:office", StartMenuPresetRules.Match("Notes", "", identity, "Microsoft Corporation")?.Key);
    }

    [Fact]
    public void RelativeDirectoryRetainsExplicitFamilyEvidenceForUngroupedCandidates()
    {
        var result = StartMenuPresetRules.Match("Support tool", @"Adobe Creative Cloud\Nested",
            LaunchIdentityService.ForPath(@"C:\Apps\support.exe"));
        Assert.Equal("preset:adobe", result?.Key);
    }
}
