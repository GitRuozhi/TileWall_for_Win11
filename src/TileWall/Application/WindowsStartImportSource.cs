using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Import;
using TileWall.Core.Shell;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using TileWall.Shell.Interop;

namespace TileWall.ApplicationModel;

internal sealed record WindowsStartObservedGroup(string Title, IReadOnlyList<WindowsStartImportEntry> Entries);

internal sealed class WindowsStartImportSnapshot(WindowsStartLayout layout, string defaultGroupName,
    IReadOnlyList<WindowsStartObservedGroup> groups)
{
    internal WindowsStartLayout Layout { get; } = layout;
    internal string DefaultGroupName { get; } = defaultGroupName;
    internal IReadOnlyList<WindowsStartObservedGroup> Groups { get; } = groups;
    internal int TotalCount => Layout.TotalCount;
    internal int UnresolvedCount => TotalCount - Groups.Sum(group => group.Entries.Count);
}

internal sealed class WindowsStartImportEntry(int sourceIndex, WindowsStartPin pin, NativeImportEntry observed)
{
    internal int SourceIndex { get; } = sourceIndex;
    internal WindowsStartPin Pin { get; } = pin;
    internal NativeImportEntry Observation { get; } = observed;
    internal string Title => Observation.Name;
    internal LaunchIdentity Identity => Observation.Identity!;
    internal string TargetInput => Observation.Kind == NativeImportTarget.Shortcut ? Identity.Target : Observation.Target;
    internal string? SourceShortcutPath => Observation.SourcePath;
    internal bool TargetIsDirectory => Observation.TargetIsDirectory;
    internal ManagedEntryKind EntryKind => Observation.Kind == NativeImportTarget.Uri ||
        SourceShortcutPath is { } path && EntryNames.KindOfRelativePath(path) == Core.Entries.EntryKind.Url
        ? ManagedEntryKind.Url : ManagedEntryKind.Lnk;

    internal EntryChange CreateEntryChange(string memberId) => Observation.Kind switch
    {
        NativeImportTarget.Shortcut => new EntryChange.CopyShortcut(memberId, SourceShortcutPath!, Observation.Sha256),
        NativeImportTarget.Path => new EntryChange.CreateLink(memberId, TargetInput),
        NativeImportTarget.Shell => new EntryChange.CreateShellLink(memberId, TargetInput, Identity.Key)
            { NativeTarget = Observation.NativeTarget },
        NativeImportTarget.Uri => new EntryChange.CreateUrl(memberId, TargetInput),
        _ => throw new InvalidOperationException("The pinned item has no entry change."),
    };
}

/// <summary>Reads the current user's actual pinned Start layout and observes only its entries.</summary>
internal sealed class WindowsStartImportSource(NativeImportCatalog catalog)
{
    private const string ExportPathVariable = "TILEWALL_START_LAYOUT_FILE";
    private const string ExportCommand = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; " +
        "Import-Module -Name ($PSHOME + '\\Modules\\StartLayout\\StartLayout.psd1'); " +
        "StartLayout\\Export-StartLayout -Path $env:" + ExportPathVariable;

    internal async Task<WindowsStartImportSnapshot> ReadAsync(string defaultGroupName, CancellationToken token)
    {
        var layout = await ExportAsync(defaultGroupName, token).ConfigureAwait(false);
        return await Task.Run(() => ReadEntries(layout, defaultGroupName, token), token).ConfigureAwait(false);
    }

    internal async Task ConfirmAsync(WindowsStartImportSnapshot snapshot, IReadOnlyCollection<int> acceptedSourceIndices,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (acceptedSourceIndices.Count == 0) return;
        var wanted = acceptedSourceIndices.ToHashSet();
        var accepted = snapshot.Groups.SelectMany(group => group.Entries).Where(entry => wanted.Contains(entry.SourceIndex)).ToArray();
        if (wanted.Count != accepted.Length) throw new ArgumentException("A confirmed pin is absent from the observation.", nameof(acceptedSourceIndices));
        try
        {
            var current = await ExportAsync(snapshot.DefaultGroupName, token).ConfigureAwait(false);
            if (!snapshot.Layout.SamePins(current)) throw new ImportSourceChangedException("The Windows Start layout changed.");
            await Task.Run(() => ConfirmEntries(accepted, token), token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ImportSourceChangedException && ObservationFailure(error))
        {
            throw new ImportSourceChangedException("The Windows Start source changed or is unavailable.", error);
        }
    }

    private WindowsStartImportSnapshot ReadEntries(WindowsStartLayout layout, string defaultGroupName, CancellationToken token)
    {
        var applications = FindApplications(layout.Groups.SelectMany(group => group.Pins), token);
        var groups = new List<WindowsStartObservedGroup>();
        var observedPins = new Dictionary<WindowsStartPin, NativeImportEntry?>();
        var index = 0;
        foreach (var group in layout.Groups)
        {
            var entries = new List<WindowsStartImportEntry>();
            foreach (var pin in group.Pins)
            {
                token.ThrowIfCancellationRequested();
                if (!observedPins.TryGetValue(pin, out var observed))
                {
                    try { observed = Observe(pin, applications, token); }
                    catch (Exception error) when (ObservationFailure(error)) { observed = null; }
                    observedPins.Add(pin, observed);
                }
                if (observed is { Error: null, Identity: not null }) entries.Add(new(index, pin, observed));
                index++;
            }
            if (entries.Count != 0) groups.Add(new(group.Title, entries.AsReadOnly()));
        }
        token.ThrowIfCancellationRequested();
        return new(layout, defaultGroupName, groups.AsReadOnly());
    }

    private void ConfirmEntries(IReadOnlyList<WindowsStartImportEntry> entries, CancellationToken token)
    {
        var applications = FindApplications(entries.Select(entry => entry.Pin), token);
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var current = Observe(entry.Pin, applications, token);
            if (current is not { Identity: not null, Error: null } || !SameEntry(entry.Observation, current))
                throw new ImportSourceChangedException("A Windows Start entry changed.");
        }
        token.ThrowIfCancellationRequested();
    }

