using Microsoft.Win32;
using TileWall.Core.Shell;

namespace TileWall.Shell.Interop;

/// <summary>Current-user classic .lnk verb; changes only TileWall's dedicated key.</summary>
internal static class ShortcutContextMenuRegistration
{
    internal const string VerbPath = @"Software\Classes\lnkfile\shell\TileWall.AddToWall";
    private sealed record Value(string Name, object Data, RegistryValueKind Kind);
    private sealed record Snapshot(Value[] Values, Dictionary<string, Snapshot> Children);

    internal static string? Register(string menuText)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            return "TileWall executable is unavailable.";
        Snapshot? before;
        try
        {
            using var previous = Registry.CurrentUser.OpenSubKey(VerbPath);
            before = previous is null ? null : Read(previous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return ex.Message; }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(VerbPath, writable: true);
            key.SetValue(string.Empty, menuText, RegistryValueKind.String);
            key.SetValue("MUIVerb", menuText, RegistryValueKind.String);
            key.SetValue("Icon", Quote(executable), RegistryValueKind.String);
            key.SetValue("MultiSelectModel", "Single", RegistryValueKind.String);
            foreach (var hiddenFlag in new[] { "Extended", "LegacyDisable", "ProgrammaticAccessOnly" })
                key.DeleteValue(hiddenFlag, throwOnMissingValue: false);
            using var command = key.CreateSubKey("command", writable: true);
            command.DeleteValue("DelegateExecute", throwOnMissingValue: false);
            // StartupIntent.Parse and SingleInstanceGate already consume this exact entry.
            command.SetValue(string.Empty, Quote(executable) + " " + ExplorerAddCommandLine.AddArgument + " \"%1\"", RegistryValueKind.String);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(VerbPath, throwOnMissingSubKey: false);
                if (before is not null)
                {
                    using var restored = Registry.CurrentUser.CreateSubKey(VerbPath, writable: true);
                    Write(restored, before);
                }
            }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { return ex.Message + " Rollback: " + rollback.Message; }
            return ex.Message;
        }
    }

    private static string Quote(string value)
    {
        // Executable paths cannot contain quotes; double terminal slashes for argv rules.
        if (value.Contains('"')) throw new ArgumentException("Invalid executable path.");
        var withoutTrailingSlashes = value.TrimEnd('\\');
        return "\"" + withoutTrailingSlashes + new string('\\', 2 * (value.Length - withoutTrailingSlashes.Length)) + "\"";
    }

    private static Snapshot Read(RegistryKey key)
    {
        var values = key.GetValueNames().Select(name => new Value(name,
            key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, key.GetValueKind(name))).ToArray();
        var children = new Dictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(name) ?? throw new IOException("Registry key changed while reading.");
            children.Add(name, Read(child));
        }
        return new Snapshot(values, children);
    }

    private static void Write(RegistryKey key, Snapshot snapshot)
    {
        foreach (var value in snapshot.Values) key.SetValue(value.Name, value.Data, value.Kind);
        foreach (var (name, snapshotChild) in snapshot.Children)
        {
            using var child = key.CreateSubKey(name, writable: true);
            Write(child, snapshotChild);
        }
    }
}
