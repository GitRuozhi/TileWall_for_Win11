using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TileWall.Core.Settings;
using TileWall.Core.Wall;

namespace TileWall.Core.Storage;

/// <summary>The sole publication record. Its complete immutable file set has one generation.</summary>
public sealed record UserDataManifest(int SchemaVersion, long Generation, string SnapshotId,
    UserDataFileDigest Settings, UserDataFileDigest Wall, UserDataFileDigest RecycleBin)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
    public bool Equals(UserDataManifest? other) => other is not null && SchemaVersion == other.SchemaVersion &&
        Generation == other.Generation && SnapshotId == other.SnapshotId && Settings == other.Settings &&
        Wall == other.Wall && RecycleBin == other.RecycleBin &&
        ForwardDataCompatibility.FieldsEqual(ExtensionData, other.ExtensionData) &&
        ForwardDataCompatibility.FieldsEqual(Settings.ExtensionData, other.Settings.ExtensionData) &&
        ForwardDataCompatibility.FieldsEqual(Wall.ExtensionData, other.Wall.ExtensionData) &&
        ForwardDataCompatibility.FieldsEqual(RecycleBin.ExtensionData, other.RecycleBin.ExtensionData);
    public override int GetHashCode() => HashCode.Combine(SchemaVersion, Generation, SnapshotId, Settings, Wall, RecycleBin);
    public const int CurrentFormatVersion = 4;
    internal static bool IsSupportedFormatVersion(int? version) => version is >= 1;
}
public sealed record UserDataFileDigest(long Length, string Sha256)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
    public bool Equals(UserDataFileDigest? other) => other is not null && Length == other.Length && Sha256 == other.Sha256;
    public override int GetHashCode() => HashCode.Combine(Length, Sha256);
}
internal sealed record UserSettingsData(int SchemaVersion, WallSettings Value)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}
internal sealed record UserWallData(int SchemaVersion, int Columns, int Rows, IReadOnlyList<WallLane> Lanes)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}
internal sealed record UserRecycleBinData(int SchemaVersion, bool Enabled, IReadOnlyList<WallMember> Members)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}
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
    // Older schemas retain their original field contract. Forward additions start at V1.43's format 4.
    private static readonly JsonSerializerOptions V1Options = CreateHistoricalOptions(1);
    private static readonly JsonSerializerOptions V2Options = CreateHistoricalOptions(2);
    private static readonly JsonSerializerOptions V3Options = CreateHistoricalOptions(3);

    private static JsonSerializerOptions CreateHistoricalOptions(int version)
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            foreach (var extension in typeInfo.Properties.Where(property => property.IsExtensionData).ToArray())
                typeInfo.Properties.Remove(extension);
            if (typeInfo.Type == typeof(MemberImageSource))
                typeInfo.Properties.Remove(typeInfo.Properties.Single(property => property.Name == "iconSeries"));
            if (version == 1 && typeInfo.Type == typeof(WallSettings))
                typeInfo.Properties.Remove(typeInfo.Properties.Single(property => property.Name == "secondaryHotKey"));
            if (version >= 3 || !typeof(WallMember).IsAssignableFrom(typeInfo.Type)) return;
            foreach (var name in new[] { "resourceOrigin", "sourceShortcutPath", "targetDisplayName" })
            {
                var property = typeInfo.Properties.SingleOrDefault(property => property.Name == name);
                if (property is not null) typeInfo.Properties.Remove(property);
            }
        });
        return new(Options) { TypeInfoResolver = resolver };
    }
    private static JsonSerializerOptions OptionsFor(int version) => version switch
        { 1 => V1Options, 2 => V2Options, 3 => V3Options, _ => Options };

    public static PreparedUserDataSnapshot Prepare(WallDocument document)
    {
        if (document.InactiveMembers.Count > 200 || !document.RecycleBinEnabled && document.InactiveMembers.Count != 0)
            throw new InvalidDataException("Invalid recycle-bin retention policy.");
        var clean = MapGalleryPaths(document, _ => null);
        if (!HotKeyGesture.TryParse(clean.Settings.SecondaryHotKey, out _))
            throw new InvalidDataException("Invalid secondary hotkey.");
        var metadata = clean.StorageMetadata;
        var version = Math.Max(UserDataManifest.CurrentFormatVersion, metadata?.ManifestVersion ?? 0);
        var settingsVersion = Math.Max(UserDataManifest.CurrentFormatVersion, metadata?.SettingsVersion ?? 0);
        var settingsOptions = OptionsFor(version);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(new UserSettingsData(settingsVersion, clean.Settings) { ExtensionData = metadata?.SettingsFields }, settingsOptions),
            ["wall.json"] = JsonSerializer.SerializeToUtf8Bytes(new UserWallData(Math.Max(1, metadata?.WallVersion ?? 0), clean.Columns, clean.Rows, clean.Lanes) { ExtensionData = metadata?.WallFields }, settingsOptions),
            ["recycle-bin.json"] = JsonSerializer.SerializeToUtf8Bytes(new UserRecycleBinData(Math.Max(1, metadata?.RecycleBinVersion ?? 0), clean.RecycleBinEnabled, clean.InactiveMembers) { ExtensionData = metadata?.RecycleBinFields }, settingsOptions),
        };
        UserDataFileDigest Digest(string name) => new(files[name].LongLength, Hash(files[name]));
        var settings = Digest("settings.json"); var wall = Digest("wall.json"); var recycle = Digest("recycle-bin.json");
        settings = settings with { ExtensionData = metadata?.SettingsDigestFields };
        wall = wall with { ExtensionData = metadata?.WallDigestFields };
        recycle = recycle with { ExtensionData = metadata?.RecycleBinDigestFields };
        var manifest = new UserDataManifest(version, document.Generation,
            SnapshotIdentity(document.Generation, settings, wall, recycle), settings, wall, recycle)
            { ExtensionData = metadata?.ManifestFields };
        return new(manifest, JsonSerializer.SerializeToUtf8Bytes(manifest, Options), files);
    }
    public static UserDataManifest ReadManifest(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes);
        RejectDuplicateProperties(json.RootElement);
        var future = json.RootElement.ValueKind == JsonValueKind.Object &&
            json.RootElement.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var version) &&
            version > UserDataManifest.CurrentFormatVersion;
        try
        {
            RequireReadableFields(bytes, ["schemaVersion", "generation", "snapshotId", "settings", "wall", "recycleBin"]);
            foreach (var name in new[] { "settings", "wall", "recycleBin" })
                RequireReadableFields(System.Text.Encoding.UTF8.GetBytes(json.RootElement.GetProperty(name).GetRawText()), ["length", "sha256"]);
            var manifest = JsonSerializer.Deserialize<UserDataManifest>(bytes, Options) ?? throw new InvalidDataException("Empty data manifest.");
            if (!UserDataManifest.IsSupportedFormatVersion(manifest.SchemaVersion) || manifest.Generation < 0 ||
                !ValidHash(manifest.SnapshotId) || !ValidDigest(manifest.Settings) || !ValidDigest(manifest.Wall) || !ValidDigest(manifest.RecycleBin))
                throw new InvalidDataException("Invalid or unsupported data manifest.");
            return manifest;
        }
        catch (Exception ex) when (future && (ex is InvalidDataException or JsonException or InvalidOperationException))
        {
            throw new UnsupportedSnapshotStructureException("The newer manifest cannot be interpreted; its data remains untouched.", ex);
        }
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
        if (SnapshotIdentity(manifest.Generation, manifest.Settings, manifest.Wall, manifest.RecycleBin) != manifest.SnapshotId)
            throw new InvalidDataException("Snapshot identity does not cover its complete file set.");
        var future = manifest.SchemaVersion > UserDataManifest.CurrentFormatVersion;
        foreach (var bytes in new[] { settingsBytes, wallBytes, recycleBytes })
        {
            using var json = JsonDocument.Parse(bytes);
            RejectDuplicateProperties(json.RootElement);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("schemaVersion", out var schema) &&
                schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var version))
                future |= version > (ReferenceEquals(bytes, settingsBytes) ? UserDataManifest.CurrentFormatVersion : 1);
        }
        try
        {
            RequireReadableFields(settingsBytes, ["schemaVersion", "value"]);
            RequireReadableFields(wallBytes, ["schemaVersion", "columns", "rows", "lanes"]);
            RequireReadableFields(recycleBytes, ["schemaVersion", "enabled", "members"]);
            var settingsOptions = OptionsFor(manifest.SchemaVersion);
            var settings = JsonSerializer.Deserialize<UserSettingsData>(settingsBytes, settingsOptions)!;
            var wall = JsonSerializer.Deserialize<UserWallData>(wallBytes, settingsOptions)!;
            var recycle = JsonSerializer.Deserialize<UserRecycleBinData>(recycleBytes, settingsOptions)!;
            if ((manifest.SchemaVersion < UserDataManifest.CurrentFormatVersion
                    ? settings.SchemaVersion != manifest.SchemaVersion : settings.SchemaVersion < UserDataManifest.CurrentFormatVersion) ||
                wall.SchemaVersion < 1 || recycle.SchemaVersion < 1 || settings.Value is null || wall.Lanes is null || recycle.Members is null)
                throw new InvalidDataException("Incomplete or unsupported snapshot set.");
            if (manifest.SchemaVersion >= 2)
            {
                using var json = JsonDocument.Parse(settingsBytes);
                if (!json.RootElement.GetProperty("value").TryGetProperty("secondaryHotKey", out _))
                    throw new InvalidDataException("Missing secondary hotkey.");
            }
            if (!HotKeyGesture.TryParse(settings.Value.SecondaryHotKey, out _))
                throw new InvalidDataException("Invalid secondary hotkey.");
            if (recycle.Members.Count > 200 || !recycle.Enabled && recycle.Members.Count != 0)
                throw new InvalidDataException("Invalid recycle-bin retention policy.");
            var document = new WallDocument
            {
                Generation = manifest.Generation, Settings = settings.Value, Columns = wall.Columns, Rows = wall.Rows,
                Lanes = wall.Lanes, InactiveMembers = recycle.Members, RecycleBinEnabled = recycle.Enabled,
                StorageMetadata = new(manifest.SchemaVersion, settings.SchemaVersion, wall.SchemaVersion, recycle.SchemaVersion,
                    manifest.ExtensionData, manifest.Settings.ExtensionData, manifest.Wall.ExtensionData, manifest.RecycleBin.ExtensionData,
                    settings.ExtensionData, wall.ExtensionData, recycle.ExtensionData),
            };
            return MapGalleryPaths(WallJson.ValidateAndFreeze(document), galleryPath);
        }
        catch (Exception ex) when (manifest.SchemaVersion >= UserDataManifest.CurrentFormatVersion && ex is JsonException ||
            future && (ex is InvalidDataException or InvalidOperationException or ArgumentException))
        {
            throw new UnsupportedSnapshotStructureException("The snapshot structure cannot be interpreted; its data remains untouched.", ex);
        }
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
    private static string SnapshotIdentity(long generation, UserDataFileDigest settings, UserDataFileDigest wall, UserDataFileDigest recycle) =>
        Hash(System.Text.Encoding.UTF8.GetBytes($"TileWall.Snapshot/1\n{generation}\n{settings.Sha256}\n{wall.Sha256}\n{recycle.Sha256}\n"));

    private static void RequireReadableFields(byte[] bytes, string[] required)
    {
        using var json = JsonDocument.Parse(bytes);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a storage object.");
        RejectDuplicateProperties(json.RootElement);
        foreach (var name in required)
            if (!json.RootElement.TryGetProperty(name, out _)) throw new InvalidDataException("Missing required storage field: " + name);
    }

    internal static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property: " + property.Name);
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
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
