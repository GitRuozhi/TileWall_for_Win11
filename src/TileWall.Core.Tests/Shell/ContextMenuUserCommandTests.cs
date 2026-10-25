using TileWall.ApplicationModel;
using TileWall.Shell.Interop;
using Xunit;

namespace TileWall.Core.Tests.Shell;

public sealed class ContextMenuUserCommandTests
{
    [Theory]
    [InlineData("enable", "zh", "添加到 TileWall")]
    [InlineData("disable", "en", "Add to TileWall")]
    [InlineData("query", "en", "Add to TileWall")]
    public void StrictFixedCommandParsesWithoutXaml(string action, string language, string label)
    {
        string[] args = [ContextMenuUserCommand.Argument, action, language];
        Assert.True(ContextMenuUserCommand.IsRequested(args));
        Assert.True(ContextMenuUserCommand.TryParse(args, out var command));
        Assert.Equal(action == "enable", command!.Enable);
        Assert.Equal(action == "query", command.Action == ContextMenuUserAction.Query);
        Assert.Equal(label, command.MenuText);
    }

    public static TheoryData<string[]> InvalidCommands => new()
    {
        new[] { "--context-menu-user" },
        new[] { "--context-menu-user", "enable" },
        new[] { "--context-menu-user", "ENABLE", "en" },
        new[] { "--context-menu-user", "enable", "fr" },
        new[] { "--CONTEXT-MENU-USER", "enable", "en" },
        new[] { "--context-menu-user", "enable", "en", "--background" },
        new[] { "--add", "--context-menu-user", "enable", "zh" },
        new[] { "-Embedding", "--context-menu-user", "enable", "en" },
        new[] { "--context-menu-user", "disable", "en", "--test-root", @"C:\test" },
        new[] { "--context-menu-user=enable", "en" },
        new[] { "--context-menu-admin", "enable", "en" },
        new[] { "--context-menu-admin", "disable", "zh" },
        new[] { "--CONTEXT-MENU-ADMIN", "enable", "en" },
        new[] { "--context-menu-admin=enable", "en" },
        new[] { "--add", "--context-menu-admin", "enable", "en" },
        new[] { "--context-menu-user", "disable", "en", "--context-menu-admin" },
    };

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public void InvalidMixedAndRetiredRequestsAreInterceptedWithoutWriting(string[] args)
    {
        Assert.True(ContextMenuUserCommand.IsRequested(args));
        Assert.False(ContextMenuUserCommand.TryParse(args, out _));
        var exit = ContextMenuUserCommand.Execute(args,
            () => throw new InvalidOperationException("Must not check token"),
            _ => throw new InvalidOperationException("Must not write"),
            () => throw new InvalidOperationException("Must not read"));
        Assert.Equal(ContextMenuUserCommand.InvalidArguments, exit);
    }

    [Theory]
    [InlineData(true, false, ContextMenuUserCommand.Success)]
    [InlineData(false, false, ContextMenuUserCommand.OperationFailed)]
    [InlineData(false, true, ContextMenuUserCommand.RollbackIncomplete)]
    public void UserCommandRunsDirectlyAndReturnsFiniteOperationExitCodes(bool success, bool incomplete, int expected)
    {
        var writes = 0;
        var exit = ContextMenuUserCommand.Execute([ContextMenuUserCommand.Argument, "disable", "zh"], () => false, command =>
        {
            writes++;
            Assert.False(command.Enable);
            Assert.Equal("zh", command.Language);
            return new(success, incomplete);
        }, () => throw new InvalidOperationException("Must not query"));
        Assert.Equal(1, writes);
        Assert.Equal(expected, exit);
    }

    [Theory]
    [InlineData("enable")]
    [InlineData("disable")]
    [InlineData("query")]
    public void ElevatedTokenNeverReadsOrWritesAdministratorUserRegistration(string action)
    {
        var exit = ContextMenuUserCommand.Execute([ContextMenuUserCommand.Argument, action, "en"], () => true,
            _ => throw new InvalidOperationException("Must not write"),
            () => throw new InvalidOperationException("Must not read"));
        Assert.Equal(ContextMenuUserCommand.ElevatedContext, exit);
    }

    [Theory]
    [InlineData((int)ContextMenuStatus.NotRegistered, ContextMenuUserCommand.QueryOff)]
    [InlineData((int)ContextMenuStatus.CurrentExecutable, ContextMenuUserCommand.QueryOwnedOn)]
    [InlineData((int)ContextMenuStatus.OtherExecutable, ContextMenuUserCommand.QueryOtherOrConflict)]
    [InlineData((int)ContextMenuStatus.PartialOrMixed, ContextMenuUserCommand.QueryOtherOrConflict)]
    [InlineData((int)ContextMenuStatus.ReadFailed, ContextMenuUserCommand.QueryReadFailed)]
    public void QueryReturnsActualCurrentUserStatusWithoutWriting(int status, int expected)
    {
        var reads = 0;
        var exit = ContextMenuUserCommand.Execute([ContextMenuUserCommand.Argument, "query", "en"], () => false,
            _ => throw new InvalidOperationException("Must not write"), () =>
            {
                reads++;
                return new((ContextMenuStatus)status, []);
            });
        Assert.Equal(1, reads);
        Assert.Equal(expected, exit);
    }

    [Fact]
    public void OrdinaryAddAndEmbeddingRequestsDoNotEnterMenuManagement()
    {
        Assert.False(ContextMenuUserCommand.IsRequested(["--add", @"C:\file.txt"]));
        Assert.False(ContextMenuUserCommand.IsRequested(["-Embedding"]));
    }
}
