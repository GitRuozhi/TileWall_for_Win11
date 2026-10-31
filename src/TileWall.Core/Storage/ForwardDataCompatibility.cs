using System.Collections.ObjectModel;
using System.Text.Json;
using TileWall.Core.Wall;

namespace TileWall.Core.Storage;

/// <summary>Only envelope provenance, never a second copy of the wall or retained members.</summary>
internal sealed record UserDataFormatMetadata(
    int ManifestVersion, int SettingsVersion, int WallVersion, int RecycleBinVersion,
    IDictionary<string, JsonElement>? ManifestFields,
    IDictionary<string, JsonElement>? SettingsDigestFields,
    IDictionary<string, JsonElement>? WallDigestFields,
    IDictionary<string, JsonElement>? RecycleBinDigestFields,
    IDictionary<string, JsonElement>? SettingsFields,
    IDictionary<string, JsonElement>? WallFields,
    IDictionary<string, JsonElement>? RecycleBinFields);

internal sealed class UnsupportedSnapshotStructureException(string message, Exception? inner = null)
    : IOException(message, inner);

/// <summary>Additive fields stay with surviving IDs; lane fields belong to their logical index.</summary>
internal static class ForwardDataCompatibility
{
    internal static bool FieldsEqual(IDictionary<string, JsonElement>? left, IDictionary<string, JsonElement>? right) =>
        (left?.Count ?? 0) == (right?.Count ?? 0) && (left is null || left.All(pair =>
            right is not null && right.TryGetValue(pair.Key, out var value) && JsonElement.DeepEquals(pair.Value, value)));

    internal static bool MetadataEqual(UserDataFormatMetadata? left, UserDataFormatMetadata? right) =>
        (left?.ManifestVersion ?? UserDataManifest.CurrentFormatVersion) == (right?.ManifestVersion ?? UserDataManifest.CurrentFormatVersion) &&
        (left?.SettingsVersion ?? UserDataManifest.CurrentFormatVersion) == (right?.SettingsVersion ?? UserDataManifest.CurrentFormatVersion) &&
        (left?.WallVersion ?? 1) == (right?.WallVersion ?? 1) &&
        (left?.RecycleBinVersion ?? 1) == (right?.RecycleBinVersion ?? 1) &&
        FieldsEqual(left?.ManifestFields, right?.ManifestFields) &&
        FieldsEqual(left?.SettingsDigestFields, right?.SettingsDigestFields) &&
        FieldsEqual(left?.WallDigestFields, right?.WallDigestFields) &&
        FieldsEqual(left?.RecycleBinDigestFields, right?.RecycleBinDigestFields) &&
        FieldsEqual(left?.SettingsFields, right?.SettingsFields) &&
        FieldsEqual(left?.WallFields, right?.WallFields) &&
        FieldsEqual(left?.RecycleBinFields, right?.RecycleBinFields);

