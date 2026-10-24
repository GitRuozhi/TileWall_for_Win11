using TileWall.Core.Shell;
using Xunit;

namespace TileWall.Core.Tests.Shell;

public sealed class StartButtonPressStateTests
{
    private static readonly StartButtonIdentity Main = new((nint)12, 123, 5, "button-1");
    private static StartButtonPressState Active(bool held = false)
    {
        var state = new StartButtonPressState();
        state.Activate(held);
        return state;
    }
    private static StartButtonPressDecision Physical(StartButtonPressState state, StartButtonInputKind kind, StartButtonIdentity? hit)
        => state.Handle(kind, CapturedInputSource.Physical, hit);

    [Fact]
    public void CompletePhysicalClick_SwallowsBothEdgesAndTogglesOnlyAtUp()
    {
        var state = Active();
        var down = Physical(state, StartButtonInputKind.LeftDown, Main);
        Assert.True(down.Suppress);
        Assert.False(down.Toggle);
        Assert.True(state.HasPendingRelease);
        var up = Physical(state, StartButtonInputKind.LeftUp, Main);
        Assert.True(up.Suppress);
        Assert.True(up.Toggle);
        Assert.False(state.HasPendingRelease);
        Assert.False(Physical(state, StartButtonInputKind.LeftUp, Main).Suppress);
    }

    [Fact]
    public void FirstMoveOutside_PermanentlyCancelsEvenAfterReturningInside()
    {
        var state = Active();
        Physical(state, StartButtonInputKind.LeftDown, Main);
        Assert.False(Physical(state, StartButtonInputKind.Move, null).Suppress);
        Physical(state, StartButtonInputKind.Move, Main);
        var up = Physical(state, StartButtonInputKind.LeftUp, Main);
        Assert.True(up.Suppress);
        Assert.False(up.Toggle);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void IdentityOrDiscoveryGenerationChange_CancelsPair(int changedField)
    {
        var other = changedField switch
        {
            0 => Main with { TaskbarWindow = (nint)13 },
            1 => Main with { ExplorerProcessId = 124 },
            2 => Main with { DiscoveryGeneration = 6 },
            _ => Main with { ButtonId = "button-2" },
        };
        var state = Active();
        Physical(state, StartButtonInputKind.LeftDown, Main);
        Physical(state, StartButtonInputKind.Move, other);
        Assert.False(Physical(state, StartButtonInputKind.LeftUp, Main).Toggle);
    }

    [Fact]
    public void InvalidatedSnapshot_CancelsWithoutNeedingMouseMove()
    {
        var state = Active();
        Physical(state, StartButtonInputKind.LeftDown, Main);
        state.InvalidateSnapshot(Main.DiscoveryGeneration + 1);
        var up = Physical(state, StartButtonInputKind.LeftUp, Main);
        Assert.True(up.Suppress);
        Assert.False(up.Toggle);
        Assert.False(state.HasPendingRelease);
    }

    [Fact]
    public void DelayedSnapshotNotification_DoesNotCancelPressFromCurrentGeneration()
    {
        var state = Active();
        Physical(state, StartButtonInputKind.LeftDown, Main);
        state.InvalidateSnapshot(Main.DiscoveryGeneration);
        var up = Physical(state, StartButtonInputKind.LeftUp, Main);
        Assert.True(up.Suppress);
        Assert.True(up.Toggle);
        Assert.False(state.HasPendingRelease);
    }

    [Fact]
    public void DownOutside_AndDragInside_DoesNotCaptureUpOrSecondDown()
    {
        var state = Active();
        Assert.False(Physical(state, StartButtonInputKind.LeftDown, null).Suppress);
        Assert.False(Physical(state, StartButtonInputKind.LeftDown, Main).Suppress);
        Assert.False(Physical(state, StartButtonInputKind.LeftUp, Main).Suppress);
        Assert.True(Physical(state, StartButtonInputKind.LeftDown, Main).Suppress);
    }

    [Fact]
    public void InitialHeldLeftButton_BypassesUntilFirstPhysicalUp()
    {
        var state = Active(held: true);
        Assert.False(Physical(state, StartButtonInputKind.LeftDown, Main).Suppress);
        Assert.False(Physical(state, StartButtonInputKind.LeftUp, Main).Suppress);
        Assert.True(Physical(state, StartButtonInputKind.LeftDown, Main).Suppress);
    }

    [Theory]
    [InlineData(CapturedInputSource.OwnInjected)]
    [InlineData(CapturedInputSource.ExternalInjected)]
    [InlineData(CapturedInputSource.PromotedPointer)]
    public void NonphysicalClick_BypassesAndCannotCompletePhysicalPress(CapturedInputSource source)
    {
        var state = Active();
        Assert.False(state.Handle(StartButtonInputKind.LeftDown, source, Main).Suppress);
        Assert.False(state.Handle(StartButtonInputKind.LeftUp, source, Main).Suppress);
        Assert.False(state.HasPendingRelease);
        Physical(state, StartButtonInputKind.LeftDown, Main);
        Assert.False(state.Handle(StartButtonInputKind.LeftUp, source, Main).Suppress);
        Assert.True(state.HasPendingRelease);
        var up = Physical(state, StartButtonInputKind.LeftUp, Main);
        Assert.True(up.Suppress);
        Assert.False(up.Toggle);
    }

    [Fact]
    public void Stop_PreservesSwallowedUpAndCancelsToggle()
    {
        var state = Active();
        Physical(state, StartButtonInputKind.LeftDown, Main);
        state.Stop();
        state.Stop();
        Assert.True(state.HasPendingRelease);
        Assert.True(Physical(state, StartButtonInputKind.LeftDown, Main).Suppress);
        var up = Physical(state, StartButtonInputKind.LeftUp, Main);
        Assert.True(up.Suppress);
        Assert.False(up.Toggle);
        Assert.False(state.HasPendingRelease);
        Assert.False(Physical(state, StartButtonInputKind.LeftDown, Main).Suppress);
    }

    [Fact]
    public void OtherMouseMessages_AreNotCaptured()
    {
        var state = Active();
        Physical(state, StartButtonInputKind.LeftDown, Main);
        Assert.False(Physical(state, StartButtonInputKind.Other, null).Suppress);
        Assert.True(Physical(state, StartButtonInputKind.LeftUp, Main).Toggle);
    }

    [Theory]
    [InlineData(0xFF515700u, true)]
    [InlineData(0xFF515780u, true)]
    [InlineData(0xFF5157FFu, true)]
    [InlineData(0xFF515600u, false)]
    [InlineData(0u, false)]
    public void PromotedPointer_UsesDocumentedMask(uint extra, bool expected)
        => Assert.Equal(expected, StartButtonPressState.IsPromotedPointer(extra));
}
