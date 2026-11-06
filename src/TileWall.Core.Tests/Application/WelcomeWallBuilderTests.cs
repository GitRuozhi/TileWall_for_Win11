using TileWall.Core.Application;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Application;

public sealed class WelcomeWallBuilderTests
{
    private static readonly WelcomeWallText Text = new(WelcomeWallBuilder.NativeTargetKeys.Append("Settings")
        .ToDictionary(key => key, key => key, StringComparer.Ordinal));
    private static readonly WelcomeWallTargets Targets = new(WelcomeWallBuilder.NativeTargetKeys.ToDictionary(key => key,
        key => key is "ThisPC" or "ControlPanel" or "RecycleBin" or "Network" or "Store" or "Edge"
            ? new WelcomeWallTarget("shell:" + key, TileWall.Core.Entries.LaunchIdentityService.ForShell("shell:" + key).Key)
            : new WelcomeWallTarget(Path.GetFullPath(key)), StringComparer.Ordinal));
    private static readonly string Gallery = Path.GetFullPath("WelcomeWall/Seasons/content-set");
    private static string EntryPath(string root, WallMember member) =>
        Path.Combine(root, member.Entry!.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    private static WallViewportBudget Budget(int columns = 2, int rows = 12)
    {
        var metrics = new WallMetrics();
        return new(metrics.WallWidth(columns), metrics.WallHeight(rows), columns, rows, false);
    }

    [Theory]
    [InlineData(2, 10)]
    [InlineData(1, 17)]
    public void CompleteContentUsesOrdinaryLayoutAndNewIdsWithoutChangingSettings(int columns, int rows)
    {
        var settings = new WallSettings { LanguageMode = WallLanguageMode.English, RunAtLogin = true,
            StayVisibleOnFocusLoss = true, WindowedColumns = 1, WindowedRows = 8, HotKey = "Ctrl+F8" };
        var before = WallDocument.Empty(1, 8) with { Generation = 9, Settings = settings };
        var prepared = WelcomeWallBuilder.Prepare(before, Targets, Text, Gallery, Budget(columns, rows));
        var proposal = Assert.IsType<WallProposal>(prepared.Layout.Proposal);
        Assert.Equal(10, proposal.Document.Generation);
        Assert.Same(settings, proposal.Document.Settings);
        Assert.Empty(WallLayoutEngine.ValidateGeometry(proposal.Document));
        Assert.True(WallViewportPolicy.Validate(proposal, Budget(columns, rows)).Success);
        Assert.Equal(columns, proposal.Document.Columns);
        Assert.Equal(4, proposal.Document.Lanes.Sum(lane => lane.Groups.Count));
        Assert.Equal(26, proposal.Document.EnumerateAllMembers().Count());
        var puzzle = Assert.Single(proposal.Document.EnumerateAllMembers().OfType<PuzzleMember>());
        Assert.Equal(Gallery, puzzle.Images.Path);
        Assert.Equal(MemberImageSourceKind.Folder, puzzle.Images.Kind);
        Assert.Equal(ImageRotationInterval.Seconds5, puzzle.RotationInterval);
        Assert.Equal(6, puzzle.Partitions.Count);
        Assert.Null(puzzle.TargetInput);
        Assert.Equal(25, prepared.EntryChanges.Count);
        var again = WelcomeWallBuilder.Prepare(before, Targets, Text, Gallery, Budget(columns, rows));
        Assert.Empty(proposal.Document.EnumerateAllMembers().Select(member => member.Id)
            .Intersect(again.Layout.Proposal!.Document.EnumerateAllMembers().Select(member => member.Id), StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(2, 9)]
    [InlineData(1, 16)]
    public void InsufficientCapacityDoesNotReturnPartialContentOrEntryRemovals(int columns, int rows)
    {
        var before = WallDocument.Empty(1, 8) with
        {
            RecycleBinEnabled = true,
            InactiveMembers = [new TileMember { Id = "retained", LocalBounds = new(0, 0, 1, 1),
                Entry = new("retained", "Objects/retained/entry.url", ManagedEntryKind.Url) }],
        };
        var prepared = WelcomeWallBuilder.Prepare(before, Targets, Text, Gallery, Budget(columns, rows));
        Assert.Null(prepared.Layout.Proposal);
        Assert.Empty(prepared.EntryChanges);
        Assert.Equal("retained", Assert.Single(before.InactiveMembers).Id);
        Assert.Equal(0, before.Generation);
    }

    [Fact]
    public void ExpansionRetainsItsOrdinaryEmptyTailWithoutChangingTheRequestedSetting()
    {
        var before = WallDocument.Empty(1, 8) with { Settings = new() { ExpandColumns = true } };
        var prepared = WelcomeWallBuilder.Prepare(before, Targets, Text, Gallery, Budget(64, 10));
        var proposal = Assert.IsType<WallProposal>(prepared.Layout.Proposal);
        Assert.True(proposal.Document.Settings.ExpandColumns);
        Assert.Same(before.Settings, proposal.Document.Settings);
        Assert.Equal(3, proposal.Document.Columns);
        Assert.Empty(proposal.Document.Lanes[^1].Groups);
        Assert.Empty(WallLayoutEngine.ValidateGeometry(proposal.Document));
    }

    [Fact]
    public void InitializationDropsOldEmptyRowsWhilePreservingSavedWindowPreferences()
    {
        var settings = new WallSettings { WindowedRows = 19, WindowedColumns = 2,
            WindowedViewportWidthDip = 1200, ManualWindowedViewportWidthDip = 1100 };
        var before = WallDocument.Empty(2, 19) with { Generation = 4, Settings = settings };
        var prepared = WelcomeWallBuilder.Prepare(before, Targets, Text, Gallery, Budget(2, 19));
        var proposal = Assert.IsType<WallProposal>(prepared.Layout.Proposal);
        Assert.Equal(12, proposal.Document.Rows);
        Assert.Equal(26, proposal.Document.EnumerateAllMembers().Count());
        Assert.Same(settings, proposal.Document.Settings);
        Assert.Equal(19, proposal.Document.Settings.WindowedRows);
        Assert.Empty(WallLayoutEngine.ValidateGeometry(proposal.Document));
    }

    [Fact]
    public void DefaultGeometryKeepsTheTwoEmptySlotsAndTheTileWallLinksBelowTheAlbum()
    {
        var prepared = WelcomeWallBuilder.Prepare(WallDocument.Empty(2, 19), Targets, Text, Gallery, Budget());
        var document = Assert.IsType<WallProposal>(prepared.Layout.Proposal).Document;
        Assert.Equal(new[] { "Windows", "Tools", "Internet" }, document.Lanes[0].Groups.Select(group => group.Title));
        var windows = document.Lanes[0].Groups[0];
        Assert.DoesNotContain(windows.Members, member => member.LocalBounds.Column is 2 or 3 && member.LocalBounds.Row == 0);
        Assert.Equal(Enumerable.Range(0, 8), windows.Members.Where(member => member.LocalBounds.Row == 2)
            .Select(member => member.LocalBounds.Column));
        var tools = document.Lanes[0].Groups[1];
        Assert.DoesNotContain(tools.Members, member => member.LocalBounds.Column is 4 or 5);
        var tileWall = Assert.Single(document.Lanes[1].Groups);
        Assert.Equal("TileWall", tileWall.Title);
        Assert.All(tileWall.Members, member =>
        {
            Assert.True(member.LocalBounds.Column >= 2);
            Assert.True(member.LocalBounds.Right <= 8);
        });
        var puzzle = Assert.Single(tileWall.Members.OfType<PuzzleMember>());
        Assert.Equal(new TileWall.Core.Grid.GridRect(2, 0, 6, 4), puzzle.LocalBounds);
        Assert.True(puzzle.Visual.ShowTitle);
        Assert.Equal("Puzzle Album", puzzle.Visual.Title);
        var guide = Assert.Single(tileWall.Members, member => member.Visual.Title == "TileWall Use Guide");
        Assert.Equal(new TileWall.Core.Grid.GridRect(2, 4, 3, 2), guide.LocalBounds);
        Assert.Equal(WelcomeWallBuilder.ProjectUrl, guide.TargetInput);
        var showcase = Assert.Single(tileWall.Members, member => member.Visual.Title == "Show your TileWall");
        Assert.Equal(new TileWall.Core.Grid.GridRect(5, 4, 3, 2), showcase.LocalBounds);
        Assert.Equal(WelcomeWallBuilder.DiscussionsUrl, showcase.TargetInput);
        Assert.Equal(6, prepared.EntryChanges.OfType<EntryChange.CreateShellLink>().Count());
    }

    [Fact]
    public void InvalidGallerySourceIsRejectedBeforeReturningEntryRemovals()
    {
        var before = WallDocument.Empty(1, 8) with
        {
            RecycleBinEnabled = true,
            InactiveMembers = [new TileMember { Id = "old", LocalBounds = new(0, 0, 1, 1),
                Entry = new("old", "Objects/old/entry.url", ManagedEntryKind.Url) }],
        };
        var prepared = WelcomeWallBuilder.Prepare(before, Targets, Text, "relative-gallery", Budget());
        Assert.Null(prepared.Layout.Proposal);
        Assert.Empty(prepared.EntryChanges);
        Assert.Equal("old", Assert.Single(before.InactiveMembers).Id);
    }

    private static (WallStore Store, InMemoryFileStore Files, string Root, WallDocument Before, string ExternalImage)
        ExistingWall()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "welcome-replace-" + Guid.NewGuid().ToString("N")));
        var files = new InMemoryFileStore();
        var store = new WallStore(files, new FakeLinkFileService(files), root);
        var empty = WallDocument.Empty(1, 8);
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(empty).Status);
        var oldMedia = PreparedMediaAsset.FromVerifiedImage(new byte[] { 1, 2, 3 }, ".png");
        var external = Path.Combine(root + "-external", "background.png");
        files.WriteAllBytes(external, [1, 2, 3]);
        var original = empty with
        {
            Generation = 1,
            Settings = new() { BackgroundMode = WallBackgroundMode.Custom, BackgroundMedia = oldMedia.Reference,
                BackgroundImagePath = external, LanguageMode = WallLanguageMode.English, ReplaceStartMenu = true,
                RunAtLogin = true, StayVisibleOnFocusLoss = true },
            Lanes = [new WallLane { Groups = [new StartGroup { Id = "old-group", Members =
                [new TileMember { Id = "active", LocalBounds = new(0, 0, 2, 2) }] }] }],
            RecycleBinEnabled = true,
            InactiveMembers = [new TileMember { Id = "retained", LocalBounds = new(0, 0, 2, 2),
                Images = new() { Kind = MemberImageSourceKind.Single, Media = oldMedia.Reference } }],
        };
        var result = store.Commit(empty, new(0, original,
            [new EntryChange.CreateUrl("active", "https://old.example/"),
                new EntryChange.CreateUrl("retained", "https://retained.example/")], [oldMedia]));
        Assert.Equal(WallCommitStatus.Committed, result.Status);
        return (store, files, root, result.CommittedDocument!, external);
    }

    [Fact]
    public void JointReplacementRemovesActiveAndRetainedEntriesButKeepsSettingsBackgroundAndExternalOriginal()
    {
        var (store, files, root, before, external) = ExistingWall();
        var oldEntries = before.EnumerateAllMembers().Select(member => EntryPath(root, member)).ToArray();
        var guideImage = PreparedMediaAsset.FromVerifiedImage(new byte[] { 4, 5, 6 }, ".png");
        var showcaseImage = PreparedMediaAsset.FromVerifiedImage(new byte[] { 7, 8, 9 }, ".png");
        var prepared = WelcomeWallBuilder.Prepare(before, Targets, Text, Gallery, Budget(),
            guideImage: guideImage.Reference, showcaseImage: showcaseImage.Reference);
        Assert.Equal(new[] { "active", "retained" }, prepared.EntryChanges.OfType<EntryChange.Remove>().Select(change => change.MemberId));
        var proposal = Assert.IsType<WallProposal>(prepared.Layout.Proposal);
        var result = store.Commit(before, new(before.Generation, proposal.Document, prepared.EntryChanges,
            [guideImage, showcaseImage]));
        Assert.Equal(WallCommitStatus.Committed, result.Status);
        var current = result.CommittedDocument!;
        Assert.Equal(before.Settings, current.Settings);
        Assert.Empty(current.InactiveMembers);
        Assert.Equal(26, current.EnumerateAllMembers().Count());
        Assert.Null(Assert.Single(current.EnumerateAllMembers().OfType<PuzzleMember>()).Entry);
        Assert.All(current.EnumerateAllMembers().OfType<TileMember>(), member => Assert.NotNull(member.Entry));
        Assert.All(oldEntries, path => Assert.False(files.Exists(path)));
        Assert.Equal(new byte[] { 1, 2, 3 }, files.ReadAllBytes(external));
        Assert.True(files.Exists(store.ManagedMediaPath(before.Settings.BackgroundMedia!)));
        var reopened = store.Open();
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.Equal(current.Generation, reopened.Document!.Generation);
        Assert.Equal(before.Settings, reopened.Document.Settings);
        Assert.Empty(reopened.Document.InactiveMembers);
        var guide = Assert.Single(reopened.Document.EnumerateAllMembers(), member => member.TargetInput == WelcomeWallBuilder.ProjectUrl);
        var showcase = Assert.Single(reopened.Document.EnumerateAllMembers(), member => member.TargetInput == WelcomeWallBuilder.DiscussionsUrl);
        Assert.Equal(MemberImageSourceKind.Single, guide.Images.Kind);
        Assert.Equal(MemberImageSourceKind.Single, showcase.Images.Kind);
        Assert.Equal(guideImage.Reference, guide.Images.Media);
        Assert.Equal(showcaseImage.Reference, showcase.Images.Media);
        Assert.Equal(new byte[] { 4, 5, 6 }, files.ReadAllBytes(store.ManagedMediaPath(guide.Images.Media!)));
        Assert.Equal(new byte[] { 7, 8, 9 }, files.ReadAllBytes(store.ManagedMediaPath(showcase.Images.Media!)));
    }

    [Theory]
    [InlineData(EntryFaultPoint.BeforeEntrySwap, false)]
    [InlineData(EntryFaultPoint.BeforeEntryBackup, false)]
    [InlineData(EntryFaultPoint.None, true)]
    public void RejectedReplacementRollsBackBothOldEntrySetsAndKeepsSettingsMedia(EntryFaultPoint fault, bool configFailure)
    {
        var (store, files, root, before, external) = ExistingWall();
        var config = files.ReadAllBytes(Path.Combine(root, "state.json"));
        var oldEntries = before.EnumerateAllMembers().ToDictionary(member => EntryPath(root, member),
            member => files.ReadAllBytes(EntryPath(root, member)));
        var prepared = WelcomeWallBuilder.Prepare(before, Targets, Text, Gallery, Budget());
        var proposal = Assert.IsType<WallProposal>(prepared.Layout.Proposal);
        files.EntryFault = fault;
        files.InjectedFault = configFailure ? FaultPoint.BeforeMove : FaultPoint.None;
        var result = store.Commit(before, new(before.Generation, proposal.Document, prepared.EntryChanges));
        Assert.Equal(WallCommitStatus.Rejected, result.Status);
        Assert.Equal(config, files.ReadAllBytes(Path.Combine(root, "state.json")));
        Assert.All(oldEntries, entry => Assert.Equal(entry.Value, files.ReadAllBytes(entry.Key)));
        Assert.True(files.Exists(store.ManagedMediaPath(before.Settings.BackgroundMedia!)));
        Assert.Equal(new byte[] { 1, 2, 3 }, files.ReadAllBytes(external));
        files.InjectedFault = FaultPoint.None;
        var reopened = store.Open();
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.Equal(before.Generation, reopened.Document!.Generation);
        Assert.Equal("retained", Assert.Single(reopened.Document.InactiveMembers).Id);
        Assert.Equal(before.Settings, reopened.Document.Settings);
    }

}
