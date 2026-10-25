using TileWall.Core.Application;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Application;

public sealed class WelcomeWallBuilderTests
{
    private static readonly WelcomeWallText Text = new("Windows", "Websites", "Explore TileWall",
        "File Explorer", "Settings", "Downloads", "Documents", "Guide", "Showcase", "TileWall");
    private static readonly WelcomeWallTargets Targets = new(
        Path.GetFullPath("explorer.exe"), Path.GetFullPath("Downloads"), Path.GetFullPath("Documents"));
    private static readonly PreparedMediaAsset Brand = PreparedMediaAsset.FromVerifiedImage(new byte[] { 7, 8, 9 }, ".png");
    private static string EntryPath(string root, WallMember member) =>
        Path.Combine(root, member.Entry!.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    private static WallViewportBudget Budget(int columns = 2, int rows = 11)
    {
        var metrics = new WallMetrics();
        return new(metrics.WallWidth(columns), metrics.WallHeight(rows), columns, rows, false);
    }

    [Theory]
    [InlineData(2, 11)]
    [InlineData(1, 17)]
    public void CompleteContentUsesOrdinaryLayoutAndNewIdsWithoutChangingSettings(int columns, int rows)
    {
        var settings = new WallSettings { LanguageMode = WallLanguageMode.English, RunAtLogin = true,
            StayVisibleOnFocusLoss = true, WindowedColumns = 1, WindowedRows = 8, HotKey = "Ctrl+F8" };
        var before = WallDocument.Empty(1, 8) with { Generation = 9, Settings = settings };
        var prepared = WelcomeWallBuilder.Prepare(before, Targets, Text, Brand.Reference, Budget(columns, rows));
        var proposal = Assert.IsType<WallProposal>(prepared.Layout.Proposal);
        Assert.Equal(10, proposal.Document.Generation);
        Assert.Same(settings, proposal.Document.Settings);
        Assert.Empty(WallLayoutEngine.ValidateGeometry(proposal.Document));
        Assert.True(WallViewportPolicy.Validate(proposal, Budget(columns, rows)).Success);
        Assert.Equal(columns, proposal.Document.Columns);
        Assert.Equal(5, proposal.Document.Lanes.Sum(lane => lane.Groups.Count));
        Assert.Equal(9, proposal.Document.EnumerateAllMembers().Count());
        var puzzle = Assert.Single(proposal.Document.EnumerateAllMembers().OfType<PuzzleMember>());
        Assert.Equal(Brand.Reference, puzzle.Images.Media);
        Assert.Equal(4, puzzle.Partitions.Count);
        Assert.Equal(WelcomeWallBuilder.DiscussionsUrl, puzzle.TargetInput);
        Assert.Equal(9, prepared.EntryChanges.Count);
        var again = WelcomeWallBuilder.Prepare(before, Targets, Text, Brand.Reference, Budget(columns, rows));
        Assert.Empty(proposal.Document.EnumerateAllMembers().Select(member => member.Id)
            .Intersect(again.Layout.Proposal!.Document.EnumerateAllMembers().Select(member => member.Id), StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(2, 10)]
    [InlineData(1, 16)]
    public void InsufficientCapacityDoesNotReturnPartialContentOrEntryRemovals(int columns, int rows)
    {
        var before = WallDocument.Empty(1, 8) with
        {
            InactiveMembers = [new TileMember { Id = "retained", LocalBounds = new(0, 0, 1, 1),
                Entry = new("retained", "Objects/retained/entry.url", ManagedEntryKind.Url) }],
        };
        var prepared = WelcomeWallBuilder.Prepare(before, Targets, Text, Brand.Reference, Budget(columns, rows));
        Assert.Null(prepared.Layout.Proposal);
        Assert.Empty(prepared.EntryChanges);
        Assert.Equal("retained", Assert.Single(before.InactiveMembers).Id);
        Assert.Equal(0, before.Generation);
    }

    [Fact]
    public void ExpansionRetainsItsOrdinaryEmptyTailWithoutChangingTheRequestedSetting()
    {
        var before = WallDocument.Empty(1, 8) with { Settings = new() { ExpandColumns = true } };
        var prepared = WelcomeWallBuilder.Prepare(before, Targets, Text, Brand.Reference, Budget(64, 11));
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
        var prepared = WelcomeWallBuilder.Prepare(before, Targets, Text, Brand.Reference, Budget(2, 19));
        var proposal = Assert.IsType<WallProposal>(prepared.Layout.Proposal);
        Assert.Equal(11, proposal.Document.Rows);
        Assert.Equal(9, proposal.Document.EnumerateAllMembers().Count());
        Assert.Same(settings, proposal.Document.Settings);
        Assert.Equal(19, proposal.Document.Settings.WindowedRows);
        Assert.Empty(WallLayoutEngine.ValidateGeometry(proposal.Document));
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
        var prepared = WelcomeWallBuilder.Prepare(before, Targets, Text, Brand.Reference, Budget());
        Assert.Equal(new[] { "active", "retained" }, prepared.EntryChanges.OfType<EntryChange.Remove>().Select(change => change.MemberId));
        var proposal = Assert.IsType<WallProposal>(prepared.Layout.Proposal);
        var result = store.Commit(before, new(before.Generation, proposal.Document, prepared.EntryChanges, [Brand]));
        Assert.Equal(WallCommitStatus.Committed, result.Status);
        var current = result.CommittedDocument!;
        Assert.Equal(before.Settings, current.Settings);
        Assert.Empty(current.InactiveMembers);
        Assert.Equal(9, current.EnumerateAllMembers().Count());
        Assert.All(current.EnumerateAllMembers(), member => Assert.NotNull(member.Entry));
        Assert.All(oldEntries, path => Assert.False(files.Exists(path)));
        Assert.Equal(new byte[] { 1, 2, 3 }, files.ReadAllBytes(external));
        Assert.True(files.Exists(store.ManagedMediaPath(before.Settings.BackgroundMedia!)));
        Assert.True(files.Exists(store.ManagedMediaPath(Brand.Reference)));
        var reopened = store.Open();
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.Equal(current.Generation, reopened.Document!.Generation);
        Assert.Equal(before.Settings, reopened.Document.Settings);
        Assert.Empty(reopened.Document.InactiveMembers);
    }

    [Theory]
    [InlineData(EntryFaultPoint.BeforeEntrySwap, false)]
    [InlineData(EntryFaultPoint.BeforeEntryBackup, false)]
    [InlineData(EntryFaultPoint.None, true)]
    public void RejectedReplacementRollsBackBothOldEntrySetsAndNewMedia(EntryFaultPoint fault, bool configFailure)
    {
        var (store, files, root, before, external) = ExistingWall();
        var config = files.ReadAllBytes(Path.Combine(root, "config.json"));
        var oldEntries = before.EnumerateAllMembers().ToDictionary(member => EntryPath(root, member),
            member => files.ReadAllBytes(EntryPath(root, member)));
        var prepared = WelcomeWallBuilder.Prepare(before, Targets, Text, Brand.Reference, Budget());
        var proposal = Assert.IsType<WallProposal>(prepared.Layout.Proposal);
        files.EntryFault = fault;
        files.InjectedFault = configFailure ? FaultPoint.BeforeMove : FaultPoint.None;
        var result = store.Commit(before, new(before.Generation, proposal.Document, prepared.EntryChanges, [Brand]));
        Assert.Equal(WallCommitStatus.Rejected, result.Status);
        Assert.Equal(config, files.ReadAllBytes(Path.Combine(root, "config.json")));
        Assert.All(oldEntries, entry => Assert.Equal(entry.Value, files.ReadAllBytes(entry.Key)));
        Assert.False(files.Exists(store.ManagedMediaPath(Brand.Reference)));
        Assert.True(files.Exists(store.ManagedMediaPath(before.Settings.BackgroundMedia!)));
        Assert.Equal(new byte[] { 1, 2, 3 }, files.ReadAllBytes(external));
        files.InjectedFault = FaultPoint.None;
        var reopened = store.Open();
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.Equal(before.Generation, reopened.Document!.Generation);
        Assert.Equal("retained", Assert.Single(reopened.Document.InactiveMembers).Id);
        Assert.Equal(before.Settings, reopened.Document.Settings);
    }

    [Fact]
    public void MissingPreparedBrandIsRejectedBeforeReplacingExistingWall()
    {
        var (store, files, root, before, _) = ExistingWall();
        var config = files.ReadAllBytes(Path.Combine(root, "config.json"));
        var prepared = WelcomeWallBuilder.Prepare(before, Targets, Text, Brand.Reference, Budget());
        var result = store.Commit(before, new(before.Generation, prepared.Layout.Proposal!.Document, prepared.EntryChanges));
        Assert.Equal(WallCommitStatus.Rejected, result.Status);
        Assert.Equal(config, files.ReadAllBytes(Path.Combine(root, "config.json")));
        Assert.False(files.Exists(store.ManagedMediaPath(Brand.Reference)));
    }
}
