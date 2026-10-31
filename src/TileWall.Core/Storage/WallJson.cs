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
        return ValidateAndFreeze(document);
    }

    internal static WallDocument ValidateAndFreeze(WallDocument document)
    {
        if (document.SchemaVersion is not (4 or WallDocument.CurrentSchemaVersion))
            throw new JsonException("Unsupported wall document schema.");
        if (document.Settings is null || document.Lanes is null || document.InactiveMembers is null || document.Lanes.Any(l => l?.Groups is null ||
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
        ExtensionData = ForwardDataCompatibility.FreezeFields(document.ExtensionData),
        StorageMetadata = ForwardDataCompatibility.FreezeMetadata(document.StorageMetadata),
        Settings = document.Settings with
        {
            ExtensionData = ForwardDataCompatibility.FreezeFields(document.Settings.ExtensionData),
            BackgroundMedia = FreezeMedia(document.Settings.BackgroundMedia),
        },
        Lanes = ReadOnly(document.Lanes.Select(lane => lane with
        {
            ExtensionData = ForwardDataCompatibility.FreezeFields(lane.ExtensionData),
            Groups = ReadOnly(lane.Groups.Select(group => group with
            {
                ExtensionData = ForwardDataCompatibility.FreezeFields(group.ExtensionData),
                ImportSource = FreezeSource(group.ImportSource),
                Members = ReadOnly(group.Members.Select(FreezeMember))
            }))
        })),
        InactiveMembers = ReadOnly(document.InactiveMembers.Select(FreezeMember))
    };

    private static ManagedMediaReference? FreezeMedia(ManagedMediaReference? media) => media is null ? null : media with
        { ExtensionData = ForwardDataCompatibility.FreezeFields(media.ExtensionData) };

    private static ImportSourceMetadata? FreezeSource(ImportSourceMetadata? source) => source is null ? null : source with
        { ExtensionData = ForwardDataCompatibility.FreezeFields(source.ExtensionData) };

    private static WallMember FreezeMember(WallMember member)
    {
        var images = member.Images with
        {
            ExtensionData = ForwardDataCompatibility.FreezeFields(member.Images.ExtensionData),
            Media = FreezeMedia(member.Images.Media),
            ManagedGallery = member.Images.ManagedGallery is { } gallery ? gallery with
                { ExtensionData = ForwardDataCompatibility.FreezeFields(gallery.ExtensionData) } : null,
        };
        if (images.IconSeries is { } series)
            images = images with { IconSeries = series with
            {
                ExtensionData = ForwardDataCompatibility.FreezeFields(series.ExtensionData),
                Frames = ReadOnly(series.Frames.Select(frame => frame is null ? frame! : frame with
                {
                    ExtensionData = ForwardDataCompatibility.FreezeFields(frame.ExtensionData),
                    Media = FreezeMedia(frame.Media)!,
                })),
            } };
        var carousel = member.Carousel is { } state ? state with
        {
            ExtensionData = ForwardDataCompatibility.FreezeFields(state.ExtensionData),
            CandidateIds = ReadOnly(state.CandidateIds), Queue = ReadOnly(state.Queue)
        } : null;
        var frozen = member with
        {
            ExtensionData = ForwardDataCompatibility.FreezeFields(member.ExtensionData),
            Visual = member.Visual with { ExtensionData = ForwardDataCompatibility.FreezeFields(member.Visual.ExtensionData) },
            ImageDisplay = member.ImageDisplay with
                { ExtensionData = ForwardDataCompatibility.FreezeFields(member.ImageDisplay.ExtensionData) },
            Entry = member.Entry is { } entry ? entry with
                { ExtensionData = ForwardDataCompatibility.FreezeFields(entry.ExtensionData) } : null,
            ImportSource = FreezeSource(member.ImportSource),
            Carousel = carousel, Images = images,
        };
        return frozen switch
        {
            PuzzleMember puzzle => puzzle with { Partitions = ReadOnly(puzzle.Partitions) },
            TileMember or ClockMember => frozen,
            _ => throw new JsonException("Unknown member type.")
        };
    }

    private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> items) =>
        new ReadOnlyCollection<T>(items.ToArray());
}
