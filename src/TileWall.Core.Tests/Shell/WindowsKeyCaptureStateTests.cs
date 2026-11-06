using TileWall.Core.Shell;
using Xunit;

namespace TileWall.Core.Tests.Shell;

public sealed class WindowsKeyCaptureStateTests
{
    private const int Left = WindowsKeyCaptureState.LeftWindowsKey;
    private const int Right = WindowsKeyCaptureState.RightWindowsKey;
    private const int E = 0x45;
    private const int L = 0x4C;
    private const int Shift = 0xA0;
    private static WindowsKeyInput Down(int key, CapturedInputSource source = CapturedInputSource.Physical)
        => new(new(key, key + 1, key is Left or Right), source);
    private static WindowsKeyInput Up(int key) => Down(key) with { Stroke = Down(key).Stroke with { IsUp = true } };
    private static WindowsKeyCaptureState Active(params int[] held)
    {
        var state = new WindowsKeyCaptureState();
        state.Activate(held);
        return state;
    }

    [Theory]
    [InlineData(Left)]
    [InlineData(Right)]
    public void BareWindowsKey_SwallowsDownRepeatsAndUp_TogglesOnce(int key)
    {
        var state = Active();
        Assert.True(state.Handle(Down(key)).Suppress);
        Assert.True(state.Handle(Down(key)).Suppress);
        var up = state.Handle(Up(key));
        Assert.True(up.Suppress);
        Assert.True(up.Toggle);
        Assert.False(state.Handle(Up(key)).Toggle);
        Assert.Equal(WindowsKeyCapturePhase.Idle, state.Phase);
        Assert.True(state.CanReleaseHook);
    }

    [Fact]
    public void TwoFastIndependentTaps_KeepBothToggles()
    {
        var state = Active();
        state.Handle(Down(Left));
        Assert.True(state.Handle(Up(Left)).Toggle);
        state.Handle(Down(Left));
        Assert.True(state.Handle(Up(Left)).Toggle);
    }

    [Theory]
    [InlineData(E)]
    [InlineData(Shift)]
    [InlineData(Right)]
    public void FirstChordDown_RequestsOneOrderedAtomicReplay_PreservesMetadata(int key)
    {
        var state = Active();
        var win = new WindowsKeyInput(new(Left, 0x5B, true));
        var first = new WindowsKeyInput(new(key, 0x48, false));
        state.Handle(win);
        var result = state.Handle(first);
        var replay = Assert.IsType<WindowsKeyReplay>(result.Replay);
        Assert.True(result.Suppress);
        Assert.Equal(WindowsKeyCapturePhase.Chord, state.Phase);
        Assert.Equal(new[] { win.Stroke, first.Stroke }, replay.Inputs);
        Assert.False(replay.IsCleanup);
        Assert.False(state.Handle(Down(Left, CapturedInputSource.OwnInjected)).Suppress);
        Assert.Equal(default, state.CompleteReplay(replay.Id, 2));
        Assert.Null(state.Handle(Down(key)).Replay);
        Assert.False(state.Handle(Up(key)).Suppress);
        var winUp = state.Handle(Up(Left));
        Assert.False(winUp.Suppress);
        Assert.False(winUp.Toggle);
        Assert.True(state.CanReleaseHook);
        Assert.Equal(WindowsKeyCapturePhase.Idle, state.Phase);
    }

    [Fact]
    public void Chord_RemainsChordWhileEitherWindowsKeyIsHeld()
    {
        var state = Active();
        state.Handle(Down(Left));
        var replay = state.Handle(Down(Right)).Replay!;
        state.CompleteReplay(replay.Id, 2);
        state.Handle(Up(Left));
        Assert.Equal(WindowsKeyCapturePhase.Chord, state.Phase);
        Assert.False(state.Handle(Down(E)).Suppress);
        Assert.False(state.Handle(Up(Right)).Toggle);
        state.Handle(Up(E));
        Assert.Equal(WindowsKeyCapturePhase.Idle, state.Phase);
    }

