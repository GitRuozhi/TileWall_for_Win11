namespace TileWall.Core.Wall;

public enum PuzzleCarouselGate { Ready, Hidden, Gesture, Modal, FlipActive }

public enum PuzzleCarouselDecisionKind { None, Wait, Prepare }

/// <summary>Session-only state for one puzzle's current due interval.</summary>
public sealed record PuzzleCarouselRuntime(
    bool Passive, int AttemptsThisDue,
    IReadOnlyList<string> FailedThisDue, string? PendingImageId);

public sealed record PuzzleCarouselDecision(
    PuzzleCarouselDecisionKind Kind, string? CandidateImageId,
    TimeSpan? Remaining, PuzzleCarouselRuntime Runtime);

public sealed record PuzzleSwitchCapture(
    PuzzleCarouselState NextPersistedState, PuzzleCarouselRuntime Runtime,
    DateTimeOffset EnteredFlipUtc);

/// <summary>Pure carousel decisions over caller-normalized Windows image IDs.</summary>
public static class PuzzleCarouselRules
{
    private static readonly StringComparer ImageIds = StringComparer.OrdinalIgnoreCase;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private const int MaximumAttemptsPerDue = 3;

    public static PuzzleCarouselDecision Evaluate(
        IReadOnlyList<string> canonicalCandidates,
        PuzzleCarouselState? persisted,
        PuzzleCarouselRuntime runtime,
        DateTimeOffset nowUtc,
        PuzzleCarouselGate gate,
        bool explicitCheck,
        uint randomIndex)
    {
        ArgumentNullException.ThrowIfNull(canonicalCandidates);
        ArgumentNullException.ThrowIfNull(runtime);

        if (gate != PuzzleCarouselGate.Ready)
            return None(runtime);

        // A visible, explicit event starts a fresh due attempt without moving its time base.
        if (explicitCheck)
            runtime = ResetFailures(runtime);

        var candidates = UniqueCandidates(canonicalCandidates);
        if (candidates.Count <= 1 || (runtime.Passive && !explicitCheck) || runtime.PendingImageId is not null)
            return None(runtime);

        if (persisted?.LastSwitchUtc is not { } lastSwitchUtc)
            return None(runtime);

        var elapsed = nowUtc - lastSwitchUtc;
        if (elapsed < Interval)
            return new PuzzleCarouselDecision(PuzzleCarouselDecisionKind.Wait, null, Interval - elapsed, runtime);

        return Prepare(candidates, persisted.CurrentImageId, runtime, randomIndex);
    }

    public static PuzzleCarouselDecision ReportLoadFailure(
        IReadOnlyList<string> canonicalCandidates,
        PuzzleCarouselState? persisted,
        PuzzleCarouselRuntime runtime,
        uint randomIndex)
    {
        ArgumentNullException.ThrowIfNull(canonicalCandidates);
        ArgumentNullException.ThrowIfNull(runtime);

        if (runtime.PendingImageId is not { } failedId)
            return None(runtime);

        var failures = new HashSet<string>(runtime.FailedThisDue, ImageIds) { failedId };
        var afterFailure = runtime with
        {
            AttemptsThisDue = checked(runtime.AttemptsThisDue + 1),
            FailedThisDue = failures.ToArray(),
            PendingImageId = null,
        };

        if (afterFailure.AttemptsThisDue >= MaximumAttemptsPerDue)
            return None(afterFailure with { Passive = true });

        return Prepare(UniqueCandidates(canonicalCandidates), persisted?.CurrentImageId,
            afterFailure, randomIndex);
    }

    public static PuzzleCarouselRuntime AbandonPending(PuzzleCarouselRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime with { PendingImageId = null };
    }

    public static PuzzleSwitchCapture EnterFlip(
        PuzzleCarouselState? persisted,
        PuzzleCarouselRuntime runtime,
        string successfullyLoadedImageId,
        DateTimeOffset enteredUtc)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(successfullyLoadedImageId);

        if (runtime.PendingImageId is not null &&
            !ImageIds.Equals(runtime.PendingImageId, successfullyLoadedImageId))
            throw new ArgumentException("The loaded image is not the pending image.", nameof(successfullyLoadedImageId));

        return new PuzzleSwitchCapture(
            new PuzzleCarouselState(successfullyLoadedImageId, enteredUtc),
            ResetFailures(runtime) with { PendingImageId = null },
            enteredUtc);
    }

    private static PuzzleCarouselDecision Prepare(
        IReadOnlyList<string> candidates,
        string? currentImageId,
        PuzzleCarouselRuntime runtime,
        uint randomIndex)
    {
        var available = candidates.Where(candidate =>
            !ImageIds.Equals(candidate, currentImageId) &&
            !runtime.FailedThisDue.Contains(candidate, ImageIds)).ToArray();

        if (available.Length == 0)
            return None(runtime with { Passive = true });

        var chosen = available[(int)(randomIndex % (uint)available.Length)];
        return new PuzzleCarouselDecision(PuzzleCarouselDecisionKind.Prepare, chosen, null,
            runtime with { PendingImageId = chosen });
    }

    private static IReadOnlyList<string> UniqueCandidates(IReadOnlyList<string> candidates) =>
        candidates.Distinct(ImageIds).ToArray();

    private static PuzzleCarouselRuntime ResetFailures(PuzzleCarouselRuntime runtime) => runtime with
    {
        Passive = false,
        AttemptsThisDue = 0,
        FailedThisDue = [],
    };

    private static PuzzleCarouselDecision None(PuzzleCarouselRuntime runtime) =>
        new(PuzzleCarouselDecisionKind.None, null, null, runtime);
}
