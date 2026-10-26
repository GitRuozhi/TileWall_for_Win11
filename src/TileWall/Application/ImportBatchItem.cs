using TileWall.Core.Entries;
using TileWall.Core.Application;
using TileWall.Core.Exchange;
using TileWall.Core.Grid;
using TileWall.Core.Import;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Graphics;

namespace TileWall.ApplicationModel;

internal sealed record ImportBatchItem(string SourcePath, TileMember Member, ExplorerAddKind Kind)
{
    public string? ExpectedIdentityKey { get; init; }
    public string? ExpectedSourceSha256 { get; init; }
    public string? ExpectedSourceStamp { get; init; }

    public static ImportBatchItem Create(string sourcePath)
    {
        if (!Path.IsPathFullyQualified(sourcePath)) throw new IOException(sourcePath);
        // Normalize before admission, including equivalent local-root spellings.
        var path = sourcePath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + sourcePath[8..]
            : sourcePath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ? sourcePath[4..] : sourcePath;
        if (path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase)) throw new IOException(sourcePath);
        path = Path.GetFullPath(path);
        if (path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase)) throw new IOException(sourcePath);
        var kind = ExplorerAddPathClassifier.Classify(path);
        if (kind == ExplorerAddKind.Drop) throw new IOException(sourcePath);
        var name = Directory.Exists(path)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) : Path.GetFileNameWithoutExtension(path);
        var errors = EntryNames.Validate(name);
        if (errors.Count != 0) throw new IOException($"{path}: {string.Join("; ", errors)}");
        return new(path, new TileMember
        {
            Id = $"tile-{Guid.NewGuid():N}", LocalBounds = new GridRect(0, 0, 2, 2),
            Visual = new MemberVisual { Title = name },
            TargetInput = path,
        }, kind);
    }

    public bool StillValid() => ExplorerAddPathClassifier.Classify(SourcePath) == Kind;
}

internal sealed record ExternalImportResult(string Message, bool Succeeded, int AddedCount);

internal sealed record ImportPageContext(WallDocument Document, WallViewportBudget Budget,
    IReadOnlyDictionary<string, string> ExistingIdentities, IReadOnlyDictionary<string, string> ExistingEntrySha256)
{
    public IReadOnlySet<string> ObservedDestinationGroups { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public bool Covers(ImportSourceProjection projection) => projection.Generation == Document.Generation &&
        projection.Groups.All(group => group.DestinationGroupId is null || ObservedDestinationGroups.Contains(group.DestinationGroupId));
}

/// <summary>Page-owned read-only descriptors, never a second formal wall or a persistent import service.</summary>
internal sealed record ImportPageSource(IReadOnlyList<ImportSourceCandidate> Candidates,
    IReadOnlyDictionary<string, StartMenuImportCandidate> StartMenuSources,
    WallPackagePreview? Package = null, int Scanned = 0, int Skipped = 0, int ReadErrors = 0,
    bool Refreshing = false, string? ErrorCode = null)
{
    public IReadOnlyDictionary<string, string> AvailabilityFacts { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public bool AvailabilityPrepared { get; init; }
    public bool IconPreparing { get; init; }
}

internal sealed record ImportPagePreparation(ImportPageContext Context, ImportPageSource Source);

internal sealed record ImportPageRequest(ImportPageContext Context, ImportPageSource Source,
    ImportSourceProjection Projection, ImportSourcePlan Plan, Func<bool> IsCurrent)
{
    public IReadOnlyDictionary<string, ImportTargetRecognition> RecognizedTargets { get; init; } =
        new Dictionary<string, ImportTargetRecognition>(StringComparer.Ordinal);
}

/// <summary>A page-local inference is reusable only for the same original input and observed source facts.</summary>
internal sealed record ImportTargetRecognition(string SourceFact, TargetImageSourceResult Result)
{
    internal static bool AppliesTo(ImportSourceCandidate candidate) =>
        candidate.Policy != ImportSourcePolicy.Attributed && candidate.Member.Images.Kind == MemberImageSourceKind.Automatic;

    internal static string FactFor(ImportPageSource source, ImportSourceCandidate candidate) =>
        System.Text.Json.JsonSerializer.Serialize(new { candidate.SourceKind, candidate.SourceKey, candidate.Member.TargetInput,
            Identity = candidate.SelectionIdentity, Fact = source.AvailabilityFacts.GetValueOrDefault(candidate.SourceKey) });

    internal bool Matches(ImportPageSource source, ImportSourceCandidate candidate) =>
        AppliesTo(candidate) && SourceFact == FactFor(source, candidate);
}

internal sealed record ImportPageResult(string? Error = null, bool RefreshRequired = false);

internal enum ImportPageMode { Import, Export }

internal sealed record ImportPageExportRequest(ImportPageContext Context, ImportPageSource Source,
    IReadOnlyList<string> SelectedMemberIds, string OutputPath, Func<bool> IsCurrent);
