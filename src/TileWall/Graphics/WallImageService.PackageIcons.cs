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

    private async Task<List<IconCandidateSeries>> CollectPackagedIconSeriesAsync(string? target, int priority, CancellationToken token)
    {
        var result = new List<IconCandidateSeries>();
        if (PackagedAppIdentity(target) is not { } identity) return result;
        try
        {
            token.ThrowIfCancellationRequested();
            var manager = new PackageManager();
            var packages = manager.FindPackagesForUser(string.Empty, identity.Family)
                .Where(item => !item.IsResourcePackage && !item.IsFramework &&
                    string.Equals(item.Id.FamilyName, identity.Family, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (packages.Length != 1) return result;
            var package = packages[0];
            var fullName = package.Id.FullName;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(package.InstalledLocation.Path));
            var manifest = PackageIconPath(root, "AppxManifest.xml");
            if (manifest is null) return result;
            XDocument document;
            using (var file = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                document = XDocument.Load(reader);
            XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
            var entry = document.Root?.Element(foundation + "Applications")?.Elements(foundation + "Application")
                .SingleOrDefault(item => string.Equals((string?)item.Attribute("Id"), identity.Application, StringComparison.Ordinal));
            var visual = entry?.Element(uap + "VisualElements");
            if (visual is null) return result;
            await Collect((string?)visual.Attribute("Square44x44Logo"), 0, true).ConfigureAwait(false);
            await Collect((string?)visual.Attribute("Square150x150Logo"), 1, false).ConfigureAwait(false);
            await Collect((string?)visual.Element(uap + "DefaultTile")?.Attribute("Square71x71Logo"), 2, false).ConfigureAwait(false);
            await Collect((string?)visual.Attribute("Square44x44Logo"), 3, false).ConfigureAwait(false);
            var current = manager.FindPackagesForUser(string.Empty, identity.Family)
                .Where(item => !item.IsResourcePackage && !item.IsFramework).Take(2).ToArray();
            if (current.Length != 1 || current[0].Id.FullName != fullName ||
                !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(current[0].InstalledLocation.Path)), root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Application package changed during icon collection.");
            return result;

            async Task Collect(string? declaration, int role, bool unplatedOnly)
            {
                if (declaration is null) return;
                try
                {
                    if (PackageIconPath(root, declaration, requireFile: false) is not { } declared) return;
                    var extension = Path.GetExtension(declared);
                    if (!new[] { ".png", ".jpg", ".jpeg" }.Contains(extension, StringComparer.OrdinalIgnoreCase)) return;
                    var directory = Path.GetDirectoryName(declared)!;
                    var stem = Path.GetFileNameWithoutExtension(declared);
                    var planes = new Dictionary<int, List<Prepared>>();
                    foreach (var file in Directory.EnumerateFiles(directory, stem + "*" + extension))
                    {
                        token.ThrowIfCancellationRequested();
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (!name.Equals(stem, StringComparison.OrdinalIgnoreCase) && !name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)) continue;
                        var qualifiers = name.Length == stem.Length ? Array.Empty<string>() : name[(stem.Length + 1)..].Split('_');
                        var unplated = false; var theme = 0; var valid = true;
                        foreach (var qualifier in qualifiers)
                        {
                            if (qualifier.StartsWith("scale-", StringComparison.OrdinalIgnoreCase) || qualifier.StartsWith("targetsize-", StringComparison.OrdinalIgnoreCase))
                            {
                                var dash = qualifier.IndexOf('-');
                                valid &= int.TryParse(qualifier.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size > 0;
                            }
                            else if (qualifier.Equals("altform-unplated", StringComparison.OrdinalIgnoreCase)) unplated = true;
                            else if (qualifier.Equals("altform-darkunplated", StringComparison.OrdinalIgnoreCase)) { unplated = true; theme = 1; }
                            else if (qualifier.Equals("theme-dark", StringComparison.OrdinalIgnoreCase)) theme = 1;
                            else if (qualifier.Equals("altform-colorful", StringComparison.OrdinalIgnoreCase)) { }
                            else valid = false; // Light/contrast/language/unknown qualifiers are not guessed.
                        }
                        if (!valid || unplatedOnly != unplated) continue;
                        try
                        {
                            if (PackageIconPath(root, Path.GetRelativePath(root, file)) is not { } safe) continue;
                            using var input = new FileStream(safe, FileMode.Open, FileAccess.Read, FileShare.Read);
                            using var stream = input.AsRandomAccessStream();
                            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token).ConfigureAwait(false);
                            var width = checked((int)decoder.PixelWidth); var height = checked((int)decoder.PixelHeight);
                            if (width <= 0 || height <= 0) continue;
                            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
                                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(token).ConfigureAwait(false);
                            var pixels = data.DetachPixelData();
                            if (pixels.LongLength != (long)width * height * 4 || PackageIconPath(root, Path.GetRelativePath(root, safe)) != safe) continue;
                            var prepared = new Prepared(WallImageStatus.Loaded, pixels, width, height,
                                $"PackageNative:{identity.AppId}:role={role}:{Path.GetFileName(safe)}:{width}x{height}") { CacheAllowed = false };
                            if (!planes.TryGetValue(theme, out var frames)) planes.Add(theme, frames = new());
                            frames.Add(prepared);
                        }
                        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException) { }
                    }
                    foreach (var item in planes)
                        result.Add(new IconCandidateSeries($"package:{identity.AppId}:{declaration}:role={role}:theme={item.Key}",
                            priority, item.Value, item.Key, role));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or XmlException or OverflowException or NotSupportedException)
        { return new(); }
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
