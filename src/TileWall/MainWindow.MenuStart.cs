using TileWall.Core.Application;
using TileWall.Core.Configuration;
using TileWall.Core.Shell;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Views;

namespace TileWall;

public sealed partial class MainWindow
{
    private MenuStartView _menuStart = null!;
    private MenuStartStateStore? _menuStateStore;
    private Task? _menuStateWrite;
    private bool _menuImagesActive;

    private void InitializeMenuStart()
    {
        var expansion = new MenuGroupExpansion();
        _menuStateStore = new(new LocalFileStore(), _session.DataRoot);
        try { expansion = _menuStateStore.Read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { AppLog.Warn("MenuStart", $"UI preference could not be read: {ex.GetType().Name}"); }
        _menuStart = new MenuStartView(Surface, _images, LaunchMember, () => Scale,
            () => !_windowClosing && !ShutdownStarted, expansion, SaveMenuExpansion);
        WallRoot.Children.Insert(1, _menuStart.Element);
        _puzzles.FrameChanged += UpdateMenuFrames;
    }

    private void RenderMenuStart(WallDocument document)
    {
        _menuStart.Render(document, _menuImagesActive);
        PositionMenuStart();
    }

    private void RefreshMenuStartStrings() => _menuStart.RefreshStrings();

    private void UpdateMenuFrames(string memberId, PuzzleFrame? frame, double progress)
    {
        if (_windowClosing || ShutdownStarted) return;
        _menuStart.UpdateFrame(memberId, frame);
        foreach (var wall in _secondaryWalls.Values)
            if (wall.IsVisible) wall.UpdateMenuFrame(memberId, frame);
    }

    private void SaveMenuExpansion(IReadOnlyList<string> ids)
    {
        if (_storageExecutor is not { } executor || _menuStateStore is not { } store) return;
        _menuStateWrite = executor.ExecuteAsync(() =>
        {
            try { store.Write(ids); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { AppLog.Warn("MenuStart", $"UI preference could not be saved: {ex.GetType().Name}"); }
            return true;
        });
    }

    private async Task ShutdownMenuStartAsync()
    {
        _puzzles.FrameChanged -= UpdateMenuFrames;
        await _menuStart.ShutdownAsync();
        if (_menuStateWrite is { } write)
            try { await write; }
            catch (Exception ex) { AppLog.Warn("MenuStart", $"UI preference write did not finish: {ex.GetType().Name}"); }
    }

    private void SetMenuImagesActive(bool active)
    {
        if (_menuImagesActive == active) return;
        _menuImagesActive = active;
        if (_hasDocument) RenderMenuStart(Document);
    }

    private void PositionMenuStart()
    {
        if (_hasDocument && PresentedViewportSettings.MenuStart) _menuStart.Position(_sidebarAnchor);
    }
}
