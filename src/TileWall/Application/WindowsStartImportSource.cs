using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using TileWall.Core.Configuration;
using TileWall.Core.Import;
using TileWall.Core.Shell;
using TileWall.Shell.Interop;

namespace TileWall.ApplicationModel;

internal sealed record WindowsStartObservedGroup(string Title, IReadOnlyList<WindowsStartImportEntry> Entries);

internal sealed class WindowsStartImportSnapshot(IReadOnlyList<WindowsStartObservedGroup> groups)
{
    private readonly IReadOnlyDictionary<string, WindowsStartImportEntry> _entries = groups.SelectMany(group => group.Entries)
        .ToDictionary(entry => entry.Observation.Key, StringComparer.Ordinal);
    private readonly IReadOnlyList<NativeImportEntry> _nativeEntries = Array.AsReadOnly(groups.SelectMany(group => group.Entries)
        .Select(entry => entry.Observation).ToArray());

    internal IReadOnlyList<WindowsStartObservedGroup> Groups { get; } = groups;
    internal int TotalCount => _nativeEntries.Count;
    internal int UnresolvedCount => _entries.Values.Count(entry => !entry.IsAvailable);
    internal IReadOnlyList<NativeImportEntry> ToNativeEntries() => _nativeEntries;

    internal WindowsStartImportEntry Find(string key) => _entries.GetValueOrDefault(key) is { IsAvailable: true } entry
        ? entry : throw new ImportSourceChangedException("The selected Windows Start entry is unavailable.");
}

internal sealed record WindowsStartImportEntry(WindowsStartPin Pin, NativeImportEntry Observation)
{
    internal bool IsAvailable => Observation is { Error: null, Identity: not null };
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
        var timing = StartTiming();
        var layout = await ExportAsync(defaultGroupName, token).ConfigureAwait(false);
        TraceTiming(timing, "export");
        timing = StartTiming();
        var snapshot = await Task.Run(() => ReadEntries(layout, defaultGroupName, token), token).ConfigureAwait(false);
        TraceTiming(timing, "observe-inputs");
        return snapshot;
    }

    internal async Task<IReadOnlyDictionary<string, NativeImportEntry>> ConfirmAsync(WindowsStartImportSnapshot snapshot,
        IReadOnlyCollection<string> keys, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var timing = StartTiming();
            var confirmed = await Task.Run(() => ConfirmEntries(keys.Select(snapshot.Find).ToArray(), token), token).ConfigureAwait(false);
            TraceTiming(timing, "confirm-inputs");
            return confirmed;
        }
        catch (Exception error) when (error is not ImportSourceChangedException && ObservationFailure(error))
        {
            throw new ImportSourceChangedException("The Windows Start source changed or is unavailable.", error);
        }
    }

    internal static long StartTiming() => Environment.GetEnvironmentVariable("TILEWALL_PERF") == "1"
        ? Stopwatch.GetTimestamp() : 0;

    internal static void TraceTiming(long start, string stage)
    {
        if (start != 0) AppLog.Warn("WindowsStartImportPerformance",
            FormattableString.Invariant($"stage={stage} elapsedMs={Stopwatch.GetElapsedTime(start).TotalMilliseconds:F3}"));
    }

    private WindowsStartImportSnapshot ReadEntries(WindowsStartLayout layout, string defaultGroupName, CancellationToken token)
    {
        var applications = FindApplications(layout.Groups.SelectMany(group => group.Pins), token);
        var groups = new List<WindowsStartObservedGroup>();
        var observedPins = new Dictionary<WindowsStartPin, NativeImportEntry?>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var companies = new Dictionary<string, (string? Company, string? Stamp)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (group, order) in layout.Groups.Select((group, order) => (group, order)))
        {
            var entries = new List<WindowsStartImportEntry>();
            var groupKey = FormattableString.Invariant($"windows-start-group-{order}");
            foreach (var pin in group.Pins)
            {
                token.ThrowIfCancellationRequested();
                if (!observedPins.TryGetValue(pin, out var observed))
                {
                    try { observed = Observe(pin, applications, companies, token); }
                    catch (Exception error) when (ObservationFailure(error)) { observed = null; }
                    observedPins.Add(pin, observed);
                }
                var pinKey = NativeImportCatalog.Key(FormattableString.Invariant($"windows-start|{pin.Kind}|{pin.Value.Length}|{pin.Value}|{group.Title}"));
                var occurrence = occurrences.GetValueOrDefault(pinKey);
                occurrences[pinKey] = occurrence + 1;
                var key = occurrence == 0 ? pinKey : NativeImportCatalog.Key(FormattableString.Invariant($"{pinKey}|{occurrence}"));
                observed ??= new(key, defaultGroupName, "", NativeImportTarget.Path, group.Title, false, false, Error: "InvalidTarget");
                entries.Add(new(pin, observed with { Key = key, Group = group.Title, GroupKey = groupKey, GroupOrder = order }));
            }
            if (entries.Count != 0) groups.Add(new(group.Title, entries.AsReadOnly()));
        }
        token.ThrowIfCancellationRequested();
        return new(groups.AsReadOnly());
    }

    private IReadOnlyDictionary<string, NativeImportEntry> ConfirmEntries(IReadOnlyList<WindowsStartImportEntry> entries,
        CancellationToken token)
    {
        var applications = FindApplications(entries.Select(entry => entry.Pin), token);
        var companies = new Dictionary<string, (string? Company, string? Stamp)>(StringComparer.OrdinalIgnoreCase);
        var confirmed = new Dictionary<string, NativeImportEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var expected = entry.Observation;
            var current = Observe(entry.Pin, applications, companies, token);
            if (current is not { Identity: not null, Error: null } || !SameEntry(expected, current with
                { Key = expected.Key, Group = expected.Group, GroupKey = expected.GroupKey, GroupOrder = expected.GroupOrder }))
                throw new ImportSourceChangedException("A Windows Start entry changed.");
            // SameEntry proves the observed bytes and canonical target still agree.
            // Keep the exact trusted snapshot already observed by the import page.
            confirmed.Add(expected.Key, expected);
        }
        token.ThrowIfCancellationRequested();
        return confirmed;
    }

    private NativeImportEntry? Observe(WindowsStartPin pin, IReadOnlyDictionary<string, ShellItemCatalog.Item> applications,
        IDictionary<string, (string? Company, string? Stamp)> companies, CancellationToken token)
    {
        var observed = pin.Kind switch
        {
            WindowsStartPinKind.LinkPath => catalog.ObservePinnedPath(pin.Value, token),
            WindowsStartPinKind.ApplicationId when applications.TryGetValue(pin.Value, out var item) => catalog.ObservePinnedApplication(item, token),
            _ => null,
        };
        return observed is null ? null : NativeImportCatalog.ObserveCompany(observed, token, companies);
    }

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
