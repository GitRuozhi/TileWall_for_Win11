using TileWall.Core.Imaging;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class MemberCarouselRulesTests
{
    private static readonly DateTimeOffset Baseline = DateTimeOffset.UnixEpoch;
    private static readonly string[] Images = ["a", "b", "c", "d"];
    private static MemberCarouselRuntime Fresh => new(false, 0, [], null);
    private static ImageCarouselState Saved() => new()
    {
        CandidateIds = Images, Queue = Images, NextIndex = 1, Round = 1,
        CurrentImageId = Images[0], LastSwitchUtc = Baseline,
    };

    [Theory]
    [InlineData(MemberCarouselGate.Hidden)]
    [InlineData(MemberCarouselGate.Gesture)]
    [InlineData(MemberCarouselGate.Modal)]
    [InlineData(MemberCarouselGate.FlipActive)]
    public void GateLeavesRuntimeUntouched(MemberCarouselGate gate)
    {
        var runtime = new MemberCarouselRuntime(true, 3, [Images[1]], Images[2]);
        var result = MemberCarouselRules.Evaluate(Images, Saved(), runtime,
            Baseline.AddHours(1), gate, true, 0, manual: true);
        Assert.Equal(MemberCarouselDecisionKind.None, result.Kind);
        Assert.Same(runtime, result.Runtime);
    }

    [Theory]
    [InlineData(ImageRotationInterval.Seconds5)]
    [InlineData(ImageRotationInterval.Seconds15)]
    [InlineData(ImageRotationInterval.Seconds30)]
    [InlineData(ImageRotationInterval.Minute1)]
    [InlineData(ImageRotationInterval.Minute5)]
    [InlineData(ImageRotationInterval.Minute15)]
    [InlineData(ImageRotationInterval.Minute30)]
    public void EveryIntervalWaitsPreciselyThenPreparesOneCandidate(ImageRotationInterval interval)
    {
        var dueUtc = Baseline.AddSeconds((int)interval);
        var waiting = MemberCarouselRules.Evaluate(Images, Saved(), Fresh,
            dueUtc.AddTicks(-1), MemberCarouselGate.Ready, false, 0, interval);
        Assert.Equal(TimeSpan.FromTicks(1), waiting.Remaining);
        var due = MemberCarouselRules.Evaluate(Images, Saved(), Fresh,
            dueUtc, MemberCarouselGate.Ready, false, 0, interval);
        Assert.Equal("b", due.CandidateImageId);
        Assert.Equal(MemberCarouselDecisionKind.Prepare, due.Kind);
        var held = MemberCarouselRules.Evaluate(Images, Saved(), due.Runtime,
            dueUtc.AddHours(1), MemberCarouselGate.Ready, false, 0, interval);
        Assert.Equal(MemberCarouselDecisionKind.None, held.Kind);
        Assert.Same(due.Runtime, held.Runtime);
    }

    [Fact]
    public void FailureSkipsQueuedCandidatesAndStopsAfterThreeAttemptsWithoutAdoptingThem()
    {
        var persisted = Saved();
        var first = MemberCarouselRules.Evaluate(Images, persisted, Fresh,
            Baseline.AddMinutes(1), MemberCarouselGate.Ready, false, 0);
        var second = MemberCarouselRules.ReportLoadFailure(Images, persisted, first.Runtime, 0);
        var third = MemberCarouselRules.ReportLoadFailure(Images, persisted, second.Runtime, 0);
        var exhausted = MemberCarouselRules.ReportLoadFailure(Images, persisted, third.Runtime, 0);
        Assert.Equal(new[] { "b", "c", "d" }, new[] { first.CandidateImageId, second.CandidateImageId, third.CandidateImageId });
        Assert.True(exhausted.Runtime.Passive);
        Assert.Equal(3, exhausted.Runtime.AttemptsThisDue);
        Assert.Null(exhausted.Runtime.PendingState);
        Assert.Equal("a", persisted.CurrentImageId);
        Assert.Equal(1, persisted.NextIndex);
    }

    [Fact]
    public void SuccessfulDecodeAdoptsPreparedQueueAndActualPresentationTime()
    {
        var due = MemberCarouselRules.Evaluate(Images, Saved(), Fresh,
            Baseline.AddMinutes(1), MemberCarouselGate.Ready, false, 0);
        var entered = Baseline.AddMinutes(1).AddMilliseconds(125);
        var capture = MemberCarouselRules.EnterFlip(Saved(), due.Runtime, "B", entered);
        Assert.Equal("B", capture.NextPersistedState.CurrentImageId);
        Assert.Equal(2, capture.NextPersistedState.NextIndex);
        Assert.Equal(entered, capture.NextPersistedState.LastSwitchUtc);
        Assert.Null(capture.Runtime.PendingImageId);
        Assert.Null(capture.Runtime.PendingState);
        Assert.Empty(capture.Runtime.FailedThisDue);
    }

    [Fact]
    public void CancelDoesNotConsumeQueueAndWrongDecodeCannotBeAdopted()
    {
        var due = MemberCarouselRules.Evaluate(Images, Saved(), Fresh,
            Baseline.AddMinutes(1), MemberCarouselGate.Ready, false, 0);
        Assert.Throws<ArgumentException>(() => MemberCarouselRules.EnterFlip(Saved(), due.Runtime, "c", Baseline));
        var abandoned = MemberCarouselRules.AbandonPending(due.Runtime);
        var retry = MemberCarouselRules.Evaluate(Images, Saved(), abandoned,
            Baseline.AddMinutes(2), MemberCarouselGate.Ready, false, 42);
        Assert.Equal(due.CandidateImageId, retry.CandidateImageId);
        Assert.Equal(0, abandoned.AttemptsThisDue);
    }

    [Fact]
    public void ExplicitCheckResetsFailuresWhileManualAdvanceBypassesTimer()
    {
        var passive = new MemberCarouselRuntime(true, 3, ["b", "c", "d"], null);
        var reset = MemberCarouselRules.Evaluate(Images, Saved(), passive,
            Baseline.AddSeconds(1), MemberCarouselGate.Ready, true, 0);
        Assert.Equal(MemberCarouselDecisionKind.Wait, reset.Kind);
        Assert.False(reset.Runtime.Passive);
        Assert.Empty(reset.Runtime.FailedThisDue);
        var manual = MemberCarouselRules.Evaluate(Images, Saved(), reset.Runtime,
            Baseline.AddSeconds(1), MemberCarouselGate.Ready, true, 0, manual: true);
        Assert.Equal(MemberCarouselDecisionKind.Prepare, manual.Kind);
    }

    [Fact]
    public void EmptySingleOrEquivalentCandidateDoesNotRotate()
    {
        foreach (var candidates in new IReadOnlyList<string>[] { [], ["a"], ["a", "A"] })
            Assert.Equal(MemberCarouselDecisionKind.None, MemberCarouselRules.Evaluate(candidates, Saved(),
                Fresh, Baseline.AddHours(1), MemberCarouselGate.Ready, false, 0).Kind);
    }

    [Fact]
    public void InitialCaptureStartsTimeAndBackwardClockWaits()
    {
        var captured = MemberCarouselRules.EnterFlip(null, Fresh, "a", Baseline);
        Assert.Equal(Baseline, captured.NextPersistedState.LastSwitchUtc);
        var result = MemberCarouselRules.Evaluate(Images, captured.NextPersistedState, Fresh,
            Baseline.AddSeconds(-2), MemberCarouselGate.Ready, false, 0);
        Assert.Equal(TimeSpan.FromSeconds(62), result.Remaining);
    }
}
