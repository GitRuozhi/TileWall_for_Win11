using System.Security.Cryptography;
using System.Text;
using TileWall.Core.Entries;

namespace TileWall.Core.Import;

/// <summary>Only source facts live here. No wall IDs, placement, selection or translated errors are cached.</summary>
public sealed record StartMenuCachedSource
{
    public required StartMenuCandidate Candidate { get; init; }
    public required string SourceSha256 { get; init; }
    public LaunchIdentity? Identity { get; init; }
    public LinkFields? LinkFields { get; init; }
    public string? UrlIconPath { get; init; }
    public int? UrlIconIndex { get; init; }
    public string? Company { get; init; }
    public string? FileVersion { get; init; }
    public IReadOnlyList<StartMenuDependencyStamp> Dependencies { get; init; } = [];
    public string? IconKey { get; init; }
    public ImportTargetAvailability TargetAvailability { get; init; }
    public bool TargetIsDirectory { get; init; }
    public string? ErrorCode { get; init; }
    public bool Stale { get; init; }
}

public sealed record StartMenuDependencyStamp(string Path, long Length, long LastWriteUtcTicks, bool Available);

public sealed record StartMenuCatalogSnapshot
{
    public int FormatVersion { get; init; } = StartMenuSourceCache.FormatVersion;
    public IReadOnlyList<StartMenuCachedSource> Sources { get; init; } = [];
    public IReadOnlyList<StartMenuRootScan> Roots { get; init; } = [];
    public int ScannedCount { get; init; }
    public int SkippedDirectories { get; init; }
    public bool Refreshing { get; init; }
    public bool IconPreparing { get; init; }
    public string? ErrorCode { get; init; }
}

public static class StartMenuSourceCache
{
    public const int FormatVersion = 2;
    public const int MaximumSourceBytes = 4 * 1024 * 1024;

    public static string SourceKey(SourceKind source, string root, string relativePath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{(int)source}\n{Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant()}\n" +
            relativePath.Replace('\\', '/').ToUpperInvariant()))).ToLowerInvariant();

    public static string GroupNameKey(string? title) => (title ?? string.Empty).Trim().ToUpperInvariant();

    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    /// <summary>A failed root never becomes an authoritative empty root. A changed root has another key space.</summary>
    public static StartMenuCatalogSnapshot Merge(StartMenuCatalogSnapshot previous, StartMenuScanResult scan,
        IReadOnlyList<StartMenuCachedSource> observed, bool refreshing = false, bool iconPreparing = false)
    {
        var sources = observed.ToDictionary(source => source.Candidate.SourceKey, StringComparer.Ordinal);
        var roots = scan.Roots;
        foreach (var old in previous.Sources)
        {
            if (sources.ContainsKey(old.Candidate.SourceKey)) continue;
            var root = roots.FirstOrDefault(root => root.Source == old.Candidate.Source);
            if (root is { Path: { } path } && !SamePath(path, old.Candidate.RootPath)) continue;
            if (root?.Complete == true) continue;
            var rejected = root?.RejectedPaths.Any(path => Within(path, old.Candidate.FullPath)) == true;
            sources.Add(old.Candidate.SourceKey, old with
            {
                Stale = true,
                ErrorCode = rejected ? "UnsafeSource" : old.ErrorCode,
            });
        }
        return new()
        {
            Sources = sources.Values.OrderBy(source => source.Candidate.Source)
                .ThenBy(source => source.Candidate.FullPath, StringComparer.OrdinalIgnoreCase).ToArray(),
            Roots = roots.ToArray(), ScannedCount = scan.Candidates.Count,
            SkippedDirectories = scan.SkippedDirectories, Refreshing = refreshing, IconPreparing = iconPreparing,
        };
    }

    public static bool Within(string root, string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(path);
        return candidate.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string left, string? right) => right is not null &&
        Path.TrimEndingDirectorySeparator(left).Equals(Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);
}
