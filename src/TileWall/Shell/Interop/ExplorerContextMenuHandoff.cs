using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using TileWall.Core.Shell;

namespace TileWall.Shell.Interop;

/// <summary>Coordinates both physical menu providers and recovers interrupted menu changes.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ExplorerContextMenuHandoff(ExplorerMenuPreference preference, bool packaged, string executable,
    Func<ExplorerContextMenuRegistry> openRegistry, Func<bool> isPackageInstalled, Func<string, bool> recognizeTileWall,
    Action notifyShell)
{
    private const string PackageFamily = "Ruozhi.TileWall_k0yb5fxbm3zce";
    private const int InsufficientBuffer = 122;
    private const int EnableVersion = 1;
    private const int DisableVersion = 2;
    private const int InstallVersion = 3;
    private static readonly IReadOnlyList<string> Paths = ExplorerContextMenuRegistration.HandoffPaths;
    private static readonly JsonSerializerOptions JournalOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    private enum EnableSource { Automatic, Settings, Installer }

    private sealed record Journal(int Version, string Target, bool PackageInstalled, bool? PackageBefore,
        ExplorerMenuVerb?[]? ClassicBefore, string? Label, string? Failure = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Started = 0)
    {
        internal bool DisablesAll => Version == DisableVersion;
        internal bool IsInstall => Version == InstallVersion;
        internal bool StartedAt(int index) => (Started & (1 << index)) != 0;
    }

    private sealed record RegisteredMenus(ExplorerMenuVerb?[] Classic, ExplorerMenuVerb?[] Owned,
        bool PackageInstalled, bool? PackageOverride, string[] Unverified)
    {
        internal bool PackageEnabled => PackageInstalled && PackageOverride != false;
        internal bool Enabled => PackageEnabled || Owned.Any(entry => entry is not null);
    }

    private string Target => packaged ? PackageFamily : executable;

    internal ExplorerContextMenuHandoff(ExplorerMenuPreference preference, bool packaged, string executable) :
        this(preference, packaged, executable, OpenPhysicalRegistry, IsPackageInstalled,
            ExplorerContextMenuRegistration.IsRecognizedTileWall, ExplorerContextMenuRegistration.NotifyShell) { }

    internal string? ReadFailure()
    {
        var journal = ReadJournal();
        if (journal is null || !journal.DisablesAll && !SameTarget(journal.Target, Target)) return null;
        return journal.Failure ?? "The Explorer menu handoff awaits recovery.";
    }

    internal ExplorerContextMenuState ReadState()
    {
        using var registry = openRegistry();
        var observed = Observe(registry);
        var complete = observed.PackageEnabled && observed.Classic.All(entry => entry is null);
        if (!packaged)
        {
            var current = ExplorerContextMenuRegistration.ReadCurrentUserState();
            if (current.Status == ContextMenuStatus.ReadFailed)
                throw new IOException(current.Error ?? "Unable to read Explorer registration.");
            complete = current.IsCurrentExecutableComplete && !observed.PackageEnabled &&
                observed.Unverified.Length == 0 &&
                observed.Classic.All(entry => entry is null || IsOwnEntry(entry, executable));
        }
        return new(observed.Enabled, complete, UnverifiedEntries: observed.Unverified);
    }

    internal ContextMenuRegistrationResult Enable(string label, bool automatic) =>
        Enable(label, automatic ? EnableSource.Automatic : EnableSource.Settings);

    internal static ContextMenuRegistrationResult EnableFromInstaller(ExplorerMenuPreference preference, string label) =>
        ExplorerContextMenuRegistration.WithWriteLock(() =>
            new ExplorerContextMenuHandoff(preference, false, ExplorerContextMenuRegistration.CurrentExecutable())
                .EnableForInstallation(label));

    internal ContextMenuRegistrationResult EnableForInstallation(string label) => Enable(label, EnableSource.Installer);

    private ContextMenuRegistrationResult Enable(string label, EnableSource source)
    {
        var automatic = source == EnableSource.Automatic;
        var installer = source == EnableSource.Installer;
        ExplorerContextMenuRegistry? registry = null;
        Journal? before = null;
        try
        {
            var journal = ReadJournal();
            if (journal?.ClassicBefore is not null && (journal.Failure is null || !automatic))
            {
                registry = openRegistry();
                var recovered = Recover(registry, journal);
                if (recovered.RollbackIncomplete || (!installer && !recovered.Success) ||
                    (automatic && SameTarget(journal.Target, Target))) return recovered;
                journal = ReadJournal();
                if (journal?.ClassicBefore is not null) return recovered;
            }
            if (automatic && journal?.Failure is not null &&
                (journal.ClassicBefore is not null || SameTarget(journal.Target, Target)))
                return new(true);

            registry ??= openRegistry();
            var observed = Observe(registry);
            var classic = observed.Classic;
            var packageInstalled = observed.PackageInstalled;
            var hasOtherClassic = classic.Any(entry => entry is not null && (packaged || !IsOwnEntry(entry, executable)));
            var otherEnabled = hasOtherClassic || !packaged && observed.PackageEnabled;
            if (automatic && (!otherEnabled || !observed.Enabled)) return new(true);

            if (observed.Unverified.Length != 0)
                throw new IOException($"Unrecognized Explorer menu registration: {string.Join(", ", observed.Unverified)}");
            if (packaged && !hasOtherClassic)
            {
                preference.WriteOverride(true);
                ClearOwnFailure(journal);
                notifyShell();
                return new(true);
            }

            var desired = DesiredClassic(Target, label);
            if (!otherEnabled && classic.SequenceEqual(desired))
            {
                ClearOwnFailure(journal);
                return new(true);
            }

            var prepared = new Journal(installer ? InstallVersion : EnableVersion, Target, packageInstalled,
                observed.PackageOverride, classic, label);
            if (!installer) before = prepared;
            SaveJournal(prepared);
            before = prepared;
            if (packageInstalled && (packaged || otherEnabled))
            {
                if (!installer) preference.WriteOverride(false);
                else if (before.PackageBefore != false)
                {
                    if (preference.ReadOverride() != before.PackageBefore)
                        throw new IOException("The Store menu setting changed before it could be closed.");
                    before = RecordStarted(before, Paths.Count);
                    preference.WriteOverride(false);
                }
            }
            SetClassic(registry, classic, desired, installer ? index => before = RecordStarted(before!, index) : null);
            if (packaged) preference.WriteOverride(true);
            if (!MatchesDestination(registry, before))
                throw new IOException("Unable to verify the Explorer menu handoff.");
            SaveJournal(null);
            notifyShell();
            return new(true);
        }
        catch (Exception ex) when (IsOperationError(ex))
        {
            if (before is not null && registry is not null)
                return RollBack(registry, before, ex.Message);
            if (installer) return new(false, Error: ex.Message);
            return FailWithoutSnapshot(registry, ex.Message);
        }
        finally { registry?.Dispose(); }
    }

    internal ContextMenuRegistrationResult Disable()
    {
        try
        {
            using var registry = openRegistry();
            var journal = ReadJournal();
            if (journal?.ClassicBefore is not null)
            {
                var recovered = Recover(registry, journal);
                if (recovered.RollbackIncomplete || !recovered.Success && ReadJournal()?.ClassicBefore is not null)
                    return recovered;
                journal = ReadJournal();
            }

            var observed = Observe(registry);
            if (!observed.Enabled)
            {
                ClearOwnFailure(journal);
                return new(true);
            }

            // Version 1 is still the existing enable/transfer format. This operation
            // records only the keys whose complete snapshot we intend to remove.
            var before = new Journal(DisableVersion, Target, observed.PackageInstalled,
                observed.PackageOverride, observed.Owned, null);
            SaveJournal(before);
            try
            {
                if (before.PackageInstalled)
                {
                    if (preference.ReadOverride() != before.PackageBefore)
                        throw new IOException("The Store menu setting changed before it could be closed.");
                    if (before.PackageBefore != false)
                    {
                        before = RecordStarted(before, Paths.Count);
                        preference.WriteOverride(false);
                    }
                }
                for (var index = 0; index < Paths.Count; index++)
                {
                    if (observed.Owned[index] is not { } entry) continue;
                    var path = Paths[index];
                    if (registry.Read(path) != entry)
                        throw new IOException($"Explorer menu ownership changed: {path}");
                    before = RecordStarted(before, index);
                    registry.Delete(path);
                    if (registry.Read(path) is not null)
                        throw new IOException($"Unable to close the Explorer menu: {path}");
                }
                if (!MatchesDisabled(registry, before))
                    throw new IOException("Unable to verify that every recognized Explorer menu was closed.");
                SaveJournal(null);
                notifyShell();
                return new(true);
            }
            catch (Exception ex) when (IsOperationError(ex)) { return RollBackDisable(registry, before, ex.Message); }
        }
        catch (Exception ex) when (IsOperationError(ex)) { return new(false, Error: ex.Message); }
    }

    private ContextMenuRegistrationResult Recover(ExplorerContextMenuRegistry registry, Journal journal)
    {
        try
        {
            if (journal.Failure is null && (journal.DisablesAll
                ? MatchesDisabled(registry, journal) : MatchesDestination(registry, journal)))
            {
                SaveJournal(null);
                notifyShell();
                return new(true);
            }
        }
        catch (Exception ex) when (IsOperationError(ex))
        {
            return journal.DisablesAll
                ? RollBackDisable(registry, journal, ex.Message) : RollBack(registry, journal, ex.Message);
        }
        var failure = journal.Failure ?? "A previous Explorer menu operation was interrupted.";
        var rollback = journal.DisablesAll
            ? RollBackDisable(registry, journal, failure) : RollBack(registry, journal, failure);
        // A later explicit retry may continue once recovery has released the two providers.
        return journal.Failure is not null && !rollback.RollbackIncomplete ? new(true) : rollback;
    }

    private ContextMenuRegistrationResult RollBack(ExplorerContextMenuRegistry registry, Journal journal, string failure)
    {
        if (journal.IsInstall) return RollBackInstallation(registry, journal, failure);

        try
        {
            var destination = DesiredClassic(journal.Target, journal.Label!);
            var prior = journal.ClassicBefore!;
            DisableTarget(registry, journal.Target, destination, prior);

            for (var index = 0; index < Paths.Count; index++)
            {
                var original = prior[index];
                if (original is null || IsOwnEntry(original, journal.Target)) continue;
                RestoreClassic(registry, Paths[index], original);
            }
            if (journal.Target != PackageFamily && journal.PackageInstalled)
                preference.WriteOverride(journal.PackageBefore);
            RecordFailure(journal.Target, failure);
            notifyShell();
            return new(false, Error: failure);
        }
        catch (Exception ex) when (IsOperationError(ex))
        {
            return RecordIncompleteRollback(journal, failure, ex.Message);
        }
    }

    private ContextMenuRegistrationResult RollBackDisable(ExplorerContextMenuRegistry registry, Journal journal,
        string failure)
    {
        try
        {
            for (var index = Paths.Count - 1; index >= 0; index--)
                if (journal.StartedAt(index))
                    RestoreClassic(registry, Paths[index], journal.ClassicBefore![index]!);

            if (journal.StartedAt(Paths.Count)) RestoreStoreMenu(journal);
            SaveJournal(null);
            if (journal.Started != 0) notifyShell();
            return new(false, Error: failure);
        }
        catch (Exception ex) when (IsOperationError(ex))
        {
            return RecordIncompleteRollback(journal, failure, ex.Message);
        }
    }

    private ContextMenuRegistrationResult RollBackInstallation(ExplorerContextMenuRegistry registry, Journal journal,
        string failure)
    {
        // Restore only providers this install began changing. A conflict at one key
        // must not prevent restoration of other verifiable snapshots.
        var desired = DesiredClassic(journal.Target, journal.Label!);
        var errors = new List<string>();
        for (var index = Paths.Count - 1; index >= 0; index--)
        {
            if (!journal.StartedAt(index)) continue;
            try { RestoreClassic(registry, Paths[index], journal.ClassicBefore![index], desired[index]); }
            catch (Exception ex) when (IsOperationError(ex)) { errors.Add(ex.Message); }
        }
        try
        {
            if (journal.StartedAt(Paths.Count)) RestoreStoreMenu(journal);
            if (errors.Count == 0) SaveJournal(null);
        }
        catch (Exception ex) when (IsOperationError(ex)) { errors.Add(ex.Message); }
        if (errors.Count != 0) return RecordIncompleteRollback(journal, failure, string.Join(" ", errors));
        if (journal.Started != 0) notifyShell();
        return new(false, Error: failure);
    }

    private void RestoreStoreMenu(Journal journal)
    {
        var actual = preference.ReadOverride();
        if (actual != false && actual != journal.PackageBefore)
            throw new IOException("The Store menu setting changed before it could be restored.");
        preference.WriteOverride(journal.PackageBefore);
    }

    private void RestoreClassic(ExplorerContextMenuRegistry registry, string path, ExplorerMenuVerb? original,
        ExplorerMenuVerb? attempted = null)
    {
        var actual = registry.Read(path);
        if (actual == original) return;
        if (original is not null && !TryRecognize(original, path, out _))
            throw new IOException($"Previous Explorer menu ownership can no longer be verified: {path}");
        if (actual is not null)
        {
            if (!(original is not null && actual.IsPartOf(original)) &&
                !(attempted is not null && actual.IsPartOf(attempted)))
                throw new IOException($"Explorer menu ownership changed: {path}");
            registry.Delete(path);
            if (registry.Read(path) is not null) throw new IOException($"Unable to close the Explorer menu: {path}");
        }
        if (original is not null) registry.Write(path, original);
        if (registry.Read(path) != original) throw new IOException($"Unable to restore the Explorer menu: {path}");
    }

    private ContextMenuRegistrationResult RecordIncompleteRollback(Journal journal, string failure, string detail)
    {
        var error = failure + " " + detail;
        try { SaveJournal(journal with { Failure = error }); }
        catch (Exception recordError) when (IsOperationError(recordError)) { error += " " + recordError.Message; }
        notifyShell();
        return new(false, RollbackIncomplete: true, Error: error);
    }

    private Journal RecordStarted(Journal journal, int index)
    {
        if (journal.StartedAt(index)) return journal;
        var started = journal with { Started = journal.Started | (1 << index) };
        SaveJournal(started);
        return started;
    }

    private ContextMenuRegistrationResult FailWithoutSnapshot(ExplorerContextMenuRegistry? registry, string failure)
    {
        try
        {
            if (packaged) preference.WriteOverride(false);
            else if (registry is not null) DisableTarget(registry, Target, null);
            else
            {
                using var currentUser = openRegistry();
                DisableTarget(currentUser, Target, null);
            }
            var journal = ReadJournal();
            if (journal?.ClassicBefore is null) RecordFailure(Target, failure);
            notifyShell();
            return new(false, Error: failure);
        }
        catch (Exception ex) when (IsOperationError(ex))
        {
            notifyShell();
            return new(false, RollbackIncomplete: true, Error: failure + " " + ex.Message);
        }
    }

    private void DisableTarget(ExplorerContextMenuRegistry registry, string target, ExplorerMenuVerb?[]? desired,
        ExplorerMenuVerb?[]? previous = null)
    {
        if (target == PackageFamily)
        {
            preference.WriteOverride(false);
            return;
        }
        var unverified = new List<string>();
        for (var index = 0; index < Paths.Count; index++)
        {
            var path = Paths[index];
            var actual = registry.Read(path);
            if (actual is null) continue;
            var owned = TryRecognize(actual, path, out var owner) && SameTarget(owner, target);
            var matchesExpected = desired?[index] is { } intended && actual.IsPartOf(intended);
            var matchesPrevious = previous?[index] == actual;
            if (!(matchesExpected || owned && (previous is null || matchesPrevious)))
            {
                if (IsOwnEntry(actual, target)) unverified.Add(path);
                continue;
            }
            registry.Delete(path);
            if (registry.Read(path) is not null) throw new IOException($"Unable to close the Explorer menu: {path}");
        }
        if (previous is not null)
        {
            for (var index = 0; index < Paths.Count; index++)
            {
                var actual = registry.Read(Paths[index]);
                if (actual is null) continue;
                var original = previous[index];
                // The original provider is safe only if unchanged, or if its exact
                // command removal is the only observable interrupted operation.
                if (original is null || IsOwnEntry(original, target) ||
                    actual != original && actual != (original with { Command = null }))
                    unverified.Add(Paths[index]);
            }
        }
        if (unverified.Count != 0)
            throw new IOException($"Unable to verify that the target menu is closed: {string.Join(", ", unverified.Distinct())}");
    }

    private static void SetClassic(ExplorerContextMenuRegistry registry, ExplorerMenuVerb?[] previous,
        ExplorerMenuVerb?[] desired, Action<int>? startChange)
    {
        for (var index = 0; index < Paths.Count; index++)
        {
            if (previous[index] == desired[index]) continue;
            var path = Paths[index];
            if (registry.Read(path) != previous[index])
                throw new IOException($"Explorer menu ownership changed: {path}");
            if (previous[index] is not null)
            {
                startChange?.Invoke(index);
                registry.Delete(path);
            }
            if (registry.Read(path) is not null)
                throw new IOException($"Unable to remove the previous Explorer menu: {path}");
        }
        for (var index = 0; index < Paths.Count; index++)
        {
            if (previous[index] == desired[index] || desired[index] is not { } entry) continue;
            var path = Paths[index];
            if (registry.Read(path) is not null) throw new IOException($"Explorer menu ownership changed: {path}");
            startChange?.Invoke(index);
            registry.Write(path, entry);
            if (registry.Read(path) != entry)
                throw new IOException($"Unable to verify the current Explorer menu: {path}");
        }
    }

    private bool MatchesDestination(ExplorerContextMenuRegistry registry, Journal journal) =>
        ReadAll(registry).SequenceEqual(DesiredClassic(journal.Target, journal.Label!)) &&
        (!(journal.PackageInstalled || journal.IsInstall && isPackageInstalled()) ||
            preference.ReadOverride() == (journal.Target == PackageFamily));

    private bool MatchesDisabled(ExplorerContextMenuRegistry registry, Journal journal)
    {
        var observed = Observe(registry);
        if (observed.Enabled || journal.PackageInstalled &&
            (observed.PackageInstalled ? observed.PackageOverride : preference.ReadOverride()) != false)
            return false;
        for (var index = 0; index < Paths.Count; index++)
            if (journal.ClassicBefore![index] is not null && observed.Classic[index] is not null)
                return false;
        return true;
    }

    private RegisteredMenus Observe(ExplorerContextMenuRegistry registry)
    {
        var classic = ReadAll(registry);
        var owned = new ExplorerMenuVerb?[Paths.Count];
        var unverified = new List<string>();
        for (var index = 0; index < Paths.Count; index++)
        {
            if (classic[index] is not { } entry) continue;
            var path = Paths[index];
            if (TryRecognize(entry, path, out _)) owned[index] = entry;
            else
            {
                var detail = ExplorerContextMenuRegistration.TryParseCommand(entry.Command, out var target)
                    ? $"HKCU\\{path} ({target})" : $"HKCU\\{path}";
                unverified.Add(detail);
            }
        }
        var packageInstalled = packaged || isPackageInstalled();
        return new(classic, owned, packageInstalled, packageInstalled ? preference.ReadOverride() : null,
            unverified.ToArray());
    }

    private static ExplorerMenuVerb?[] ReadAll(ExplorerContextMenuRegistry registry) =>
        Paths.Select(registry.Read).ToArray();

    private static ExplorerMenuVerb?[] DesiredClassic(string target, string label) => Paths.Select(path =>
        target == PackageFamily || path is ExplorerContextMenuRegistration.LegacyInternetShortcutVerbPath or
            ExplorerContextMenuRegistration.LegacyUrlAssociationVerbPath ? null :
        new ExplorerMenuVerb(label, label, "\"" + target + "\"", "Single",
            path == ExplorerContextMenuRegistration.UrlVerbPath ? ExplorerContextMenuRegistration.UrlAppliesTo : null,
            ExplorerContextMenuRegistration.BuildCommand(target))).ToArray();

    private bool TryRecognize(ExplorerMenuVerb entry, string path, out string target)
    {
        if (!entry.Unknown && ExplorerContextMenuRegistration.TryParseCommand(entry.Command, out target) &&
            (SameTarget(target, executable) || recognizeTileWall(target)) &&
            (entry.Icon is null || entry.Icon.Equals("\"" + target + "\"", StringComparison.OrdinalIgnoreCase)) &&
            (entry.Selection is null or "Single") &&
            (entry.AppliesTo is null || path == ExplorerContextMenuRegistration.UrlVerbPath &&
                entry.AppliesTo == ExplorerContextMenuRegistration.UrlAppliesTo)) return true;
        target = string.Empty;
        return false;
    }

    private static bool IsOwnEntry(ExplorerMenuVerb entry, string executable) =>
        !entry.Unknown && ExplorerContextMenuRegistration.TryParseCommand(entry.Command, out var target) &&
        SameTarget(executable, target);

    private static bool SameTarget(string left, string right) =>
        left == PackageFamily || right == PackageFamily ? left == right :
        ExplorerContextMenuRegistration.SameExecutable(left, right);

    private void ClearOwnFailure(Journal? journal)
    {
        if (journal is { Failure: not null, ClassicBefore: null } && SameTarget(journal.Target, Target))
            SaveJournal(null);
    }

    private void RecordFailure(string target, string detail) =>
        SaveJournal(new(EnableVersion, target, false, null, null, null, detail));

    private Journal? ReadJournal()
    {
        if (preference.ReadHandoff() is not { } bytes) return null;
        var journal = JsonSerializer.Deserialize<Journal>(bytes, JournalOptions);
        if (journal is null || journal.Version is not (EnableVersion or DisableVersion or InstallVersion) ||
            string.IsNullOrWhiteSpace(journal.Target) ||
            (journal.Target != PackageFamily && !Path.IsPathFullyQualified(journal.Target)) ||
            !HasValidSnapshot(journal))
            throw new IOException("Invalid Explorer menu handoff state.");
        if (!journal.DisablesAll && journal.ClassicBefore is not null && journal.Target != PackageFamily &&
            !IsValidPendingTarget(journal.Target))
            throw new IOException("The pending Explorer menu handoff no longer has a recognized target.");
        return journal;
    }

    private static bool HasValidSnapshot(Journal journal)
    {
        var keys = journal.ClassicBefore;
        if (keys is not null && (keys.Length != Paths.Count || keys.Any(entry => entry?.Unknown == true)))
            return false;
        if (journal.Version == EnableVersion)
            return journal.Started == 0 && (keys is null ? !string.IsNullOrWhiteSpace(journal.Failure) :
                !string.IsNullOrWhiteSpace(journal.Label));
        if (keys is null || !journal.PackageInstalled && journal.PackageBefore is not null)
            return false;
        var allowed = journal.PackageInstalled && journal.PackageBefore != false ? 1 << Paths.Count : 0;
        if (journal.IsInstall)
        {
            if (journal.Target == PackageFamily || string.IsNullOrWhiteSpace(journal.Label)) return false;
            var desired = DesiredClassic(journal.Target, journal.Label);
            for (var index = 0; index < keys.Length; index++)
                if (keys[index] != desired[index]) allowed |= 1 << index;
            return (journal.Started & ~allowed) == 0;
        }
        if (journal.Label is not null) return false;
        for (var index = 0; index < keys.Length; index++)
            if (keys[index] is not null) allowed |= 1 << index;
        return (journal.Started & ~allowed) == 0;
    }

    private bool IsValidPendingTarget(string target) => SameTarget(target, executable) ||
        recognizeTileWall(target);

    private void SaveJournal(Journal? value) =>
        preference.WriteHandoff(value is null ? null : JsonSerializer.SerializeToUtf8Bytes(value, JournalOptions));

    private static ExplorerContextMenuRegistry OpenPhysicalRegistry()
    {
#if TILEWALL_PACKAGED
        return new ExplorerContextMenuRegistry.PhysicalCurrentUser();
#else
        return new ExplorerContextMenuRegistry.CurrentUser();
#endif
    }

    private static bool IsPackageInstalled()
    {
        uint count = 0, length = 0;
        var result = GetPackagesByPackageFamily(PackageFamily, ref count, IntPtr.Zero, ref length, IntPtr.Zero);
        if (result is not (0 or InsufficientBuffer)) throw new Win32Exception(result);
        return count != 0;
    }

    internal static bool IsOperationError(Exception error) => error is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException or InvalidOperationException or NotSupportedException
        or Win32Exception or COMException or JsonException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagesByPackageFamily(string packageFamilyName, ref uint count,
        IntPtr packageFullNames, ref uint bufferLength, IntPtr buffer);
}
