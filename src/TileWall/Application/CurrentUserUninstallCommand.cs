using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TileWall.Shell.Interop;

namespace TileWall.ApplicationModel;

/// <summary>Compatibility activation; native Inno owns confirmation and the final uninstall result.</summary>
internal sealed record CurrentUserUninstallCommand(bool Quiet, bool FromDesktop = false)
{
    internal const string Argument = "--uninstall-current-user";
    internal const string UninstallRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{55D7663D-DFB4-4735-B28A-2EF358DB19F4}_is1";
    // Legacy management activation is only a handoff; Inno owns the final result.
    internal const int Accepted = 10;
    internal const int Cancelled = 21;
    internal const string NotificationArgument = "--uninstall-notification-cleanup";

    internal static bool IsNotificationRequested(string[] args) => args.Any(value =>
        value.StartsWith(NotificationArgument, StringComparison.OrdinalIgnoreCase));

    [SupportedOSPlatform("windows")]
    internal static int RunNotificationCleanup(string[] args, Action cleanup)
    {
        if (args.Length != 3 || args[0] != NotificationArgument || args[1].Length != 32 ||
            args[1].Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var brokerPid) || brokerPid <= 0)
            return ContextMenuUserCommand.InvalidArguments;
        if (ContextMenuUserCommand.IsElevated()) return ContextMenuUserCommand.ElevatedContext;
        try
        {
            var executable = Path.GetFullPath(Environment.ProcessPath ?? throw new IOException("Missing executable."));
            _ = ReadInstalledUninstaller(executable);
            using var pipe = new NamedPipeClientStream(".", "TileWall.Uninstall." + args[1] + ".notify",
                PipeDirection.InOut, PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            pipe.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actualPid) || actualPid != brokerPid)
                return ContextMenuUserCommand.OperationFailed;
            using var broker = Process.GetProcessById(brokerPid);
            using var ownProcess = Process.GetCurrentProcess();
            if (broker.HasExited || broker.SessionId != ownProcess.SessionId ||
                !string.Equals(broker.MainModule?.FileName, Path.Combine(Path.GetDirectoryName(executable)!, "TileWall.UninstallBroker.exe"), StringComparison.OrdinalIgnoreCase))
                return ContextMenuUserCommand.OperationFailed;
            var request = new byte[1];
            if (pipe.ReadAsync(request.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult() != 1 || request[0] != 0x4E)
                return ContextMenuUserCommand.OperationFailed;
            // Only the authenticated broker opens this first-instance channel,
            // after Inno's confirmed D request. Arbitrary command activation cannot unregister.
            cleanup();
            return ContextMenuUserCommand.Success;
        }
        catch (Exception) { return ContextMenuUserCommand.OperationFailed; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out int processId);

