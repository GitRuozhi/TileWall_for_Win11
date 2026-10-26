using TileWall.Core.Grid;
using TileWall.Core.Imaging;
using TileWall.Core.Storage;
using TileWall.Core.Wall;

namespace TileWall.Core.Application;

internal sealed record WelcomeWallTarget(string Input, string? ShellIdentityKey = null);
internal sealed record WelcomeWallTargets(IReadOnlyDictionary<string, WelcomeWallTarget> Items);
internal sealed record WelcomeWallText(IReadOnlyDictionary<string, string> Items);

/// <summary>Builds a complete ordinary proposal; neither trigger nor disk state belongs here.</summary>
internal static class WelcomeWallBuilder
{
    internal const string SettingsUrl = "ms-settings:";
    internal const string ProjectUrl = "https://github.com/GitRuozhi/TileWall_for_Win11";
    internal const string DiscussionsUrl = ProjectUrl + "/discussions";
    internal static readonly string[] NativeTargetKeys =
        ["ThisPC", "ControlPanel", "RecycleBin", "Network", "Desktop", "Downloads", "Videos", "Pictures", "Documents", "Music", "Calculator", "Notepad", "CMD", "Store", "Edge"];
    internal static readonly string[] SeasonFileNames = ["01-spring.png", "02-summer.png", "03-autumn.png", "04-winter.png"];
    internal static readonly string[] WebsiteUrls = ["https://www.bilibili.com/", "https://www.youtube.com/", "https://x.com/",
        "https://store.steampowered.com/", "https://openai.com/", "https://chat.deepseek.com/", "https://grok.com/"];

    internal static (WallLayoutResult Layout, IReadOnlyList<EntryChange> EntryChanges) Prepare(
        WallDocument before, WelcomeWallTargets targets, WelcomeWallText text,
        string galleryFolder, WallViewportBudget budget, WallMetrics? metrics = null,
        ManagedGalleryReference? managedGallery = null)
    {
        metrics ??= new();
        if (budget.MaxColumns < 1 || budget.MaxRows < 1)
            return (new(null, WallRejection.WorkAreaTooSmall, "CONTENT_TOO_LARGE"), []);
        if (before.Generation >= long.MaxValue - 1)
            return (new(null, WallRejection.InvalidDocument, "GENERATION_OVERFLOW"), []);
        if (!Path.IsPathFullyQualified(galleryFolder) || NativeTargetKeys.Any(key =>
                !targets.Items.TryGetValue(key, out var target) || string.IsNullOrWhiteSpace(target.Input) ||
                (target.ShellIdentityKey is null ? !Path.IsPathFullyQualified(target.Input) : string.IsNullOrWhiteSpace(target.ShellIdentityKey))))
            return (new(null, WallRejection.InvalidAction, "Invalid welcome content source."), []);

        // Include retained members: replacing Lanes alone would silently retain unpinned content.
        var changes = before.EnumerateAllMembers().Where(member => member.Entry is not null)
            .Select(member => (EntryChange)new EntryChange.Remove(member.Id)).ToList();
        static string NewId() => "welcome-" + Guid.NewGuid().ToString("N");
        static MemberVisual Title(string title) => new() { TitleMode = MemberTitleMode.Manual, Title = title };
        TileMember Native(string key, GridRect bounds)
        {
            var target = targets.Items[key];
            var member = new TileMember { Id = NewId(), LocalBounds = bounds, TargetInput = target.Input,
                Visual = Title(text.Items.GetValueOrDefault(key, key)) };
            changes.Add(target.ShellIdentityKey is { } identity
                ? new EntryChange.CreateShellLink(member.Id, target.Input, identity)
                : new EntryChange.CreateLink(member.Id, target.Input));
            return member;
        }
        TileMember Url(string title, string target, GridRect bounds)
        {
            var member = new TileMember { Id = NewId(), LocalBounds = bounds, TargetInput = target, Visual = Title(title) };
            changes.Add(new EntryChange.CreateUrl(member.Id, target));
            return member;
        }
        var windows = new StartGroup
        {
            Id = NewId(), Title = "Windows",
            Members = [Native("ThisPC", new(0, 0, 2, 2)), Native("ControlPanel", new(4, 0, 2, 2)),
                Url(text.Items["Settings"], SettingsUrl, new(6, 0, 2, 2)),
                Native("RecycleBin", new(0, 2, 1, 1)), Native("Network", new(1, 2, 1, 1)),
                Native("Desktop", new(2, 2, 1, 1)), Native("Downloads", new(3, 2, 1, 1)),
                Native("Videos", new(4, 2, 1, 1)), Native("Pictures", new(5, 2, 1, 1)),
                Native("Documents", new(6, 2, 1, 1)), Native("Music", new(7, 2, 1, 1))],
        };
        var tools = new StartGroup
        {
            Id = NewId(), Title = "Tools",
            Members = [Native("Calculator", new(0, 0, 2, 2)), Native("Notepad", new(2, 0, 2, 2)), Native("CMD", new(6, 0, 2, 2))],
        };
        var internet = new StartGroup
        {
            Id = NewId(), Title = "Internet",
            Members = [Native("Store", new(0, 0, 2, 2)), Native("Edge", new(2, 0, 2, 2)),
                Url("Bilibili", WebsiteUrls[0], new(4, 0, 1, 1)), Url("YouTube", WebsiteUrls[1], new(5, 0, 1, 1)),
                Url("X", WebsiteUrls[2], new(6, 0, 1, 1)), Url("Steam", WebsiteUrls[3], new(7, 0, 1, 1)),
                Url("OpenAI", WebsiteUrls[4], new(4, 1, 1, 1)), Url("DeepSeek", WebsiteUrls[5], new(5, 1, 1, 1)),
                Url("Grok", WebsiteUrls[6], new(6, 1, 1, 1))],
        };
        var puzzle = new PuzzleMember
        {
            Id = NewId(), LocalBounds = new(2, 0, 6, 4),
            Visual = Title("Puzzle Album"), Images = new() { Kind = MemberImageSourceKind.Folder, Path = galleryFolder, ManagedGallery = managedGallery },
            ImageDisplay = new() { Mode = ImageDisplayMode.Fit }, RotationInterval = ImageRotationInterval.Seconds5,
            Partitions = [new(0, 0, 2, 2), new(2, 0, 2, 2), new(4, 0, 2, 2),
                new(0, 2, 2, 2), new(2, 2, 2, 2), new(4, 2, 2, 2)],
        };
        var tileWall = new StartGroup
        {
            Id = NewId(), Title = "TileWall",
            Members = [puzzle, Url("TileWall Use Guide", ProjectUrl, new(2, 4, 3, 2)),
                Url("Show your TileWall", DiscussionsUrl, new(5, 4, 3, 2))],
        };
        var seed = before with
        {
            Columns = 2, Rows = 256, InactiveMembers = [],
            Lanes = [new WallLane { Groups = [windows, tools, internet] }, new WallLane { Groups = [tileWall] }],
        };
        var columns = Math.Min(2, budget.MaxColumns);
        // Replace content layout and empty tail rows, preserving saved window preferences.
        var rows = Math.Min(10, budget.MaxRows);
        var layout = WallViewportPolicy.TryResize(seed, columns, rows, before.Settings, budget, metrics);
        if (!layout.Success && rows < budget.MaxRows)
            layout = WallViewportPolicy.TryResize(seed, columns, budget.MaxRows, before.Settings, budget, metrics);
        return (layout, layout.Success ? changes.ToArray() : []);
    }
}
