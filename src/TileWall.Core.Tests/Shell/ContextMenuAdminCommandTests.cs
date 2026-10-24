using System.ComponentModel;
using System.Runtime.Versioning;
using TileWall.ApplicationModel;
using TileWall.Shell.Interop;
using Xunit;

namespace TileWall.Core.Tests.Shell;

public sealed class ContextMenuAdminCommandTests
{
    [Theory]
    [InlineData("enable", "zh", "添加到 TileWall")]
    [InlineData("disable", "en", "Add to TileWall")]
    public void StrictFixedCommandParsesWithoutXaml(string action, string language, string label)
    {
        string[] args = [ContextMenuAdminCommand.Argument, action, language];
        Assert.True(ContextMenuAdminCommand.IsRequested(args));
        Assert.True(ContextMenuAdminCommand.TryParse(args, out var command));
        Assert.Equal(action == "enable", command!.Enable);
        Assert.Equal(label, command.MenuText);
    }

    public static TheoryData<string[]> InvalidCommands => new()
    {
        new[] { "--context-menu-admin" },
        new[] { "--context-menu-admin", "enable" },
        new[] { "--context-menu-admin", "ENABLE", "en" },
        new[] { "--context-menu-admin", "enable", "fr" },
        new[] { "--CONTEXT-MENU-ADMIN", "enable", "en" },
        new[] { "--context-menu-admin", "enable", "en", "--background" },
        new[] { "--add", "--context-menu-admin", "enable", "zh" },
        new[] { "-Embedding", "--context-menu-admin", "enable", "en" },
        new[] { "--context-menu-admin", "disable", "en", "--test-root", @"C:\test" },
        new[] { "--context-menu-admin=enable", "en" },
    };

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public void InvalidAndMixedAdminRequestsNeverReachElevationOrOperation(string[] args)
    {
        Assert.True(ContextMenuAdminCommand.IsRequested(args));
        var exit = ContextMenuAdminCommand.Execute(args,
            () => throw new InvalidOperationException("Must not check elevation"),
            _ => throw new InvalidOperationException("Must not write"));
        Assert.Equal(ContextMenuAdminCommand.InvalidArguments, exit);
    }

    [Fact]
    public void UnelevatedHelperRejectsWithoutRecursingOrWriting()
    {
        var exit = ContextMenuAdminCommand.Execute([ContextMenuAdminCommand.Argument, "enable", "en"], () => false,
            _ => throw new InvalidOperationException("Must not write"));
        Assert.Equal(ContextMenuAdminCommand.NotElevated, exit);
    }

    [Theory]
    [InlineData(true, false, ContextMenuAdminCommand.Success)]
    [InlineData(false, false, ContextMenuAdminCommand.OperationFailed)]
    [InlineData(false, true, ContextMenuAdminCommand.RollbackIncomplete)]
    public void HelperReturnsFiniteOperationExitCodes(bool success, bool incomplete, int expected)
    {
        var exit = ContextMenuAdminCommand.Execute([ContextMenuAdminCommand.Argument, "disable", "zh"], () => true,
            command =>
            {
                Assert.False(command.Enable);
                Assert.Equal("zh", command.Language);
                return new(success, incomplete);
            });
        Assert.Equal(expected, exit);
    }

    [Fact]
    public void OrdinaryAddAndEmbeddingRequestsDoNotEnterAdministration()
    {
        Assert.False(ContextMenuAdminCommand.IsRequested(["--add", @"C:\file.txt"]));
        Assert.False(ContextMenuAdminCommand.IsRequested(["-Embedding"]));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ElevationUsesOnlyCurrentExecutableAndFixedArgumentsAndReadsAfterExit()
    {
        if (!OperatingSystem.IsWindows()) return;
        var exited = false;
        var actual = new MachineContextMenuState(MachineContextMenuStatus.OtherExecutable, [@"D:\Other\TileWall.exe"]);
        var result = await ContextMenuElevation.ApplyAsync(true, "zh", start =>
        {
            Assert.Equal(Environment.ProcessPath, start.FileName);
            Assert.True(start.UseShellExecute);
            Assert.Equal("runas", start.Verb);
            Assert.Equal("--context-menu-admin enable zh", start.Arguments);
            exited = true;
            return Task.FromResult(0);
        }, () =>
        {
            Assert.True(exited);
            return actual;
        });
        Assert.Equal(ContextMenuElevationStatus.Completed, result.Status);
        Assert.Same(actual, result.State); // A later owner is never replaced by the requested state.
    }

    [Theory]
    [InlineData(0, (int)ContextMenuElevationStatus.Completed)]
    [InlineData(4, (int)ContextMenuElevationStatus.HelperFailed)]
    [InlineData(5, (int)ContextMenuElevationStatus.HelperFailed)]
    [SupportedOSPlatform("windows")]
    public async Task AllHelperExitsReReadActualState(int exitCode, int expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        var reads = 0;
        var result = await ContextMenuElevation.ApplyAsync(false, "en", _ => Task.FromResult(exitCode), () =>
        {
            reads++;
            return new(MachineContextMenuStatus.PartialOrMixed, []);
        });
        Assert.Equal((ContextMenuElevationStatus)expected, result.Status);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData(1223, (int)ContextMenuElevationStatus.Cancelled)]
    [InlineData(5, (int)ContextMenuElevationStatus.LaunchFailed)]
    [SupportedOSPlatform("windows")]
    public async Task UacCancellationAndLaunchFailureReReadActualState(int nativeError, int expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        var reads = 0;
        var result = await ContextMenuElevation.ApplyAsync(false, "en", _ => throw new Win32Exception(nativeError), () =>
        {
            reads++;
            return new(MachineContextMenuStatus.NotRegistered, []);
        });
        Assert.Equal((ContextMenuElevationStatus)expected, result.Status);
        Assert.Null(result.ExitCode);
        Assert.Equal(1, reads);
    }
}
