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
    public MemberImageSource Images { get; init; } = new();
    public MemberImageDisplay ImageDisplay { get; init; } = new();
    public ImageRotationInterval RotationInterval { get; init; } = ImageRotationInterval.Minute1;
    public IReadOnlyList<WallPackageBounds>? Partitions { get; init; }
    public string? TimeFormat { get; init; }
    public string? DateFormat { get; init; }

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
            TargetInput = TargetInput, Images = Images, ImageDisplay = ImageDisplay, RotationInterval = RotationInterval,
        };
    }
}
