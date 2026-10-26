using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using TileWall.Shell.Interop;

namespace TileWall.Graphics;

public sealed partial class WallImageService
{
    private const int MaximumMmcConsoleFileBytes = 4 * 1024 * 1024;
    private const int MaximumMmcResourceStampBytes = 32 * 1024 * 1024;

    private sealed record MmcIconDeclaration(string Path, int Index);

    private sealed record MmcIconSource(string ConsolePath, MmcIconDeclaration? Declaration, string Stamp, bool CacheAllowed);

    private sealed record MmcResourceDependency(string Stamp, bool CacheAllowed, bool SafeToRead);

    private static bool IsMmcConsolePath(string? path) =>
        path is not null && Path.GetExtension(path).Equals(".msc", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Read only the console-level icon declaration. The XML and declared PE/ICO resource are
    /// bounded, checked for reparse redirection, and fingerprinted as real input dependencies.
    /// Unsupported or incomplete files leave the existing Shell/fallback chain in control.
    /// </summary>
    private static MmcIconSource ReadMmcIconSource(string path)
    {
        var fullPath = NormalizePath(path);
        if (fullPath is null || !IsMmcConsolePath(fullPath))
            return new(path, null, "mmc:invalid-path", false);

        if (!TryReadMmcFile(fullPath, MaximumMmcConsoleFileBytes, out var bytes))
        {
            var metadata = SafeFileMetadata(fullPath);
            return new(fullPath, null, $"mmc:unreadable-or-over-limit|{fullPath}|{metadata}", false);
        }

        var sourceHash = Convert.ToHexString(SHA256.HashData(bytes));
        var sourceStamp = $"mmc:source|{fullPath}|sha256={sourceHash}";
        MmcIconDeclaration? declaration;
        string declarationStamp;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumMmcConsoleFileBytes,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            declaration = ParseMmcIconDeclaration(document);
            declarationStamp = declaration is null
                ? "declaration=none-or-unsupported"
                : $"declaration={declaration.Path}|index={declaration.Index.ToString(CultureInfo.InvariantCulture)}";
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException or OverflowException)
        {
            // The source content hash still invalidates old mappings if this console is repaired.
            return new(fullPath, null, sourceStamp + "|xml=invalid", true);
        }

        if (declaration is null)
            return new(fullPath, null, sourceStamp + "|" + declarationStamp, true);

        var resource = ReadMmcResourceDependency(declaration.Path);
        var stamp = sourceStamp + "|" + declarationStamp + "|" + resource.Stamp;
        return new(fullPath, resource.SafeToRead ? declaration : null, stamp, resource.CacheAllowed);
    }

    private static Prepared? TryPrepareMmcNativeIcon(MmcIconDeclaration declaration, int width, int height,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsSafeExternalFilePath(declaration.Path)) return null;

