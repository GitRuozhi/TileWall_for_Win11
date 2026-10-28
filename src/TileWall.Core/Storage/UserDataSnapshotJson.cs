using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TileWall.Core.Settings;
using TileWall.Core.Wall;

namespace TileWall.Core.Storage;

/// <summary>The sole publication record. Its complete immutable file set has one generation.</summary>
public sealed record UserDataManifest(int SchemaVersion, long Generation, string SnapshotId,
    UserDataFileDigest Settings, UserDataFileDigest Wall, UserDataFileDigest RecycleBin)
{
    public const int CurrentFormatVersion = 2;
    internal static bool IsSupportedFormatVersion(int? version) => version is 1 or CurrentFormatVersion;
}
public sealed record UserDataFileDigest(long Length, string Sha256);
internal sealed record UserSettingsData(int SchemaVersion, WallSettings Value);
internal sealed record UserWallData(int SchemaVersion, int Columns, int Rows, IReadOnlyList<WallLane> Lanes);
internal sealed record UserRecycleBinData(int SchemaVersion, bool Enabled, IReadOnlyList<WallMember> Members);
internal sealed record PreparedUserDataSnapshot(UserDataManifest Manifest, byte[] ManifestBytes,
    IReadOnlyDictionary<string, byte[]> Files);

