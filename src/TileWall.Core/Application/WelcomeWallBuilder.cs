using TileWall.Core.Grid;
using TileWall.Core.Imaging;
using TileWall.Core.Storage;
using TileWall.Core.Wall;

namespace TileWall.Core.Application;

internal sealed record WelcomeWallTargets(string Explorer, string Downloads, string Documents);

internal sealed record WelcomeWallText(string Windows, string Websites, string Explore,
    string Explorer, string Settings, string Downloads, string Documents,
    string Tutorial, string Showcase, string Brand);

/// <summary>Builds a complete ordinary proposal; neither trigger nor disk state belongs here.</summary>
internal static class WelcomeWallBuilder
{
    internal const string SettingsUrl = "ms-settings:";
    internal const string MicrosoftUrl = "https://www.microsoft.com/";
    internal const string GitHubUrl = "https://github.com/";
    internal const string DiscussionsUrl = "https://github.com/GitRuozhi/TileWall_for_Win11/discussions";

    internal static (WallLayoutResult Layout, IReadOnlyList<EntryChange> EntryChanges) Prepare(
        WallDocument before, WelcomeWallTargets targets, WelcomeWallText text,
        ManagedMediaReference brand, WallViewportBudget budget, WallMetrics? metrics = null)
    {
        metrics ??= new();
        if (budget.MaxColumns < 1 || budget.MaxRows < 1)
            return (new(null, WallRejection.WorkAreaTooSmall, "CONTENT_TOO_LARGE"), []);
        if (before.Generation >= long.MaxValue - 1)
            return (new(null, WallRejection.InvalidDocument, "GENERATION_OVERFLOW"), []);
        if (!PreparedMediaAsset.IsValidReference(brand) ||
            !Path.IsPathFullyQualified(targets.Explorer) ||
            !Path.IsPathFullyQualified(targets.Downloads) || !Path.IsPathFullyQualified(targets.Documents))
            return (new(null, WallRejection.InvalidAction, "Invalid welcome content source."), []);

        // Include retained members: replacing Lanes alone would silently retain unpinned content.
        var changes = before.EnumerateAllMembers().Where(member => member.Entry is not null)
            .Select(member => (EntryChange)new EntryChange.Remove(member.Id)).ToList();
        static string NewId() => "welcome-" + Guid.NewGuid().ToString("N");
        TileMember Tile(string title, string target, GridRect bounds, bool url)
        {
            var member = new TileMember
            {
                Id = NewId(), LocalBounds = bounds, TargetInput = target,
                Visual = new() { TitleMode = MemberTitleMode.Manual, Title = title },
            };
            changes.Add(url ? new EntryChange.CreateUrl(member.Id, target) : new EntryChange.CreateLink(member.Id, target));
            return member;
        }
        var windows = new StartGroup
        {
            Id = NewId(), Title = text.Windows,
            Members = [Tile(text.Explorer, targets.Explorer, new(0, 0, 2, 2), false),
                Tile(text.Settings, SettingsUrl, new(2, 0, 2, 2), true),
                Tile(text.Downloads, targets.Downloads, new(4, 0, 2, 2), false),
                Tile(text.Documents, targets.Documents, new(6, 0, 2, 2), false)],
        };
        var websites = new StartGroup
        {
            Id = NewId(), Title = text.Websites,
            Members = [Tile("Microsoft", MicrosoftUrl, new(0, 0, 4, 2), true),
                Tile("GitHub", GitHubUrl, new(4, 0, 4, 2), true)],
        };
        var puzzle = new PuzzleMember
        {
            Id = NewId(), LocalBounds = new(0, 0, 8, 4), TargetInput = DiscussionsUrl,
            Visual = new() { TitleMode = MemberTitleMode.Manual, Title = text.Brand, ShowTitle = false },
            Images = new() { Kind = MemberImageSourceKind.Single, Media = brand },
            ImageDisplay = new() { Mode = ImageDisplayMode.Fit },
            Partitions = [new(0, 0, 4, 2), new(4, 0, 4, 2), new(0, 2, 4, 2), new(4, 2, 4, 2)],
        };
        changes.Add(new EntryChange.CreateUrl(puzzle.Id, DiscussionsUrl));
        var explore = new StartGroup
        {
            Id = NewId(), Title = text.Explore,
            Members = [puzzle],
        };
        // Public topics may not exist yet. Equal homepage identities remain legal in
        // distinct ordinary groups, without weakening the shared group deduplication rule.
        var tutorial = new StartGroup
        {
            Id = NewId(), Title = text.Tutorial,
            Members = [Tile(text.Tutorial, DiscussionsUrl, new(0, 0, 4, 2), true)],
        };
        var showcase = new StartGroup
        {
            Id = NewId(), Title = text.Showcase,
            Members = [Tile(text.Showcase, DiscussionsUrl, new(0, 0, 4, 2), true)],
        };
        var seed = before with
        {
            // A structurally valid source for the existing whole-group resize/reflow rules.
            Columns = 2, Rows = 256, InactiveMembers = [],
            Lanes = [new WallLane { Groups = [windows, websites] }, new WallLane { Groups = [explore, tutorial, showcase] }],
        };
        var columns = Math.Min(2, budget.MaxColumns);
        // Initialization replaces the content layout, including empty tail rows.
        // Saved window preferences remain in Settings and are not rewritten here.
        var rows = Math.Min(11, budget.MaxRows);
        var layout = WallViewportPolicy.TryResize(seed, columns, rows, before.Settings, budget, metrics);
        if (!layout.Success && rows < budget.MaxRows)
            layout = WallViewportPolicy.TryResize(seed, columns, budget.MaxRows, before.Settings, budget, metrics);
        return (layout, layout.Success ? changes.ToArray() : []);
    }
}
