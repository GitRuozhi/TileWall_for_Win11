// Adapted from ReStart (MIT), commits 297c8f5 / b9c2718 and their base.
// Copyright (c) 2026 ReStart contributors. See Licenses/ReStartLicense.txt.
namespace TileWall.ApplicationModel;

/// <summary>Recovers only orphaned, validated ownership records while holding the same cross-process lock as acquisition.</summary>
internal static class TaskbarRecovery
{
    /// <summary>Restores an abandoned record or reports that recovery is blocked, without replacing another live owner's record.</summary>
    /// <param name="store">Private durable store; invalid records remain untouched.</param>
    /// <param name="backend">Identity-validating restoration backend; tests supply an inert fake.</param>
    /// <param name="isAlive">Returns whether this exact process ID and start-time identity is still alive; uncertainty must return true.</param>
    /// <param name="expectedParent">Optional watchdog owner identity. A record for any other process is left untouched.</param>
    /// <returns>True for no pending record, completed recovery, or a different watchdog owner; false when recovery is blocked or fails.</returns>
    internal static bool Run(ITaskbarRecoveryStore store, ITaskbarLeaseBackend backend, Func<int, long, bool> isAlive,
        (int Id, long Started)? expectedParent = null)
    {
        try
        {
            using var ownership = store.Acquire();
            var state = store.Read();
            if (state == null) return true;
            if (!state.IsValid()) return false;
            if (expectedParent is { } parent && (state.ParentId != parent.Id || state.ParentStarted != parent.Started)) return true;
            if (isAlive(state.ParentId, state.ParentStarted)) return false;
            Complete(store, backend, state);
            return true;
        }
        catch (Exception error) when (TaskbarLease.Expected(error)) { return false; }
    }

    internal static void Complete(ITaskbarRecoveryStore store, ITaskbarLeaseBackend backend, TaskbarLeaseState state)
    {
        if (state.Phase is not (TaskbarRecoveryPhase.Prepared or TaskbarRecoveryPhase.Restored))
        {
            backend.Restore(state);
            // Checkpoint completion before deleting. A different watchdog can
            // then retry a failed deletion without replaying shell messages.
            if (state.Version >= 2) store.Update(state with { Version = 3, Phase = TaskbarRecoveryPhase.Restored });
        }
        store.Delete(state.Token);
    }
}
