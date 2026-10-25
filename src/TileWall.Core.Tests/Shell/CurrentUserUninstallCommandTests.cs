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
        Assert.Equal(ContextMenuUserCommand.Success, exit);
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
    public void UnelevatedEntryKeepsTheExistingConfirmedUninstallFlow(bool fromDesktop)
    {
        var exit = CurrentUserUninstallCommand.ExecuteEntry(new(false, fromDesktop), () => false,
            () => ContextMenuUserCommand.RollbackIncomplete,
            () => throw new InvalidOperationException("Ordinary user must not delegate"));
        Assert.Equal(ContextMenuUserCommand.RollbackIncomplete, exit);
    }

    [Theory]
    [InlineData(-1, ContextMenuUserCommand.Success)]
    [InlineData(0x58, ContextMenuUserCommand.OperationFailed)]
    public void CancellationOrInvalidRequestDoesNotCleanCurrentUserState(int request, int expected)
    {
        using var pipe = new DuplexStream(request == -1 ? [] : [(byte)request]);
        var cleanups = 0;
        var exit = CurrentUserUninstallCommand.ExecuteHandshake(pipe, _ =>
        {
            cleanups++;
            throw new InvalidOperationException("Must not clean current-user state");
        });
        Assert.Equal(expected, exit);
        Assert.Equal(0, cleanups);
        byte[] expectedOutput = request == -1 ? [] : [0x45];
        Assert.Equal(expectedOutput, pipe.Output);
    }

    [Fact]
    public void ConfirmedDeleteAndAcknowledgementCommitBeforeSuccessfulExit()
    {
        using var pipe = new DuplexStream([0x44, 0x41]);
        var transaction = new CleanupTransaction();
        var exit = CurrentUserUninstallCommand.ExecuteHandshake(pipe, transaction.Execute);
        Assert.Equal(ContextMenuUserCommand.Success, exit);
        Assert.Equal(new byte[] { 0x4F }, pipe.Output);
        Assert.True(transaction.Committed);
        Assert.False(transaction.MenuExists);
        Assert.False(transaction.StartupExists);
        Assert.False(transaction.RolledBack);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrInvalidAcknowledgementRollsBackBothUserEffects(bool invalidAck)
    {
        using var pipe = new DuplexStream(invalidAck ? [0x44, 0x58] : [0x44]);
        var transaction = new CleanupTransaction();
        var exit = CurrentUserUninstallCommand.ExecuteHandshake(pipe, transaction.Execute);
        Assert.Equal(ContextMenuUserCommand.OperationFailed, exit);
        Assert.Equal(new byte[] { 0x4F, 0x45 }, pipe.Output);
        Assert.False(transaction.Committed);
        Assert.True(transaction.MenuExists);
        Assert.True(transaction.StartupExists);
        Assert.True(transaction.RolledBack);
    }

    [Fact]
    public void CallbackWriteFailureRollsBackBeforeErrorExit()
    {
        using var pipe = new DuplexStream([0x44, 0x41]) { FailRemovedWrite = true };
        var transaction = new CleanupTransaction();
        var exit = CurrentUserUninstallCommand.ExecuteHandshake(pipe, transaction.Execute);
        Assert.Equal(ContextMenuUserCommand.OperationFailed, exit);
        Assert.Equal(new byte[] { 0x45 }, pipe.Output);
        Assert.True(transaction.MenuExists);
        Assert.True(transaction.StartupExists);
        Assert.True(transaction.RolledBack);
    }

    [Fact]
    public void IncompleteCleanupRollbackHasDistinctFailureExit()
    {
        using var pipe = new DuplexStream([0x44]);
        var transaction = new CleanupTransaction { FailRollback = true };
        var exit = CurrentUserUninstallCommand.ExecuteHandshake(pipe, transaction.Execute);
        Assert.Equal(ContextMenuUserCommand.RollbackIncomplete, exit);
        Assert.False(transaction.Committed);
        Assert.True(transaction.RolledBack);
        Assert.Equal(new byte[] { 0x4F, 0x45 }, pipe.Output);
    }

    private sealed class CleanupTransaction
    {
        public bool MenuExists { get; private set; } = true;
        public bool StartupExists { get; private set; } = true;
        public bool Committed { get; private set; }
        public bool RolledBack { get; private set; }
        public bool FailRollback { get; init; }

        public ContextMenuRegistrationResult Execute(Action beforeCommit)
        {
            MenuExists = false;
            StartupExists = false;
            try
            {
                beforeCommit();
                Committed = true;
                return new(true);
            }
            catch (Exception ex)
            {
                RolledBack = true;
                MenuExists = true;
                StartupExists = !FailRollback;
                return new(false, FailRollback, ex.Message);
            }
        }
    }

    private sealed class DuplexStream(byte[] input) : Stream
    {
        private readonly MemoryStream _input = new(input);
        private readonly MemoryStream _output = new();
        public byte[] Output => _output.ToArray();
        public bool FailRemovedWrite { get; init; }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_input.Read(buffer.Span));
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (FailRemovedWrite && count > 0 && buffer[offset] == 0x4F) throw new IOException("Callback pipe write failed");
            _output.Write(buffer, offset, count);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = buffer.ToArray();
            Write(bytes, 0, bytes.Length);
            return ValueTask.CompletedTask;
        }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _input.Dispose(); _output.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
