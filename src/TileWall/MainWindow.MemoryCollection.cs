namespace TileWall;

public sealed partial class MainWindow
{
    internal (bool Busy, long LastActivity) MemoryCollectionActivity()
    {
        var images = _images.MemoryCollectionActivity;
        var start = _startMenuImports?.MemoryCollectionActivity ?? default;
        var busy = AppWindow.IsVisible || _modal || LayoutMutationBlocked || _windowClosing || ShutdownStarted ||
            _appearanceWork.Any(task => !task.IsCompleted) || _puzzles.HasPendingPresentationWork ||
            _reclamationWorker is { IsCompleted: false } ||
            _nativeRefreshes.Values.Any(task => !task.IsCompleted) || _nativeCacheReads.Values.Any(task => !task.IsCompleted);
        return (busy || images.Busy || start.Busy, Math.Max(images.LastActivity, start.LastActivity));
    }
}
