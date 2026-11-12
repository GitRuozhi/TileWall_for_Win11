// Adapted from ReStart (MIT), commits 297c8f5 / b9c2718 and their base.
// Copyright (c) 2026 ReStart contributors. See Licenses/ReStartLicense.txt.
using System.ComponentModel;
using System.Text.Json;

namespace TileWall.ApplicationModel;

/// <summary>Chooses temporary reveal or identifies a legacy hide record for recovery; new native hide operations are disabled.</summary>
internal enum TaskbarLeaseMode { Reveal, Hide }
/// <summary>Distinguishes a reservation, possibly applied effects, and completed recovery.</summary>
internal enum TaskbarRecoveryPhase { Legacy, Prepared, RestorePending, Restored }
internal sealed class TaskbarMenuBusyException() : IOException("Explorer is using a taskbar menu.");
/// <summary>Stores physical screen pixels in private recovery state only.</summary>
internal readonly record struct TaskbarBounds(int X, int Y, int Width, int Height);
/// <summary>Identifies a direct native taskbar content child and its captured visibility for conditional recovery.</summary>
internal sealed record TaskbarContentState(long Window, bool Visible);
/// <summary>Stores a bounded window-local native clipping snapshot; a missing region means the normal rectangular surface.</summary>
internal sealed record TaskbarClipState(bool HasRegion, TaskbarBounds[] Rectangles)
{
    internal bool IsValid() => Rectangles is { Length: <= 256 } && (HasRegion || Rectangles.Length == 0)
        && Rectangles.All(rect => rect.Width is > 0 and <= 65536 && rect.Height is > 0 and <= 65536
            && rect.X is >= -262144 and <= 262144 && rect.Y is >= -262144 and <= 262144);
}
/// <summary>Identifies the exact Explorer window and recoverable effects; older records without HoldAutoHide never release an unowned timer hold.</summary>
internal sealed record TaskbarWindowState(long Window, int ProcessId, long ProcessStarted, bool Visible, bool AutoHide,
    TaskbarBounds Monitor, TaskbarBounds Original, TaskbarBounds Target, bool? Topmost = null, TaskbarContentState[]? Content = null, TaskbarClipState? Clip = null,
    bool HoldAutoHide = false);
/// <summary>Stores a private, process-bound recovery transaction; never sent to tiles or shared diagnostics.</summary>
internal sealed record TaskbarLeaseState(int Version, string Token, int ParentId, long ParentStarted, TaskbarLeaseMode Mode,
    TaskbarWindowState[] Windows, TaskbarRecoveryPhase Phase = TaskbarRecoveryPhase.Legacy)
{
    internal bool IsValid() => Version is 1 or 2 or 3 && Guid.TryParseExact(Token, "N", out _) && ParentId > 0 && ParentStarted > 0
        && (Version == 3 ? Phase is TaskbarRecoveryPhase.Prepared or TaskbarRecoveryPhase.RestorePending or TaskbarRecoveryPhase.Restored
            : Phase == TaskbarRecoveryPhase.Legacy)
        && Enum.IsDefined(Mode) && Windows is { Length: > 0 and <= 32 }
        && Windows.All(item => item != null && item.Window > 0 && item.ProcessId > 0 && item.ProcessStarted > 0
            && Valid(item.Monitor) && Valid(item.Original) && Valid(item.Target) && (item.Clip == null || item.Clip.IsValid())
            && (item.Content == null || item.Content.Length <= 4 && item.Content.All(child => child != null && child.Window > 0 && child.Window != item.Window)
                && item.Content.Select(child => child.Window).Distinct().Count() == item.Content.Length))
        && Windows.Select(item => item.Window).Distinct().Count() == Windows.Length
        && (Version != 2 && Windows.All(item => !item.HoldAutoHide)
            || Version != 1 && Mode == TaskbarLeaseMode.Reveal && Windows.All(item => item.HoldAutoHide));
    private static bool Valid(TaskbarBounds bounds) => bounds.Width is > 0 and <= 65536 && bounds.Height is > 0 and <= 65536
        && bounds.X is >= -262144 and <= 262144 && bounds.Y is >= -262144 and <= 262144;
}

/// <summary>Provides durable private recovery storage. Save must refuse to overwrite an existing transaction.</summary>
internal interface ITaskbarRecoveryStore
{
    IDisposable Acquire();
    TaskbarLeaseState? Read();
    void Save(TaskbarLeaseState state);
    void Update(TaskbarLeaseState state);
    void Delete(string token);
}
/// <summary>Provides scoped shell operations; Restore must revalidate identities and preserve changes no longer owned by the lease.</summary>
internal interface ITaskbarLeaseBackend
{
    TaskbarLeaseState? Capture(TaskbarLeaseMode mode);
    bool IsCurrent(TaskbarLeaseState state);
    void Apply(TaskbarLeaseState state, Action armRecovery);
    void Restore(TaskbarLeaseState state);
}

