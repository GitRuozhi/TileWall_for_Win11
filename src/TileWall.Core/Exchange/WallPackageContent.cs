using TileWall.Core.Components;
using TileWall.Core.Grid;
using TileWall.Core.Imaging;
using TileWall.Core.Storage;
using TileWall.Core.Wall;

namespace TileWall.Core.Exchange;

// Deliberately independent of the persisted document: importing a package only restores
// active wall content, not settings, inactive objects, generation, or derived state.
public sealed record WallPackageContent
{
    public required int Columns { get; init; }
    public required int Rows { get; init; }
    public required IReadOnlyList<WallPackageLane> Lanes { get; init; }
    public required IReadOnlyList<WallPackageGroup> Groups { get; init; }
    public required IReadOnlyList<WallPackageMember> Members { get; init; }
    public IReadOnlyList<WallPackageGalleryReference> Galleries { get; init; } = [];

    /// <summary>Project only selected active members; source lanes and local geometry stay intact.</summary>
    public static WallPackageContent FromSelection(WallDocument document, IEnumerable<string> selectedMemberIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selectedMemberIds);
        var selected = selectedMemberIds.ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0 || selected.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Select at least one active wall member.");
        var active = document.Lanes.SelectMany(lane => lane.Groups).SelectMany(group => group.Members)
            .ToDictionary(member => member.Id, StringComparer.Ordinal);
        if (selected.Any(id => !active.ContainsKey(id)))
            throw new InvalidDataException("Selected member no longer belongs to the active wall.");
        var lanes = document.Lanes.Select(lane => new WallPackageLane(lane.Groups
            .Where(group => group.Members.Any(member => selected.Contains(member.Id))).Select(group => group.Id).ToArray())).ToArray();
        var groups = document.Lanes.SelectMany(lane => lane.Groups).Where(group => group.Members.Any(member => selected.Contains(member.Id)))
            .Select(group => new WallPackageGroup(group.Id, group.Title, group.Members.Where(member => selected.Contains(member.Id)).Select(member => member.Id).ToArray())).ToArray();
        return WallPackageJson.Freeze(new()
        {
            Columns = document.Columns, Rows = document.Rows, Lanes = lanes, Groups = groups,
            Members = document.Lanes.SelectMany(lane => lane.Groups).SelectMany(group => group.Members)
                .Where(member => selected.Contains(member.Id)).Select(WallPackageMember.FromMember).ToArray(),
            Galleries = document.Lanes.SelectMany(lane => lane.Groups).SelectMany(group => group.Members)
                .Where(member => selected.Contains(member.Id)).Select(member => member.Images.ManagedGallery)
                .OfType<ManagedGalleryReference>().Distinct().OrderBy(gallery => gallery.ContentId, StringComparer.Ordinal)
                .Select(gallery => new WallPackageGalleryReference(gallery.ContentId, gallery.ManifestSha256)).ToArray(),
        });
    }

    public WallDocument ToDocument()
    {
        var members = Members.ToDictionary(m => m.Id, m => m.ToMember(), StringComparer.Ordinal);
        var groups = Groups.ToDictionary(g => g.Id, g => new StartGroup
        {
            Id = g.Id, Title = g.Title, Members = g.MemberIds.Select(id => members[id]).ToArray(),
        }, StringComparer.Ordinal);
        return WallJson.Freeze(new WallDocument
        {
            Columns = Columns, Rows = Rows,
            Lanes = Lanes.Select(l => new WallLane { Groups = l.GroupIds.Select(id => groups[id]).ToArray() }).ToArray(),
        });
    }
}

public sealed record WallPackageLane(IReadOnlyList<string> GroupIds);
public sealed record WallPackageGroup(string Id, string? Title, IReadOnlyList<string> MemberIds);
public sealed record WallPackageGalleryReference(string Id, string ManifestSha256);

