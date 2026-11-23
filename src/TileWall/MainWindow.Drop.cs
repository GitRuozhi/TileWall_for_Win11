using Microsoft.UI.Xaml;
using TileWall.Core.Shell;
using Windows.ApplicationModel.DataTransfer;

namespace TileWall;

public sealed partial class MainWindow
{
    internal Func<ExplorerAddMessage, bool>? AcceptDroppedPaths { private get; set; }

    private bool CanAcceptWallDrop => CanProcessExternalImport && AppWindow.IsVisible &&
        !_windowClosing && !ShutdownStarted && AcceptDroppedPaths is not null;

    private bool IsFileDrop(DragEventArgs e) => CanAcceptWallDrop &&
        (e.AllowedOperations & DataPackageOperation.Copy) != 0 && e.DataView.Contains(StandardDataFormats.StorageItems);

    private void OnWallDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = IsFileDrop(e) ? DataPackageOperation.Copy : DataPackageOperation.None;
        e.Handled = true;
    }

    private async void OnWallDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.None;
        if (!IsFileDrop(e)) return;

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (!CanAcceptWallDrop) return;
            var paths = items.Select(item => item.Path).ToArray();
            var message = await Task.Run(() => ExplorerAddPayload.FromPaths(paths));
            if (CanAcceptWallDrop && AcceptDroppedPaths?.Invoke(message) == true)
                e.AcceptedOperation = DataPackageOperation.Copy;
        }
        catch (Exception ex) { AppLog.Warn("ExternalAdd", $"Could not read dropped items: {ex.Message}"); }
        finally { deferral.Complete(); }
    }
}