    internal static IDictionary<string, JsonElement>? FreezeFields(IDictionary<string, JsonElement>? fields) =>
        fields is null || fields.Count == 0 ? null : new ReadOnlyDictionary<string, JsonElement>(
            fields.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal));

    internal static UserDataFormatMetadata? FreezeMetadata(UserDataFormatMetadata? value) => value is null ? null : value with
    {
        ManifestFields = FreezeFields(value.ManifestFields), SettingsFields = FreezeFields(value.SettingsFields),
        WallFields = FreezeFields(value.WallFields), RecycleBinFields = FreezeFields(value.RecycleBinFields),
        SettingsDigestFields = FreezeFields(value.SettingsDigestFields), WallDigestFields = FreezeFields(value.WallDigestFields),
        RecycleBinDigestFields = FreezeFields(value.RecycleBinDigestFields),
    };

    internal static ManagedMediaReference? MergeMedia(ManagedMediaReference? stored, ManagedMediaReference? proposed) =>
        proposed is null || stored != proposed ? proposed : proposed with
        { ExtensionData = stored!.ExtensionData };

    private static ManagedGalleryReference? MergeGallery(ManagedGalleryReference? stored, ManagedGalleryReference? proposed) =>
        proposed is null || stored != proposed ? proposed : proposed with
        { ExtensionData = stored!.ExtensionData };

    private static ImportSourceMetadata? MergeSource(ImportSourceMetadata? stored, ImportSourceMetadata? proposed) =>
        proposed is null || stored is null || stored.BatchId != proposed.BatchId ||
        stored.GroupBucketKey != proposed.GroupBucketKey || stored.OriginGroupId != proposed.OriginGroupId ? proposed : proposed with
        { ExtensionData = stored.ExtensionData };

    internal static WallDocument Merge(WallDocument stored, WallDocument proposed)
    {
        // Invalid proposals still belong to the existing planner's rejection path.
        if (proposed.Settings is null || proposed.Lanes is null || proposed.InactiveMembers is null ||
            proposed.Lanes.Any(lane => lane?.Groups is null || lane.Groups.Any(group => group?.Members is null)) ||
            proposed.EnumerateAllMembers().Any(member => member is null || member.Images is null || member.Visual is null ||
                member.ImageDisplay is null || member.Images.IconSeries is { } series &&
                    (series.Frames is null || series.Frames.Any(frame => frame is null)) ||
                member.Carousel is { } carousel && (carousel.CandidateIds is null || carousel.Queue is null) ||
                member is PuzzleMember { Partitions: null })) return proposed;
        var groups = stored.Lanes.SelectMany(lane => lane.Groups).ToDictionary(group => group.Id, StringComparer.Ordinal);
        var members = stored.EnumerateAllMembers().ToDictionary(member => member.Id, StringComparer.Ordinal);
        WallMember MergeMember(WallMember value)
        {
            if (!members.TryGetValue(value.Id, out var old)) return ClearNewMemberFields(value);
            var images = value.Images with
            {
                ExtensionData = old.Images.ExtensionData,
                Media = MergeMedia(old.Images.Media, value.Images.Media),
                ManagedGallery = MergeGallery(old.Images.ManagedGallery, value.Images.ManagedGallery),
            };
            if (images.IconSeries is { } series && old.Images.IconSeries is { } oldSeries && series.Identity == oldSeries.Identity)
                images = images with { IconSeries = series with
                {
                    ExtensionData = oldSeries.ExtensionData,
                    Frames = series.Frames.Select(frame =>
                    {
                        var previous = oldSeries.Frames.FirstOrDefault(candidate => candidate.Media == frame.Media &&
                            candidate.Width == frame.Width && candidate.Height == frame.Height);
                        return previous is null ? frame : frame with
                        {
                            ExtensionData = previous.ExtensionData,
                            Media = MergeMedia(previous.Media, frame.Media)!,
                        };
                    }).ToArray(),
                } };
            return value with
            {
                ExtensionData = old.ExtensionData,
                Visual = value.Visual with { ExtensionData = old.Visual.ExtensionData },
                Images = images,
                ImageDisplay = value.ImageDisplay with
                    { ExtensionData = old.ImageDisplay.ExtensionData },
                Entry = value.Entry is { } entry && old.Entry == entry ? entry with
                    { ExtensionData = old.Entry!.ExtensionData } : value.Entry,
                Carousel = value.Carousel is { } carousel && old.Carousel is { } oldCarousel ? carousel with
                    { ExtensionData = oldCarousel.ExtensionData } : value.Carousel,
                ImportSource = MergeSource(old.ImportSource, value.ImportSource),
            };
        }
        return WallJson.Freeze(proposed with
        {
            StorageMetadata = stored.StorageMetadata,
            ExtensionData = stored.ExtensionData,
            Settings = proposed.Settings with
            {
                ExtensionData = stored.Settings.ExtensionData,
                BackgroundMedia = MergeMedia(stored.Settings.BackgroundMedia, proposed.Settings.BackgroundMedia),
            },
            Lanes = proposed.Lanes.Select((lane, index) => lane with
            {
                ExtensionData = index < stored.Lanes.Count ? stored.Lanes[index].ExtensionData : null,
                Groups = lane.Groups.Select(group =>
                {
                    groups.TryGetValue(group.Id, out var old);
                    return group with
                    {
                        ExtensionData = old?.ExtensionData,
                        ImportSource = MergeSource(old?.ImportSource, group.ImportSource),
                        Members = group.Members.Select(MergeMember).ToArray(),
                    };
                }).ToArray(),
            }).ToArray(),
            InactiveMembers = proposed.InactiveMembers.Select(MergeMember).ToArray(),
        });
    }

    private static WallMember ClearNewMemberFields(WallMember member) => member with
    {
        ExtensionData = null,
        Visual = member.Visual with { ExtensionData = null },
        ImageDisplay = member.ImageDisplay with { ExtensionData = null },
        Entry = member.Entry is { } entry ? entry with { ExtensionData = null } : null,
        ImportSource = member.ImportSource is { } source ? source with { ExtensionData = null } : null,
        Carousel = member.Carousel is { } carousel ? carousel with { ExtensionData = null } : null,
        Images = member.Images with
        {
            ExtensionData = null,
            Media = member.Images.Media is { } media ? media with { ExtensionData = null } : null,
            ManagedGallery = member.Images.ManagedGallery is { } gallery ? gallery with { ExtensionData = null } : null,
            IconSeries = member.Images.IconSeries is { } series ? series with
            {
                ExtensionData = null,
                Frames = series.Frames.Select(frame => frame with
                {
                    ExtensionData = null,
                    Media = frame.Media is { } frameMedia ? frameMedia with { ExtensionData = null } : null!,
                }).ToArray(),
            } : null,
        },
    };

    internal static bool HasUnknownData(WallDocument document)
    {
        static bool Any(IDictionary<string, JsonElement>? fields) => fields is { Count: > 0 };
        static bool Source(ImportSourceMetadata? source) => source?.ExtensionData is { Count: > 0 };
        if (Any(document.ExtensionData) || Any(document.Settings.ExtensionData) || Any(document.Settings.BackgroundMedia?.ExtensionData)) return true;
        if (document.StorageMetadata is { } metadata &&
            new[] { metadata.ManifestFields, metadata.SettingsDigestFields, metadata.WallDigestFields,
                metadata.RecycleBinDigestFields, metadata.SettingsFields, metadata.WallFields, metadata.RecycleBinFields }.Any(Any)) return true;
        foreach (var lane in document.Lanes)
        {
            if (Any(lane.ExtensionData)) return true;
            foreach (var group in lane.Groups)
                if (Any(group.ExtensionData) || Source(group.ImportSource)) return true;
        }
        foreach (var member in document.EnumerateAllMembers())
        {
            if (Any(member.ExtensionData) || Any(member.Visual.ExtensionData) || Any(member.ImageDisplay.ExtensionData) ||
                Any(member.Entry?.ExtensionData) || Any(member.Images.ExtensionData) || Any(member.Images.Media?.ExtensionData) ||
                Any(member.Images.ManagedGallery?.ExtensionData) || Any(member.Carousel?.ExtensionData) || Source(member.ImportSource)) return true;
            if (member.Images.IconSeries is { } series && (Any(series.ExtensionData) ||
                series.Frames.Any(frame => Any(frame.ExtensionData) || Any(frame.Media.ExtensionData)))) return true;
        }
        return false;
    }
}
