using System.Text.Json.Serialization;
using TileWall.Core.Components;
using TileWall.Core.Grid;
using TileWall.Core.Imaging;

namespace TileWall.Core.Wall;

/// <summary>Wall state. Storage uses a temporary joint document; the session retains only its active view.</summary>
public sealed record WallDocument
{
    public const int CurrentSchemaVersion = 4;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public long Generation { get; init; }
    public required int Columns { get; init; }
    public required int Rows { get; init; }
    public required IReadOnlyList<WallLane> Lanes { get; init; }
    public IReadOnlyList<WallMember> InactiveMembers { get; init; } = [];
    public bool RecycleBinEnabled { get; init; } = true;
    public WallSettings Settings { get; init; } = new();

    /// <summary>Releases retained members before a document becomes session or ordinary UI state.</summary>
    public WallDocument ActiveView() => InactiveMembers.Count == 0 ? this : this with { InactiveMembers = [] };

    /// <summary>All formally owned members, including retained unpinned content.</summary>
    public IEnumerable<WallMember> EnumerateAllMembers() =>
        Lanes.SelectMany(lane => lane.Groups).SelectMany(group => group.Members).Concat(InactiveMembers);

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
    public string? ImportBatchId { get; init; }
}

public sealed record StartGroup
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public required IReadOnlyList<WallMember> Members { get; init; }
    public ImportSourceMetadata? ImportSource { get; init; }
    public bool UserClaimed { get; init; }
}

public enum AnchorCorner { BottomLeft, BottomRight, TopLeft, TopRight }
public enum WallLanguageMode { System = 0, Chinese = 1, English = 2 }
public enum WallVisualStyle { Win10 = 0, Win11 = 1 }
public enum WallThemeMode { System = 0, Light = 1, Dark = 2 }
public enum WallBackgroundMode { Acrylic = 0, Mica = 1, Custom = 2 }

public sealed record WallSettings
{
    public AnchorCorner AnchorCorner { get; init; } = AnchorCorner.BottomLeft;
    public bool FullScreen { get; init; }
    public bool ExpandColumns { get; init; }
    public bool MenuStart { get; init; } = true;
    public int? WindowedColumns { get; init; }
    public int? WindowedRows { get; init; }
    /// <summary>Visible window client width, including the fixed sidebar and optional menu.</summary>
    public double? WindowedViewportWidthDip { get; init; }
    /// <summary>Last successfully committed manual window width; automatic sizing does not update it.</summary>
    public double? ManualWindowedViewportWidthDip { get; init; }
    public WallVisualStyle VisualStyle { get; init; } = WallVisualStyle.Win10;
    public WallThemeMode ThemeMode { get; init; } = WallThemeMode.System;
    public WallBackgroundMode BackgroundMode { get; init; } = WallBackgroundMode.Acrylic;
    public int BackgroundTransparency { get; init; } = 5;
    public string? BackgroundImagePath { get; init; }
    public ManagedMediaReference? BackgroundMedia { get; init; }
    public bool AlwaysOnTop { get; init; } = true;
    public bool StayVisibleOnFocusLoss { get; init; }
    public bool ShowGrid { get; init; }
    public WallLanguageMode LanguageMode { get; init; } = WallLanguageMode.System;
    public string? HotKey { get; init; } = "Win+Oem3";
    public string SecondaryHotKey { get; init; } = "Win+Ctrl+Oem3";
    public bool ReplaceStartMenu { get; init; }
    public bool RunAtLogin { get; init; }
}

public enum ManagedEntryKind { Lnk, Url }
public sealed record ManagedEntryReference(string ObjectId, string RelativePath, ManagedEntryKind Kind);

/// <summary>An immutable original whose SHA-256 is also its asset directory identity.</summary>
public sealed record ManagedMediaReference(string Sha256, string Extension, long Length);

/// <summary>Content-addressed, immutable application-owned gallery. Its path is derived by storage.</summary>
public sealed record ManagedGalleryReference(string ContentId, string ManifestSha256);

public sealed record MemberVisual
{
    public MemberTitleMode TitleMode { get; init; } = MemberTitleMode.Automatic;
    public string? Title { get; init; }
    public bool ShowTitle { get; init; } = true;
    public MemberBackgroundMode BackgroundMode { get; init; } = MemberBackgroundMode.Default;
    public int BackgroundTransparency { get; init; } = 50;
    public string? BackgroundColor { get; init; }
}

public enum MemberTitleMode { Automatic, Manual }
public enum MemberBackgroundMode { Default, Color, Blur, None }
public enum MemberImageSourceKind { Automatic, Single, Folder, None }
public enum MemberResourceOrigin { Local, Package }
public enum ImageRotationInterval { Seconds5 = 5, Seconds15 = 15, Seconds30 = 30, Minute1 = 60,
    Minute5 = 300, Minute15 = 900, Minute30 = 1800 }

/// <summary>One source and display policy for a complete tile or puzzle canvas.</summary>
public sealed record MemberImageSource
{
    public MemberImageSourceKind Kind { get; init; } = MemberImageSourceKind.Automatic;
    public string? Path { get; init; }
    public ManagedMediaReference? Media { get; init; }
    public ManagedGalleryReference? ManagedGallery { get; init; }
}

/// <summary>Queue position is persisted only when the asynchronously prepared image is adopted.</summary>
public sealed record ImageCarouselState
{
    public string? CurrentImageId { get; init; }
    public DateTimeOffset? LastSwitchUtc { get; init; }
    public IReadOnlyList<string> CandidateIds { get; init; } = [];
    public IReadOnlyList<string> Queue { get; init; } = [];
    public int NextIndex { get; init; }
    public long Round { get; init; }
}

public enum ImportSourceKind { StartMenu }
public sealed record ImportSourceMetadata
{
    public ImportSourceKind Kind { get; init; } = ImportSourceKind.StartMenu;
    public required string BatchId { get; init; }
    public required string GroupBucketKey { get; init; }
    public required string OriginGroupId { get; init; }
    // Group metadata spans both roots. Member metadata identifies the exact original source.
    public string? SourceRoot { get; init; }
    public string? RelativePath { get; init; }
}

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
    public string? TargetInput { get; init; }
    public string? SourceShortcutPath { get; init; }
    public string? TargetDisplayName { get; init; }
    public MemberResourceOrigin ResourceOrigin { get; init; } = MemberResourceOrigin.Local;
    public MemberImageSource Images { get; init; } = new();
    public MemberImageDisplay ImageDisplay { get; init; } = new();
    public ImageRotationInterval RotationInterval { get; init; } = ImageRotationInterval.Minute1;
    public ImageCarouselState? Carousel { get; init; }
    public ImportSourceMetadata? ImportSource { get; init; }
    public bool UserClaimed { get; init; }
}

public sealed record TileMember : WallMember;

public sealed record PuzzleMember : WallMember
{
    public required IReadOnlyList<GridRect> Partitions { get; init; }
}

public sealed record ClockMember : WallMember
{
    public string TimeFormat { get; init; } = ClockFormatOptions.DefaultTimeFormat;
    public string DateFormat { get; init; } = ClockFormatOptions.DefaultDateFormat;
}
