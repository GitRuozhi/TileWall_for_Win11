using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using AsmResolver;
using AsmResolver.IO;
using AsmResolver.PE;
using AsmResolver.PE.Win32Resources.Icon;

namespace TileWall.Graphics;

public enum PeIconReadStatus { Success, MissingFile, NotExe, FileTooLarge, NoIconGroups, InvalidResource }
public enum PeIconFrameFormat { Png, Dib, Unknown }

/// <summary>A native RT_ICON frame from one RT_GROUP_ICON. RawData is source data, not a resized HICON.</summary>
public sealed record PeIconFrame(
    uint ResourceId,
    uint LanguageId,
    int Width,
    int Height,
    int BitsPerPixel,
    int ColorCount,
    int Planes,
    PeIconFrameFormat Format,
    ReadOnlyMemory<byte> RawData,
    string Sha256);

public sealed record PeIconGroup(uint ResourceId, string? ResourceName, uint LanguageId, IReadOnlyList<PeIconFrame> Frames);

public sealed record PeIconCatalog(string ExePath, long FileBytes, DateTime LastWriteUtc, IReadOnlyList<PeIconGroup> Groups)
{
    /// <summary>IconLocation's index applies only when its path resolves to this same direct-target EXE.</summary>
    public PeIconGroup? SelectGroup(string? iconLocationPath = null, int? iconLocationIndex = null,
        bool fallbackToDefault = true)
    {
        var distinctResources = Groups
            .DistinctBy(group => new GroupIdentity(group.ResourceName is not null,
                group.ResourceName is null ? group.ResourceId : 0, group.ResourceName))
            .ToArray();
        if (iconLocationIndex is { } index && SamePath(iconLocationPath, ExePath))
        {
            if (index >= 0 && index < distinctResources.Length)
                return SelectLanguageVariant(distinctResources[index]);
            if (index < 0 && index != int.MinValue)
            {
                var id = (uint)-index;
                var byId = distinctResources.FirstOrDefault(group => group.ResourceName is null && group.ResourceId == id);
                if (byId is not null)
                    return SelectLanguageVariant(byId);
            }
            if (!fallbackToDefault) return null;
        }

        // Stable default; never combine frames from different resource groups.
        var primary = distinctResources.OrderBy(group => group.ResourceName is null ? 0 : 1)
            .ThenBy(group => group.ResourceId)
            .ThenBy(group => group.ResourceName, StringComparer.Ordinal)
            .FirstOrDefault();
        return primary is null ? null : SelectLanguageVariant(primary);
    }

    private PeIconGroup SelectLanguageVariant(PeIconGroup resource)
    {
        var currentLcid = (uint)CultureInfo.CurrentUICulture.LCID;
        return Groups.Where(group => SameIdentity(group, resource))
            .OrderBy(group => group.LanguageId == currentLcid ? 0 : group.LanguageId == 0 ? 1 : 2)
            .ThenBy(group => group.LanguageId)
            .First();
    }

    private static bool SameIdentity(PeIconGroup first, PeIconGroup second) =>
        first.ResourceName is null && second.ResourceName is null
            ? first.ResourceId == second.ResourceId
            : string.Equals(first.ResourceName, second.ResourceName, StringComparison.Ordinal);

    private readonly record struct GroupIdentity(bool Named, uint Id, string? Name);

