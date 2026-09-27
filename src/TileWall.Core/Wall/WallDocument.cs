using System.Text.Json.Serialization;
using TileWall.Core.Components;
using TileWall.Core.Grid;
using TileWall.Core.Imaging;

namespace TileWall.Core.Wall;

/// <summary>The sole persisted wall snapshot. Lane and group order, plus member-local cells, determine placement.</summary>
public sealed record WallDocument
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public long Generation { get; init; }
    public required int Columns { get; init; }
    public required int Rows { get; init; }
    public required IReadOnlyList<WallLane> Lanes { get; init; }
    public WallSettings Settings { get; init; } = new();

    public static WallDocument Empty(int columns, int rows) => new()
    {
        Columns = columns,
        Rows = rows,
        Lanes = Enumerable.Range(0, columns).Select(_ => new WallLane { Groups = [] }).ToArray(),
    };
}

public sealed record WallLane
{
    public required IReadOnlyList<StartGroup> Groups { get; init; }
}

public sealed record StartGroup
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public required IReadOnlyList<WallMember> Members { get; init; }
}

public enum AnchorCorner { BottomLeft, BottomRight, TopLeft, TopRight }
public enum WallLanguageMode { System, Chinese }

public sealed record WallSettings
{
    public AnchorCorner AnchorCorner { get; init; } = AnchorCorner.BottomLeft;
    public bool AlwaysOnTop { get; init; } = true;
    public bool StayVisibleOnFocusLoss { get; init; }
    public WallLanguageMode LanguageMode { get; init; } = WallLanguageMode.System;
    public string? HotKey { get; init; } = "Win+Oem3";
    public bool RunAtLogin { get; init; }
}

public enum ManagedEntryKind { Lnk, Url }
public sealed record ManagedEntryReference(string ObjectId, string RelativePath, ManagedEntryKind Kind);

public sealed record MemberVisual
{
    public string? Title { get; init; }
    public bool ShowTitle { get; init; } = true;
    public string? BackgroundColor { get; init; }
    public string? BackgroundImagePath { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(AutoEntryIcon), "auto")]
[JsonDerivedType(typeof(CustomImageIcon), "custom")]
[JsonDerivedType(typeof(NoForegroundIcon), "none")]
public abstract record ForegroundChoice;
public sealed record AutoEntryIcon : ForegroundChoice;
public sealed record CustomImageIcon(string SourcePath) : ForegroundChoice;
public sealed record NoForegroundIcon : ForegroundChoice;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(TileMember), "tile")]
[JsonDerivedType(typeof(PuzzleMember), "puzzle")]
[JsonDerivedType(typeof(ClockMember), "clock")]
public abstract record WallMember
{
    public required string Id { get; init; }
    public required GridRect LocalBounds { get; init; }
    public MemberVisual Visual { get; init; } = new();
    public ManagedEntryReference? Entry { get; init; }
}

public sealed record TileMember : WallMember
{
    public ForegroundChoice Foreground { get; init; } = new AutoEntryIcon();
}

public enum PuzzleImageSourceKind { None, Single, Multiple, Folder }
public sealed record PuzzleImageTransform(string ImageId, FitMode Fit, ImageTransform Transform);
public sealed record PuzzleImageSource
{
    public PuzzleImageSourceKind Kind { get; init; }
    public IReadOnlyList<string> ImagePaths { get; init; } = [];
    public string? FolderPath { get; init; }
    public IReadOnlyList<PuzzleImageTransform> Transforms { get; init; } = [];
}

public sealed record PuzzleCarouselState(string? CurrentImageId, DateTimeOffset? LastSwitchUtc);

public sealed record PuzzleMember : WallMember
{
    public required IReadOnlyList<GridRect> Partitions { get; init; }
    public PuzzleImageSource Images { get; init; } = new();
    public PuzzleCarouselState? Carousel { get; init; }
}

public sealed record ClockMember : WallMember
{
    public string TimeFormat { get; init; } = ClockFormatOptions.DefaultTimeFormat;
    public string DateFormat { get; init; } = ClockFormatOptions.DefaultDateFormat;
}
