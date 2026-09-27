using System.Collections.ObjectModel;
using System.Text.Json;
using TileWall.Core.Wall;

namespace TileWall.Core.Storage;

/// <summary>Schema-two document serialization and immutable storage boundary.</summary>
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
        if (document.Lanes is null || document.Lanes.Any(l => l?.Groups is null ||
            l.Groups.Any(g => g?.Members is null || g.Members.Any(m => m is null ||
                m is PuzzleMember p && (p.Images is null || p.Partitions is null ||
                    p.Images.ImagePaths is null || p.Images.Transforms is null)))))
            throw new JsonException("Malformed wall document collection.");
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
        }))
    };

    private static WallMember FreezeMember(WallMember member) => member switch
    {
        PuzzleMember puzzle => puzzle with
        {
            Partitions = ReadOnly(puzzle.Partitions),
            Images = puzzle.Images with
            {
                ImagePaths = ReadOnly(puzzle.Images.ImagePaths),
                Transforms = ReadOnly(puzzle.Images.Transforms)
            }
        },
        TileMember tile => tile with { },
        ClockMember clock => clock with { },
        _ => throw new JsonException("Unknown member type.")
    };

    private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> items) =>
        new ReadOnlyCollection<T>(items.ToArray());
}
