using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TileWall.Core.Entries;
using TileWall.Core.Wall;

namespace TileWall.Core.Storage;

public sealed record PreparedManagedGalleryFile(string RelativePath, PreparedMediaAsset Asset);
public sealed record ManagedGalleryFile(string RelativePath, ManagedMediaReference Media);
public sealed record ManagedGalleryManifest(int SchemaVersion, IReadOnlyList<ManagedGalleryFile> Files);

/// <summary>Verified, frozen gallery bytes. Preparing never writes a formal source directory.</summary>
public sealed class PreparedManagedGallery
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            foreach (var property in typeInfo.Properties.Where(property => property.IsExtensionData).ToArray())
                typeInfo.Properties.Remove(property);
        });
        return new(UserDataSnapshotJson.Options) { TypeInfoResolver = resolver };
    }

    public const int MaximumFiles = 2000;
    public const long MaximumTotalBytes = 128L * 1024 * 1024;
    private readonly byte[] _manifest;
    private PreparedManagedGallery(PreparedManagedGalleryFile[] files)
    {
        Files = new ReadOnlyCollection<PreparedManagedGalleryFile>(files);
        _manifest = JsonSerializer.SerializeToUtf8Bytes(new ManagedGalleryManifest(1,
            files.Select(file => new ManagedGalleryFile(file.RelativePath, file.Asset.Reference)).ToArray()), JsonOptions);
        var hash = UserDataSnapshotJson.Hash(_manifest);
        Reference = new(hash, hash);
    }
    public ManagedGalleryReference Reference { get; }
    public IReadOnlyList<PreparedManagedGalleryFile> Files { get; }
    public ReadOnlyMemory<byte> ManifestBytes => _manifest.ToArray();
    public static PreparedManagedGallery FromVerifiedFiles(IEnumerable<PreparedManagedGalleryFile> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var files = source.Take(MaximumFiles + 1).ToArray();
        if (files.Length is < 1 or > MaximumFiles || files.Any(file => file is null || !ValidName(file.RelativePath) ||
                file.Asset is null || !PreparedMediaAsset.IsValidReference(file.Asset.Reference)) ||
            files.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length ||
            files.Sum(file => file.Asset.Reference.Length) > MaximumTotalBytes)
            throw new InvalidDataException("Invalid or oversized managed gallery.");
        Array.Sort(files, (a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        return new(files);
    }
    public static bool IsValidReference(ManagedGalleryReference? reference) => reference is not null &&
        UserDataSnapshotJson.ValidHash(reference.ContentId) && reference.ManifestSha256 == reference.ContentId;
    internal static bool ValidName(string? name) => name is { Length: > 0 and <= 240 } && !name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) &&
        name.IndexOfAny(['/', '\\', ':', '\0']) < 0 && EntryNames.Validate(name).Count == 0;
    internal static ManagedGalleryManifest ReadManifest(byte[] bytes, ManagedGalleryReference reference)
    {
        if (!IsValidReference(reference) || bytes.Length > 1024 * 1024 || UserDataSnapshotJson.Hash(bytes) != reference.ManifestSha256)
            throw new InvalidDataException("Managed gallery manifest digest differs.");
        UserDataSnapshotJson.RequireFields(bytes, ["schemaVersion", "files"]);
        var manifest = JsonSerializer.Deserialize<ManagedGalleryManifest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Empty managed gallery manifest.");
        if (manifest.SchemaVersion != 1 || manifest.Files is null || manifest.Files.Count is < 1 or > MaximumFiles ||
            manifest.Files.Any(file => file is null || !ValidName(file.RelativePath) || !PreparedMediaAsset.IsValidReference(file.Media)) ||
            manifest.Files.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count ||
            manifest.Files.Sum(file => file.Media.Length) > MaximumTotalBytes)
            throw new InvalidDataException("Invalid managed gallery manifest.");
        var sorted = manifest.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new ManagedGalleryManifest(1, sorted), JsonOptions);
        if (!canonical.AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Managed gallery manifest is not canonical.");
        return manifest with { Files = Array.AsReadOnly(sorted) };
    }
}
