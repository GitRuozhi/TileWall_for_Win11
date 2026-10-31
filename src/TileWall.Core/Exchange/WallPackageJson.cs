using System.Security.Cryptography;
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
    private static readonly JsonSerializerOptions V2Options = CreateV2Options();
    private static JsonSerializerOptions CreateV2Options()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            foreach (var property in typeInfo.Properties.Where(property => property.IsExtensionData).ToArray())
                typeInfo.Properties.Remove(property);
            if (typeInfo.Type != typeof(WallPackageMember)) return;
            foreach (var name in new[] { "sourceShortcutPath", "targetDisplayName" })
                typeInfo.Properties.Remove(typeInfo.Properties.Single(property => property.Name == name));
        });
        return new(Options) { TypeInfoResolver = resolver };
    }
    internal static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    internal static byte[] Serialize<T>(T value, int formatVersion) =>
        JsonSerializer.SerializeToUtf8Bytes(value, formatVersion == 2 ? V2Options : Options);
    internal static T Deserialize<T>(byte[] bytes, int formatVersion = WallPackageManifest.CurrentFormatVersion)
    {
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        RejectDuplicateProperties(json.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, formatVersion == 2 ? V2Options : Options) ?? throw new InvalidDataException("Empty package JSON.");
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
        { Partitions = m.Partitions is null ? null : Array.AsReadOnly(m.Partitions.ToArray()) }).ToArray()),
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
        var singles = content.Members.Select(member => member.Images.Media).OfType<ManagedMediaReference>()
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
        var references = content.Members.Select(member => member.Images.Media).OfType<ManagedMediaReference>()
            .Concat(galleries.SelectMany(gallery => gallery.Files.Select(file => file.Media))).Distinct().ToArray();
        if (references.Any(reference => !PreparedMediaAsset.IsValidReference(reference)) ||
            references.GroupBy(reference => reference.Sha256, StringComparer.Ordinal).Any(group => group.Select(reference => reference.Length).Distinct().Count() != 1))
            throw new InvalidDataException("Conflicting original-image digest references.");
        return references;
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
            content.Lanes.Sum(lane => (long)lane.GroupIds.Count) + content.Groups.Sum(group => (long)group.MemberIds.Count) > limits.MaximumReferences)
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
            !groupedMembers.ToHashSet(StringComparer.Ordinal).SetEquals(memberIds) || content.Groups.Any(g => g.MemberIds.Count == 0))
            throw new InvalidDataException("Each member must belong to exactly one nonempty source group.");
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
                (member.Images.Path is not null || member.Images.Media is not null || member.Images.GalleryId is not null))
                throw new InvalidDataException("Invalid empty image source.");
            if (member.SourceShortcutPath is { Length: > 32768 } || member.TargetDisplayName is { Length: > 32768 })
                throw new InvalidDataException("Oversized entry presentation fields.");
            if (member.Images.GalleryId is { } galleryId && (!ValidHash(galleryId) || member.Images.Kind != MemberImageSourceKind.Folder ||
                member.Images.Path is not null || member.Images.Media is not null))
                throw new InvalidDataException("A package gallery must use only a logical source ID.");
        }
        var conflicts = content.Members.Select(m => m.Images.Media).OfType<ManagedMediaReference>()
            .GroupBy(m => m.Sha256, StringComparer.Ordinal).Any(g => g.Distinct().Count() != 1);
        if (conflicts) throw new InvalidDataException("Conflicting media references.");
        token.ThrowIfCancellationRequested();
        var errors = WallLayoutEngine.ValidateGeometry(content.ToDocument(), token: token);
        if (errors.Count != 0) throw new InvalidDataException(string.Join("; ", errors));
    }
}
