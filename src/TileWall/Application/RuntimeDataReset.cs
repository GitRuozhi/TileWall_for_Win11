using System.Runtime.InteropServices;
using TileWall.Core.Configuration;
using TileWall.Core.Storage;
using TileWall.Resources;

namespace TileWall.ApplicationModel;

internal static class RuntimeDataReset
{
    public static void Validate(RuntimeScope scope)
    {
        var paths = new WallStoragePaths(new LocalFileStore(), scope.DataRoot);
        paths.GuardTree(scope.DataRoot);
    }

    public static void ClearAndRestart(RuntimeScope scope)
    {
        try
        {
            var paths = new WallStoragePaths(new LocalFileStore(), scope.DataRoot);
            paths.GuardTree(scope.DataRoot);
            DeleteTree(paths, scope.DataRoot);
        }
        catch (Exception ex)
        {
            ReportFailure("Settings.ResetDataFailed", ex.Message);
            return;
        }

        try
        {
            // The current process still owns the data-root mutex. The SDK starts
            // this same distribution after terminating the current instance.
            var arguments = scope.IsTest ? $"--test-root \"{scope.DataRoot}\"" : string.Empty;
            var reason = Microsoft.Windows.AppLifecycle.AppInstance.Restart(arguments);
            ReportFailure("Settings.ResetRestartFailed", reason.ToString());
        }
        catch (Exception ex)
        {
            ReportFailure("Settings.ResetRestartFailed", ex.Message);
        }
    }

    private static void DeleteTree(WallStoragePaths paths, string directory)
    {
        paths.Guard(directory);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            paths.Guard(file);
            File.Delete(file);
        }

        paths.Guard(directory);
        foreach (var child in Directory.EnumerateDirectories(directory))
            DeleteTree(paths, child);

        paths.Guard(directory);
        Directory.Delete(directory);
    }

    public static void ReportFailure(string key, string detail) =>
        _ = MessageBoxW(IntPtr.Zero, string.Format(ProductStrings.Get(key), detail), "TileWall", 0x00000010);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