    [Theory]
    [InlineData(Shift)]
    [InlineData(E)]
    [InlineData(Left)]
    public void InitialHeldKey_BypassesWholeRoundUntilAllKeysAreUp(int held)
    {
        var state = Active(held);
        Assert.False(state.Handle(Down(Right)).Suppress);
        state.Handle(Up(held));
        Assert.Equal(WindowsKeyCapturePhase.Bypass, state.Phase);
        Assert.False(state.Handle(Up(Right)).Toggle);
        Assert.Equal(WindowsKeyCapturePhase.Idle, state.Phase);
        Assert.True(state.Handle(Down(Left)).Suppress);
    }

    [Fact]
    public void WinL_MissingRealUpCanBeReconciledBeforeNextBareWindowsToggle()
    {
        var state = Active();
        state.Handle(Down(Left));
        var replay = state.Handle(Down(L)).Replay!;
        state.CompleteReplay(replay.Id, 2);

        state.Handle(Up(Left));
        Assert.Equal(WindowsKeyCapturePhase.Chord, state.Phase);
        Assert.True(state.ReconcileAfterDesktopSwitch(new HashSet<int>()));
        Assert.Equal(WindowsKeyCapturePhase.Idle, state.Phase);
        Assert.True(state.CanReleaseHook);

        Assert.True(state.Handle(Down(Right)).Suppress);
        Assert.True(state.Handle(Up(Right)).Toggle);
    }

    [Fact]
    public void Reconcile_CancelsPendingToggleButPreservesSwallowedPairUntilRealUp()
    {
        var state = Active();
        Assert.True(state.Handle(Down(Left)).Suppress);

        Assert.True(state.ReconcileAfterDesktopSwitch(new HashSet<int>()));
        Assert.Equal(WindowsKeyCapturePhase.Bypass, state.Phase);
        Assert.True(state.HasPendingRelease);
        Assert.False(state.CanReleaseHook);

        var up = state.Handle(Up(Left));
        Assert.True(up.Suppress);
        Assert.False(up.Toggle);
        Assert.Equal(WindowsKeyCapturePhase.Idle, state.Phase);
        Assert.True(state.CanReleaseHook);
    }

    [Fact]
    public void CancelIdleGesture_BypassesNextWindowsTapThenAllowsNewCandidateAfterRelease()
    {
        var state = Active();
        state.CancelCurrentGesture();
        Assert.Equal(WindowsKeyCapturePhase.Bypass, state.Phase);

        Assert.False(state.Handle(Down(Left)).Suppress);
        var bypassedUp = state.Handle(Up(Left));
        Assert.False(bypassedUp.Suppress);
        Assert.False(bypassedUp.Toggle);
        Assert.Equal(WindowsKeyCapturePhase.Idle, state.Phase);

        Assert.True(state.Handle(Down(Right)).Suppress);
        Assert.True(state.Handle(Up(Right)).Toggle);
    }

    [Fact]
    public void Reconcile_PreservesStillHeldPhysicalAndConfirmedInjectedKeys()
    {
        var state = Active();
        state.Handle(Down(Left));
        var replay = state.Handle(Down(L)).Replay!;
        state.CompleteReplay(replay.Id, 2);

        Assert.True(state.ReconcileAfterDesktopSwitch(new HashSet<int> { Left, L }));
        Assert.Equal(WindowsKeyCapturePhase.Bypass, state.Phase);
        Assert.True(state.HasPendingRelease);
        Assert.False(state.Handle(Up(L)).Suppress);
        Assert.False(state.Handle(Up(Left)).Toggle);
        Assert.Equal(WindowsKeyCapturePhase.Idle, state.Phase);
        Assert.True(state.CanReleaseHook);
    }

    [Fact]
    public void Reconcile_ObservedHeldKeyMustBeReleasedBeforeCaptureResumes()
    {
        var state = Active();
        Assert.True(state.ReconcileAfterDesktopSwitch(new HashSet<int> { Shift }));
        Assert.Equal(WindowsKeyCapturePhase.Bypass, state.Phase);
        Assert.False(state.Handle(Down(Left)).Suppress);
        Assert.False(state.Handle(Up(Left)).Toggle);
        Assert.Equal(WindowsKeyCapturePhase.Bypass, state.Phase);

        state.Handle(Up(Shift));
        Assert.Equal(WindowsKeyCapturePhase.Idle, state.Phase);
        state.Handle(Down(Left));
        Assert.True(state.Handle(Up(Left)).Toggle);
    }

