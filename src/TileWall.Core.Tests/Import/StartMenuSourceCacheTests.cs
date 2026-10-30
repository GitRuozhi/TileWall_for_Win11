using TileWall.Core.Entries;
using TileWall.Core.Import;
using TileWall.Core.Grid;
using TileWall.Core.Wall;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KnownRejectedSubtreeDisablesItsCachedRowsDuringIncompleteRoot(bool refreshing)
    {
        var old = Source("app");
        var root = new StartMenuRootScan(SourceKind.User, Root(SourceKind.User), false)
            { RejectedPaths = [Root(SourceKind.User)] };
        var result = StartMenuSourceCache.Merge(new() { Sources = [old] }, Scan(root), [], refreshing: refreshing);
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
    public void IncompleteFactRefreshPreservesFreshRowsWithoutAuthorizingColdCache()
    {
        var cold = Source("cached") with { Stale = true }; var fresh = Source("fresh"); var observed = Source("verified");
        var result = StartMenuSourceCache.Merge(new() { Sources = [cold, fresh] },
            Scan(new StartMenuRootScan(SourceKind.User, Root(SourceKind.User), false)), [observed], refreshing: true);
        Assert.True(result.Refreshing); Assert.False(result.IconPreparing);
        Assert.True(result.Sources.Single(source => source.Candidate.DisplayName == "cached").Stale);
        Assert.False(result.Sources.Single(source => source.Candidate.DisplayName == "fresh").Stale);
        Assert.False(result.Sources.Single(source => source.Candidate.DisplayName == "verified").Stale);
        Assert.False(Projection(result).Candidates.Single(candidate => candidate.Source.DisplayName == "cached").CanAdd);
    }

    [Fact]
    public void FreshSelectionSurvivesOneRowProgressAndTheCompleted51RowRefresh()
    {
        var sources = Enumerable.Range(0, 51).Select(index => Source("app" + index)).ToArray();
        var previous = new StartMenuCatalogSnapshot { Sources = sources };
        var selected = sources.ToDictionary(source => source.Candidate.SourceKey, source => source.Identity!.Key, StringComparer.Ordinal);
        var progress = StartMenuSourceCache.Merge(previous,
            Scan(new StartMenuRootScan(SourceKind.User, Root(SourceKind.User), false)),
            [sources[0] with { Company = "Updated metadata", TargetAvailability = ImportTargetAvailability.Available }], refreshing: true);

        var pendingSelection = ImportSourcePlanner.MigrateSelection(selected, Projection(progress));

        Assert.True(progress.Refreshing);
        Assert.Equal(51, pendingSelection.Selected.Count);
        Assert.Equal(0, pendingSelection.RemovedCount);
        var completed = StartMenuSourceCache.Merge(previous,
            Scan(new StartMenuRootScan(SourceKind.User, Root(SourceKind.User), true)), sources);
        var finalSelection = ImportSourcePlanner.MigrateSelection(pendingSelection.Selected, Projection(completed));
        Assert.False(completed.Refreshing);
        Assert.Equal(51, finalSelection.Selected.Count);
        Assert.Equal(0, finalSelection.RemovedCount);
    }

    [Fact]
    public void CompletedDeletionAndChangedIdentityStillRemoveTheirSelections()
    {
        var kept = Source("kept"); var removed = Source("removed"); var changed = Source("changed");
        var previous = new StartMenuCatalogSnapshot { Sources = [kept, removed, changed] };
        var selected = previous.Sources.ToDictionary(source => source.Candidate.SourceKey, source => source.Identity!.Key, StringComparer.Ordinal);
        var progress = StartMenuSourceCache.Merge(previous,
            Scan(new StartMenuRootScan(SourceKind.User, Root(SourceKind.User), false)), [kept], refreshing: true);
        var pending = ImportSourcePlanner.MigrateSelection(selected, Projection(progress));
        Assert.Equal(3, pending.Selected.Count);

        var completed = StartMenuSourceCache.Merge(previous,
            Scan(new StartMenuRootScan(SourceKind.User, Root(SourceKind.User), true)),
            [kept, changed with { SourceSha256 = new string('B', 64), Identity = LaunchIdentityService.ForPath(Path.Combine(Path.GetTempPath(), "new-target.exe")) }]);
        var final = ImportSourcePlanner.MigrateSelection(pending.Selected, Projection(completed));

        Assert.DoesNotContain(completed.Sources, source => source.Candidate.SourceKey == removed.Candidate.SourceKey);
        Assert.Equal(kept.Candidate.SourceKey, Assert.Single(final.Selected).Key);
        Assert.Equal(2, final.RemovedCount);
    }

    [Fact]
    public void AFailedRootDisablesPriorFreshFactsWhenTheRefreshSettles()
    {
        var source = Source("known"); var previous = new StartMenuCatalogSnapshot { Sources = [source] };
        var scan = Scan(new StartMenuRootScan(SourceKind.User, null, false, "RootUnavailable"));
        var progress = StartMenuSourceCache.Merge(previous, scan, [], refreshing: true);
        Assert.True(progress.Refreshing);
        var completed = StartMenuSourceCache.Merge(previous, scan, []);
        Assert.True(Assert.Single(completed.Sources).Stale);
        Assert.False(completed.Refreshing);
        Assert.False(Assert.Single(Projection(completed).Candidates).CanAdd);
    }

    private static ImportSourceProjection Projection(StartMenuCatalogSnapshot snapshot) => ImportSourcePlanner.Project(
        WallDocument.Empty(1, 12), snapshot.Sources.Select(source => new ImportSourceCandidate(
            source.Candidate.SourceKey, source.Candidate.DisplayName,
            new TileMember { Id = "source-" + source.Candidate.SourceKey, LocalBounds = new GridRect(0, 0, 1, 1) })
        {
            SourceKind = ImportPageSourceKind.StartMenu, Policy = ImportSourcePolicy.Shortcut,
            Identity = source.Identity, RequiresIdentity = true, Company = source.Company,
            ErrorCode = source.ErrorCode ?? (source.Stale ? "SourceChanged" : null),
        }).ToArray(), recognizeGroups: false, autoFilter: false);
}
