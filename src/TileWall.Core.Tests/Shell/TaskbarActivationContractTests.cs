using TileWall.ApplicationModel;
using TileWall.Shell.Interop;
using Xunit;

namespace TileWall.Core.Tests.Shell;

public sealed class TaskbarActivationContractTests
{
    [Fact]
    public void NativeRevealMayReplaceTheOwnedHiddenStripWithAFullRegion()
    {
        var monitor = new TaskbarBounds(0, 0, 2560, 1440);
        var target = new TaskbarBounds(0, 1400, 2560, 40);
        var hiddenStrip = new TaskbarClipState(true, [new(0, 0, 2560, 2)]);
        var saved = new TaskbarWindowState(1, 2, 3, true, true, monitor,
            new(0, 1438, 2560, 40), target, Clip: hiddenStrip, HoldAutoHide: true);
        var observed = saved;
        var backend = new TaskbarActivationBackend(() => [observed]) { Scope = monitor };
        var state = backend.Capture(TaskbarLeaseMode.Reveal)!;

        observed = saved with { Original = target, Clip = new(true, [new(0, 0, 2560, 40)]) };
        Assert.True(backend.IsCurrent(state));
        observed = observed with { Clip = new(false, []) };
        Assert.True(backend.IsCurrent(state));
        observed = observed with { Clip = new(true, [new(0, 0, 2560, 20)]) };
        Assert.False(backend.IsCurrent(state));
        observed = observed with { Clip = new(true, [new(0, 0, 2560, 40)]), HoldAutoHide = false };
        Assert.False(backend.IsCurrent(state));

        // A full region is allowed only as the native transition of an owned
        // auto-hide strip, not as a replacement for an unrelated custom region.
        observed = saved with { Clip = new(true, [new(0, 0, 1280, 40)]) };
        state = backend.Capture(TaskbarLeaseMode.Reveal)!;
        observed = observed with { Clip = new(true, [new(0, 0, 2560, 40)]) };
        Assert.False(backend.IsCurrent(state));
    }

    [Theory]
    [InlineData(0, -60, 0, 160, 800)]
    [InlineData(1, -100, 40, 200, 760)]
    [InlineData(2, -100, 0, 160, 800)]
    [InlineData(3, -100, 0, 200, 760)]
    public void TaskbarReservationUsesOneEdgeWithoutDoubleDeductingWorkArea(int edge,
        int expectedX, int expectedY, int width, int height)
    {
        const int x = -100;
        var bar = edge switch
        {
            0 => new TaskbarBounds(x, 0, 40, 800),
            1 => new TaskbarBounds(x, 0, 200, 40),
            2 => new TaskbarBounds(60, 0, 40, 800),
            _ => new TaskbarBounds(x, 760, 200, 40)
        };
        var actual = TaskbarActivationBackend.ExcludeEdge(new(x, 0, 200, 800), bar, edge);
        Assert.Equal(new TaskbarArea(expectedX, expectedY, width, height), actual);
        Assert.Equal(actual, TaskbarActivationBackend.ExcludeEdge(actual, bar, edge));
    }

    [Theory]
    [InlineData("--taskbar-recovery")]
    [InlineData("--taskbar-recovery-invalid")]
    [InlineData("--TASKBAR-RECOVERY")]
    public void MalformedHelperRequestsStayOutOfOrdinaryStartup(string verb)
    {
        Assert.True(TaskbarRecoveryCommand.IsRequested([verb]));
        Assert.False(TaskbarRecoveryCommand.TryParse([verb], out _, out _, out _));
    }

    [Fact]
    public void HelperRejectsMixedArgumentsAndArbitraryRecoveryPaths()
    {
        var arguments = new[] { TaskbarRecoveryCommand.Argument, "1", "2", Guid.NewGuid().ToString("N") };
        Assert.True(TaskbarRecoveryCommand.TryParse(arguments, out var parent, out var started, out _));
        Assert.Equal(1, parent); Assert.Equal(2, started);
        Assert.False(TaskbarRecoveryCommand.TryParse([.. arguments, "--background"], out _, out _, out _));
        Assert.False(TaskbarRecoveryCommand.TryParse([.. arguments, "C:\\other\\recovery.json"], out _, out _, out _));
        Assert.False(TaskbarRecoveryCommand.TryParse(["--add", .. arguments], out _, out _, out _));
    }
}
