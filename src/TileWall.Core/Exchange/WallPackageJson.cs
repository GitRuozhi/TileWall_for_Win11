using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TileWall.Core.Storage;
using TileWall.Core.Wall;

namespace TileWall.Core.Exchange;

internal static class WallPackageJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64,
    };
    internal static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    internal static T Deserialize<T>(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        RejectDuplicateProperties(json.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new InvalidDataException("Empty package JSON.");
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
    };

    internal static string Fingerprint(WallPackageContent content, IEnumerable<WallPackageFile> files) =>
        Hash(Serialize(new { Content = content, Assets = files.Where(f => f.Path != "wall.json")
            .OrderBy(f => f.Path, StringComparer.Ordinal).ToArray() }));

    internal static string EntryPath(WallPackageMember member) =>
        $"objects/{member.Id}/entry.{(member.EntryKind == ManagedEntryKind.Lnk ? "lnk" : "url")}";
    internal static string MediaPath(ManagedMediaReference media) => $"media/{media.Sha256}/original{media.Extension}";

    internal static void ValidateContent(WallPackageContent content)
    {
        if (content.Lanes is null || content.Groups is null || content.Members is null ||
            content.Lanes.Any(l => l?.GroupIds is null) || content.Groups.Any(g => g?.MemberIds is null) ||
            content.Members.Any(m => m is null || m.Visual is null || m.Images is null || m.ImageDisplay is null))
            throw new InvalidDataException("Malformed content collections.");
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
            if (member.EntryKind is { } kind && !Enum.IsDefined(kind) ||
                member.Kind == "puzzle" && member.Partitions is null ||
                member.Kind != "puzzle" && member.Partitions is not null ||
                member.Kind != "clock" && (member.TimeFormat is not null || member.DateFormat is not null))
                throw new InvalidDataException("Invalid member type data.");
            if (member.Images.Kind == MemberImageSourceKind.Single && member.Images.Media is null ||
                member.Images.Media is { } media && !PreparedMediaAsset.IsValidReference(media))
                throw new InvalidDataException("Invalid formal image reference.");
        }
        var conflicts = content.Members.Select(m => m.Images.Media).OfType<ManagedMediaReference>()
            .GroupBy(m => m.Sha256, StringComparer.Ordinal).Any(g => g.Distinct().Count() != 1);
        if (conflicts) throw new InvalidDataException("Conflicting media references.");
        var errors = WallLayoutEngine.ValidateGeometry(content.ToDocument());
        if (errors.Count != 0) throw new InvalidDataException(string.Join("; ", errors));
    }
}
