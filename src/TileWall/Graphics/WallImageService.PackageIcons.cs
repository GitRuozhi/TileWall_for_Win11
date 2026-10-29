using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Xml;
using System.Xml.Linq;
using Windows.Graphics.Imaging;
using Windows.Management.Deployment;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Wall;
using TileWall.Shell.Interop;

namespace TileWall.Graphics;

public sealed partial class WallImageService
{
    // A target label is only a candidate. Check it against the native managed
    // link's actual PIDL/path before using any application package resource.
    private string? VerifiedPackageIconTarget(string? source, WallMember? member)
    {
        if (member?.ResourceOrigin == MemberResourceOrigin.Package || source is null || !Path.IsPathFullyQualified(source) ||
            !Path.GetExtension(source).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            if (new FileInfo(source).Length is < 1 or > EntryObservation.MaximumBytes) return null;
            var fields = _links.Read(source);
            var native = fields.HasIdList ? PackagedAppIdentity(ShellItemCatalog.LinkTargetParsingName(source)) : null;
            var candidateIdentity = native ?? PackagedAppIdentity(member?.TargetInput);
            if (candidateIdentity is not { } app) return null;
            var identity = new LaunchIdentityService(new LocalFileStore(), _links).ResolveObservedInput(EntryKind.Lnk, source,
                File.ReadAllBytes(source), fields with { Arguments = string.Empty, WorkingDirectory = string.Empty });
            var candidate = "shell:AppsFolder\\" + app.AppId;
            var actual = ShellItemCatalog.ReadForLink(candidate);
            if (identity is not null && actual.Identity.Key == identity.Key) return candidate;
            // A registered app's PIDL bytes can change across package updates.
            // In that case use the Shell's target property of this real link,
            // never the wall's descriptive TargetInput alone.
            return native is { } nativeIdentity && PackagedAppIdentity(actual.Item.ParsingName) is { } registered &&
                registered.AppId.Equals(nativeIdentity.AppId, StringComparison.OrdinalIgnoreCase) ? candidate : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException or InvalidOperationException)
        { return null; }
    }

