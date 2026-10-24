using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using TileWall.ApplicationModel;

namespace TileWall.Shell.Interop;

internal enum ContextMenuElevationStatus { Completed, Cancelled, LaunchFailed, HelperFailed }
internal sealed record ContextMenuElevationResult(ContextMenuElevationStatus Status, int? ExitCode,
    MachineContextMenuState State, string? Error = null);

[SupportedOSPlatform("windows")]
internal static class ContextMenuElevation
{
    internal static Task<ContextMenuElevationResult> ApplyAsync(bool enable, string language) =>
        ApplyAsync(enable, language, LaunchAndWaitAsync, ExplorerContextMenuRegistration.ReadMachineState);

    // No cancellation token: closing the page must await, never kill an in-flight writer.
    internal static async Task<ContextMenuElevationResult> ApplyAsync(bool enable, string language,
        Func<ProcessStartInfo, Task<int>> launchAndWait, Func<MachineContextMenuState> readState)
    {
        var status = ContextMenuElevationStatus.LaunchFailed;
        int? exitCode = null;
        string? error = null;
        try
        {
            if (language is not ("zh" or "en")) throw new ArgumentException("Unsupported context-menu language.", nameof(language));
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) throw new IOException("TileWall executable is unavailable.");
            var start = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = ContextMenuAdminCommand.Argument + (enable ? " enable " : " disable ") + language,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            // ShellExecute/runas itself may wait for UAC. Keep that off the UI thread too.
            exitCode = await Task.Run(() => launchAndWait(start)).ConfigureAwait(false);
            status = exitCode == ContextMenuAdminCommand.Success ? ContextMenuElevationStatus.Completed : ContextMenuElevationStatus.HelperFailed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { status = ContextMenuElevationStatus.Cancelled; }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Security.SecurityException)
        { error = ex.Message; }

        // Even a zero exit code is followed by a fresh read: another copy may have won.
        var state = await Task.Run(readState).ConfigureAwait(false);
        return new(status, exitCode, state, error);
    }

    private static async Task<int> LaunchAndWaitAsync(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The context-menu helper did not start.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }
}