    [Fact]
    public void ExternalUp_ReleasesInitialUnknownObservedBaseline()
    {
        var state = Active(Shift);
        state.Handle(Up(Shift) with { Source = CapturedInputSource.ExternalInjected });

        Assert.Equal(WindowsKeyCapturePhase.Idle, state.Phase);
        state.Handle(Down(Left));
        Assert.True(state.Handle(Up(Left)).Toggle);
    }

    [Fact]
    public void Reconcile_RejectsOutstandingReplayWithoutChangingPairingLedger()
    {
        var state = Active();
        state.Handle(Down(Left));
        var replay = state.Handle(Down(L)).Replay!;
        Assert.True(state.HasPendingRelease);

        Assert.False(state.ReconcileAfterDesktopSwitch(new HashSet<int>()));
        Assert.Equal(WindowsKeyCapturePhase.Chord, state.Phase);
        Assert.True(state.HasPendingRelease);

        state.CompleteReplay(replay.Id, 2);
        Assert.False(state.Handle(Up(L)).Suppress);
        Assert.False(state.Handle(Up(Left)).Toggle);
        Assert.True(state.CanReleaseHook);
    }

    [Fact]
    public void Reconcile_RejectsUnknownReplayWithoutChangingPairingLedger()
    {
        var state = Active();
        state.Handle(Down(Left));
        var replay = state.Handle(Down(L)).Replay!;
        state.CompleteReplay(replay.Id, null);
        Assert.True(state.HasUnconfirmedReplay);
        Assert.True(state.HasPendingRelease);

        Assert.False(state.ReconcileAfterDesktopSwitch(new HashSet<int>()));
        Assert.Equal(WindowsKeyCapturePhase.Faulted, state.Phase);
        Assert.True(state.HasUnconfirmedReplay);
        Assert.True(state.HasPendingRelease);

        Assert.True(state.Handle(Up(L)).Suppress);
        Assert.True(state.Handle(Up(Left)).Suppress);
    }

    [Fact]
    public void Reconcile_AcceptsConfirmedPartialReplayButKeepsFaultedPhaseAndSwallowedPair()
    {
        var state = Active();
        state.Handle(Down(Left));
        var replay = state.Handle(Down(L)).Replay!;
        Assert.Equal(WindowsKeyCaptureFailure.ReplayIncomplete, state.CompleteReplay(replay.Id, 1).Failure);
        Assert.Equal(WindowsKeyCapturePhase.Faulted, state.Phase);

        Assert.True(state.ReconcileAfterDesktopSwitch(new HashSet<int>()));
        Assert.Equal(WindowsKeyCapturePhase.Faulted, state.Phase);
        Assert.True(state.HasPendingRelease);
        var up = state.Handle(Up(L));
        Assert.True(up.Suppress);
        Assert.False(up.Toggle);
        Assert.Equal(WindowsKeyCapturePhase.Faulted, state.Phase);
    }

    [Fact]
    public void ReconcileAfterStop_DoesNotReactivateCapture()
    {
        var state = Active();
        state.Handle(Down(Left));
        state.Stop();

        Assert.True(state.ReconcileAfterDesktopSwitch(new HashSet<int>()));
        Assert.Equal(WindowsKeyCapturePhase.Stopped, state.Phase);
        Assert.True(state.Handle(Up(Left)).Suppress);
        Assert.False(state.Handle(Down(Right)).Suppress);
        Assert.Equal(WindowsKeyCapturePhase.Stopped, state.Phase);
    }

    [Fact]
    public void OtherPhysicalKeyFirst_BypassesWindowsEvenAfterOtherKeyRelease()
    {
        var state = Active();
        state.Handle(Down(Shift));
        Assert.False(state.Handle(Down(Left)).Suppress);
        state.Handle(Up(Shift));
        Assert.False(state.Handle(Up(Left)).Toggle);
        Assert.True(state.Handle(Down(Right)).Suppress);
    }

