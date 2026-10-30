using System.Collections.ObjectModel;
using System.Text.Json;
using TileWall.Core.Wall;

namespace TileWall.Core.Storage;

/// <summary>Current-format document serialization and immutable storage boundary.</summary>
public static class WallJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static byte[] Serialize(WallDocument document) => JsonSerializer.SerializeToUtf8Bytes(document, Options);

    public static WallDocument Deserialize(byte[] bytes)
    {
        var document = JsonSerializer.Deserialize<WallDocument>(bytes, Options)
            ?? throw new JsonException("Empty wall document.");
        if (document.SchemaVersion is not (4 or WallDocument.CurrentSchemaVersion))
            throw new JsonException("Unsupported wall document schema.");
        if (document.Lanes is null || document.InactiveMembers is null || document.Lanes.Any(l => l?.Groups is null ||
            l.Groups.Any(g => g?.Members is null || g.Members.Any(m => m is null ||
                m.Images is null || m.Images.IconSeries is { Frames: null } || m.ImageDisplay is null || m.Visual is null ||
                m.Carousel is { } carousel && (carousel.CandidateIds is null || carousel.Queue is null) ||
                m is PuzzleMember p && p.Partitions is null))) ||
            document.InactiveMembers.Any(m => m is null || m.Images is null || m.Images.IconSeries is { Frames: null } || m.ImageDisplay is null ||
                m.Visual is null || m.Carousel is { } carousel && (carousel.CandidateIds is null || carousel.Queue is null) ||
                m is PuzzleMember p && p.Partitions is null))
            throw new JsonException("Malformed wall document collection.");
        if (document.SchemaVersion == 4 && document.EnumerateAllMembers().Any(member => member.Images.IconSeries is not null))
            throw new JsonException("Automatic icon series require the current wall schema.");
        document = document with { SchemaVersion = WallDocument.CurrentSchemaVersion };
        return Freeze(document);
    }

    public static WallDocument Freeze(WallDocument document) => document with
    {
        Lanes = ReadOnly(document.Lanes.Select(lane => lane with
        {
            Groups = ReadOnly(lane.Groups.Select(group => group with
            {
                Members = ReadOnly(group.Members.Select(FreezeMember))
            }))
        })),
        InactiveMembers = ReadOnly(document.InactiveMembers.Select(FreezeMember))
    };

    private static WallMember FreezeMember(WallMember member)
    {
        var images = member.Images.IconSeries is { } series
            ? member.Images with { IconSeries = series with { Frames = ReadOnly(series.Frames) } } : member.Images;
        var carousel = member.Carousel is { } state ? state with
        {
            CandidateIds = ReadOnly(state.CandidateIds), Queue = ReadOnly(state.Queue)
        } : null;
        return member switch
        {
            PuzzleMember puzzle => puzzle with { Partitions = ReadOnly(puzzle.Partitions), Carousel = carousel, Images = images },
            TileMember tile => tile with { Carousel = carousel, Images = images },
            ClockMember clock => clock with { Carousel = carousel, Images = images },
            _ => throw new JsonException("Unknown member type.")
        };
    }

    private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> items) =>
        new ReadOnlyCollection<T>(items.ToArray());
}