        var requestedPixels = Math.Max(width, height);
        var edge = requestedPixels <= 48 ? 48 : requestedPixels <= 100 ? 100 : 256;
        IntPtr icon = IntPtr.Zero;
        try
        {
            var count = PrivateExtractIconsW(declaration.Path, declaration.Index, edge, edge,
                out icon, IntPtr.Zero, 1, 0);
            token.ThrowIfCancellationRequested();
            if (count is 0 or uint.MaxValue || icon == IntPtr.Zero) return null;

            // WIC/PE parsing above can reject otherwise valid native system icon
            // resources. Ask Windows to extract only the explicitly declared
            // file/index, then use the existing dual-background alpha recovery.
            var decoded = ShellIconPixels.ReadIcon(icon);
            if (decoded is not { } plane || plane.Width is < 1 or > 256 || plane.Height is < 1 or > 256 ||
                plane.Pixels.Length != checked(plane.Width * plane.Height * 4)) return null;
            var visible = false;
            for (var offset = 3; offset < plane.Pixels.Length; offset += 4)
                visible |= plane.Pixels[offset] != 0;
            if (!visible) return null;

            return new Prepared(WallImageStatus.Loaded, plane.Pixels, plane.Width, plane.Height,
                $"MmcPrivateExtractIcons:{plane.Width}x{plane.Height}:requested={edge}x{edge}:index={declaration.Index}:resource={declaration.Path}:cache=disabled")
            { CacheAllowed = false };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or ExternalException or DllNotFoundException or EntryPointNotFoundException or OverflowException)
        { return null; }
        finally
        {
            if (icon != IntPtr.Zero) _ = Win32Api.DestroyIcon(icon);
        }
    }

    private static MmcIconDeclaration? ParseMmcIconDeclaration(XDocument document)
    {
        var root = document.Root;
        if (root?.Name.LocalName != "MMC_ConsoleFile") return null;

        // MMC stores the console's icon in the root VisualAttributes element.
        // Ambiguous or nested layouts are deliberately left to Shell.
        var visuals = root.Elements().Where(element => element.Name.LocalName == "VisualAttributes").Take(2).ToArray();
        if (visuals.Length != 1) return null;
        var icons = visuals[0].Elements().Where(element => element.Name.LocalName == "Icon").Take(2).ToArray();
        if (icons.Length != 1) return null;

        var rawPath = ((string?)icons[0].Attribute("File"))?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(rawPath)) return null;
        int index = 0;
        var indexText = (string?)icons[0].Attribute("Index");
        if (indexText is not null && !int.TryParse(indexText, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out index)) return null;

        var expanded = Environment.ExpandEnvironmentVariables(rawPath);
        if (!Path.IsPathFullyQualified(expanded)) return null;
        string fullPath;
        try { fullPath = Path.GetFullPath(expanded); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }

        // Positive MMC indices are zero-based icon-group ordinals. PeIconCatalog also
        // supports negative resource IDs, but this layer never invents or rewrites one.
        return new(fullPath, index);
    }

    private static MmcResourceDependency ReadMmcResourceDependency(string path)
    {
        if (!TryGetSafeFileInfo(path, out var length, out var lastWriteUtc))
            return new($"mmc-resource=unavailable|{path}", false, false);

        if (length > MaximumMmcResourceStampBytes)
            return new($"mmc-resource=over-limit|{path}|{length}|{lastWriteUtc.Ticks}", false, false);

        if (!TryReadMmcFile(path, MaximumMmcResourceStampBytes, out var bytes))
            return new($"mmc-resource=unreadable|{path}|{length}|{lastWriteUtc.Ticks}", false, false);

        return new($"mmc-resource=sha256|{path}|{Convert.ToHexString(SHA256.HashData(bytes))}", true, true);
    }

    private static string SafeFileMetadata(string path) => TryGetSafeFileInfo(path, out var length, out var lastWriteUtc)
        ? $"{length}|{lastWriteUtc.Ticks}"
        : "unavailable";

    private static bool TryReadMmcFile(string path, int maximumBytes, out byte[] bytes)
    {
        bytes = [];
        if (!IsSafeExternalFilePath(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.SequentialScan);
            if (stream.Length is < 1 || stream.Length > maximumBytes) return false;
            var result = new byte[checked((int)stream.Length)];
            var offset = 0;
            while (offset < result.Length)
            {
                var read = stream.Read(result, offset, result.Length - offset);
                if (read == 0) return false;
                offset += read;
            }
            if (stream.ReadByte() != -1 || !IsSafeExternalFilePath(path)) return false;
            bytes = result;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or OverflowException or SecurityException)
        { return false; }
    }

    private static bool TryGetSafeFileInfo(string path, out long length, out DateTime lastWriteUtc)
    {
        length = 0;
        lastWriteUtc = default;
        if (!IsSafeExternalFilePath(path)) return false;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || !IsSafeExternalFilePath(path)) return false;
            length = info.Length;
            lastWriteUtc = info.LastWriteTimeUtc;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or SecurityException)
        { return false; }
    }

    private static bool IsSafeExternalFilePath(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return false;
            var relative = fullPath[root.Length..];
            if (relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries).Any(segment => segment.Contains(':'))) return false;

            var current = fullPath;
            while (true)
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                var isRoot = string.Equals(Path.TrimEndingDirectorySeparator(current),
                    Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);
                if (isRoot)
                    return (attributes & FileAttributes.Directory) != 0;
                if (!string.Equals(current, fullPath, StringComparison.OrdinalIgnoreCase) &&
                    (attributes & FileAttributes.Directory) == 0) return false;
                var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    return false;
                current = parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or SecurityException)
        { return false; }
    }

    [DllImport("user32.dll", EntryPoint = "PrivateExtractIconsW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PrivateExtractIconsW(string fileName, int iconIndex, int iconWidth, int iconHeight,
        out IntPtr icon, IntPtr iconId, uint iconCount, uint flags);
}
