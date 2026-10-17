using System.Security.Cryptography;
using TileWall.Core.Configuration;
using TileWall.Core.Wall;

namespace TileWall.Core.Entries;

public sealed record LaunchIdentity(string Key, string Target, string Arguments, string WorkingDirectory, bool IsUrl);

/// <summary>Shared, read-only identity for scans, external additions and editor commits.</summary>
public sealed class LaunchIdentityService(IFileStore files, ILnkFileService links)
{
    public LaunchIdentity? ResolveMember(WallMember member, string? managedEntryPath) =>
        ResolveInput(managedEntryPath ?? member.TargetInput ?? string.Empty);

    public LaunchIdentity? ResolveInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        if (UrlShortcut.ValidateUrl(input).Count == 0) return ForUrl(input);
        if (!Path.IsPathFullyQualified(input)) return null;
        var path = NormalizePath(input);
        var extension = Path.GetExtension(path);
        if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            var url = UrlShortcut.ReadUrlLine(files.ReadAllBytes(path));
            return url is not null && UrlShortcut.ValidateUrl(url).Count == 0 ? ForUrl(url) : null;
        }
        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var fields = links.Read(path);
            if (!string.IsNullOrWhiteSpace(fields.TargetPath))
                return ForPath(fields.TargetPath, fields.Arguments, fields.WorkingDirectory);
            if (!string.IsNullOrWhiteSpace(fields.ShellParsingName))
                return ForShell(fields.ShellParsingName, fields.Arguments, fields.WorkingDirectory);
            // Identical copied shortcuts remain identical. Unknown, different PIDLs are never all merged.
            var hash = Convert.ToHexString(SHA256.HashData(files.ReadAllBytes(path)));
            return new("opaque:" + hash, path, fields.Arguments, fields.WorkingDirectory, false);
        }
        return ForPath(path, string.Empty, string.Empty);
    }

    public static LaunchIdentity ForPath(string target, string arguments = "", string workingDirectory = "")
    {
        var normalized = NormalizePath(target);
        var work = string.IsNullOrEmpty(workingDirectory) ? string.Empty : NormalizePath(workingDirectory);
        return new(Key("path", normalized.ToUpperInvariant(), arguments, work.ToUpperInvariant()),
            normalized, arguments, work, false);
    }

    private static LaunchIdentity ForShell(string shell, string arguments, string workingDirectory) =>
        new(Key("shell", shell.ToUpperInvariant(), arguments, workingDirectory.ToUpperInvariant()),
            shell, arguments, workingDirectory, false);

    public static LaunchIdentity ForUrl(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        // Uri canonicalizes scheme/host/default port while preserving path/query/fragment case and content.
        var normalized = uri.AbsoluteUri;
        return new(Key("url", normalized, "", ""), normalized, "", "", true);
    }

    public static string NormalizePath(string path)
    {
        var value = Environment.ExpandEnvironmentVariables(path).Replace('/', '\\');
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) value = @"\\" + value[8..];
        else if (value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) value = value[4..];
        if (value.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Device paths are not launch identities.");
        if (Path.IsPathFullyQualified(value))
        {
            value = Path.GetFullPath(value);
            if (OperatingSystem.IsWindows()) value = ShellLinkFileService.NormalizePath(value);
        }
        return Path.TrimEndingDirectorySeparator(value);
    }

    private static string Key(string kind, string target, string args, string work) =>
        $"{kind}:{target.Length}:{target}{args.Length}:{args}{work.Length}:{work}";
}
