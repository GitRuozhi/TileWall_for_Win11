using TileWall.Core.Application;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Import;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Import;

public sealed class StartMenuCandidateProjectionTests
{
    private static StartMenuImportCandidate Item(string file, string folder = "", SourceKind source = SourceKind.User,
        string identity = "same", string? title = null)
    {
        var root = Path.Combine(Path.GetTempPath(), source == SourceKind.User ? "user-programs" : "common-programs");
        var candidate = new StartMenuCandidate(Path.Combine(root, folder, file + ".lnk"), title ?? file, EntryKind.Lnk, source, folder)
            { RootPath = root };
        return new(candidate, new("pending", "", 0), LaunchIdentityService.ForPath(Path.Combine(Path.GetTempPath(), identity + ".exe")),
            StartMenuExclusion.None, SourceSha256: new string('A', 64));
    }
    private static Dictionary<string, string> Select(params StartMenuImportCandidate[] items) =>
        items.ToDictionary(item => item.SelectionKey, item => item.Identity!.Key, StringComparer.Ordinal);

    [Fact]
    public void SameIdentityIsReducedOnlyAfterFinalDisplayedGroupAndUserWinsLocally()
    {
        var common = Item("Common", "Office", SourceKind.Common);
        var user = Item("User", "Office");
        var anotherGroup = Item("Other", "Tools");
        var result = StartMenuCandidateProjection.Project([common, user, anotherGroup], false, false);
        Assert.Equal(2, result.Count);
        var office = result.Single(item => item.Bucket.Title == "Office");
        Assert.Equal(user.SelectionKey, office.SelectionKey);
        Assert.Equal(2, office.RepresentedSources.Count);
        Assert.Equal(anotherGroup.SelectionKey, result.Single(item => item.Bucket.Title == "Tools").SelectionKey);
    }

    [Fact]
    public void GroupCountsUseDistinctValidIdentitiesBeforeSingletonFallback()
    {
        var user = Item("Word", "Office", title: "Word");
        var common = Item("Word", "Office", SourceKind.Common, title: "Word");
        var result = StartMenuCandidateProjection.Project([user, common], false, true);
        Assert.Equal("W", Assert.Single(result).Bucket.Title);
        Assert.Equal("letter:W", result[0].Bucket.Key);
        Assert.Equal(2, result[0].RepresentedSources.Count);
    }

    [Fact]
    public void SameLetterAndOriginalFolderTitleShareOneGroupAndEarliestDisplayCategory()
    {
        var letter = Item("Alpha", identity: "alpha");
        var folder = Item("Tool", "A", identity: "tool");
        var result = StartMenuCandidateProjection.Project([folder, letter], false, false);
        Assert.Single(result.Select(item => item.Bucket.Key).Distinct());
        Assert.Equal("letter:A", result[0].Bucket.Key);
        Assert.All(result, item => Assert.Equal(1, item.Bucket.Order));
    }