/// <summary>Exchange projection: a managed folder is a logical package ID, never a sender path.</summary>
public sealed record WallPackageImageSource
{
    public MemberImageSourceKind Kind { get; init; } = MemberImageSourceKind.Automatic;
    public string? Path { get; init; }
    public ManagedMediaReference? Media { get; init; }
    public string? GalleryId { get; init; }
    internal static WallPackageImageSource From(MemberImageSource source) => new()
    {
        Kind = source.Kind, Media = source.Media,
        Path = source.ManagedGallery is null && source.Media is null ? source.Path : null,
        GalleryId = source.ManagedGallery?.ContentId,
    };
    internal MemberImageSource ToSource() => new()
    {
        Kind = Kind, Path = GalleryId is null ? Path : null, Media = Media,
        ManagedGallery = GalleryId is { } id ? new(id, id) : null,
    };
}
public readonly record struct WallPackageBounds(int Column, int Row, int Width, int Height)
{
    public static implicit operator GridRect(WallPackageBounds value) => new(value.Column, value.Row, value.Width, value.Height);
    public static implicit operator WallPackageBounds(GridRect value) => new(value.Column, value.Row, value.Width, value.Height);
}

public sealed record WallPackageMember
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required WallPackageBounds LocalBounds { get; init; }
    public MemberVisual Visual { get; init; } = new();
    public ManagedEntryKind? EntryKind { get; init; }
    public string? TargetInput { get; init; }
    public string? SourceShortcutPath { get; init; }
    public string? TargetDisplayName { get; init; }
    public WallPackageImageSource Images { get; init; } = new();
    public MemberImageDisplay ImageDisplay { get; init; } = new();
    public ImageRotationInterval RotationInterval { get; init; } = ImageRotationInterval.Minute1;
    public IReadOnlyList<WallPackageBounds>? Partitions { get; init; }
    public string? TimeFormat { get; init; }
    public string? DateFormat { get; init; }

    internal static WallPackageMember FromMember(WallMember member) => new()
    {
        Id = member.Id,
        Kind = member switch { TileMember => "tile", PuzzleMember => "puzzle", ClockMember => "clock", _ => throw new InvalidDataException("Unknown member kind.") },
        LocalBounds = member.LocalBounds, Visual = member.Visual, EntryKind = member.Entry?.Kind,
        TargetInput = member.TargetInput, SourceShortcutPath = member.SourceShortcutPath,
        TargetDisplayName = member.TargetDisplayName ?? MemberPresentationRules.AutomaticTitle(member.SourceShortcutPath ?? member.TargetInput),
        Images = WallPackageImageSource.From(member.Images), ImageDisplay = member.ImageDisplay,
        RotationInterval = member.RotationInterval,
        Partitions = member is PuzzleMember puzzle ? puzzle.Partitions.Select(part => (WallPackageBounds)part).ToArray() : null,
        TimeFormat = member is ClockMember clock ? clock.TimeFormat : null,
        DateFormat = member is ClockMember clock2 ? clock2.DateFormat : null,
    };

    public WallMember ToMember(string? remappedId = null)
    {
        var id = remappedId ?? Id;
        WallMember member = Kind switch
        {
            "tile" => new TileMember { Id = id, LocalBounds = LocalBounds },
            "puzzle" => new PuzzleMember { Id = id, LocalBounds = LocalBounds, Partitions = Partitions?.Select(p => (GridRect)p).ToArray() ?? throw new InvalidDataException("Missing partitions.") },
            "clock" => new ClockMember { Id = id, LocalBounds = LocalBounds, TimeFormat = TimeFormat ?? ClockFormatOptions.DefaultTimeFormat, DateFormat = DateFormat ?? ClockFormatOptions.DefaultDateFormat },
            _ => throw new InvalidDataException("Unknown member kind."),
        };
        return member with
        {
            Visual = Visual, Entry = EntryKind is { } kind ? new(id, $"Objects/{id}/entry.{(kind == ManagedEntryKind.Lnk ? "lnk" : "url")}", kind) : null,
            TargetInput = TargetInput, SourceShortcutPath = SourceShortcutPath, TargetDisplayName = TargetDisplayName,
            ResourceOrigin = MemberResourceOrigin.Package,
            Images = Images.Kind == MemberImageSourceKind.Automatic ? new() { Kind = MemberImageSourceKind.None } : Images.ToSource(),
            ImageDisplay = ImageDisplay, RotationInterval = RotationInterval,
        };
    }
}