    private NativeImportEntry? Observe(WindowsStartPin pin, IReadOnlyDictionary<string, ShellItemCatalog.Item> applications,
        CancellationToken token) => pin.Kind switch
    {
        WindowsStartPinKind.LinkPath => catalog.ObservePinnedPath(pin.Value, token),
        WindowsStartPinKind.ApplicationId when applications.TryGetValue(pin.Value, out var item) => catalog.ObservePinnedApplication(item, token),
        _ => null,
    };

    private static IReadOnlyDictionary<string, ShellItemCatalog.Item> FindApplications(IEnumerable<WindowsStartPin> pins,
        CancellationToken token)
    {
        var ids = pins.Where(pin => pin.Kind == WindowsStartPinKind.ApplicationId && !string.IsNullOrWhiteSpace(pin.Value))
            .Select(pin => pin.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try { return ShellItemCatalog.FindApplicationsById(ids, token); }
        catch (Exception error) when (ObservationFailure(error))
        { throw new IOException("The registered Windows applications could not be read.", error); }
    }

    private static bool SameEntry(NativeImportEntry expected, NativeImportEntry current)
    {
        if (expected.NativeTarget is { } native)
        {
            if (current.NativeTarget is not { } latest ||
                expected.PackageFullName != current.PackageFullName || expected.PackageInstallPath != current.PackageInstallPath ||
                !ShellItemCatalog.SameNativeTarget(native, latest)) return false;
            current = current with { Identity = expected.Identity, NativeTarget = native };
        }
        return expected == current;
    }

    private static async Task<WindowsStartLayout> ExportAsync(string defaultGroupName, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var json = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        string? directory = null;
        string? output = null;
        try
        {
            using var user = WindowsIdentity.GetCurrent();
            if (!Environment.UserInteractive || new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator))
                throw new IOException("The Start layout requires the ordinary interactive Windows user.");
            NativeImportCatalog.ValidateDirectoryRoot(Path.GetTempPath());
            directory = Directory.CreateTempSubdirectory("TileWall-StartLayout-").FullName;
            NativeImportCatalog.ValidateDirectoryRoot(directory);
            output = Path.Combine(directory, json ? "layout.json" : "layout.xml");
            await RunExportAsync(output, token).ConfigureAwait(false);

            NativeImportCatalog.ValidateFilePath(output);
            await using var stream = new LocalFileStore().OpenReadProtected(output);
            if (stream.Length is < 1 or > WindowsStartLayout.MaximumBytes)
                throw new InvalidDataException("Invalid Windows Start layout size.");
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            NativeImportCatalog.ValidateFilePath(output);
            return WindowsStartLayout.Parse(bytes, json, defaultGroupName, token);
        }
        catch (Exception error) when (error is not InvalidDataException && ObservationFailure(error))
        { throw new IOException("Windows could not export the current Start layout.", error); }
        finally
        {
            if (directory is not null) CleanExport(directory, output);
        }
    }

    private static async Task RunExportAsync(string output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // The Sysnative alias selects the native system module from the x86 build on 64-bit Windows.
        var system = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative") : Environment.SystemDirectory;
        var powershell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(powershell)!,
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", ExportCommand })
            start.ArgumentList.Add(argument);
        start.Environment[ExportPathVariable] = output;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(start) ?? throw new IOException("Windows PowerShell did not start.");
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            throw new IOException("Windows Start layout export timed out.");
        }
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new IOException($"Windows Start layout export exited with code {process.ExitCode}.");
    }

    private static void CleanExport(string directory, string? output)
    {
        try
        {
            NativeImportCatalog.ValidateDirectoryRoot(directory);
            if (output is not null)
            {
                var info = new LocalFileStore().GetPathInfo(output);
                if (info is { Exists: true, IsDirectory: false, IsReparsePoint: false }) File.Delete(output);
            }
            Directory.Delete(directory);
        }
        catch (Exception error) when (ObservationFailure(error))
        { AppLog.Warn("WindowsStartImport", $"Temporary export cleanup failed: {error.GetType().Name}"); }
    }

    private static bool ObservationFailure(Exception error) => error is IOException or InvalidDataException or
        UnauthorizedAccessException or ArgumentException or COMException or InvalidCastException or Win32Exception or SecurityException;
}