    [Fact]
    public void PreviewBucketProvenanceSurvivesMergeAndOrdersNewLettersBeforePersistedCustomGroups()
    {
        var currentGroups = new[]
        {
            PersistedGroup("legacy-a", "A", "title:A"),
            PersistedGroup("legacy-git", "GIT", "title:GIT"),
            PersistedGroup("legacy-adobe", "ADOBE", "title:ADOBE"),
        };
        var current = WallDocument.Empty(1, 24) with
        {
            Lanes = [new WallLane { Groups = currentGroups }],
        };
        var preview = StartMenuCandidateProjection.Project(
        [
            Item("Horizon", identity: "horizon"),
            Item("Iris", identity: "iris"),
            Item("Kite", identity: "kite"),
            Item("Nexus", identity: "nexus"),
            Item("Umbrella", identity: "umbrella"),
        ], false, true);

        Assert.Equal(new[] { "letter:H", "letter:I", "letter:K", "letter:N", "letter:U" },
            preview.Select(candidate => candidate.Bucket.Key));

        var incoming = preview.GroupBy(candidate => candidate.Bucket.Key, StringComparer.Ordinal).Select(bucket =>
        {
            var first = bucket.First();
            var id = "incoming-" + first.Bucket.Title;
            var source = new ImportSourceMetadata
            {
                BatchId = "new-batch", GroupBucketKey = first.Bucket.Key, OriginGroupId = id,
            };
            var members = bucket.Select(candidate => new StartMenuMergeMember(new TileMember
            {
                Id = id + "-" + candidate.Candidate.DisplayName,
                TargetInput = candidate.Identity!.Target,
                LocalBounds = new GridRect(0, 0, 1, 1),
                ImportSource = source with
                {
                    SourceRoot = candidate.Candidate.RootPath,
                    RelativePath = Path.GetRelativePath(candidate.Candidate.RootPath!, candidate.Candidate.FullPath),
                },
            }, candidate.Identity.Key)).ToArray();
            return new StartMenuMergeGroup(new StartGroup
            {
                Id = id, Title = first.Bucket.Title, ImportSource = source,
                Members = members.Select(member => member.Member).ToArray(),
            }, members);
        }).ToArray();

        var merged = StartMenuReimport.Merge(current, incoming, member => member.TargetInput);
        var result = StartMenuImportPlanner.TryPlan(current, current.Generation, merged.Groups);

        Assert.True(result.Success, result.Detail);
        var ordered = result.Proposal!.Document.Lanes.SelectMany(lane => lane.Groups).ToArray();
        Assert.Equal(new[]
        {
            "incoming-H", "incoming-I", "incoming-K", "incoming-N", "incoming-U",
            "legacy-a", "legacy-git", "legacy-adobe",
        }, ordered.Select(group => group.Id));
        Assert.Equal(new[] { "letter:H", "letter:I", "letter:K", "letter:N", "letter:U" },
            ordered.Take(5).Select(group => group.ImportSource!.GroupBucketKey));
    }

    private static StartGroup PersistedGroup(string id, string title, string bucketKey)
    {
        var source = new ImportSourceMetadata
        {
            BatchId = "old-batch", GroupBucketKey = bucketKey, OriginGroupId = id,
        };
        return new StartGroup
        {
            Id = id,
            Title = title,
            ImportSource = source,
            Members = [new TileMember
            {
                Id = id + "-member", TargetInput = "old-target:" + id,
                LocalBounds = new GridRect(0, 0, 1, 1),
                ImportSource = source with { SourceRoot = @"C:\StartMenu", RelativePath = id + ".lnk" },
            }],
        };
    }

    [Fact]
    public void MergeOrsSelectionsAndSplitKeepsOnlyOriginalSourceProvenance()
    {
        var one = Item("App one", "One");
        var two = Item("App two", "Two", SourceKind.Common);
        var raw = new[] { one, two };
        var separate = StartMenuCandidateProjection.Project(raw, false, false);
        var joined = StartMenuCandidateProjection.Project(raw, false, true);
        Assert.Single(joined);
        var merge = StartMenuCandidateProjection.Migrate(Select(two), separate, joined, raw);
        Assert.Equal(0, merge.RemovedCount);
        Assert.Equal(two.SelectionKey, Assert.Single(merge.Selected).Key);
        var split = StartMenuCandidateProjection.Migrate(merge.Selected, joined, separate, raw);
        Assert.Equal(two.SelectionKey, Assert.Single(split.Selected).Key);
        Assert.DoesNotContain(one.SelectionKey, split.Selected.Keys);
    }

    [Fact]
    public void FilteredSelectionsAreRemovedAndDoNotResurrectWhenFilterIsDisabled()
    {
        var hidden = Item("Uninstall") with { Exclusion = StartMenuExclusion.Uninstall };
        var visible = Item("App", identity: "app");
        var all = new[] { hidden, visible };
        var before = StartMenuCandidateProjection.Project(all, false, false);
        var filtered = StartMenuCandidateProjection.Project(all, true, false);
        var removed = StartMenuCandidateProjection.Migrate(Select(hidden), before, filtered, all);
        Assert.Empty(removed.Selected); Assert.Equal(1, removed.RemovedCount);
        var returned = StartMenuCandidateProjection.Migrate(removed.Selected, filtered, before, all);
        Assert.Empty(returned.Selected);
    }