    [Fact]
    public void OwnInjectedInput_DoesNotCreateOrCancelPhysicalCandidate()
    {
        var state = Active();
        Assert.False(state.Handle(Down(Left, CapturedInputSource.OwnInjected)).Suppress);
        Assert.Equal(WindowsKeyCapturePhase.Idle, state.Phase);
        state.Handle(Down(Left));
        state.Handle(Down(E, CapturedInputSource.OwnInjected));
        Assert.True(state.Handle(Up(Left)).Toggle);
    }

    [Fact]
    public void ExternalInjectedInput_CancelsPendingWithoutInventingReleases()
    {
        var state = Active();
        state.Handle(Down(Left));
        var external = state.Handle(Down(E, CapturedInputSource.ExternalInjected));
        Assert.False(external.Suppress);
        Assert.Null(external.Replay);
        Assert.Equal(WindowsKeyCaptureFailure.ExternalInjectionInterrupted, external.Failure);
        var up = state.Handle(Up(Left));
        Assert.True(up.Suppress);
        Assert.False(up.Toggle);
        Assert.True(state.CanReleaseHook);
    }

    [Fact]
    public void ExternalInjectedHeldKey_PreventsPhysicalBareWindowsCandidate()
    {
        var state = Active();
        state.Handle(Down(Shift, CapturedInputSource.ExternalInjected));
        Assert.False(state.Handle(Down(Left)).Suppress);
        state.Handle(Up(Shift) with { Source = CapturedInputSource.ExternalInjected });
        Assert.False(state.Handle(Up(Left)).Toggle);
        Assert.Equal(WindowsKeyCapturePhase.Idle, state.Phase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PartialReplay_Faults_OnlyConfirmedDownsAreEligibleForCleanup(int inserted)
    {
        var state = Active();
        state.Handle(Down(Left));
        var replay = state.Handle(Down(E)).Replay!;
        Assert.Equal(WindowsKeyCaptureFailure.ReplayIncomplete, state.CompleteReplay(replay.Id, inserted).Failure);
        var cleanup = state.ReleaseKnownInjectedDowns().Replay;
        if (inserted == 0) Assert.Null(cleanup);
        else
        {
            Assert.NotNull(cleanup);
            var release = Assert.Single(cleanup.Inputs);
            Assert.Equal(Left, release.VirtualKey);
            Assert.True(release.IsUp);
            Assert.True(release.Extended);
            state.CompleteReplay(cleanup.Id, 1);
        }

        Assert.False(state.CanReleaseHook);
        Assert.True(state.Handle(Up(E)).Suppress);
        var up = state.Handle(Up(Left));
        Assert.True(up.Suppress);
        Assert.False(up.Toggle);
        Assert.True(state.CanReleaseHook);
    }

    [Fact]
    public void UnknownReplayResult_DoesNotClaimThatAnythingWasInsertedOrReleased()
    {
        var state = Active();
        state.Handle(Down(Left));
        var replay = state.Handle(Down(E)).Replay!;
        Assert.Equal(WindowsKeyCaptureFailure.ReplayResultUnknown, state.CompleteReplay(replay.Id, null).Failure);
        Assert.True(state.HasUnconfirmedReplay);
        Assert.Null(state.ReleaseKnownInjectedDowns().Replay);
        state.Handle(Up(E));
        state.Handle(Up(Left));
        Assert.False(state.CanReleaseHook);
    }

    [Fact]
    public void UnsupportedFirstKey_FaultsWithoutReplay_AndSwallowsBothPairs()
    {
        var state = Active();
        state.Handle(Down(Left));
        var result = state.Handle(new(new(E, 0, false, IsReplayable: false)));
        Assert.Equal(WindowsKeyCaptureFailure.UnsupportedReplay, result.Failure);
        Assert.True(result.Suppress);
        Assert.Null(result.Replay);
        Assert.True(state.Handle(Up(E)).Suppress);
        Assert.False(state.Handle(Up(Left)).Toggle);
        Assert.True(state.CanReleaseHook);
    }

    [Fact]
    public void StopPending_DoesNotInjectOrToggle_RetainsHookUntilMatchingUp()
    {
        var state = Active();
        state.Handle(Down(Left));
        Assert.Null(state.Stop().Replay);
        state.Stop();
        Assert.False(state.CanReleaseHook);
        Assert.True(state.Handle(Down(Left)).Suppress);
        Assert.False(state.Handle(Down(E)).Suppress);
        var up = state.Handle(Up(Left));
        Assert.True(up.Suppress);
        Assert.False(up.Toggle);
        Assert.True(state.CanReleaseHook);
    }

    [Fact]
    public void StopReplayedChord_DefaultWaitsForRealUpsWithoutSyntheticRelease()
    {
        var state = Active();
        state.Handle(Down(Left));
        var replay = state.Handle(Down(Right)).Replay!;
        state.CompleteReplay(replay.Id, 2);
        Assert.Null(state.Stop().Replay);
        Assert.False(state.CanReleaseHook);
        Assert.False(state.Handle(Up(Left)).Suppress);
        Assert.False(state.CanReleaseHook);
        Assert.False(state.Handle(Up(Right)).Toggle);
        Assert.True(state.CanReleaseHook);
    }

    [Fact]
    public void ExplicitCleanup_OnlyReleasesSelfInsertedKeys_AndTracksPartialRelease()
    {
        var state = Active();
        state.Handle(Down(Left));
        var replay = state.Handle(Down(E)).Replay!;
        state.CompleteReplay(replay.Id, 2);
        state.Handle(Down(Shift)); // Passed through; never owned by this state machine.
        var cleanup = state.ReleaseKnownInjectedDowns().Replay!;
        Assert.Equal(new[] { E, Left }, cleanup.Inputs.Select(stroke => stroke.VirtualKey));
        Assert.All(cleanup.Inputs, stroke => Assert.True(stroke.IsUp));
        Assert.Equal(WindowsKeyCaptureFailure.ReplayIncomplete, state.CompleteReplay(cleanup.Id, 1).Failure);
        var retry = state.ReleaseKnownInjectedDowns().Replay!;
        Assert.Equal(Left, Assert.Single(retry.Inputs).VirtualKey);
        state.CompleteReplay(retry.Id, 1);
        Assert.True(state.Handle(Down(E)).Suppress);
        Assert.True(state.Handle(Up(E)).Suppress);
        Assert.True(state.Handle(Up(Left)).Suppress);
        Assert.True(state.CanReleaseHook);
        Assert.False(state.Handle(Up(Shift)).Suppress);
    }

    [Fact]
    public void StopDuringReplay_ConfirmationCannotReactivateCapture()
    {
        var state = Active();
        state.Handle(Down(Left));
        var replay = state.Handle(Down(E)).Replay!;
        state.Stop();
        Assert.False(state.CanReleaseHook);
        state.CompleteReplay(replay.Id, 2);
        state.Handle(Up(E));
        Assert.False(state.Handle(Up(Left)).Toggle);
        Assert.False(state.Handle(Down(Right)).Suppress);
    }

    [Fact]
    public void PartialCleanup_UnreleasedInjectedDownStillReceivesItsRealUp()
    {
        var state = Active();
        state.Handle(Down(Left));
        var replay = state.Handle(Down(E)).Replay!;
        state.CompleteReplay(replay.Id, 2);
        var cleanup = state.ReleaseKnownInjectedDowns().Replay!;
        state.CompleteReplay(cleanup.Id, 1); // E up inserted; Windows up was not inserted.
        Assert.True(state.Handle(Up(E)).Suppress);
        Assert.False(state.Handle(Up(Left)).Suppress);
        Assert.True(state.CanReleaseHook);
    }

    [Fact]
    public void ConfirmationMustMatchOutstandingRequest_AndCannotRunTwice()
    {
        var state = Active();
        state.Handle(Down(Left));
        var replay = state.Handle(Down(E)).Replay!;
        Assert.Throws<InvalidOperationException>(() => state.CompleteReplay(replay.Id + 1, 2));
        state.CompleteReplay(replay.Id, 2);
        Assert.Throws<InvalidOperationException>(() => state.CompleteReplay(replay.Id, 2));
    }
}
