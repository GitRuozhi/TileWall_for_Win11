using System.Security;
using Microsoft.Win32;
using TileWall.Core.Entries;

namespace TileWall.Shell.Interop;

/// <summary>Read-only check for URL schemes registered with Windows Shell.</summary>
public static class RegisteredUrlProtocols
{
    /// <summary>
    /// HTTP(S) are supported by Windows. Other syntactically valid, non-file absolute URIs
    /// require an HKCR protocol key containing the conventional "URL Protocol" marker.
    /// This checks registration only; it never launches or modifies the registration.
    /// </summary>
    public static bool IsSupported(string? uriText)
    {
        if (UrlShortcut.ValidateUrl(uriText).Count != 0 ||
            !Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var protocolKey = Registry.ClassesRoot.OpenSubKey(uri.Scheme);
            return protocolKey?.GetValueNames().Contains("URL Protocol", StringComparer.OrdinalIgnoreCase) == true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
