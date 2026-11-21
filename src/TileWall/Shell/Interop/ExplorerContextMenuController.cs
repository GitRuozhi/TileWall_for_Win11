using TileWall.ApplicationModel;
using TileWall.Core.Configuration;
using TileWall.Core.Shell;
using TileWall.Core.Settings;
using TileWall.Core.Wall;

namespace TileWall.Shell.Interop;

internal sealed record ExplorerContextMenuState(bool Enabled, bool Complete, string? Error = null,
    string? HandoffFailure = null, string[]? UnverifiedEntries = null)
{
    internal bool ReadSucceeded => Error is null;
}

internal sealed record ExplorerContextMenuChange(ExplorerContextMenuState State, string? OutcomeKey = null,
    string? Detail = null)
{
    internal bool Failed => OutcomeKey is not null;
}

/// <summary>Owns the current instance's menu actions and the language projected to Explorer.</summary>
internal sealed class ExplorerContextMenuController
{
    private readonly ExplorerMenuPreference _preference;
    private readonly ExplorerContextMenuHandoff? _handoff;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private string? _unsavedHandoffFailure;

    private static bool IsPackaged =>
#if TILEWALL_PACKAGED
        true;
#else
        false;
#endif

    internal string HelpKey => "Settings.ContextMenuHelp";

    private ExplorerContextMenuController(string dataRoot, bool isolated)
    {
        _preference = new(new LocalFileStore(), dataRoot);
        _handoff = isolated ? null : new(_preference, IsPackaged,
            Environment.ProcessPath ?? throw new IOException("TileWall executable is unavailable."));
    }

    private static string NormalRoot => Path.Combine(KnownFolderPaths.LocalAppData, "TileWall");

    internal static ExplorerContextMenuController Create(string dataRoot)
    {
        var isolated = !ExplorerContextMenuRegistration.SameExecutable(
            Path.TrimEndingDirectorySeparator(dataRoot), Path.TrimEndingDirectorySeparator(NormalRoot));
        return new(dataRoot, isolated);
    }

    internal Task<ExplorerContextMenuState> ReadAsync() => Task.Run(Read);

    private ExplorerContextMenuState Read()
    {
        try
        {
            var state = _handoff is null ? FromPreference(_preference.Read()) : _handoff.ReadState();
            var failure = Volatile.Read(ref _unsavedHandoffFailure) ?? _handoff?.ReadFailure();
            return failure is null ? state : state with { HandoffFailure = failure };
        }
        catch (Exception ex) when (ExplorerContextMenuHandoff.IsOperationError(ex))
        {
            return new(false, false, Error: ex.Message);
        }
    }

    internal Task<ExplorerContextMenuChange?> SynchronizeAsync(WallLanguageMode language,
        bool allowAutomaticHandoff = false)
    {
        var tag = WallLanguages.ResolveTag(language);
        return MutateAsync<ExplorerContextMenuChange?>(() =>
        {
            if (_handoff is null || ContextMenuUserCommand.IsElevated()) return null;
            if (allowAutomaticHandoff)
            {
                var handoff = ExplorerContextMenuRegistration.WithWriteLock(() =>
                    _handoff.Enable(MenuText(tag), automatic: true));
                if (!handoff.Success)
                {
                    PublishFailure(handoff);
                    return Changed(handoff);
                }
            }

            var result = ExplorerContextMenuRegistration.WithWriteLock(() => SynchronizeLanguage(tag));
            return result.Success ? null : Changed(result);
        });
    }

    internal Task<ExplorerContextMenuChange> ApplyAsync(bool enable, string language) =>
        MutateAsync(() =>
        {
            if (ContextMenuUserCommand.IsElevated())
                return new ExplorerContextMenuChange(Read(), "Settings.ContextMenuOrdinaryUserRequired");
            if (!WallLanguages.TryParseTag(language, out var mode))
                return new ExplorerContextMenuChange(Read(), "Settings.ContextMenuOperationFailed");
            var tag = WallLanguages.ResolveTag(mode);
            if (_handoff is null)
            {
                var result = _preference.Write(enable);
                return new ExplorerContextMenuChange(FromPreference(result),
                    result.ReadSucceeded ? null : "Settings.ContextMenuOperationFailed", result.Error);
            }

            var write = ExplorerContextMenuRegistration.WithWriteLock(() => enable
                ? _handoff.Enable(MenuText(tag), automatic: false) : _handoff.Disable());
            if (write.Success || enable) PublishFailure(write);
            if (!write.Success) return Changed(write);
            if (enable)
            {
                var synchronized = ExplorerContextMenuRegistration.WithWriteLock(() => SynchronizeLanguage(tag));
                if (!synchronized.Success) return Changed(synchronized);
            }
            var actual = Read();
            return new ExplorerContextMenuChange(actual, actual.ReadSucceeded &&
                (enable ? actual.Complete : !actual.Enabled) ? null : "Settings.ContextMenuChangedElsewhere", actual.Error);
        });

    private ContextMenuRegistrationResult SynchronizeLanguage(string tag)
    {
        try
        {
            if (!IsPackaged)
                return ExplorerContextMenuRegistration.WithCurrentUserWriteLock(root =>
                    ExplorerContextMenuRegistration.SynchronizeCurrentUserLabel(root,
                        ExplorerContextMenuRegistration.CurrentExecutable(), MenuText(tag),
                        ExplorerContextMenuRegistration.NotifyShell));
            if (_preference.PublishLanguage(tag)) ExplorerContextMenuRegistration.NotifyShell();
            return new(true);
        }
        catch (Exception ex) when (ExplorerContextMenuHandoff.IsOperationError(ex))
        {
            return new(false, Error: ex.Message);
        }
    }

    internal static ContextMenuRegistrationResult ApplyInstallerCommand(ContextMenuUserCommand command) =>
        command.Enable
            ? ExplorerContextMenuHandoff.EnableFromInstaller(new ExplorerMenuPreference(new LocalFileStore(), NormalRoot),
                command.MenuText)
            : ExplorerContextMenuRegistration.UnregisterCurrentUserOwnedByCurrentExecutable();

    private async Task<T> MutateAsync<T>(Func<T> action)
    {
        await _writes.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(action).ConfigureAwait(false); }
        finally { _writes.Release(); }
    }

    private void PublishFailure(ContextMenuRegistrationResult result) =>
        Volatile.Write(ref _unsavedHandoffFailure, result.Success ? null : result.Error ?? "Explorer menu handoff failed.");

    private ExplorerContextMenuChange Changed(ContextMenuRegistrationResult result) =>
        new(Read(), result.RollbackIncomplete ? "Settings.ContextMenuRollbackIncomplete" :
            "Settings.ContextMenuOperationFailed", result.Error);

    private static string MenuText(string tag) => new ContextMenuUserCommand(ContextMenuUserAction.Enable, tag).MenuText;

    private static ExplorerContextMenuState FromPreference(ExplorerMenuPreferenceState state) =>
        new(state.Enabled, state.Enabled, state.Error);

}
