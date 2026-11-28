using TileWall.Core.Shell;
using Xunit;

namespace TileWall.Core.Tests.Shell;

public sealed class WallShowMachineTests
{
    [Theory]
    [InlineData(WallShowState.Hidden, WallShowTrigger.Toggle, WallShowState.Visible, "SnapShow")]
    [InlineData(WallShowState.Hidden, WallShowTrigger.Show, WallShowState.Visible, "SnapShow")]
    [InlineData(WallShowState.Hidden, WallShowTrigger.Hide, WallShowState.Hidden, null)]
    [InlineData(WallShowState.Visible, WallShowTrigger.Toggle, WallShowState.Hidden, "SnapHide")]
    [InlineData(WallShowState.Visible, WallShowTrigger.Show, WallShowState.Visible, "FocusWall")]
    [InlineData(WallShowState.Visible, WallShowTrigger.Hide, WallShowState.Hidden, "SnapHide")]
    public void RequestsSettleVisibilityAndFocusBeforeReturning(WallShowState initial, WallShowTrigger trigger,
        WallShowState expected, string? action)
    {
        var host = new FakeHost();
        var machine = new WallShowMachine(initial, host);
        Assert.Empty(host.Calls);

        machine.Request(trigger);

        Assert.Equal(expected, machine.State);
        if (action is null) Assert.Empty(host.Calls);
        else Assert.Equal(action, Assert.Single(host.Calls));
    }

    [Fact]
    public void RapidTogglesSettleInOrder()
    {
        var host = new FakeHost();
        var machine = new WallShowMachine(WallShowState.Hidden, host);

        machine.Request(WallShowTrigger.Toggle);
        machine.Request(WallShowTrigger.Toggle);
        machine.Request(WallShowTrigger.Toggle);

        Assert.Equal(WallShowState.Visible, machine.State);
        Assert.Equal(["SnapShow", "SnapHide", "SnapShow"], host.Calls);
    }

    private sealed class FakeHost : IWallShowHost
    {
        public List<string> Calls { get; } = [];
        public void SnapShow() => Calls.Add(nameof(SnapShow));
        public void SnapHide() => Calls.Add(nameof(SnapHide));
        public void FocusWall() => Calls.Add(nameof(FocusWall));
    }
}
