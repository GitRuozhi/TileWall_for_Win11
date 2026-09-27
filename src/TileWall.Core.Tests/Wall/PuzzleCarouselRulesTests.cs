using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class PuzzleCarouselRulesTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 9, 23, 1, 2, 3, TimeSpan.Zero);
    private static readonly string[] Images = [@"C:\Photos\A.png", @"C:\Photos\B.png", @"C:\Photos\C.png", @"C:\Photos\D.png"];
    private static PuzzleCarouselRuntime Fresh => new(false, 0, [], null);
    private static PuzzleCarouselState Saved(string? image = null) => new(image ?? Images[0], Baseline);

    [Fact]
    public void GateAlwaysLeavesRuntimeUntouchedEvenForExplicitChecks()
    {
        var runtime = new PuzzleCarouselRuntime(true, 3, [Images[1]], Images[2]);
        foreach (var gate in new[] { PuzzleCarouselGate.Hidden, PuzzleCarouselGate.Gesture,
                     PuzzleCarouselGate.Modal, PuzzleCarouselGate.FlipActive })
        {
            var result = PuzzleCarouselRules.Evaluate(Images, Saved(), runtime,
                Baseline.AddHours(3), gate, explicitCheck: true, randomIndex: 0);
            Assert.Equal(PuzzleCarouselDecisionKind.None, result.Kind);
            Assert.Same(runtime, result.Runtime);
        }
    }

    [Fact]
    public void EmptySingleAndCaseVariantOfOneImageNeverRotate()
    {
        foreach (var candidates in new IReadOnlyList<string>[]
                 { [], [Images[0]], [Images[0], @"c:\photos\a.PNG"] })
        {
            var result = PuzzleCarouselRules.Evaluate(candidates, Saved(), Fresh,
                Baseline.AddHours(1), PuzzleCarouselGate.Ready, false, 0);
            Assert.Equal(PuzzleCarouselDecisionKind.None, result.Kind);
        }
    }

    [Fact]
    public void FirstImageWithoutSavedTimeNeedsSuccessfulPresentationBeforeTickCanRotate()
    {
        var result = PuzzleCarouselRules.Evaluate(Images, new PuzzleCarouselState(Images[0], null),
            Fresh, Baseline.AddYears(1), PuzzleCarouselGate.Ready, true, 0);
        Assert.Equal(PuzzleCarouselDecisionKind.None, result.Kind);
    }

    [Fact]
    public void FirstSuccessfulPresentationCanCaptureItsInitialTimeBase()
    {
        var capture = PuzzleCarouselRules.EnterFlip(null, Fresh, Images[0], Baseline);
        Assert.Equal(Images[0], capture.NextPersistedState.CurrentImageId);
        Assert.Equal(Baseline, capture.NextPersistedState.LastSwitchUtc);

        // A reconstructed UI session evaluates the saved time rather than starting a new interval.
        var restored = PuzzleCarouselRules.Evaluate(Images, capture.NextPersistedState, Fresh,
            Baseline.AddSeconds(59), PuzzleCarouselGate.Ready, false, 0);
        Assert.Equal(PuzzleCarouselDecisionKind.Wait, restored.Kind);
        Assert.Equal(TimeSpan.FromSeconds(1), restored.Remaining);
    }

    [Fact]
    public void SixtySecondBoundaryWaitsPreciselyAndPreparesOnlyOneImage()
    {
        var early = PuzzleCarouselRules.Evaluate(Images, Saved(), Fresh,
            Baseline.AddSeconds(60).AddTicks(-1), PuzzleCarouselGate.Ready, false, 0);
        Assert.Equal(PuzzleCarouselDecisionKind.Wait, early.Kind);
        Assert.Equal(TimeSpan.FromTicks(1), early.Remaining);

        var due = PuzzleCarouselRules.Evaluate(Images, Saved(), early.Runtime,
            Baseline.AddSeconds(60), PuzzleCarouselGate.Ready, false, 1);
        Assert.Equal(PuzzleCarouselDecisionKind.Prepare, due.Kind);
        Assert.Equal(Images[2], due.CandidateImageId);
        Assert.Equal(Images[2], due.Runtime.PendingImageId);

        var later = PuzzleCarouselRules.Evaluate(Images, Saved(), due.Runtime,
            Baseline.AddHours(10), PuzzleCarouselGate.Ready, false, 0);
        Assert.Equal(PuzzleCarouselDecisionKind.None, later.Kind);
        Assert.Same(due.Runtime, later.Runtime);
    }

    [Fact]
    public void BackwardClockWaitsAndDoesNotMovePersistedTimeBase()
    {
        var result = PuzzleCarouselRules.Evaluate(Images, Saved(), Fresh,
            Baseline.AddSeconds(-2), PuzzleCarouselGate.Ready, false, 0);
        Assert.Equal(PuzzleCarouselDecisionKind.Wait, result.Kind);
        Assert.Equal(TimeSpan.FromSeconds(62), result.Remaining);
    }

    [Fact]
    public void CurrentAndFailedImagesAreExcludedIgnoringWindowsPathCase()
    {
        var runtime = new PuzzleCarouselRuntime(false, 1, [@"c:\PHOTOS\b.PNG"], null);
        var result = PuzzleCarouselRules.Evaluate(Images,
            Saved(@"c:\photos\a.PNG"), runtime, Baseline.AddMinutes(2),
            PuzzleCarouselGate.Ready, false, uint.MaxValue);
        Assert.Equal(PuzzleCarouselDecisionKind.Prepare, result.Kind);
        Assert.Equal(Images[3], result.CandidateImageId); // uint.MaxValue % 2 == 1
    }

    [Fact]
    public void FailureRetriesOtherImagesThenBecomesPassiveAfterThreeAttempts()
    {
        var first = PuzzleCarouselRules.Evaluate(Images, Saved(), Fresh,
            Baseline.AddMinutes(1), PuzzleCarouselGate.Ready, false, 0);
        Assert.Equal(Images[1], first.CandidateImageId);

        var second = PuzzleCarouselRules.ReportLoadFailure(Images, Saved(), first.Runtime, 0);
        Assert.Equal(PuzzleCarouselDecisionKind.Prepare, second.Kind);
        Assert.Equal(Images[2], second.CandidateImageId);
        Assert.Equal(1, second.Runtime.AttemptsThisDue);

        var third = PuzzleCarouselRules.ReportLoadFailure(Images, Saved(), second.Runtime, 0);
        Assert.Equal(PuzzleCarouselDecisionKind.Prepare, third.Kind);
        Assert.Equal(Images[3], third.CandidateImageId);
        Assert.Equal(2, third.Runtime.AttemptsThisDue);

        var exhausted = PuzzleCarouselRules.ReportLoadFailure(Images, Saved(), third.Runtime, 0);
        Assert.Equal(PuzzleCarouselDecisionKind.None, exhausted.Kind);
        Assert.True(exhausted.Runtime.Passive);
        Assert.Equal(3, exhausted.Runtime.AttemptsThisDue);
        Assert.Equal([Images[1], Images[2], Images[3]], exhausted.Runtime.FailedThisDue);
        Assert.Null(exhausted.Runtime.PendingImageId);

        var tick = PuzzleCarouselRules.Evaluate(Images, Saved(), exhausted.Runtime,
            Baseline.AddDays(1), PuzzleCarouselGate.Ready, false, 0);
        Assert.Equal(PuzzleCarouselDecisionKind.None, tick.Kind);
        Assert.Same(exhausted.Runtime, tick.Runtime);
    }

    [Fact]
    public void CandidateExhaustionStopsEarlierThanThirdAttempt()
    {
        var candidates = Images[..2];
        var first = PuzzleCarouselRules.Evaluate(candidates, Saved(), Fresh,
            Baseline.AddMinutes(1), PuzzleCarouselGate.Ready, false, 0);
        var exhausted = PuzzleCarouselRules.ReportLoadFailure(candidates, Saved(), first.Runtime, 0);
        Assert.True(exhausted.Runtime.Passive);
        Assert.Equal(1, exhausted.Runtime.AttemptsThisDue);
        Assert.Equal(PuzzleCarouselDecisionKind.None, exhausted.Kind);
    }

    [Fact]
    public void ExplicitReadyCheckResetsPassiveFailureStateButPreservesPendingAndTime()
    {
        var passive = new PuzzleCarouselRuntime(true, 3, [Images[1], Images[2]], null);
        var reset = PuzzleCarouselRules.Evaluate(Images, Saved(), passive,
            Baseline.AddSeconds(59), PuzzleCarouselGate.Ready, true, 0);
        Assert.Equal(PuzzleCarouselDecisionKind.Wait, reset.Kind);
        Assert.Equal(TimeSpan.FromSeconds(1), reset.Remaining);
        Assert.False(reset.Runtime.Passive);
        Assert.Equal(0, reset.Runtime.AttemptsThisDue);
        Assert.Empty(reset.Runtime.FailedThisDue);

        var pending = passive with { PendingImageId = Images[3] };
        var held = PuzzleCarouselRules.Evaluate(Images, Saved(), pending,
            Baseline.AddHours(1), PuzzleCarouselGate.Ready, true, 0);
        Assert.Equal(PuzzleCarouselDecisionKind.None, held.Kind);
        Assert.Equal(Images[3], held.Runtime.PendingImageId);
        Assert.Equal(0, held.Runtime.AttemptsThisDue);
    }

    [Fact]
    public void CancelPendingDoesNotCountFailureOrResetDueInterval()
    {
        var prepared = PuzzleCarouselRules.Evaluate(Images, Saved(), Fresh,
            Baseline.AddSeconds(60), PuzzleCarouselGate.Ready, false, 0);
        var abandoned = PuzzleCarouselRules.AbandonPending(prepared.Runtime);
        Assert.Null(abandoned.PendingImageId);
        Assert.False(abandoned.Passive);
        Assert.Equal(0, abandoned.AttemptsThisDue);
        Assert.Empty(abandoned.FailedThisDue);

        var resumed = PuzzleCarouselRules.Evaluate(Images, Saved(), abandoned,
            Baseline.AddSeconds(61), PuzzleCarouselGate.Ready, false, 0);
        Assert.Equal(PuzzleCarouselDecisionKind.Prepare, resumed.Kind);
        Assert.Equal(Images[1], resumed.CandidateImageId);
    }

    [Fact]
    public void ReportingWithoutPendingIsIdempotent()
    {
        var runtime = new PuzzleCarouselRuntime(false, 1, [Images[1]], null);
        var result = PuzzleCarouselRules.ReportLoadFailure(Images, Saved(), runtime, 0);
        Assert.Equal(PuzzleCarouselDecisionKind.None, result.Kind);
        Assert.Same(runtime, result.Runtime);
    }

    [Fact]
    public void EnterFlipCapturesStartTimeAndClearsFailures()
    {
        var runtime = new PuzzleCarouselRuntime(false, 1, [Images[1]], Images[2]);
        var entered = Baseline.AddMinutes(5).AddMilliseconds(125);
        var capture = PuzzleCarouselRules.EnterFlip(Saved(), runtime,
            @"c:\photos\c.PNG", entered);
        Assert.Equal(@"c:\photos\c.PNG", capture.NextPersistedState.CurrentImageId);
        Assert.Equal(entered, capture.NextPersistedState.LastSwitchUtc);
        Assert.Equal(entered, capture.EnteredFlipUtc);
        Assert.False(capture.Runtime.Passive);
        Assert.Equal(0, capture.Runtime.AttemptsThisDue);
        Assert.Empty(capture.Runtime.FailedThisDue);
        Assert.Null(capture.Runtime.PendingImageId);

        var beforeNextDue = PuzzleCarouselRules.Evaluate(Images, capture.NextPersistedState,
            capture.Runtime, entered.AddSeconds(59), PuzzleCarouselGate.Ready, false, 0);
        Assert.Equal(TimeSpan.FromSeconds(1), beforeNextDue.Remaining);
    }

    [Fact]
    public void EnterFlipRejectsAResultForAnotherPendingImage()
    {
        var runtime = Fresh with { PendingImageId = Images[1] };
        Assert.Throws<ArgumentException>(() =>
            PuzzleCarouselRules.EnterFlip(Saved(), runtime, Images[2], Baseline.AddMinutes(1)));
    }
}