    private static bool SamePath(string? first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return false;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(first);
            return Path.IsPathFullyQualified(expanded) &&
                string.Equals(Path.GetFullPath(expanded), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}

public sealed record PeIconReadResult(PeIconReadStatus Status, PeIconCatalog? Catalog, string? ErrorType = null);

/// <summary>Read-only PE resource parser. The target is never loaded as an assembly or executed.</summary>
public sealed class PeIconResourceReader
{
    // The PE is memory-mapped. Large single-file .NET/Electron executables are ordinary inputs.
    private const long MaxFileBytes = 1024L * 1024 * 1024;
    private const int MaxGroups = 256;
    private const int MaxFramesPerGroup = 64;
    private const int MaxFrameBytes = 4 * 1024 * 1024;
    private const int MaxTotalFrameBytes = 32 * 1024 * 1024;
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    public PeIconReadResult Read(string? exePath, bool allowResourceModule = false)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return new(PeIconReadStatus.MissingFile, null);

        string path;
        try { path = Path.GetFullPath(exePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return new(PeIconReadStatus.MissingFile, null, ex.GetType().Name); }

        if (!allowResourceModule && !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            return new(PeIconReadStatus.NotExe, null);

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return new(PeIconReadStatus.MissingFile, null);
            if (info.Length < 64)
                return new(PeIconReadStatus.InvalidResource, null, "TooShort");
            if (info.Length > MaxFileBytes)
                return new(PeIconReadStatus.FileTooLarge, null);

            using var mappedFile = new MemoryMappedInputFile(path);
            var image = PEImage.FromFile(mappedFile);
            var iconResource = image.Resources is null ? null : IconResource.FromDirectory(image.Resources, IconType.Icon);
            if (iconResource is null || iconResource.Groups.Count == 0)
                return new(PeIconReadStatus.NoIconGroups, null);
            if (iconResource.Groups.Count > MaxGroups)
                return new(PeIconReadStatus.InvalidResource, null, "TooManyGroups");

            var totalFrameBytes = 0L;
            var groups = new List<PeIconGroup>(iconResource.Groups.Count);
            foreach (var group in iconResource.Groups)
            {
                if (group.Icons.Count is 0 or > MaxFramesPerGroup)
                    return new(PeIconReadStatus.InvalidResource, null, "InvalidFrameCount");
                var frames = new List<PeIconFrame>(group.Icons.Count);
                foreach (var icon in group.Icons)
                {
                    var width = icon.Width == 0 ? 256 : icon.Width;
                    var height = icon.Height == 0 ? 256 : icon.Height;
                    var segment = icon.PixelData;
                    if (segment is null || segment.GetPhysicalSize() is 0 or > MaxFrameBytes || width is < 1 or > 256 || height is < 1 or > 256)
                        return new(PeIconReadStatus.InvalidResource, null, "InvalidFrame");
                    totalFrameBytes += (long)segment.GetPhysicalSize();
                    if (totalFrameBytes > MaxTotalFrameBytes)
                        return new(PeIconReadStatus.InvalidResource, null, "TooMuchFrameData");

                    var raw = segment.WriteIntoArray();
                    var format = DetectFormat(raw, width, height);
                    if (format == PeIconFrameFormat.Unknown)
                        continue;
                    frames.Add(new PeIconFrame(
                        icon.Id, icon.Lcid, width, height, icon.BitsPerPixel, icon.ColorCount, icon.Planes,
                        format, raw, Convert.ToHexString(SHA256.HashData(raw))));
                }
                // Preserve even an undecodable group's ordinal. Shell positive indices
                // address resource groups, not only groups that contain usable frames.
                groups.Add(new PeIconGroup(group.Id, group.Name, group.Lcid, frames));
            }

            if (groups.All(group => group.Frames.Count == 0))
                return new(PeIconReadStatus.NoIconGroups, null);
            return new(PeIconReadStatus.Success, new PeIconCatalog(path, info.Length, info.LastWriteTimeUtc, groups));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new(PeIconReadStatus.InvalidResource, null, ex.GetType().Name);
        }
    }

    internal static PeIconFrameFormat DetectFormat(ReadOnlySpan<byte> bytes, int width, int height)
    {
        if (bytes.StartsWith(PngSignature) && bytes.Length >= 33)
        {
            var pngWidth = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4));
            var pngHeight = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4));
            return pngWidth == width && pngHeight == height ? PeIconFrameFormat.Png : PeIconFrameFormat.Unknown;
        }
        if (bytes.Length >= 40 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) >= 40)
        {
            var dibWidth = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(4, 4));
            var dibHeight = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(8, 4));
            return dibWidth == width && Math.Abs((long)dibHeight) >= height ? PeIconFrameFormat.Dib : PeIconFrameFormat.Unknown;
        }
        return PeIconFrameFormat.Unknown;
    }

    /// <summary>Wrap one native frame as ICO for the Windows Imaging Component ICO decoder.</summary>
    public static byte[] WrapSingleFrameIco(PeIconFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width is < 1 or > 256 || frame.Height is < 1 or > 256 || frame.RawData.Length is < 1 or > MaxFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(frame));
        using var output = new MemoryStream(22 + frame.RawData.Length);
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((byte)(frame.Width == 256 ? 0 : frame.Width));
        writer.Write((byte)(frame.Height == 256 ? 0 : frame.Height));
        writer.Write((byte)frame.ColorCount);
        writer.Write((byte)0);
        writer.Write((ushort)frame.Planes);
        writer.Write((ushort)frame.BitsPerPixel);
        writer.Write((uint)frame.RawData.Length);
        writer.Write((uint)22);
        writer.Write(frame.RawData.Span);
        return output.ToArray();
    }

    public static PeIconFrame? SelectFrame(PeIconGroup group, int targetPhysicalPixels)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (targetPhysicalPixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetPhysicalPixels));
        return group.Frames
            .OrderBy(frame => Math.Max(frame.Width, frame.Height) >= targetPhysicalPixels ? 0 : 1)
            .ThenBy(frame => Math.Max(frame.Width, frame.Height) >= targetPhysicalPixels
                ? Math.Max(frame.Width, frame.Height) : -Math.Max(frame.Width, frame.Height))
            .ThenByDescending(frame => frame.BitsPerPixel)
            .ThenBy(frame => frame.ResourceId)
            .FirstOrDefault();
    }
}
