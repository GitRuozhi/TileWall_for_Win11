using TileWall.Core.Shell;
using TileWall.Core.Wall;

namespace TileWall.ApplicationModel;

internal sealed partial class WallApplicationLifetime
{
    private bool _automaticMenuHandoffPending;
    private Task _explorerMenuSynchronization = Task.CompletedTask;

    private Task QueueExplorerMenuSynchronization()
    {
        if (_isTest || _exitPending || _tearingDown || _window.SettingsPersistenceBlocked)
            return _explorerMenuSynchronization;
        var allowHandoff = _automaticMenuHandoffPending;
        _automaticMenuHandoffPending = false;
        return _explorerMenuSynchronization = SynchronizeExplorerMenuAsync(_window.Settings.LanguageMode, allowHandoff);
    }

    private async Task SynchronizeExplorerMenuAsync(WallLanguageMode language, bool allowHandoff)
    {
        try
        {
            var change = await _window.ExplorerContextMenu.SynchronizeAsync(language, allowHandoff);
            if (change?.Failed == true)
                AppLog.Warn("ContextMenu", change.Detail ?? change.OutcomeKey ?? "Explorer menu synchronization failed.");
        }
        catch (Exception ex) { AppLog.Warn("ContextMenu", ex.Message); }
    }
}
