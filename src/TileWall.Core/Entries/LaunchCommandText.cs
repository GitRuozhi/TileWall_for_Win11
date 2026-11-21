namespace TileWall.Core.Entries;

public static class LaunchCommandText
{
    public static string Format(string? path, string arguments)
    {
        if (string.IsNullOrEmpty(path) || arguments.Length == 0) return path ?? string.Empty;
        return (path.Any(char.IsWhiteSpace) ? $"\"{path}\"" : path) + " " + arguments;
    }

    public static string? ForFile(LaunchIdentity? identity) =>
        identity is { IsUrl: false, Arguments.Length: > 0 } &&
        identity.Key.StartsWith("path:", StringComparison.Ordinal) && Path.IsPathFullyQualified(identity.Target)
            ? Format(identity.Target, identity.Arguments)
            : null;
}
