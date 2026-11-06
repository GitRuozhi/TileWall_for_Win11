using System.Runtime.InteropServices;
using TileWall.Core.Configuration;
using TileWall.Core.Shell;

namespace TileWall.Shell.Interop;

internal sealed record ExplorerContextMenuState(bool Enabled, bool Complete, string[] OtherTargets,
    bool HasUnrecognizedConflict = false, string? Error = null)
{
    internal bool ReadSucceeded => Error is null;
}

internal sealed record ExplorerContextMenuChange(ExplorerContextMenuState State, string? OutcomeKey = null,
    string? Detail = null, string[]? TransferTargets = null)
{
    internal bool Failed => OutcomeKey is not null;
}

/// <summary>Distribution choice and ownership policy for the shared settings-page operation.</summary>
internal sealed class ExplorerContextMenuController
{
    private readonly ExplorerMenuPreference? _preference;
    internal string HelpKey => _preference is null ? "Settings.ContextMenuHelp" : "Settings.PackagedContextMenuHelp";

    private ExplorerContextMenuController(ExplorerMenuPreference? preference) => _preference = preference;

    internal static ExplorerContextMenuController Create(string dataRoot)
    {
        // Isolated runtime roots must never change the normal Explorer registration.
        var normalRoot = Path.Combine(KnownFolderPaths.LocalAppData, "TileWall");
        var isolated = !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(normalRoot)), StringComparison.OrdinalIgnoreCase);
#if TILEWALL_PACKAGED
        isolated = true;
#endif
        return new(isolated ? new ExplorerMenuPreference(new LocalFileStore(), dataRoot) : null);
    }

    internal Task<ExplorerContextMenuState> ReadAsync() => Task.Run(Read);

    private ExplorerContextMenuState Read() => _preference is null
        ? FromCurrentUser(ExplorerContextMenuRegistration.ReadCurrentUserState())
        : FromPreference(_preference.Read());

    internal async Task<ExplorerContextMenuChange> ApplyAsync(bool enable, string language, string[]? confirmedTargets)
    {
        // Recheck ownership at each confirmation/write boundary; the UI's last snapshot is not authority.
        var before = await ReadAsync();
        if (!before.ReadSucceeded) return new(before, "Settings.ContextMenuReadFailed", before.Error);
        if (ApplicationModel.ContextMenuUserCommand.IsElevated())
            return new(before, "Settings.ContextMenuOrdinaryUserRequired");
        if (enable && before.HasUnrecognizedConflict) return new(before, "Settings.ContextMenuConflict");
        if (enable && before.OtherTargets.Length != 0 && (confirmedTargets is null ||
            !before.OtherTargets.SequenceEqual(confirmedTargets, StringComparer.OrdinalIgnoreCase)))
            return new(before, TransferTargets: before.OtherTargets);

        if (_preference is not null)
        {
            var written = await Task.Run(() => _preference.Write(enable));
            // Failures retain and re-read the actual single value, with no desired-state shadow.
            var actual = await ReadAsync();
            if (!written.ReadSucceeded) return new(actual, "Settings.ContextMenuOperationFailed", written.Error);
            if (!actual.ReadSucceeded || actual.Enabled != enable)
                return new(actual, "Settings.ContextMenuChangedElsewhere", actual.Error);
            NotifyShell();
            return new(actual);
        }

        if (language is not ("zh" or "en")) return new(before, "Settings.ContextMenuOperationFailed");
        var result = await Task.Run(() => enable
            ? ExplorerContextMenuRegistration.RegisterCurrentUserForCurrentExecutable(
                new ApplicationModel.ContextMenuUserCommand(ApplicationModel.ContextMenuUserAction.Enable, language).MenuText)
            : ExplorerContextMenuRegistration.UnregisterCurrentUserOwnedByCurrentExecutable());
        var state = await ReadAsync();
        var matchesRequest = state.ReadSucceeded && (enable ? state.Complete : !state.Enabled);
        var outcome = result.RollbackIncomplete ? "Settings.ContextMenuRollbackIncomplete"
            : !result.Success ? "Settings.ContextMenuOperationFailed"
            : matchesRequest ? null : "Settings.ContextMenuChangedElsewhere";
        return new(state, outcome, result.Error);
    }

    private static ExplorerContextMenuState FromPreference(ExplorerMenuPreferenceState state) =>
        new(state.Enabled, state.Enabled, [], Error: state.Error);

    private static ExplorerContextMenuState FromCurrentUser(ContextMenuState state) =>
        new(state.Targets.Any(IsCurrentTarget), state.IsCurrentExecutableComplete,
            state.Targets.Where(target => !IsCurrentTarget(target)).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            state.HasUnrecognizedConflict,
            state.Status == ContextMenuStatus.ReadFailed ? state.Error ?? "Unable to read Explorer registration." : null);

    private static bool IsCurrentTarget(string target) => Environment.ProcessPath is { } executable &&
        string.Equals(Path.GetFullPath(target), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);

    // Windows SDK ShlObj_core.h: SHCNE_ASSOCCHANGED / SHCNF_IDLIST. This is an invalidation hint, not proof.
    private static void NotifyShell() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
