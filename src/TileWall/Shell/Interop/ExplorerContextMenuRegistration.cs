using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TileWall.Core.Shell;

namespace TileWall.Shell.Interop;

/// <summary>Current-user, single-selection classic verbs for files and directories.</summary>
[SupportedOSPlatform("windows")]
internal static class ExplorerContextMenuRegistration
{
    internal const string FileVerbPath = @"Software\Classes\*\shell\TileWall.AddToWall";
    internal const string DirectoryVerbPath = @"Software\Classes\Directory\shell\TileWall.AddToWall";
    // InternetShortcut has its own Shell menu; cover both built-in .url ProgIDs explicitly.
    internal const string InternetShortcutVerbPath = @"Software\Classes\InternetShortcut\shell\TileWall.AddToWall";
    internal const string UrlAssociationVerbPath = @"Software\Classes\IE.AssocFile.URL\shell\TileWall.AddToWall";
    internal const string LegacyVerbPath = @"Software\Classes\lnkfile\shell\TileWall.AddToWall";
    private static readonly string[] VerbPaths =
        [FileVerbPath, DirectoryVerbPath, InternetShortcutVerbPath, UrlAssociationVerbPath, LegacyVerbPath];
    private sealed record Value(string Name, object Data, RegistryValueKind Kind);
    private sealed record Snapshot(Value[] Values, Dictionary<string, Snapshot> Children);

    internal static string? Register(string menuText)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            return "TileWall executable is unavailable.";
        return Register(menuText, Registry.CurrentUser, executable, NotifyShell);
    }

    // Tests supply a random registry root and inject failure after a complete key change.
    // Production resolves the executable above and uses the interactive user's HKCU.
    internal static string? Register(string menuText, RegistryKey root, string executable,
        Action notifyShell, Action<string>? afterChange = null)
    {
        var before = new Dictionary<string, Snapshot?>(StringComparer.Ordinal);
        try
        {
            foreach (var path in VerbPaths)
            {
                using var previous = root.OpenSubKey(path);
                before.Add(path, previous is null ? null : Read(previous));
            }
        }
        catch (Exception ex) when (IsRegistryError(ex))
        { return ex.Message; }

        var changed = new List<string>();
        string? error = null;
        try
        {
            var quotedExecutable = Quote(executable);
            foreach (var path in VerbPaths)
            {
                // Track before touching the key so a partially failed write is restored too.
                changed.Add(path);
                if (path == LegacyVerbPath)
                    root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                else
                {
                    using var key = root.CreateSubKey(path, writable: true);
                    key.SetValue(string.Empty, menuText, RegistryValueKind.String);
                    key.SetValue("MUIVerb", menuText, RegistryValueKind.String);
                    key.SetValue("Icon", quotedExecutable, RegistryValueKind.String);
                    key.SetValue("MultiSelectModel", "Single", RegistryValueKind.String);
                    foreach (var hiddenFlag in new[] { "Extended", "LegacyDisable", "ProgrammaticAccessOnly" })
                        key.DeleteValue(hiddenFlag, throwOnMissingValue: false);
                    using var command = key.CreateSubKey("command", writable: true);
                    command.DeleteValue("DelegateExecute", throwOnMissingValue: false);
                    // StartupIntent.Parse and SingleInstanceGate consume this exact entry.
                    command.SetValue(string.Empty, quotedExecutable + " " + ExplorerAddCommandLine.AddArgument + " \"%1\"", RegistryValueKind.String);
                }
                afterChange?.Invoke(path);
            }
        }
        catch (Exception ex) when (IsRegistryError(ex))
        {
            error = ex.Message;
            // Keep restoring other changed keys if one restoration fails.
            foreach (var path in changed.AsEnumerable().Reverse())
            {
                try
                {
                    root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                    if (before[path] is { } snapshot)
                    {
                        using var restored = root.CreateSubKey(path, writable: true);
                        Write(restored, snapshot);
                    }
                }
                catch (Exception rollback) when (IsRegistryError(rollback))
                { error += $" Rollback ({path}): {rollback.Message}"; }
            }
        }
        notifyShell();
        return error;
    }

    private static bool IsRegistryError(Exception error) => error is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException;

    // Windows SDK 10.0.26100.0, ShlObj_core.h: SHCNE_ASSOCCHANGED / SHCNF_IDLIST.
    private static void NotifyShell() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

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
