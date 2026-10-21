using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Import;
using TileWall.Core.Shell;
using TileWall.Core.Wall;

namespace TileWall.ApplicationModel;

internal sealed record ImportBatchItem(string SourcePath, TileMember Member, ExplorerAddKind Kind)
{
    public string? GroupBucketKey { get; init; }
    public string? GroupTitle { get; init; }
    public int GroupOrder { get; init; }
    public string? SourceRoot { get; init; }
    public string? RelativePath { get; init; }
    public StartMenuCandidate? CandidateSnapshot { get; init; }
    public string? ExpectedIdentityKey { get; init; }
    public string? ExpectedSourceSha256 { get; init; }
    public string? IconDirectory { get; init; }
    public string? IconKey { get; init; }

    public static ImportBatchItem Create(string sourcePath, string? displayName = null, int size = 2)
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
        var name = displayName ?? (Directory.Exists(path)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) : Path.GetFileNameWithoutExtension(path));
        var errors = EntryNames.Validate(name);
        if (errors.Count != 0) throw new IOException($"{path}: {string.Join("; ", errors)}");
        return new(path, new TileMember
        {
            Id = $"tile-{Guid.NewGuid():N}", LocalBounds = new GridRect(0, 0, size, size),
            Visual = new MemberVisual { Title = name },
            TargetInput = path,
        }, kind);
    }

    public bool StillValid() => ExplorerAddPathClassifier.Classify(SourcePath) == Kind;
}

internal sealed record ExternalImportResult(string Message, bool Succeeded, int AddedCount);
