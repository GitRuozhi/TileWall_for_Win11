using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TileWall.Core.Storage;
using TileWall.Core.Wall;

namespace TileWall.Core.Exchange;

internal static class WallPackageJson
{
    internal static readonly JsonSerializerOptions Options = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            // Runtime forward data is not part of the wall-package contract.
            foreach (var property in typeInfo.Properties.Where(property => property.IsExtensionData).ToArray())
                typeInfo.Properties.Remove(property);
        });
        return new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 64,
            TypeInfoResolver = resolver,
        };
    }
    private static readonly JsonSerializerOptions V2Options = CreateLegacyOptions(2);
    private static readonly JsonSerializerOptions LegacyOptions = CreateLegacyOptions(4);
    private static readonly JsonSerializerOptions V5Options = CreateLegacyOptions(5);
    private static JsonSerializerOptions CreateLegacyOptions(int version)
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            foreach (var property in typeInfo.Properties.Where(property => property.IsExtensionData).ToArray())
                typeInfo.Properties.Remove(property);
            if (typeInfo.Type == typeof(WallPackageGroup))
                typeInfo.Properties.Remove(typeInfo.Properties.Single(property => property.Name == "reservedRows"));
            if (version < 5 && typeInfo.Type == typeof(WallPackageImageSource))
                typeInfo.Properties.Remove(typeInfo.Properties.Single(property => property.Name == "iconSeries"));
            if (version == 2 && typeInfo.Type == typeof(WallPackageMember))
                foreach (var name in new[] { "sourceShortcutPath", "targetDisplayName" })
                    typeInfo.Properties.Remove(typeInfo.Properties.Single(property => property.Name == name));
        });
        return new(Options) { TypeInfoResolver = resolver };
    }
    internal static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    internal static byte[] Serialize<T>(T value, int formatVersion) =>
        JsonSerializer.SerializeToUtf8Bytes(value, FormatOptions(formatVersion));
    private static JsonSerializerOptions FormatOptions(int version) => version == 2 ? V2Options : version < 5 ? LegacyOptions : version == 5 ? V5Options : Options;
    internal static T Deserialize<T>(byte[] bytes, int formatVersion = WallPackageManifest.CurrentFormatVersion)
    {
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        RejectDuplicateProperties(json.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, FormatOptions(formatVersion)) ?? throw new InvalidDataException("Empty package JSON.");
    }
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit) && hash == hash.ToUpperInvariant();

    internal static WallPackageContent Freeze(WallPackageContent content) => content with
    {
        Lanes = Array.AsReadOnly(content.Lanes.Select(l => l with { GroupIds = Array.AsReadOnly(l.GroupIds.ToArray()) }).ToArray()),
        Groups = Array.AsReadOnly(content.Groups.Select(g => g with { MemberIds = Array.AsReadOnly(g.MemberIds.ToArray()) }).ToArray()),
        Members = Array.AsReadOnly(content.Members.Select(m => m with
        {
            Images = m.Images with { IconSeries = m.Images.IconSeries is { } series
                ? series with { Frames = Array.AsReadOnly(series.Frames.ToArray()) } : null },
            Partitions = m.Partitions is null ? null : Array.AsReadOnly(m.Partitions.ToArray())
        }).ToArray()),
        Galleries = Array.AsReadOnly(content.Galleries.ToArray()),
    };

    internal static string Fingerprint(WallPackageContent content, IEnumerable<WallPackageFile> files,
        int formatVersion = WallPackageManifest.CurrentFormatVersion) =>
        Hash(Serialize(new { Content = content, Assets = files.Where(f => f.Path != "wall.json")
            .OrderBy(f => f.Path, StringComparer.Ordinal).ToArray() }, formatVersion));

    internal static string EntryPath(WallPackageMember member) =>
        $"objects/{member.Id}/entry.{(member.EntryKind == ManagedEntryKind.Lnk ? "lnk" : "url")}";
    internal static string MediaPath(ManagedMediaReference media) => $"media/{media.Sha256}/original";
    internal static string GalleryPath(string id) => $"galleries/{id}/manifest.json";

    internal static void ValidateGalleryExpansion(WallPackageContent content, IEnumerable<ManagedGalleryManifest> source,
        WallPackageLimits limits)
    {
        var singles = content.Members.SelectMany(member => ImageMedia(member.Images))
            .DistinctBy(reference => reference.Sha256, StringComparer.Ordinal).ToArray();
        long count = content.Members.Count(member => member.EntryKind is not null) + singles.Length + content.Galleries.Count + 2L;
        long bytes = singles.Sum(reference => reference.Length);
        foreach (var gallery in source)
        {
            count = checked(count + gallery.Files.Count);
            bytes = checked(bytes + gallery.Files.Sum(file => file.Media.Length));
            if (count > limits.MaximumEntries || bytes > limits.MaximumExpandedBytes)
                throw new InvalidDataException("Managed gallery file expansion exceeds the package budget.");
        }
    }

    internal static IReadOnlyList<ManagedMediaReference> AllMedia(WallPackageContent content,
        IEnumerable<ManagedGalleryManifest> galleries)
    {
        var references = content.Members.SelectMany(member => ImageMedia(member.Images))
            .Concat(galleries.SelectMany(gallery => gallery.Files.Select(file => file.Media))).Distinct().ToArray();
        if (references.Any(reference => !PreparedMediaAsset.IsValidReference(reference)) ||
            references.GroupBy(reference => reference.Sha256, StringComparer.Ordinal).Any(group => group.Select(reference => reference.Length).Distinct().Count() != 1))
            throw new InvalidDataException("Conflicting original-image digest references.");
        return references;
    }

    internal static IEnumerable<ManagedMediaReference> ImageMedia(WallPackageImageSource images) =>
        (images.Media is { } media ? new[] { media } : [])
            .Concat(images.IconSeries?.Frames.Select(frame => frame.Media) ?? []);

    // The injected platform probe validates decoding; this binds native size metadata to the actual PNG header.
    internal static void ValidateSeriesImage(WallPackageContent content, ManagedMediaReference reference, ReadOnlySpan<byte> bytes)
    {
        var frames = content.Members.SelectMany(member => member.Images.IconSeries?.Frames ?? [])
            .Where(frame => frame.Media.Sha256 == reference.Sha256).ToArray();
        if (frames.Length == 0) return;
        if (reference.Extension != ".png" || bytes.Length < 33 ||
            !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(8, 4)) != 13 ||
            !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("An automatic series frame must be a native PNG.");
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4));
        if (frames.Any(frame => frame.Width != width || frame.Height != height))
            throw new InvalidDataException("Automatic series native dimensions differ from its PNG.");
    }

    internal static void ValidateContent(WallPackageContent content, WallPackageLimits? limits = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        limits ??= new(); limits.Validate();
        if (content.Lanes is null || content.Groups is null || content.Members is null || content.Galleries is null ||
            content.Lanes.Any(l => l?.GroupIds is null) || content.Groups.Any(g => g?.MemberIds is null) ||
            content.Members.Any(m => m is null || m.Visual is null || m.Images is null || m.ImageDisplay is null))
            throw new InvalidDataException("Malformed content collections.");
        if (content.Lanes.Count > 64 || content.Groups.Count > limits.MaximumGroups || content.Members.Count > limits.MaximumMembers ||
            content.Galleries.Count > limits.MaximumEntries ||
            content.Lanes.Sum(lane => (long)lane.GroupIds.Count) + content.Groups.Sum(group => (long)group.MemberIds.Count) +
                content.Members.Sum(member => (long)(member.Images.IconSeries?.Frames?.Count ?? 0)) > limits.MaximumReferences)
            throw new InvalidDataException("Wall content exceeds its structure budget.");
        if (content.Galleries.Any(gallery => gallery is null || !WallPackageJson.ValidHash(gallery.Id) || gallery.ManifestSha256 != gallery.Id) ||
            content.Galleries.Select(gallery => gallery.Id).Distinct(StringComparer.Ordinal).Count() != content.Galleries.Count)
            throw new InvalidDataException("Malformed managed-gallery declarations.");
        var referencedGalleries = content.Members.Select(member => member.Images.GalleryId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (!referencedGalleries.SetEquals(content.Galleries.Select(gallery => gallery.Id)))
            throw new InvalidDataException("Managed-gallery reference closure differs.");
        var groupIds = content.Groups.Select(g => g.Id).ToArray();
        var memberIds = content.Members.Select(m => m.Id).ToArray();
        var laneGroups = content.Lanes.SelectMany(l => l.GroupIds).ToArray();
        var groupedMembers = content.Groups.SelectMany(g => g.MemberIds).ToArray();
        if (groupIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != groupIds.Length ||
            memberIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != memberIds.Length ||
            laneGroups.Length != groupIds.Length || laneGroups.Distinct(StringComparer.Ordinal).Count() != groupIds.Length ||
            !laneGroups.ToHashSet(StringComparer.Ordinal).SetEquals(groupIds) ||
            groupedMembers.Length != memberIds.Length || groupedMembers.Distinct(StringComparer.Ordinal).Count() != memberIds.Length ||
            !groupedMembers.ToHashSet(StringComparer.Ordinal).SetEquals(memberIds) ||
            content.Groups.Any(g => g.MemberIds.Count == 0 && g.ReservedRows is not > 0))
            throw new InvalidDataException("Each member must belong to exactly one source group; empty groups require a reserved extent.");
        foreach (var member in content.Members)
        {
            token.ThrowIfCancellationRequested();
            if (member.EntryKind is { } kind && !Enum.IsDefined(kind) ||
                member.Kind == "puzzle" && member.Partitions is null ||
                member.Kind != "puzzle" && member.Partitions is not null ||
                member.Kind != "clock" && (member.TimeFormat is not null || member.DateFormat is not null))
                throw new InvalidDataException("Invalid member type data.");
            if (member.Images.Kind == MemberImageSourceKind.Single && member.Images.Media is null ||
                member.Images.Media is { } media && (member.Images.Kind != MemberImageSourceKind.Single || !PreparedMediaAsset.IsValidReference(media)))
                throw new InvalidDataException("Invalid formal image reference.");
            if (!Enum.IsDefined(member.Images.Kind) || member.Images.Kind == MemberImageSourceKind.None &&
                (member.Images.Path is not null || member.Images.Media is not null || member.Images.GalleryId is not null || member.Images.IconSeries is not null))
                throw new InvalidDataException("Invalid empty image source.");
            if (member.Images.IconSeries is { } series &&
                (member.Images.Kind != MemberImageSourceKind.Automatic || member.Images.Path is not null ||
                 member.Images.Media is not null || member.Images.GalleryId is not null ||
                 string.IsNullOrWhiteSpace(series.Identity) || series.Identity.Length > 32768 || series.Columns <= 0 || series.Rows <= 0 ||
                 series.Frames is null || series.Frames.Count == 0 || series.SelectedFrame < 0 || series.SelectedFrame >= series.Frames.Count ||
                 series.Frames.Any(frame => frame is null || frame.Width <= 0 || frame.Height <= 0 ||
                     !PreparedMediaAsset.IsValidReference(frame.Media) || frame.Media.Extension != ".png") ||
                 series.Frames.Select(frame => (frame.Width, frame.Height)).Distinct().Count() != series.Frames.Count))
                throw new InvalidDataException("Invalid automatic icon series.");
            if (member.SourceShortcutPath is { Length: > 32768 } || member.TargetDisplayName is { Length: > 32768 })
                throw new InvalidDataException("Oversized entry presentation fields.");
            if (member.Images.GalleryId is { } galleryId && (!ValidHash(galleryId) || member.Images.Kind != MemberImageSourceKind.Folder ||
                member.Images.Path is not null || member.Images.Media is not null))
                throw new InvalidDataException("A package gallery must use only a logical source ID.");
        }
        var conflicts = content.Members.SelectMany(m => ImageMedia(m.Images))
            .GroupBy(m => m.Sha256, StringComparer.Ordinal).Any(g => g.Distinct().Count() != 1);
        if (conflicts) throw new InvalidDataException("Conflicting media references.");
        token.ThrowIfCancellationRequested();
        var errors = WallLayoutEngine.ValidateGeometry(content.ToDocument(), token: token);
        if (errors.Count != 0) throw new InvalidDataException(string.Join("; ", errors));
    }
}
