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
        var result = StartMenuPresetRules.Match(Candidate(title), LaunchIdentityService.ForPath(target), company);
        Assert.Equal("preset:" + expected, result?.Key);
    }

    [Theory]
    [InlineData("steam://rungameid/570", "steam")]
    [InlineData("com.epicgames.launcher://apps/xyz?action=launch", "epic")]
    [InlineData("origin://launchgame/123", "ea")]
    [InlineData("uplay://launch/123", "ubisoft")]
    [InlineData("battlenet://Pro", "blizzard")]
    public void RecognizedPlatformProtocolIsEvidence(string target, string expected) =>
        Assert.Equal("preset:" + expected, StartMenuPresetRules.Match(Candidate("Game"), LaunchIdentityService.ForUrl(target))?.Key);

    [Theory]
    [InlineData("My Adobe Notes", @"C:\Apps\notes.exe", "")]
    [InlineData("NVIDIAish", @"C:\Apps\notes.exe", "")]
    [InlineData("Intel notes", @"C:\Intel\Unrelated\notes.exe", "")]
    [InlineData("Foreign app", @"C:\Windows\explorer.exe", "Microsoft Corporation")]
    [InlineData("Foreign app", @"C:\Windows\rundll32.exe", "Microsoft Corporation")]
    [InlineData("Unrelated", @"C:\Adobe Backup\unrelated.exe", "")]
    public void SimilarNamesAndPublicHostsDoNotInventPublisherGroups(string title, string target, string company) =>
        Assert.Null(StartMenuPresetRules.Match(Candidate(title), LaunchIdentityService.ForPath(target), company));

    [Theory]
    [InlineData("Game")]
    [InlineData("Steam")]
    public void UnknownProtocolDoesNotGuessPlatformFromName(string name) =>
        Assert.Null(StartMenuPresetRules.Match(Candidate(name), LaunchIdentityService.ForUrl("foreign://launch/1")));

    [Fact]
    public void ExplicitHostedPackageIsUsedInsteadOfHostsCompany()
    {
        var identity = LaunchIdentityService.ForPath(@"C:\Windows\explorer.exe", @"shell:AppsFolder\Microsoft.Office.OneNote_8wekyb3d8bbwe!App");
        Assert.Equal("preset:office", StartMenuPresetRules.Match(Candidate("Notes"), identity, "Microsoft Corporation")?.Key);
    }

    [Fact]
    public void GroupingCountsVisibleAddableCandidatesAndMovesSinglePresetsStraightToLetters()
    {
        var items = new[]
        {
            Item("Alpha", @"Tools\Nested", SourceKind.User),
            Item("Uninstall tool", "TOOLS", SourceKind.Common),
            Item("Beta", "Singleton", SourceKind.Common),
            Item("Hidden duplicate", "Singleton", SourceKind.User, false),
            Item("Already on wall", "Singleton", SourceKind.Common, false),
            Item("Microsoft Edge", "Singleton", SourceKind.User),
            Item("Adobe Photoshop", "Creative", SourceKind.User),
            Item("Adobe Illustrator", "Another", SourceKind.Common),
        };
        var result = StartMenuGroupingRules.Assign(items);
        Assert.Equal("folder:TOOLS", result[0].Key);
        Assert.Equal(result[0].Key, result[1].Key); // Non-recommended uninstall still counts.
        Assert.Equal("letter:B", result[2].Key); // Presets and hidden candidates do not inflate folder count.
        Assert.Equal("letter:M", result[5].Key); // A one-item preset does not fall back into its folder.
        Assert.Equal("preset:adobe", result[6].Key);
        Assert.Equal(result[6].Key, result[7].Key);
        Assert.True(result[6].Order < result[0].Order);
        Assert.True(result[0].Order < result[2].Order);
    }

    [Fact]
    public void TurningRecognitionOffKeepsSingleFolderGroupsAndSkipsPresetAssignment()
    {
        var items = new[]
        {
            Item("Adobe Photoshop", "Creative", SourceKind.User),
            Item("Word", "Office", SourceKind.User),
            Item("Root item", "", SourceKind.User),
        };

        var result = StartMenuGroupingRules.Assign(items, recognizePresets: false);

        Assert.Equal("folder:CREATIVE", result[0].Key);
        Assert.Equal("folder:OFFICE", result[1].Key);
        Assert.Equal("letter:R", result[2].Key);
        Assert.Equal(100, result[0].Order);
        Assert.Equal(100, result[1].Order);
        Assert.Equal(218, result[2].Order);
    }

    [Fact]
    public void SingleChineseShortcutUsesLocalPinyinAndRootItemUsesFallback()
    {
        var result = StartMenuGroupingRules.Assign([Item("中文", "Single", SourceKind.User), Item("7-Zip", "", SourceKind.User)]);
        Assert.Equal("letter:Z", result[0].Key);
        Assert.Equal("letter:#", result[1].Key);
    }

    private static StartMenuCandidate Candidate(string title, string folder = "", SourceKind source = SourceKind.User) =>
        new(@"C:\Programs\" + folder + "\\" + title + ".lnk", title, EntryKind.Lnk, source, folder);

    private static StartMenuGroupingItem Item(string title, string folder, SourceKind source, bool canAdd = true) =>
        new(Candidate(title, folder, source), LaunchIdentityService.ForPath(@"C:\Apps\app.exe", title), canAdd);
}
