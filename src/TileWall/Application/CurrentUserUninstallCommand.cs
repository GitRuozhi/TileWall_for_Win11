using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using TileWall.Shell.Interop;

namespace TileWall.ApplicationModel;

/// <summary>One-shot ordinary-user broker for an installed, administrator-owned file uninstaller.</summary>
internal sealed record CurrentUserUninstallCommand(bool Quiet, bool FromDesktop = false)
{
    internal const string Argument = "--uninstall-current-user";
    internal const string UninstallRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{55D7663D-DFB4-4735-B28A-2EF358DB19F4}_is1";
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const byte DeleteRequest = 0x44; // D: Inno has entered usUninstall, after confirmation.
    private const byte Removed = 0x4F; // O: menu and owned startup value removed, transaction still open.
    private const byte Acknowledge = 0x41; // A: client will wait for successful broker exit before deleting files.
    private const byte Error = 0x45;

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
            return ContextMenuUserCommand.Success;
        }
        catch (Exception) { result = ContextMenuUserCommand.OperationFailed; }
        if (result != ContextMenuUserCommand.Success && !command!.Quiet) ShowFailure(result);
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
        return ContextMenuUserCommand.Success; // Request accepted; not an uninstall-completion receipt.
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
        var executable = Environment.ProcessPath ?? throw new IOException("The installed executable is unavailable.");
        executable = Path.GetFullPath(executable);
        var uninstaller = ReadInstalledUninstaller(executable);
        var id = Guid.NewGuid().ToString("N");
        using var pipe = CreatePipe("TileWall.Uninstall." + id);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            using var child = Process.Start(new ProcessStartInfo
            {
                FileName = uninstaller,
                Arguments = "/TWUSERPIPE=" + id + " /TWUSERPID=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) +
                    (command.Quiet ? " /SILENT /SUPPRESSMSGBOXES" : string.Empty),
                WorkingDirectory = Path.GetDirectoryName(uninstaller)!,
                UseShellExecute = true,
                Verb = "runas"
            }) ?? throw new IOException("The installed uninstaller did not start.");

            var connected = pipe.WaitForConnectionAsync(deadline.Token);
            // Inno keeps its original loader alive until its temporary clone exits; the peer PID can be the clone.
            var exited = child.WaitForExitAsync(deadline.Token);
            var first = Task.WhenAny(connected, exited).GetAwaiter().GetResult();
            if (first == exited && !pipe.IsConnected)
            {
                exited.GetAwaiter().GetResult();
                return ContextMenuUserCommand.Success;
            }
            connected.GetAwaiter().GetResult();
            return ExecuteHandshake(pipe, afterMutation => RemoveCurrentUserState(executable, afterMutation), deadline.Token);
        }
        finally { deadline.Cancel(); }
        // Do not wait for child after O/A: it waits for this EXE to exit before removing program files.
    }

    [SupportedOSPlatform("windows")]
    private static string ReadInstalledUninstaller(string executable)
    {
        if (!string.Equals(Path.GetFileName(executable), "TileWall.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("This is not the installed TileWall executable.");
        var directory = Path.GetDirectoryName(executable) ?? throw new IOException("Missing install directory.");
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var registration = machine.OpenSubKey(UninstallRegistryPath) ?? throw new IOException("The installation is not registered.");
        if (registration.GetValue("TWCurrentUserMenuProtocol") is not int version || version != 1 ||
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
    private static NamedPipeServerStream CreatePipe(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new IOException("The current user identity is unavailable.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        // A different administrator may approve UAC. CurrentUserOnly would reject that legitimate client.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 1024, 1024, security,
            HandleInheritability.None, (PipeAccessRights)0);
    }

    // The cleanup delegate owns rollback, invoking its callback before committing the menu/startup transaction.
    // Tests supply an ordinary Stream and a small transaction fixture; no desktop or registry is needed here.
    internal static int ExecuteHandshake(Stream pipe, Func<Action, ContextMenuRegistrationResult> cleanup,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = ReadByte(pipe, cancellationToken);
            if (request == -1) return ContextMenuUserCommand.Success; // Confirmation cancelled before usUninstall.
            if (request != DeleteRequest) throw new IOException("Invalid uninstall request.");
            var result = cleanup(() =>
            {
                WriteByte(pipe, Removed, cancellationToken);
                using var acknowledgement = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                acknowledgement.CancelAfter(TimeSpan.FromSeconds(5));
                if (ReadByte(pipe, acknowledgement.Token) != Acknowledge)
                    throw new IOException("The uninstaller did not acknowledge current-user cleanup.");
            });
            if (result.Success) return ContextMenuUserCommand.Success;
            TryWriteError(pipe);
            return result.RollbackIncomplete ? ContextMenuUserCommand.RollbackIncomplete : ContextMenuUserCommand.OperationFailed;
        }
        catch (Exception)
        {
            TryWriteError(pipe);
            return ContextMenuUserCommand.OperationFailed;
        }
    }

    private static int ReadByte(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        return stream.ReadAsync(buffer.AsMemory(), cancellationToken).AsTask().GetAwaiter().GetResult() == 0 ? -1 : buffer[0];
    }

    private static void WriteByte(Stream stream, byte value, CancellationToken cancellationToken)
    {
        stream.WriteAsync(new byte[] { value }.AsMemory(), cancellationToken).AsTask().GetAwaiter().GetResult();
        stream.FlushAsync(cancellationToken).GetAwaiter().GetResult();
    }

    private static void TryWriteError(Stream stream)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            WriteByte(stream, Error, timeout.Token);
        }
        catch (Exception) { }
    }

    [SupportedOSPlatform("windows")]
    private static ContextMenuRegistrationResult RemoveCurrentUserState(string executable, Action afterMutation)
    {
        string? previous = null;
        var previousKind = RegistryValueKind.Unknown;
        var startupChanged = false;
        string? startupRollbackError = null;
        var result = ExplorerContextMenuRegistration.UnregisterCurrentUserOwnedByCurrentExecutable(() =>
        {
            try
            {
                using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                using var startup = user.OpenSubKey(StartupRegistryPath, writable: true);
                if (startup is not null && startup.GetValue("TileWall", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value)
                {
                    var kind = startup.GetValueKind("TileWall");
                    if ((kind is RegistryValueKind.String or RegistryValueKind.ExpandString) &&
                        string.Equals(value, "\"" + executable + "\" --background", StringComparison.Ordinal))
                    {
                        previous = value;
                        previousKind = kind;
                        startupChanged = true; // A partially failed delete also requires restoration.
                        startup.DeleteValue("TileWall", throwOnMissingValue: false);
                    }
                }
                afterMutation();
            }
            catch (Exception)
            {
                // Restore Run before leaving the menu callback, while the current-user registration lock is held.
                if (startupChanged)
                {
                    try
                    {
                        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                        using var startup = user.CreateSubKey(StartupRegistryPath, writable: true);
                        startup.SetValue("TileWall", previous!, previousKind);
                    }
                    catch (Exception ex) { startupRollbackError = ex.Message; }
                }
                throw; // The registration transaction now restores its menu snapshots.
            }
        });
        return startupRollbackError is null ? result :
            new(false, RollbackIncomplete: true, Error: result.Error + " Startup rollback: " + startupRollbackError);
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
