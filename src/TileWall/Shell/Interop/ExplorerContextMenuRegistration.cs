using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TileWall.Core.Shell;

namespace TileWall.Shell.Interop;

internal enum MachineContextMenuStatus { NotRegistered, CurrentExecutable, OtherExecutable, PartialOrMixed, ReadFailed }
internal sealed record MachineContextMenuState(MachineContextMenuStatus Status, IReadOnlyList<string> Targets,
    bool HasUnrecognizedConflict = false, string? Error = null)
{
    public bool IsCurrentExecutableComplete => Status == MachineContextMenuStatus.CurrentExecutable;
}
internal sealed record ContextMenuRegistrationResult(bool Success, bool RollbackIncomplete = false, string? Error = null);

/// <summary>One machine-wide, 64-bit classic-menu registration for every distribution mode.</summary>
[SupportedOSPlatform("windows")]
internal static class ExplorerContextMenuRegistration
{
    internal const string FileVerbPath = @"Software\Classes\*\shell\TileWall.AddToWall";
    internal const string DirectoryVerbPath = @"Software\Classes\Directory\shell\TileWall.AddToWall";
    // Internet shortcuts use a special menu provider; its shared file-system verb needs an explicit URL scope.
    internal const string UrlVerbPath = @"Software\Classes\AllFilesystemObjects\shell\TileWall.AddToWall";
    internal const string UrlAppliesTo = "System.FileExtension:=\".url\"";
    internal const string LegacyInternetShortcutVerbPath = @"Software\Classes\InternetShortcut\shell\TileWall.AddToWall";
    internal const string LegacyUrlAssociationVerbPath = @"Software\Classes\IE.AssocFile.URL\shell\TileWall.AddToWall";
    internal const string LegacyVerbPath = @"Software\Classes\lnkfile\shell\TileWall.AddToWall";
    private static readonly string[] VerbPaths =
        [FileVerbPath, DirectoryVerbPath, UrlVerbPath];
    private static readonly string[] LegacyCleanupPaths =
        [.. VerbPaths, LegacyInternetShortcutVerbPath, LegacyUrlAssociationVerbPath, LegacyVerbPath];
    private const string WriteMutexName = @"Global\TileWall.ContextMenu.Write.v1";
    private const string CommandSuffix = "\" " + ExplorerAddCommandLine.AddArgument + " \"%1\"";
    private sealed record Value(string Name, object Data, RegistryValueKind Kind);
    private sealed record Snapshot(Value[] Values, Dictionary<string, Snapshot> Children);