    internal static bool IsRequested(string[] args) => args.Any(value =>
        string.Equals(value, Argument, StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith(Argument + "=", StringComparison.OrdinalIgnoreCase));

    internal static bool TryParse(string[] args, out CurrentUserUninstallCommand? command)
    {
        command = null;
        if (args.Length is < 1 or > 3 || args[0] != Argument) return false;
        var quiet = false;
        var fromDesktop = false;
        foreach (var value in args.Skip(1))
        {
            if (value == "--quiet" && !quiet) quiet = true;
            else if (value == "--from-desktop" && !fromDesktop) fromDesktop = true;
            else return false;
        }
        command = new(quiet, fromDesktop);
        return true;
    }

    [SupportedOSPlatform("windows")]
    internal static int Run(string[] args)
    {
        if (!TryParse(args, out var command)) return ContextMenuUserCommand.InvalidArguments;
        int result;
        try
        {
            result = ExecuteEntry(command!, ContextMenuUserCommand.IsElevated,
                () => RunInstalled(command!), () => DelegateToDesktop(command!));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Declining file-uninstall elevation happens before any current-user mutation.
            return Cancelled;
        }
        catch (Exception) { result = ContextMenuUserCommand.OperationFailed; }
        if (result != ContextMenuUserCommand.Success && result != Accepted && !command!.Quiet) ShowFailure(result);
        return result;
    }

    internal static int ExecuteEntry(CurrentUserUninstallCommand command, Func<bool> isElevated,
        Func<int> runForCurrentUser, Action delegateToDesktop)
    {
        if (!isElevated()) return runForCurrentUser();
        // Windows may elevate the registered uninstall entry. Return to Explorer's
        // user rather than treating that ordinary system launch as an error.
        // The receiving process must still be unelevated; never delegate recursively.
        if (command.FromDesktop) return ContextMenuUserCommand.ElevatedContext;
        delegateToDesktop();
        return Accepted; // Request accepted; not an uninstall-completion receipt.
    }

    [SupportedOSPlatform("windows")]
    private static void DelegateToDesktop(CurrentUserUninstallCommand command)
    {
        var executable = Path.GetFullPath(Environment.ProcessPath
            ?? throw new IOException("The installed executable is unavailable."));
        _ = ReadInstalledUninstaller(executable);
        DesktopUserUninstallLauncher.Launch(executable, command.Quiet);
    }

    [SupportedOSPlatform("windows")]
    private static int RunInstalled(CurrentUserUninstallCommand command)
    {
        // Compatibility activation only: release the installed runtime and let
        // the native Inno clone own confirmation, cleanup and the final result.
        var executable = Path.GetFullPath(Environment.ProcessPath ?? throw new IOException("Missing executable."));
        var uninstaller = ReadInstalledUninstaller(executable);
        using var child = Process.Start(new ProcessStartInfo
        {
            FileName = uninstaller,
            Arguments = command.Quiet ? "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART" : string.Empty,
            WorkingDirectory = Path.GetDirectoryName(uninstaller)!, UseShellExecute = true, Verb = "runas"
        }) ?? throw new IOException("The native uninstaller did not start.");
        return Accepted;
    }
    [SupportedOSPlatform("windows")]
    private static string ReadInstalledUninstaller(string executable)
    {
        if (!string.Equals(Path.GetFileName(executable), "TileWall.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("This is not the installed TileWall executable.");
        var directory = Path.GetDirectoryName(executable) ?? throw new IOException("Missing install directory.");
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var registration = machine.OpenSubKey(UninstallRegistryPath) ?? throw new IOException("The installation is not registered.");
        if (registration.GetValue("TWCurrentUserMenuProtocol") is not int version || version != 2 ||
            registration.GetValueKind("TWCurrentUserMenuProtocol") != RegistryValueKind.DWord)
            throw new IOException("The installed uninstaller does not support current-user cleanup.");
        var location = ReadRegistryString(registration, "InstallLocation");
        var uninstaller = ReadRegistryString(registration, "TWUserUninstallExe");
        if (!Path.IsPathFullyQualified(location) || !Path.IsPathFullyQualified(uninstaller) ||
            !SameDirectory(location, directory)) throw new IOException("The installation directory does not match this executable.");
        uninstaller = Path.GetFullPath(uninstaller);
        if (!SameDirectory(Path.GetDirectoryName(uninstaller)!, directory) || !IsUninstallerName(Path.GetFileName(uninstaller)))
            throw new IOException("The registered uninstaller is outside this installation.");
        RequireOrdinaryFile(executable);
        RequireOrdinaryFile(uninstaller);
        for (var ancestor = new DirectoryInfo(directory); ancestor is not null; ancestor = ancestor.Parent)
        {
            if (!ancestor.Exists || (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The installation uses an unsafe directory.");
        }
        return uninstaller;
    }

    private static bool SameDirectory(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static bool IsUninstallerName(string name) => name.Length == 12 &&
        name.StartsWith("unins", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        name[5..8].All(value => value is >= '0' and <= '9');

    [SupportedOSPlatform("windows")]
    private static string ReadRegistryString(RegistryKey key, string name)
    {
        if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value ||
            key.GetValueKind(name) != RegistryValueKind.String || string.IsNullOrWhiteSpace(value))
            throw new IOException("The installation registration is incomplete.");
        return value;
    }

    private static void RequireOrdinaryFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("The installation uses an unsafe file.");
    }

    [SupportedOSPlatform("windows")]
    private static void ShowFailure(int result)
    {
        var chinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
        var message = result == ContextMenuUserCommand.ElevatedContext
            ? chinese ? "无法切换到当前桌面用户，卸载尚未执行。程序文件已保留，请重试。" : "Could not switch to the desktop user. Uninstall did not run and program files were preserved. Please retry."
            : result == ContextMenuUserCommand.RollbackIncomplete
                ? chinese ? "卸载未完成，当前用户菜单恢复失败。请修复安装后重试。" : "Uninstall did not finish and current-user menu recovery failed. Repair the installation and retry."
                : chinese ? "卸载未完成，程序文件已保留。请重试或修复安装。" : "Uninstall did not finish. Program files were preserved. Retry or repair the installation.";
        _ = MessageBoxW(IntPtr.Zero, message, "TileWall", 0x00000010); // MB_OK | MB_ICONERROR.
    }

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