/// <summary>Only storage DTOs cross the disk boundary; the runtime wall remains a joint view.</summary>
internal static class UserDataSnapshotJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };
    // V1 identity must retain the old settings shape, even though the runtime model
    // initializes the newly introduced second shortcut when that field is absent.
    private static readonly JsonSerializerOptions V1Options = CreateV1Options();

    private static JsonSerializerOptions CreateV1Options()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (typeInfo.Type != typeof(WallSettings)) return;
            var secondary = typeInfo.Properties.Single(property => property.Name == "secondaryHotKey");
            typeInfo.Properties.Remove(secondary);
        });
        return new(Options) { TypeInfoResolver = resolver };
    }

    public static PreparedUserDataSnapshot Prepare(WallDocument document) => Prepare(document, UserDataManifest.CurrentFormatVersion);

    private static PreparedUserDataSnapshot Prepare(WallDocument document, int version)
    {
        if (document.InactiveMembers.Count > 200 || !document.RecycleBinEnabled && document.InactiveMembers.Count != 0)
            throw new InvalidDataException("Invalid recycle-bin retention policy.");
        var clean = MapGalleryPaths(document, _ => null);
        if (!HotKeyGesture.TryParse(clean.Settings.SecondaryHotKey, out _))
            throw new InvalidDataException("Invalid secondary hotkey.");
        var settingsOptions = version == 1 ? V1Options : Options;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(new UserSettingsData(version, clean.Settings), settingsOptions),
            ["wall.json"] = JsonSerializer.SerializeToUtf8Bytes(new UserWallData(1, clean.Columns, clean.Rows, clean.Lanes), Options),
            ["recycle-bin.json"] = JsonSerializer.SerializeToUtf8Bytes(new UserRecycleBinData(1, clean.RecycleBinEnabled, clean.InactiveMembers), Options),
        };
        UserDataFileDigest Digest(string name) => new(files[name].LongLength, Hash(files[name]));
        var settings = Digest("settings.json"); var wall = Digest("wall.json"); var recycle = Digest("recycle-bin.json");
        var identity = System.Text.Encoding.UTF8.GetBytes($"TileWall.Snapshot/1\n{document.Generation}\n{settings.Sha256}\n{wall.Sha256}\n{recycle.Sha256}\n");
        var manifest = new UserDataManifest(version, document.Generation, Hash(identity), settings, wall, recycle);
        return new(manifest, JsonSerializer.SerializeToUtf8Bytes(manifest, Options), files);
    }
    public static UserDataManifest ReadManifest(byte[] bytes)
    {
        RequireFields(bytes, ["schemaVersion", "generation", "snapshotId", "settings", "wall", "recycleBin"]);
        using (var json = JsonDocument.Parse(bytes))
            foreach (var name in new[] { "settings", "wall", "recycleBin" })
                RequireFields(System.Text.Encoding.UTF8.GetBytes(json.RootElement.GetProperty(name).GetRawText()), ["length", "sha256"]);
        var manifest = JsonSerializer.Deserialize<UserDataManifest>(bytes, Options) ?? throw new InvalidDataException("Empty data manifest.");
        if (!UserDataManifest.IsSupportedFormatVersion(manifest.SchemaVersion) || manifest.Generation < 0 ||
            !ValidHash(manifest.SnapshotId) || !ValidDigest(manifest.Settings) || !ValidDigest(manifest.Wall) || !ValidDigest(manifest.RecycleBin))
            throw new InvalidDataException("Invalid or unsupported data manifest.");
        return manifest;
    }
    public static WallDocument Read(UserDataManifest manifest, Func<string, byte[]> read, Func<ManagedGalleryReference, string> galleryPath)
    {
        if (!UserDataManifest.IsSupportedFormatVersion(manifest.SchemaVersion))
            throw new InvalidDataException("Unsupported data manifest.");
        byte[] File(string name, UserDataFileDigest expected)
        {
            var bytes = read(name);
            if (bytes.LongLength != expected.Length || Hash(bytes) != expected.Sha256)
                throw new InvalidDataException($"Snapshot file digest differs: {name}");
            return bytes;
        }
        var settingsBytes = File("settings.json", manifest.Settings);
        var wallBytes = File("wall.json", manifest.Wall);
        var recycleBytes = File("recycle-bin.json", manifest.RecycleBin);
        RequireFields(settingsBytes, ["schemaVersion", "value"]);
        RequireFields(wallBytes, ["schemaVersion", "columns", "rows", "lanes"]);
        RequireFields(recycleBytes, ["schemaVersion", "enabled", "members"]);
        var settingsOptions = manifest.SchemaVersion == 1 ? V1Options : Options;
        var settings = JsonSerializer.Deserialize<UserSettingsData>(settingsBytes, settingsOptions)!;
        var wall = JsonSerializer.Deserialize<UserWallData>(wallBytes, Options)!;
        var recycle = JsonSerializer.Deserialize<UserRecycleBinData>(recycleBytes, Options)!;
        if (settings.SchemaVersion != manifest.SchemaVersion || wall.SchemaVersion != 1 || recycle.SchemaVersion != 1 ||
            settings.Value is null || wall.Lanes is null || recycle.Members is null)
            throw new InvalidDataException("Incomplete or unsupported snapshot set.");
        if (manifest.SchemaVersion == UserDataManifest.CurrentFormatVersion)
        {
            using var json = JsonDocument.Parse(settingsBytes);
            if (!json.RootElement.GetProperty("value").TryGetProperty("secondaryHotKey", out _))
                throw new InvalidDataException("Missing secondary hotkey.");
        }
        if (recycle.Members.Count > 200 || !recycle.Enabled && recycle.Members.Count != 0)
            throw new InvalidDataException("Invalid recycle-bin retention policy.");
        var document = new WallDocument
        {
            Generation = manifest.Generation, Settings = settings.Value, Columns = wall.Columns, Rows = wall.Rows,
            Lanes = wall.Lanes, InactiveMembers = recycle.Members, RecycleBinEnabled = recycle.Enabled,
        };
        document = WallJson.Deserialize(WallJson.Serialize(document));
        if (Prepare(document, manifest.SchemaVersion).Manifest.SnapshotId != manifest.SnapshotId)
            throw new InvalidDataException("Snapshot identity does not cover its complete file set.");
        return MapGalleryPaths(document, galleryPath);
    }
    public static WallDocument MapGalleryPaths(WallDocument document, Func<ManagedGalleryReference, string?> path)
    {
        WallMember Map(WallMember member) => member.Images.ManagedGallery is { } gallery
            ? member with { Images = member.Images with { Path = path(gallery) } } : member;
        return WallJson.Freeze(document with
        {
            Lanes = document.Lanes.Select(lane => lane with { Groups = lane.Groups.Select(group => group with
                { Members = group.Members.Select(Map).ToArray() }).ToArray() }).ToArray(),
            InactiveMembers = document.InactiveMembers.Select(Map).ToArray(),
        });
    }
    internal static void RequireFields(byte[] bytes, string[] expected)
    {
        using var json = JsonDocument.Parse(bytes);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a storage object.");
        var names = json.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != expected.Length || names.Distinct(StringComparer.Ordinal).Count() != expected.Length ||
            names.Except(expected, StringComparer.Ordinal).Any()) throw new InvalidDataException("Missing, duplicate or unknown storage fields.");
    }
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    internal static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit) && value == value.ToUpperInvariant();
    private static bool ValidDigest(UserDataFileDigest? value) => value is { Length: > 0 and <= 64 * 1024 * 1024 } && ValidHash(value.Sha256);
}
