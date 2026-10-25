using System.Runtime.Versioning;
using System.Security.Principal;
using TileWall.Shell.Interop;

namespace TileWall.ApplicationModel;

internal enum ContextMenuUserAction { Enable, Disable, Query }

/// <summary>Fixed, non-XAML current-user menu entry point, before ordinary application startup.</summary>
internal sealed record ContextMenuUserCommand(ContextMenuUserAction Action, string Language)
{
    internal const string Argument = "--context-menu-user";
    private const string RetiredArgument = "--context-menu-admin";
    internal const int Success = 0;
    internal const int InvalidArguments = 2;
    internal const int ElevatedContext = 3;
    internal const int OperationFailed = 4;
    internal const int RollbackIncomplete = 5;
    internal const int QueryOff = 10;
    internal const int QueryOwnedOn = 11;
    internal const int QueryOtherOrConflict = 12;
    internal const int QueryReadFailed = 13;

    internal bool Enable => Action == ContextMenuUserAction.Enable;

    // Detect both switches anywhere so retired, mixed COM/add/test-root requests cannot fall through to XAML.
    internal static bool IsRequested(string[] args) => args.Any(arg => IsSwitch(arg, Argument) || IsSwitch(arg, RetiredArgument));

    private static bool IsSwitch(string value, string argument) =>
        string.Equals(value, argument, StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith(argument + "=", StringComparison.OrdinalIgnoreCase);

    internal static bool TryParse(string[] args, out ContextMenuUserCommand? command)
    {
        command = null;
        if (args.Length != 3 || args[0] != Argument || args[1] is not ("enable" or "disable" or "query") || args[2] is not ("zh" or "en"))
            return false;
        command = new(args[1] switch
        {
            "enable" => ContextMenuUserAction.Enable,
            "disable" => ContextMenuUserAction.Disable,
            _ => ContextMenuUserAction.Query
        }, args[2]);
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
                ? ExplorerContextMenuRegistration.RegisterCurrentUserForCurrentExecutable(command.MenuText)
                : ExplorerContextMenuRegistration.UnregisterCurrentUserOwnedByCurrentExecutable(),
                ExplorerContextMenuRegistration.ReadCurrentUserState);
        }
        catch (Exception) { return OperationFailed; }
    }

    internal static int Execute(string[] args, Func<bool> isElevated,
        Func<ContextMenuUserCommand, ContextMenuRegistrationResult> operation, Func<ContextMenuState> readState)
    {
        if (!TryParse(args, out var command)) return InvalidArguments;
        // An elevated installer may have no original-user token. Never use its administrator HKCU as a substitute.
        if (isElevated()) return ElevatedContext;
        if (command!.Action == ContextMenuUserAction.Query)
            return readState().Status switch
            {
                ContextMenuStatus.NotRegistered => QueryOff,
                ContextMenuStatus.CurrentExecutable => QueryOwnedOn,
                ContextMenuStatus.ReadFailed => QueryReadFailed,
                _ => QueryOtherOrConflict
            };
        var result = operation(command!);
        return result.Success ? Success : result.RollbackIncomplete ? RollbackIncomplete : OperationFailed;
    }

    [SupportedOSPlatform("windows")]
    internal static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