/// <summary>Requires verified recovery persistence and a ready watchdog before any taskbar mutation.</summary>
/// <param name="store">Private recovery store, owned by the caller.</param>
/// <param name="backend">Window-identity-scoped backend; tests supply an inert fake.</param>
/// <param name="protect">Starts a recovery watchdog and returns only after readiness; its disposable signals completion after successful restore.</param>
/// <param name="protectionReady">Optional live guard check before each mutation. Production supplies it; null permits trusted inert test guards.</param>
internal sealed class TaskbarLease(ITaskbarRecoveryStore store, ITaskbarLeaseBackend backend, Func<TaskbarLeaseState, IDisposable> protect, Func<bool>? protectionReady = null) : IDisposable
{
    private const int MaxRevealFailures = 2;
    private TaskbarLeaseState? _state;
    private IDisposable? _protection;
    private bool _disposed;
    private bool _applied;
    private int _revealRequestId, _revealFailures;
    internal bool IsActive => _state != null;
    internal bool RecoveryFailed { get; private set; }
    /// <summary>Retains the last local acquisition/recovery exception for explicit owned diagnostics; never exported to tiles or shared reports.</summary>
    internal Exception? LastFailure { get; private set; }
    internal TaskbarLeaseMode? Mode => _state?.Mode;
    /// <summary>Gets only successfully applied geometry; retained recovery ownership alone must not resize the launcher.</summary>
    internal TaskbarLeaseState? Snapshot => _applied ? _state : null;

    /// <summary>Enforces an optional temporary effect; failure never bypasses recovery prerequisites.</summary>
    /// <param name="mode">Desired effect, or null to release ownership and return control to the backend's current native policy.</param>
    /// <param name="stillRequested">Optional current-request check, evaluated after recovery readiness and before shell mutation. False cancels and restores acquisition.</param>
    /// <param name="revealRequestId">Identifies an opening and its reveal scope across monitoring ticks.</param>
    /// <returns>True when the requested effect is active, or restoration completed; false for unavailable/failed acquisition or recovery.</returns>
    internal bool Synchronize(TaskbarLeaseMode? mode, Func<bool>? stillRequested = null, int revealRequestId = 0)
    {
        LastFailure = null;
        if (_disposed) return mode == null && _state == null;
        if (mode != TaskbarLeaseMode.Reveal || _revealRequestId != revealRequestId)
        {
            _revealRequestId = revealRequestId;
            _revealFailures = 0;
        }
        if (mode == null && _state == null) return true;
        try
        {
            using var ownership = store.Acquire();
            return SynchronizeLocked(mode, stillRequested);
        }
        catch (Exception error) when (Expected(error)) { LastFailure = error; RecoveryFailed = _state != null; return false; }
    }

    private bool SynchronizeLocked(TaskbarLeaseMode? mode, Func<bool>? stillRequested)
    {
        var attemptedReveal = false;
        try
        {
            if (_state != null && (RecoveryFailed || _state.Mode != mode || !backend.IsCurrent(_state)))
                if (!Restore()) return false;
            if (mode == null) return true;
            if (_state == null)
            {
                if (mode == TaskbarLeaseMode.Reveal && _revealFailures >= MaxRevealFailures) return false;
                var candidate = backend.Capture(mode.Value);
                if (candidate == null) return false;
                if (!candidate.IsValid()) throw new InvalidDataException("Invalid taskbar recovery snapshot.");
                candidate = candidate with { Version = 3, Phase = TaskbarRecoveryPhase.Prepared };
                store.Save(candidate);
                VerifyStored(candidate);
                _state = candidate;
                _protection = protect(candidate) ?? throw new InvalidOperationException("Missing taskbar recovery watchdog.");
            }
            if (stillRequested?.Invoke() == false) { Restore(); return false; }
            if (protectionReady?.Invoke() == false) throw new IOException("Taskbar recovery watchdog is no longer ready.");
            backend.Apply(_state, () =>
            {
                ArmRecovery();
                attemptedReveal = mode == TaskbarLeaseMode.Reveal;
            });
            _applied = true;
            RecoveryFailed = false;
            return true;
        }
        catch (Exception error) when (Expected(error))
        {
            LastFailure = error;
            // A real menu temporarily owns the same Explorer lifecycle state.
            // Keep an established lease intact and resume after the menu closes.
            if (error is TaskbarMenuBusyException && _applied) return false;
            if (attemptedReveal) _revealFailures++;
            if (_state != null) Restore();
            return false;
        }
    }

    private void ArmRecovery()
    {
        if (_state is not { Phase: TaskbarRecoveryPhase.Prepared } state) return;
        // A timeout can occur after Explorer starts handling a message. Leave
        // a durable recovery obligation before the first external effect.
        var armed = state with { Phase = TaskbarRecoveryPhase.RestorePending };
        store.Update(armed);
        VerifyStored(armed);
        _state = armed;
    }

    private void VerifyStored(TaskbarLeaseState expected)
    {
        var saved = store.Read();
        if (saved == null || !saved.IsValid() || JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(saved))
            throw new InvalidDataException("Taskbar recovery verification failed.");
    }

    private bool Restore()
    {
        if (_state == null) return true;
        _applied = false;
        try
        {
            var saved = store.Read();
            if (saved == null || !saved.IsValid() || saved.Token != _state.Token)
                throw new InvalidDataException("Taskbar recovery ownership changed.");
            TaskbarRecovery.Complete(store, backend, saved);
            _state = null;
            _protection?.Dispose(); _protection = null;
            RecoveryFailed = false;
            return true;
        }
        catch (Exception error) when (Expected(error)) { LastFailure = error; RecoveryFailed = true; return false; }
    }

    internal static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or Win32Exception;

    /// <summary>Restores owned effects; failed recovery retains the marker and watchdog for another recovery attempt.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        Synchronize(null); _disposed = true;
    }
}
