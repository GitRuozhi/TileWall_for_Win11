using TileWall.Core.Imaging;

namespace TileWall.Core.Wall;

public enum MemberCarouselGate { Ready, Hidden, Gesture, Modal, FlipActive }
public enum MemberCarouselDecisionKind { None, Wait, Prepare }

/// <summary>Session-only failures and a tentatively consumed queue, shared by tiles and puzzles.</summary>
public sealed record MemberCarouselRuntime(bool Passive, int AttemptsThisDue,
    IReadOnlyList<string> FailedThisDue, string? PendingImageId)
{
    public ImageCarouselState? PendingState { get; init; }
}

public sealed record MemberCarouselDecision(MemberCarouselDecisionKind Kind, string? CandidateImageId,
    TimeSpan? Remaining, MemberCarouselRuntime Runtime);
public sealed record MemberSwitchCapture(ImageCarouselState NextPersistedState,
    MemberCarouselRuntime Runtime, DateTimeOffset EnteredFlipUtc);

/// <summary>Gates, bounded failure handling and ready-before-adoption for the shared shuffle queue.</summary>
public static class MemberCarouselRules
{
    private static readonly StringComparer ImageIds = StringComparer.OrdinalIgnoreCase;
    private const int MaximumAttemptsPerDue = 3;

    public static MemberCarouselDecision Evaluate(IReadOnlyList<string> canonicalCandidates,
        ImageCarouselState? persisted, MemberCarouselRuntime runtime, DateTimeOffset nowUtc,
        MemberCarouselGate gate, bool explicitCheck, uint randomIndex,
        ImageRotationInterval interval = ImageRotationInterval.Minute1, bool manual = false)
    {
        ArgumentNullException.ThrowIfNull(canonicalCandidates);
        ArgumentNullException.ThrowIfNull(runtime);
        if (!Enum.IsDefined(interval)) throw new ArgumentOutOfRangeException(nameof(interval));
        if (gate != MemberCarouselGate.Ready) return None(runtime);
        if (explicitCheck) runtime = ResetFailures(runtime);
        var candidates = Unique(canonicalCandidates);
        if (candidates.Count <= 1 || runtime.Passive && !explicitCheck || runtime.PendingImageId is not null)
            return None(runtime);
        if (!manual)
        {
            if (persisted?.LastSwitchUtc is not { } lastSwitchUtc) return None(runtime);
            var remaining = TimeSpan.FromSeconds((int)interval) - (nowUtc - lastSwitchUtc);
            if (remaining > TimeSpan.Zero)
                return new(MemberCarouselDecisionKind.Wait, null, remaining, runtime);
        }
        return Prepare(candidates, persisted, runtime, nowUtc, randomIndex);
    }

    public static MemberCarouselDecision ReportLoadFailure(IReadOnlyList<string> canonicalCandidates,
        ImageCarouselState? persisted, MemberCarouselRuntime runtime, uint randomIndex)
    {
        ArgumentNullException.ThrowIfNull(canonicalCandidates);
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.PendingImageId is not { } failedId) return None(runtime);
        var afterFailure = runtime with
        {
            AttemptsThisDue = checked(runtime.AttemptsThisDue + 1),
            FailedThisDue = runtime.FailedThisDue.Append(failedId).Distinct(ImageIds).ToArray(),
            PendingImageId = null,
        };
        if (afterFailure.AttemptsThisDue >= MaximumAttemptsPerDue)
            return None(afterFailure with { Passive = true, PendingState = null });
        return Prepare(Unique(canonicalCandidates), persisted, afterFailure,
            runtime.PendingState?.LastSwitchUtc ?? persisted?.LastSwitchUtc ?? DateTimeOffset.MinValue, randomIndex);
    }

    public static MemberCarouselRuntime AbandonPending(MemberCarouselRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime with { PendingImageId = null, PendingState = null };
    }

    public static MemberSwitchCapture EnterFlip(ImageCarouselState? persisted,
        MemberCarouselRuntime runtime, string successfullyLoadedImageId, DateTimeOffset enteredUtc)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(successfullyLoadedImageId);
        if (runtime.PendingImageId is not null && !ImageIds.Equals(runtime.PendingImageId, successfullyLoadedImageId))
            throw new ArgumentException("The loaded image is not the pending image.", nameof(successfullyLoadedImageId));
        var prepared = runtime.PendingState;
        if (prepared is null || !ImageIds.Equals(prepared.CurrentImageId, successfullyLoadedImageId))
        {
            // Initial/restored presentation has no pending operation. Preserve a matching saved queue.
            prepared = ImageIds.Equals(persisted?.CurrentImageId, successfullyLoadedImageId) ? persisted :
                ImageCarouselSequence.Next([successfullyLoadedImageId], null, enteredUtc);
        }
        var captured = prepared! with { CurrentImageId = successfullyLoadedImageId, LastSwitchUtc = enteredUtc };
        return new(captured, ResetFailures(runtime) with { PendingImageId = null, PendingState = null }, enteredUtc);
    }

    private static MemberCarouselDecision Prepare(IReadOnlyList<string> candidates, ImageCarouselState? persisted,
        MemberCarouselRuntime runtime, DateTimeOffset nowUtc, uint randomIndex)
    {
        var state = runtime.PendingState ?? persisted;
        var random = new Random(unchecked((int)randomIndex));
        // At most one pass: failed candidates and the current image never trigger unbounded re-enumeration.
        for (var attempt = 0; attempt < candidates.Count; attempt++)
        {
            state = ImageCarouselSequence.Next(candidates, state, nowUtc, random);
            if (state is null) break;
            if (ImageIds.Equals(state.CurrentImageId, persisted?.CurrentImageId) ||
                runtime.FailedThisDue.Contains(state.CurrentImageId!, ImageIds)) continue;
            return new(MemberCarouselDecisionKind.Prepare, state.CurrentImageId, null,
                runtime with { PendingImageId = state.CurrentImageId, PendingState = state });
        }
        return None(runtime with { Passive = true, PendingImageId = null, PendingState = null });
    }

    private static IReadOnlyList<string> Unique(IReadOnlyList<string> candidates) =>
        candidates.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(ImageIds).ToArray();

    private static MemberCarouselRuntime ResetFailures(MemberCarouselRuntime runtime) => runtime with
    {
        Passive = false, AttemptsThisDue = 0, FailedThisDue = [],
    };

    private static MemberCarouselDecision None(MemberCarouselRuntime runtime) =>
        new(MemberCarouselDecisionKind.None, null, null, runtime);
}
