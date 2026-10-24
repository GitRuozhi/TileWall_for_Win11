using System.Runtime.Versioning;
using System.Security.Principal;
using TileWall.Shell.Interop;

namespace TileWall.ApplicationModel;

/// <summary>Fixed, non-XAML administration entry point, before ordinary application startup.</summary>
internal sealed record ContextMenuAdminCommand(bool Enable, string Language)
{
    internal const string Argument = "--context-menu-admin";
    internal const int Success = 0;
    internal const int InvalidArguments = 2;
    internal const int NotElevated = 3;
    internal const int OperationFailed = 4;
    internal const int RollbackIncomplete = 5;

    // Detect the switch anywhere so mixed COM/add/test-root requests cannot fall through.
    internal static bool IsRequested(string[] args) => args.Any(arg =>
        string.Equals(arg, Argument, StringComparison.OrdinalIgnoreCase) ||
        arg.StartsWith(Argument + "=", StringComparison.OrdinalIgnoreCase));

    internal static bool TryParse(string[] args, out ContextMenuAdminCommand? command)
    {
        command = null;
        if (args.Length != 3 || args[0] != Argument || args[1] is not ("enable" or "disable") || args[2] is not ("zh" or "en"))
            return false;
        command = new(args[1] == "enable", args[2]);
        return true;
    }

    // Menu text belongs here rather than ProductStrings: this branch has no Application.
    internal string MenuText => Language == "zh" ? "添加到 TileWall" : "Add to TileWall";

    [SupportedOSPlatform("windows")]
    internal static int Run(string[] args)
    {
        try
        {
            return Execute(args, IsElevated, command => command.Enable
                ? ExplorerContextMenuRegistration.RegisterMachineForCurrentExecutable(command.MenuText)
                : ExplorerContextMenuRegistration.UnregisterMachineOwnedByCurrentExecutable());
        }
        catch (Exception) { return OperationFailed; }
    }

    internal static int Execute(string[] args, Func<bool> isElevated,
        Func<ContextMenuAdminCommand, ContextMenuRegistrationResult> operation)
    {
        if (!TryParse(args, out var command)) return InvalidArguments;
        if (!isElevated()) return NotElevated;
        var result = operation(command!);
        return result.Success ? Success : result.RollbackIncomplete ? RollbackIncomplete : OperationFailed;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
