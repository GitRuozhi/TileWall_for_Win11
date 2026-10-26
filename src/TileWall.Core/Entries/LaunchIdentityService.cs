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
        var kind = EntryNames.KindOfRelativePath(path);
        // A folder keeps its own path identity even if its name ends in .lnk or .url.
        // Ordinary paths need no additional IO; only possible shortcut files are inspected.
        if (kind == EntryKind.None || files.GetPathInfo(path).IsDirectory)
            return ForPath(path, string.Empty, string.Empty);
        return ResolveObservedInput(kind, path, files.ReadAllBytes(path));
    }

    /// <summary>Resolve observed shortcut bytes, including a staged file whose extension is .tmp.</summary>
    public LaunchIdentity? ResolveObservedInput(EntryKind kind, string originalSourcePath, byte[] observedBytes,
        LinkFields? fields = null)
    {
        ArgumentNullException.ThrowIfNull(observedBytes);
        if (kind == EntryKind.Url)
        {
            var url = UrlShortcut.ReadUrlLine(observedBytes);
            return url is not null && UrlShortcut.ValidateUrl(url).Count == 0 ? ForUrl(url) : null;
        }
        if (kind != EntryKind.Lnk) return null;
        fields ??= links.Read(originalSourcePath);
        if (!string.IsNullOrWhiteSpace(fields.TargetPath))
            return ForPath(fields.TargetPath, fields.Arguments, fields.WorkingDirectory);
        if (!string.IsNullOrWhiteSpace(fields.ShellParsingName))
            return ForShell(fields.ShellParsingName, fields.Arguments, fields.WorkingDirectory);
        // Identical opaque shortcuts remain identical without guessing their PIDL target.
        var hash = Convert.ToHexString(SHA256.HashData(observedBytes));
        return new("opaque:" + hash, originalSourcePath, fields.Arguments, fields.WorkingDirectory, false);
    }

    public static LaunchIdentity ForPath(string target, string arguments = "", string workingDirectory = "")
    {
        var normalized = NormalizePath(target);
        var work = string.IsNullOrEmpty(workingDirectory) ? string.Empty : NormalizePath(workingDirectory);
        return new(Key("path", normalized.ToUpperInvariant(), arguments, work.ToUpperInvariant()),
            normalized, arguments, work, false);
    }

    public static LaunchIdentity ForShell(string shell, string arguments = "", string workingDirectory = "") =>
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