    private static string? PackageRegistrationStamp(string? target)
    {
        if (PackagedAppIdentity(target) is not { } identity) return null;
        try
        {
            var matches = new PackageManager().FindPackagesForUser(string.Empty, identity.Family)
                .Where(package => !package.IsResourcePackage && !package.IsFramework &&
                    string.Equals(package.Id.FamilyName, identity.Family, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            return matches.Length == 1
                ? matches[0].Id.FullName + "|" + Path.GetFullPath(matches[0].InstalledLocation.Path)
                : "package-registration-unavailable";
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        { return "package-registration-unavailable"; }
    }

    private async Task<Prepared?> TryPreparePackagedAppIconAsync(string? target, int width, int height, CancellationToken token)
    {
        if (PackagedAppIdentity(target) is not { } identity) return null;
        var (appId, family, application) = identity;
        try
        {
            token.ThrowIfCancellationRequested();
            var manager = new PackageManager();
            var packages = manager.FindPackagesForUser(string.Empty, family)
                .Where(item => !item.IsResourcePackage && !item.IsFramework &&
                    string.Equals(item.Id.FamilyName, family, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (packages.Length != 1) return null;
            var package = packages[0];
            var fullName = package.Id.FullName;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(package.InstalledLocation.Path));
            var manifest = PackageIconPath(root, "AppxManifest.xml");
            if (manifest is null) return null;
            XDocument document;
            using (var file = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = XmlReader.Create(file, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 }))
                document = XDocument.Load(reader);
            XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
            var entry = document.Root?.Element(foundation + "Applications")?.Elements(foundation + "Application")
                .SingleOrDefault(item => string.Equals((string?)item.Attribute("Id"), application, StringComparison.Ordinal));
            var logo = (string?)entry?.Element(uap + "VisualElements")?.Attribute("Square44x44Logo");
            if (logo is null || PackageIconPath(root, logo, requireFile: false) is not { } logoPath ||
                !Path.GetExtension(logoPath).Equals(".png", StringComparison.OrdinalIgnoreCase)) return null;
            var directory = Path.GetDirectoryName(logoPath)!;
            var stem = Path.GetFileNameWithoutExtension(logoPath);
            // Exact neutral unplated targetsize variants preserve the native
            // icon's own alpha and design padding. No alpha cropping or PRI,
            // language/theme guessing is applied; unsupported layouts use Shell.
            var candidates = new List<(string Path, int Width, int Height)>();
            var examined = 0;
            foreach (var file in Directory.EnumerateFiles(directory, stem + ".targetsize-*_altform-unplated.png").Take(129))
            {
                token.ThrowIfCancellationRequested();
                if (++examined > 128) return null;
                var name = Path.GetFileName(file);
                var prefix = stem + ".targetsize-";
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var number = name.AsSpan(prefix.Length);
                const string suffix = "_altform-unplated.png";
                if (!number.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                    !int.TryParse(number[..^suffix.Length], NumberStyles.None, CultureInfo.InvariantCulture, out var edge) || edge is < 1 or > 4096)
                    continue;
                if (PackageIconPath(root, Path.GetRelativePath(root, file)) is not { } safe) continue;
                using var headerFile = new FileStream(safe, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (headerFile.Length is < 1 or > 32 * 1024 * 1024) continue;
                using var headerStream = headerFile.AsRandomAccessStream();
                var header = await BitmapDecoder.CreateAsync(headerStream).AsTask(token).ConfigureAwait(false);
                if (header.PixelWidth is < 1 or > 4096 || header.PixelHeight is < 1 or > 4096) continue;
                candidates.Add((safe, (int)header.PixelWidth, (int)header.PixelHeight));
            }
            if (candidates.Count == 0) return null;
            var requested = Math.Max(width, height);
            // targetsize is a resource qualifier, not a promise about decoded
            // dimensions (PowerShell's 48 resource can actually be 44 pixels).
            var chosen = candidates.OrderBy(item => Math.Min(item.Width, item.Height) >= requested ? 0 : 1)
                .ThenBy(item => Math.Min(item.Width, item.Height) >= requested
                    ? Math.Max(item.Width, item.Height) : -Math.Min(item.Width, item.Height)).First();
            using var input = new FileStream(chosen.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is < 1 or > 32 * 1024 * 1024) return null;
            using var stream = input.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token).ConfigureAwait(false);
            if (decoder.PixelWidth != chosen.Width || decoder.PixelHeight != chosen.Height) return null;
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(token).ConfigureAwait(false);
            var pixels = data.DetachPixelData();
            if (pixels.Length != checked(chosen.Width * chosen.Height * 4)) return null;
            var visible = false;
            for (var index = 3; index < pixels.Length; index += 4) visible |= pixels[index] != 0;
            token.ThrowIfCancellationRequested();
            // A package update during the read must not publish an old logo as
            // the current registered application's result. Do not persist this
            // result under the shortcut's weaker automatic cache stamp.
            var current = manager.FindPackagesForUser(string.Empty, family)
                .Where(item => !item.IsResourcePackage && !item.IsFramework).Take(2).ToArray();
            if (!visible || PackageIconPath(root, Path.GetRelativePath(root, chosen.Path)) != chosen.Path ||
                current.Length != 1 || current[0].Id.FullName != fullName ||
                !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(current[0].InstalledLocation.Path)), root, StringComparison.OrdinalIgnoreCase)) return null;
            return new Prepared(WallImageStatus.Loaded, pixels, chosen.Width, chosen.Height,
                $"PackageAppList:{appId}:{Path.GetFileName(chosen.Path)}:{chosen.Width}x{chosen.Height}:requested={width}x{height}")
                { CacheAllowed = false };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or XmlException or OverflowException or NotSupportedException)
        { return null; }
    }

    private static (string AppId, string Family, string Application)? PackagedAppIdentity(string? target)
    {
        const string appsFolder = "shell:AppsFolder\\";
        var appId = target?.StartsWith(appsFolder, StringComparison.OrdinalIgnoreCase) == true ? target[appsFolder.Length..] : target;
        var separator = appId?.IndexOf('!') ?? -1;
        if (separator <= 0 || separator != appId!.LastIndexOf('!')) return null;
        var family = appId[..separator];
        var application = appId[(separator + 1)..];
        return family.Length <= 255 && application.Length is >= 1 and <= 64 && family.Contains('_') &&
            family.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') &&
            application.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
                ? (appId, family, application) : null;
    }

    private static string? PackageIconPath(string root, string relative, bool requireFile = true)
    {
        if (Path.IsPathRooted(relative)) return null;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        // Package manifests and resources are external read-only files. Reject
        // a redirected component rather than following it outside the package.
        for (var current = requireFile ? path : Path.GetDirectoryName(path)!; ; current = Path.GetDirectoryName(current)!)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return path;
    }
}