    [Fact]
    public void SourceIdentityChangeRejectsSelectionAndOrdinaryNewSourcesStayUnchecked()
    {
        var old = Item("App", "Tools");
        var changed = old with { Identity = LaunchIdentityService.ForPath(Path.Combine(Path.GetTempPath(), "different.exe")) };
        var added = Item("New", "Tools", identity: "new");
        var before = StartMenuCandidateProjection.Project([old], false, false);
        var after = StartMenuCandidateProjection.Project([changed, added], false, false);
        var migrated = StartMenuCandidateProjection.Migrate(Select(old), before, after, [changed, added]);
        Assert.Empty(migrated.Selected); Assert.Equal(1, migrated.RemovedCount);
    }

    [Fact]
    public void EmptyInitialSelectionStaysEmptyAsSourcesArriveAndRefresh()
    {
        var first = Item("First", "Tools", identity: "first");
        var added = Item("Added", "Tools", identity: "added");
        var initial = StartMenuCandidateProjection.Project([first], false, false);
        var discovered = StartMenuCandidateProjection.Migrate(new Dictionary<string, string>(), [], initial, [first]);
        Assert.Empty(discovered.Selected);

        var refreshed = StartMenuCandidateProjection.Project([first, added], false, false);
        var update = StartMenuCandidateProjection.Migrate(discovered.Selected, initial, refreshed, [first, added]);
        Assert.Empty(update.Selected);
    }

    [Fact]
    public void NewUserRepresentativeCanInheritSelectedCommonOnlyWithinSameGroupAndIdentity()
    {
        var common = Item("App", "Tools", SourceKind.Common);
        var user = Item("App", "Tools");
        var before = StartMenuCandidateProjection.Project([common], false, false);
        var after = StartMenuCandidateProjection.Project([user], false, false);
        var migrated = StartMenuCandidateProjection.Migrate(Select(common), before, after, [user]);
        Assert.Equal(user.SelectionKey, Assert.Single(migrated.Selected).Key); Assert.Equal(0, migrated.RemovedCount);
        var overlapping = StartMenuCandidateProjection.Project([common, user], false, false);
        var replaced = StartMenuCandidateProjection.Migrate(Select(common), before, overlapping, [common, user]);
        Assert.Equal(user.SelectionKey, Assert.Single(replaced.Selected).Key);
        var removedLater = StartMenuCandidateProjection.Migrate(replaced.Selected, overlapping, after, [user]);
        Assert.Equal(user.SelectionKey, Assert.Single(removedLater.Selected).Key);
        var elsewhere = Item("App", "Office");
        var other = StartMenuCandidateProjection.Migrate(Select(common), before,
            StartMenuCandidateProjection.Project([elsewhere], false, false), [elsewhere]);
        Assert.Empty(other.Selected); Assert.Equal(1, other.RemovedCount);
    }

    [Fact]
    public void SelectionMigrationUsesDisplayedGroupWhenItsSourceBucketChanges()
    {
        var common = Item("Tool", "A", SourceKind.Common, identity: "shared");
        var user = Item("Alpha", identity: "shared");
        var before = StartMenuCandidateProjection.Project([common], false, false);
        var after = StartMenuCandidateProjection.Project([user], false, false);

        Assert.Equal("A", Assert.Single(before).Bucket.Title);
        Assert.Equal("folder:A", before[0].Bucket.Key);
        Assert.Equal("A", Assert.Single(after).Bucket.Title);
        Assert.Equal("letter:A", after[0].Bucket.Key);

        var migrated = StartMenuCandidateProjection.Migrate(Select(common), before, after, [user]);

        Assert.Equal(user.SelectionKey, Assert.Single(migrated.Selected).Key);
        Assert.Equal(0, migrated.RemovedCount);
    }
}
