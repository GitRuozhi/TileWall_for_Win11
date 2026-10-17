using Microsoft.Win32;
using System.Security.Principal;

namespace TileWall.Shell;

/// <summary>Reads the current user's existing Windows account-picture cache without changing it.</summary>
internal static class LocalAccountPicture
{
    private const string AccountPictureKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\";
    private static readonly string[] PreferredPictures =
    [
        "Image96", "Image64", "Image48", "Image40", "Image32",
        "Image192", "Image240", "Image208", "Image424", "Image448", "Image1080",
    ];

    internal static string? FindSmallPicturePath()
    {
        string? sid;
        try
        {
            sid = WindowsIdentity.GetCurrent().User?.Value;
        }
        catch (Exception ex) when (IsExpectedReadFailure(ex))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(sid)) return null;
        var keyPath = AccountPictureKey + sid;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hive.OpenSubKey(keyPath, writable: false);
                if (key is null) continue;

                foreach (var valueName in PreferredPictures)
                {
                    if (key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value)
                        continue;
                    var picture = ValidatePicturePath(value, sid);
                    if (picture is not null) return picture;
                }
            }
            catch (Exception ex) when (IsExpectedReadFailure(ex))
            {
                // A missing or inaccessible registry view must not break the rail.
            }
        }

        return null;
    }

    private static string? ValidatePicturePath(string value, string sid)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
            var extension = Path.GetExtension(path);
            if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
                return null;

            if (IsDefaultPicturePath(path) || !IsKnownUserCachePath(path, sid)) return null;
            var info = new FileInfo(path);
            return info.Exists && info.Length is > 0 and <= 10 * 1024 * 1024 ? path : null;
        }
        catch (Exception ex) when (IsExpectedReadFailure(ex))
        {
            return null;
        }
    }

    private static bool IsKnownUserCachePath(string path, string sid)
    {
        var publicRoot = Environment.GetEnvironmentVariable("PUBLIC");
        if (!string.IsNullOrWhiteSpace(publicRoot) &&
            IsUnder(path, Path.Combine(publicRoot, "AccountPictures", sid)))
            return true;

        var roamingRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return !string.IsNullOrWhiteSpace(roamingRoot) &&
            IsUnder(path, Path.Combine(roamingRoot, "Microsoft", "Windows", "AccountPictures"));
    }

    private static bool IsDefaultPicturePath(string path)
    {
        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(commonData)) return false;
        var stockPictures = Path.Combine(commonData, "Microsoft", "User Account Pictures");
        return IsUnder(path, stockPictures);
    }

    private static bool IsUnder(string path, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExpectedReadFailure(Exception exception) => exception is
        ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException or
        System.ComponentModel.Win32Exception or PlatformNotSupportedException or NotSupportedException;
}
