using System.Globalization;

namespace TileWall.Core.Imaging;

/// <summary>Describes one verified, bundled website icon without performing resource I/O.</summary>
internal readonly record struct WebsiteIconAsset(
    string RelativePath,
    string Sha256,
    int Width,
    int Height);

internal readonly record struct WebsiteIconMatch(string Id, WebsiteIconAsset Default);

/// <summary>
/// Maps explicit HTTP(S) host names to the finite set of website assets shipped with TileWall.
/// Host matching is exact: subdomains are recognized only when they are listed explicitly.
/// </summary>
internal static partial class WebsiteIconCatalog
{
    // Updated whenever catalog.json or one of its image payloads changes. This value participates
    // in the shared automatic-icon dependency stamp and intentionally requires no disk access.
    internal static string VersionStamp => CatalogVersion;

    // Generated from the versioned catalog; no directory scan or resource reads occur here.
    private static readonly IReadOnlyDictionary<string, WebsiteIconMatch> ByHost = CreateHostMap();

    private static void Register(IDictionary<string, WebsiteIconMatch> hosts, WebsiteIconMatch icon,
        params string[] names)
    {
        foreach (var name in names) hosts.Add(name, icon);
    }

    /// <summary>Returns the match for an absolute HTTP(S) URI whose normalized host is explicit.</summary>
    internal static bool TryGet(Uri uri, out WebsiteIconMatch match)
    {
        match = default;
        if (uri is null || !uri.IsAbsoluteUri ||
            !(uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
              uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (!TryNormalizeHost(uri.IdnHost, out var host)) return false;
        return ByHost.TryGetValue(host, out match);
    }

    /// <summary>Canonicalizes a DNS host for exact lookup, including IDN and one root dot.</summary>
    internal static bool TryNormalizeHost(string? host, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(host)) return false;

        if (!string.Equals(host, host.Trim(), StringComparison.Ordinal)) return false;
        if (host.EndsWith(".", StringComparison.Ordinal)) host = host[..^1];
        if (host.Length is 0 or > 253 || host.EndsWith(".", StringComparison.Ordinal)) return false;

        try
        {
            normalized = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(host).ToLowerInvariant();
            if (normalized.Length is 0 or > 253 || normalized.Split('.').Any(label =>
                    label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-'))
            {
                normalized = string.Empty;
                return false;
            }
            return true;
        }
        catch (ArgumentException)
        {
            normalized = string.Empty;
            return false;
        }
    }
}
