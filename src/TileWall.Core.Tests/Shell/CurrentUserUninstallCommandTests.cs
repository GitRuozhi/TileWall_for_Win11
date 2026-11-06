using TileWall.ApplicationModel;
using TileWall.Shell.Interop;
using Xunit;

namespace TileWall.Core.Tests.Shell;

public sealed class CurrentUserUninstallCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyFixedUninstallCommandAndOptionalQuietFlagParse(bool quiet)
    {
        string[] args = quiet ? [CurrentUserUninstallCommand.Argument, "--quiet"] : [CurrentUserUninstallCommand.Argument];
        Assert.True(CurrentUserUninstallCommand.IsRequested(args));
        Assert.True(CurrentUserUninstallCommand.TryParse(args, out var command));
        Assert.Equal(quiet, command!.Quiet);
    }

    public static TheoryData<string[]> InvalidCommands => new()
    {
        new[] { "--UNINSTALL-CURRENT-USER" },
        new[] { "--uninstall-current-user=quiet" },
        new[] { "--uninstall-current-user", "--QUIET" },
        new[] { "--uninstall-current-user", @"C:\unins000.exe" },
        new[] { "--uninstall-current-user", "--quiet", "--quiet" },
        new[] { "--add", "--uninstall-current-user" },
        new[] { "--uninstall-current-user", "--context-menu-user", "disable", "en" },
        new[] { "--uninstall-current-user", "--test-root", @"C:\test" },
    };

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public void InvalidAndMixedRequestsAreInterceptedBeforeGuiStartup(string[] args)
    {
        Assert.True(CurrentUserUninstallCommand.IsRequested(args));
        Assert.False(CurrentUserUninstallCommand.TryParse(args, out _));
    }

    [Fact]
    public void OrdinaryActivationDoesNotRequestUninstall()
    {
        Assert.False(CurrentUserUninstallCommand.IsRequested(["--add", @"C:\file.txt"]));
        Assert.False(CurrentUserUninstallCommand.TryParse([], out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DesktopHandoffMarkerParsesWithOptionalQuiet(bool quiet)
    {
        string[] args = quiet ? [CurrentUserUninstallCommand.Argument, "--quiet", "--from-desktop"]
            : [CurrentUserUninstallCommand.Argument, "--from-desktop"];
        Assert.True(CurrentUserUninstallCommand.TryParse(args, out var command));
        Assert.Equal(quiet, command!.Quiet);
        Assert.True(command.FromDesktop);
        Assert.False(CurrentUserUninstallCommand.TryParse(
            [CurrentUserUninstallCommand.Argument, "--from-desktop", "--from-desktop"], out _));
    }

    [Fact]
    public void ElevatedSystemEntryDelegatesWithoutCleaningAdministratorState()
    {
        var delegated = false;
        var exit = CurrentUserUninstallCommand.ExecuteEntry(new(false), () => true,
            () => throw new InvalidOperationException("Administrator HKCU must not be used"), () => delegated = true);
        Assert.Equal(CurrentUserUninstallCommand.Accepted, exit);
        Assert.True(delegated);
    }

    [Fact]
    public void HandoffThatRemainsElevatedFailsWithoutRecursionOrCleanup()
    {
        var exit = CurrentUserUninstallCommand.ExecuteEntry(new(false, FromDesktop: true), () => true,
            () => throw new InvalidOperationException("Must not clean"),
            () => throw new InvalidOperationException("Must not delegate again"));
        Assert.Equal(ContextMenuUserCommand.ElevatedContext, exit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnelevatedEntryPropagatesHandoffFailureWithoutDesktopDelegation(bool fromDesktop)
    {
        var exit = CurrentUserUninstallCommand.ExecuteEntry(new(false, fromDesktop), () => false,
            () => ContextMenuUserCommand.RollbackIncomplete,
            () => throw new InvalidOperationException("Ordinary user must not delegate"));
        Assert.Equal(ContextMenuUserCommand.RollbackIncomplete, exit);
    }

    [Theory]
    [InlineData("--uninstall-notification-cleanup")]
    [InlineData("--UNINSTALL-NOTIFICATION-CLEANUP")]
    [InlineData("--uninstall-notification-cleanup=anything")]
    public void NotificationManagementRequestsAreInterceptedBeforeGuiEvenWhenMixed(string argument)
    {
        Assert.True(CurrentUserUninstallCommand.IsNotificationRequested([argument]));
        Assert.True(CurrentUserUninstallCommand.IsNotificationRequested(["--add", argument]));
        Assert.False(CurrentUserUninstallCommand.IsNotificationRequested(["--add", @"C:\file.txt"]));
    }


}