    internal static MachineContextMenuState ReadMachineState()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return ReadMachineState(root, CurrentExecutable(), IsRecognizedTileWall);
        }
        catch (Exception ex) when (IsRegistryError(ex))
        { return new(MachineContextMenuStatus.ReadFailed, [], Error: ex.Message); }
    }

    internal static MachineContextMenuState ReadMachineState(RegistryKey root, string executable,
        Func<string, bool> recognizeOther)
    {
        try
        {
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var labels = new HashSet<string>(StringComparer.Ordinal);
            var present = 0;
            var complete = 0;
            var conflict = false;
            foreach (var path in VerbPaths)
            {
                using var key = root.OpenSubKey(path);
                if (key is null) continue;
                present++;
                var target = ReadTarget(key);
                if (target is null || (!SameExecutable(target, executable) && !recognizeOther(target)))
                { conflict = true; continue; }
                targets.Add(target);
                if (IsComplete(key, target, path))
                {
                    complete++;
                    labels.Add((string)key.GetValue("MUIVerb")!);
                }
            }
            var status = present == 0 ? MachineContextMenuStatus.NotRegistered
                : !conflict && complete == VerbPaths.Length && targets.Count == 1 && labels.Count == 1
                    ? SameExecutable(targets.Single(), executable) ? MachineContextMenuStatus.CurrentExecutable : MachineContextMenuStatus.OtherExecutable
                    : MachineContextMenuStatus.PartialOrMixed;
            return new(status, targets.Order(StringComparer.OrdinalIgnoreCase).ToArray(), conflict);
        }
        catch (Exception ex) when (IsRegistryError(ex))
        { return new(MachineContextMenuStatus.ReadFailed, [], Error: ex.Message); }
    }

    internal static ContextMenuRegistrationResult RegisterMachineForCurrentExecutable(string menuText) =>
        WithMachineWriteLock(root => RegisterMachine(menuText, root, CurrentExecutable(), NotifyShell, IsRecognizedTileWall));

    internal static ContextMenuRegistrationResult UnregisterMachineOwnedByCurrentExecutable() =>
        WithMachineWriteLock(root => UnregisterOwned(root, CurrentExecutable(), NotifyShell, includeLegacy: true));

    private static ContextMenuRegistrationResult WithMachineWriteLock(Func<RegistryKey, ContextMenuRegistrationResult> operation)
    {
        try
        {
            using var mutex = new Mutex(false, WriteMutexName);
            var held = false;
            try
            {
                try { held = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) return new(false, Error: "Another context-menu operation is still running.");
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                return operation(root);
            }
            finally { if (held) mutex.ReleaseMutex(); }
        }
        catch (Exception ex) when (IsRegistryError(ex) || ex is WaitHandleCannotBeOpenedException)
        { return new(false, Error: ex.Message); }
    }

    // Tests use a random HKCU root; production only reaches this under the machine lock.
    internal static ContextMenuRegistrationResult RegisterMachine(string menuText, RegistryKey root, string executable,
        Action notifyShell, Func<string, bool> recognizeOther, Action<string>? afterChange = null,
        Action<string>? beforeRollback = null)
    {
        if (string.IsNullOrWhiteSpace(menuText)) return new(false, Error: "A menu label is required.");
        return Mutate(root, VerbPaths, notifyShell, (path, previous) =>
        {
            if (previous is not null)
            {
                var target = ReadTarget(previous);
                if (target is null || (!SameExecutable(target, executable) && !recognizeOther(target)))
                    throw new IOException($"Unrecognized context-menu registration: {path}");
            }
            return true;
        }, path =>
        {
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            using var key = root.CreateSubKey(path, writable: true);
            key.SetValue(string.Empty, menuText, RegistryValueKind.String);
            key.SetValue("MUIVerb", menuText, RegistryValueKind.String);
            key.SetValue("Icon", Quote(executable), RegistryValueKind.String);
            key.SetValue("MultiSelectModel", "Single", RegistryValueKind.String);
            if (path == UrlVerbPath) key.SetValue("AppliesTo", UrlAppliesTo, RegistryValueKind.String);
            using var command = key.CreateSubKey("command", writable: true);
            command.SetValue(string.Empty, BuildCommand(executable), RegistryValueKind.String);
        }, afterChange, beforeRollback);
    }

    internal static ContextMenuRegistrationResult UnregisterOwned(RegistryKey root, string executable,
        Action notifyShell, Action<string>? afterChange = null, Action<string>? beforeRollback = null,
        bool includeLegacy = false) => Mutate(root, includeLegacy ? LegacyCleanupPaths : VerbPaths, notifyShell,
            (_, previous) => previous is not null && ReadTarget(previous) is { } target && SameExecutable(target, executable),
            path => root.DeleteSubKeyTree(path, throwOnMissingSubKey: false), afterChange, beforeRollback);

    private static ContextMenuRegistrationResult Mutate(RegistryKey root, string[] paths, Action notifyShell,
        Func<string, RegistryKey?, bool> shouldChange, Action<string> change, Action<string>? afterChange,
        Action<string>? beforeRollback)
    {
        var before = new Dictionary<string, Snapshot?>(StringComparer.Ordinal);
        try
        {
            // Check every ownership and snapshot before the first write, including disable.
            foreach (var path in paths)
            {
                using var key = root.OpenSubKey(path);
                if (shouldChange(path, key)) before.Add(path, key is null ? null : Read(key));
            }
        }
        catch (Exception ex) when (IsRegistryError(ex)) { return new(false, Error: ex.Message); }

        var changed = new List<string>();
        ContextMenuRegistrationResult result = new(true);
        try
        {
            foreach (var path in before.Keys)
            {
                changed.Add(path); // Include a partially written/deleted key in rollback.
                change(path);
                afterChange?.Invoke(path);
            }
        }
        catch (Exception ex) when (IsRegistryError(ex))
        {
            var errors = new List<string>();
            foreach (var path in changed.AsEnumerable().Reverse())
            {
                try
                {
                    beforeRollback?.Invoke(path);
                    root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                    if (before[path] is { } snapshot)
                    {
                        using var restored = root.CreateSubKey(path, writable: true);
                        Write(restored, snapshot);
                    }
                }
                catch (Exception rollback) when (IsRegistryError(rollback))
                { errors.Add($"Rollback ({path}): {rollback.Message}"); }
            }
            result = new(false, errors.Count != 0, ex.Message + (errors.Count == 0 ? string.Empty : " " + string.Join(" ", errors)));
        }
        if (changed.Count != 0) notifyShell();
        return result;
    }

    internal static string BuildCommand(string executable) => Quote(executable) + " " + ExplorerAddCommandLine.AddArgument + " \"%1\"";

    internal static bool TryParseCommand(string? command, out string executable)
    {
        executable = string.Empty;
        if (command is null || command.Length <= CommandSuffix.Length + 1 || !command.StartsWith('"') ||
            !command.EndsWith(CommandSuffix, StringComparison.Ordinal)) return false;
        var target = command[1..^CommandSuffix.Length];
        if (target.Contains('"') || target.Contains('\0') || !Path.IsPathFullyQualified(target) ||
            !string.Equals(BuildCommand(target), command, StringComparison.Ordinal)) return false;
        executable = target;
        return true;
    }

    private static string? ReadTarget(RegistryKey key)
    {
        using var command = key.OpenSubKey("command");
        // DelegateExecute supersedes the textual command and cannot prove ownership.
        if (command is null || command.GetValue("DelegateExecute") is not null || command.GetValue(string.Empty) is not string value ||
            command.GetValueKind(string.Empty) != RegistryValueKind.String) return null;
        return TryParseCommand(value, out var target) ? target : null;
    }

    private static bool IsComplete(RegistryKey key, string target, string path) =>
        key.GetValue("MultiSelectModel") as string == "Single" &&
        string.Equals(key.GetValue("Icon") as string, Quote(target), StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(key.GetValue("MUIVerb") as string) &&
        string.Equals(key.GetValue("MUIVerb") as string, key.GetValue(string.Empty) as string, StringComparison.Ordinal) &&
        (path == UrlVerbPath
            ? key.GetValue("AppliesTo") as string == UrlAppliesTo && key.GetValueKind("AppliesTo") == RegistryValueKind.String
            : key.GetValue("AppliesTo") is null) &&
        new[] { "Extended", "LegacyDisable", "ProgrammaticAccessOnly" }.All(name => key.GetValue(name) is null);

    private static bool IsRecognizedTileWall(string target)
    {
        if (!string.Equals(Path.GetFileName(target), "TileWall.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) return false;
        try
        {
            var version = FileVersionInfo.GetVersionInfo(target);
            return version.ProductName == "TileWall" && version.OriginalFilename == "TileWall.dll";
        }
        catch (Exception ex) when (IsRegistryError(ex)) { return false; }
    }

    private static bool SameExecutable(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string CurrentExecutable()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) throw new IOException("TileWall executable is unavailable.");
        return Path.GetFullPath(executable);
    }

    private static bool IsRegistryError(Exception error) => error is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException or System.ComponentModel.Win32Exception;

    // Windows SDK ShlObj_core.h: SHCNE_ASSOCCHANGED / SHCNF_IDLIST.
    private static void NotifyShell() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private static string Quote(string value)
    {
        if (value.Contains('"') || !Path.IsPathFullyQualified(value)) throw new ArgumentException("Invalid executable path.");
        return "\"" + value + "\"";
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
        return new(values, children);
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
