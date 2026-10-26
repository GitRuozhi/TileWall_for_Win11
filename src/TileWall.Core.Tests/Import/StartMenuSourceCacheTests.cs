using TileWall.Core.Entries;
using TileWall.Core.Import;
using Xunit;

namespace TileWall.Core.Tests.Import;

public sealed class StartMenuSourceCacheTests
{
    private static string Root(SourceKind source) => Path.Combine(Path.GetTempPath(), source + "-programs");
    private static StartMenuCachedSource Source(string file, SourceKind source = SourceKind.User, string? root = null) => new()
    {
        Candidate = new(Path.Combine(root ?? Root(source), file + ".lnk"), file, EntryKind.Lnk, source, "")
            { RootPath = root ?? Root(source) },
        SourceSha256 = new string('A', 64), Identity = LaunchIdentityService.ForPath(Path.Combine(Path.GetTempPath(), file + ".exe")),
    };
    private static StartMenuScanResult Scan(params StartMenuRootScan[] roots) => new([], roots.Count(root => !root.Complete)) { Roots = roots };

    [Fact]
    public void OnlyCompleteRootsPruneUnseenSourcesAndObservedRowsOverrideOldFacts()
    {
        var user = Source("user"); var common = Source("common", SourceKind.Common);
        var changed = common with { Company = "New publisher", Stale = false };
        var previous = new StartMenuCatalogSnapshot { Sources = [user, common, Source("unseen", SourceKind.Common)] };
        var result = StartMenuSourceCache.Merge(previous,
            Scan(new StartMenuRootScan(SourceKind.User, Root(SourceKind.User), true),
                new StartMenuRootScan(SourceKind.Common, Root(SourceKind.Common), false)), [changed]);
        Assert.DoesNotContain(result.Sources, source => source.Candidate.SourceKey == user.Candidate.SourceKey);
        Assert.Equal("New publisher", result.Sources.Single(source => source.Candidate.SourceKey == common.Candidate.SourceKey).Company);
        Assert.True(result.Sources.Single(source => source.Candidate.DisplayName == "unseen").Stale);
    }

    [Fact]
    public void FailedRootLookupRetainsOldFactsButChangedActualRootDoesNotMixKeySpaces()
    {
        var old = Source("app"); var previous = new StartMenuCatalogSnapshot { Sources = [old] };
        var unavailable = StartMenuSourceCache.Merge(previous,
            Scan(new StartMenuRootScan(SourceKind.User, null, false, "RootUnavailable")), []);
        Assert.True(Assert.Single(unavailable.Sources).Stale);
        var changed = StartMenuSourceCache.Merge(previous,
            Scan(new StartMenuRootScan(SourceKind.User, Path.Combine(Path.GetTempPath(), "different-programs"), false)), []);
        Assert.Empty(changed.Sources);
    }

    [Fact]
    public void KnownRejectedSubtreeDisablesItsCachedRowsDuringIncompleteRoot()
    {
        var old = Source("app");
        var root = new StartMenuRootScan(SourceKind.User, Root(SourceKind.User), false)
            { RejectedPaths = [Root(SourceKind.User)] };
        var result = StartMenuSourceCache.Merge(new() { Sources = [old] }, Scan(root), []);
        var retained = Assert.Single(result.Sources);
        Assert.True(retained.Stale); Assert.Equal("UnsafeSource", retained.ErrorCode);
    }

    [Fact]
    public void CompleteEmptyRootIsAuthoritativeAndStableKeyDoesNotContainFinalGrouping()
    {
        var old = Source("app");
        var empty = StartMenuSourceCache.Merge(new() { Sources = [old] },
            Scan(new StartMenuRootScan(SourceKind.User, Root(SourceKind.User), true)), []);
        Assert.Empty(empty.Sources);
        Assert.Equal(old.Candidate.SourceKey, (old.Candidate with { DisplayName = "New title", RelativeDir = "Display only" }).SourceKey);
        Assert.NotEqual(old.Candidate.SourceKey, Source("app", root: Path.Combine(Path.GetTempPath(), "another-programs")).Candidate.SourceKey);
        Assert.True(StartMenuSourceCache.IsHash(old.Candidate.SourceKey));
    }
    [Fact]
    public void IconPreparationDoesNotKeepVerifiedFactsInRefreshingState()
    {
        var observed = Source("ready") with { TargetAvailability = ImportTargetAvailability.Available, TargetIsDirectory = true };
        var result = StartMenuSourceCache.Merge(new(), Scan(new StartMenuRootScan(SourceKind.User, Root(SourceKind.User), true)),
            [observed], iconPreparing: true);
        Assert.False(result.Refreshing); Assert.True(result.IconPreparing);
        var ready = Assert.Single(result.Sources);
        Assert.False(ready.Stale); Assert.Equal(ImportTargetAvailability.Available, ready.TargetAvailability); Assert.True(ready.TargetIsDirectory);
    }

    [Fact]
    public void IncompleteFactRefreshRetainsUnseenRowsAsStaleIndependentlyOfIconState()
    {
        var old = Source("cached"); var observed = Source("verified");
        var result = StartMenuSourceCache.Merge(new() { Sources = [old] },
            Scan(new StartMenuRootScan(SourceKind.User, Root(SourceKind.User), false)), [observed], refreshing: true);
        Assert.True(result.Refreshing); Assert.False(result.IconPreparing);
        Assert.True(result.Sources.Single(source => source.Candidate.DisplayName == "cached").Stale);
        Assert.False(result.Sources.Single(source => source.Candidate.DisplayName == "verified").Stale);
    }
}
