using TileWall.Shell.Interop;

namespace TileWall.ApplicationModel;

/// <summary>Owns only TileWall's temporary primary-taskbar effect and its crash recovery.</summary>
internal sealed class TaskbarActivationService : IAsyncDisposable
{
    private readonly bool _inert;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly TaskbarRecoveryStore? _store;
    private readonly TaskbarActivationBackend? _backend;
    private readonly TaskbarRecoveryCommand? _guard;
    private readonly TaskbarLease? _lease;
    private int _stopped;
    private int _disposed;
    private readonly CancellationTokenSource _stop = new();
    private Task<TaskbarActivationResult>? _stopOperation;
    private Task? _retry;
    private Task? _maintenance;
    private Func<bool>? _desired;
    private int _openingId;
    private Func<bool>? _publishedDesired;
    private int _publishedOpening;
    private TaskbarActivationResult? _publishedState;
    private readonly object _retryLock = new();
    internal event Action<int, Func<bool>, TaskbarActivationResult>? StateChanged;

    internal TaskbarActivationService(string dataRoot, bool isTest, nint launcherWindow)
    {
        _inert = isTest;
        if (_inert) return;
        // Real effects always share the normal per-user/session recovery location,
        // including when more than one installed copy is present.
        var expected = Path.GetFullPath(Path.Combine(KnownFolderPaths.LocalAppData, "TileWall"));
        if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).Equals(
                Path.TrimEndingDirectorySeparator(expected), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Taskbar activation requires the normal application data root.", nameof(dataRoot));
        _store = TaskbarRecoveryCommand.CreateStore();
        _backend = new() { LauncherWindow = launcherWindow };
        _guard = new(_store);
        _lease = new(_store, _backend, _guard.Protect, () => _guard.IsReady);
    }

    internal TaskbarActivationObservation ObservePrimary() => _inert
        ? new(false, false, 0, default, default, default)
        : TaskbarActivationBackend.ObservePrimary();

    internal Task<TaskbarActivationResult> PrepareAsync() => RunAsync(() =>
    {
        var observation = ObservePrimary();
        return new(false, observation.Failure);
    });

    internal Task<TaskbarActivationResult> RecoverPreviousAsync() => RunAsync(() =>
        TaskbarRecovery.Run(_store!, _backend!, TaskbarRecoveryCommand.IsAlive)
            ? new(false) : new(false, "A previous taskbar effect still requires recovery."));

    internal Task<TaskbarActivationResult> ApplyAsync(int openingId, Func<bool> stillRequested) => RunAsync(() =>
    {
        if (Volatile.Read(ref _stopped) != 0 || !stillRequested()) return new(false);
        Volatile.Write(ref _desired, stillRequested);
        Interlocked.Exchange(ref _openingId, openingId);
        var observation = ObservePrimary();
        if (!observation.Available)
        {
            Volatile.Write(ref _desired, null);
            var released = _lease!.Synchronize(null);
            if (!released && _lease.RecoveryFailed) QueueRestoreRetry();
            return new(false, observation.Failure ?? (released ? null : Failure()));
        }
        if (!observation.AutoHide)
        {
            Volatile.Write(ref _desired, null);
            var restored = _lease!.Synchronize(null);
            if (!restored && _lease.RecoveryFailed) QueueRestoreRetry();
            return new(false, restored ? null : Failure());
        }
        if (!TaskbarRecovery.Run(_store!, _backend!, TaskbarRecoveryCommand.IsAlive)
            && !_lease!.IsActive)
        {
            Volatile.Write(ref _desired, null);
            return new(false, "A previous taskbar effect still requires recovery.");
        }
        var screen = observation.Screen;
        _backend!.Scope = new(screen.X, screen.Y, screen.Width, screen.Height);
        _backend.StillRequested = () => Volatile.Read(ref _stopped) == 0 && stillRequested();
        var applied = _lease!.Synchronize(TaskbarLeaseMode.Reveal,
            () => Volatile.Read(ref _stopped) == 0 && stillRequested(), openingId);
        if (!applied && _lease.RecoveryFailed) QueueRestoreRetry();
        if (applied || _lease.LastFailure is TaskbarMenuBusyException) QueueMaintenance();
        return new(applied && _lease.Snapshot != null, applied ? null : Failure());
    });

    internal async Task<TaskbarActivationResult> RestoreAsync()
    {
        Volatile.Write(ref _desired, null);
        var result = await RunAsync(RestoreCore).ConfigureAwait(false);
        if (result.Failure != null && Volatile.Read(ref _stopped) == 0) QueueRestoreRetry();
        return result;
    }

    internal Task<TaskbarActivationResult> StopAsync()
    {
        lock (_retryLock) return _stopOperation ??= StopCoreAsync();
    }

    private async Task<TaskbarActivationResult> StopCoreAsync()
    {
        Interlocked.Exchange(ref _stopped, 1);
        Volatile.Write(ref _desired, null);
        _stop.Cancel();
        var result = await RunAsync(RestoreCore).ConfigureAwait(false);
        Task? retry; Task? maintenance;
        lock (_retryLock) { retry = _retry; maintenance = _maintenance; }
        await Task.WhenAll(retry ?? Task.CompletedTask, maintenance ?? Task.CompletedTask).ConfigureAwait(false);
        return result;
    }

    private TaskbarActivationResult RestoreCore()
    {
        var restored = _lease!.Synchronize(null);
        return new(false, restored ? null : Failure());
    }

    private string Failure() => _lease!.LastFailure?.Message ?? "The taskbar activation request could not be completed.";

    private async Task<TaskbarActivationResult> RunAsync(Func<TaskbarActivationResult> action)
    {
        if (_inert || Volatile.Read(ref _disposed) != 0) return new(false);
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                try { return action(); }
                catch (Exception error) when (TaskbarLease.Expected(error)) { return new TaskbarActivationResult(false, error.Message); }
            }).ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }

    private void QueueRestoreRetry()
    {
        lock (_retryLock)
        {
            if (Volatile.Read(ref _stopped) != 0) return;
            if (_retry is { IsCompleted: false }) return;
            _retry = RetryAsync();
        }
    }

    private async Task RetryAsync()
    {
        while (Volatile.Read(ref _stopped) == 0)
        {
            if (!await WaitTickAsync(350).ConfigureAwait(false)) return;
            if (Volatile.Read(ref _stopped) != 0) return;
            var result = await RunAsync(() => _lease!.RecoveryFailed ? RestoreCore() : new(false)).ConfigureAwait(false);
            if (result.Failure == null) return;
        }
    }

    private void QueueMaintenance()
    {
        lock (_retryLock)
        {
            if (Volatile.Read(ref _stopped) != 0) return;
            if (_maintenance is { IsCompleted: false }) return;
            _maintenance = MaintainAsync();
        }
    }

    private async Task MaintainAsync()
    {
        while (Volatile.Read(ref _stopped) == 0)
        {
            if (!await WaitTickAsync(Volatile.Read(ref _desired) == null ? 2000 : 350).ConfigureAwait(false)) return;
            var desired = Volatile.Read(ref _desired);
            if (Volatile.Read(ref _stopped) != 0) return;
            if (desired == null || !desired()) continue;
            var openingId = Volatile.Read(ref _openingId);
            var skippedMenu = false;
            var result = await RunAsync(() =>
            {
                if (!ReferenceEquals(desired, Volatile.Read(ref _desired)) || !desired()
                    || Volatile.Read(ref _stopped) != 0) return new(false);
                // Real menus own Explorer's menu state. Only renew the existing
                // hold once that interaction is over; do not interrupt the menu.
                if (_backend!.MenuIsActive()) { skippedMenu = true; return new(false); }
                _backend.StillRequested = () => Volatile.Read(ref _stopped) == 0
                    && ReferenceEquals(desired, Volatile.Read(ref _desired)) && desired();
                var applied = _lease!.Synchronize(TaskbarLeaseMode.Reveal, _backend.StillRequested, openingId);
                return new(applied, applied ? null : Failure());
            }).ConfigureAwait(false);
            if (skippedMenu) continue;
            if (ReferenceEquals(desired, Volatile.Read(ref _desired)) && desired() &&
                Volatile.Read(ref _stopped) == 0 &&
                (!ReferenceEquals(desired, _publishedDesired) || openingId != _publishedOpening || result != _publishedState))
            {
                _publishedDesired = desired;
                _publishedOpening = openingId;
                _publishedState = result;
                StateChanged?.Invoke(openingId, desired, result);
            }
            if (result.Failure != null)
            {
                if (_lease!.LastFailure is TaskbarMenuBusyException) continue;
                if (_lease!.RecoveryFailed) QueueRestoreRetry();
                // Preserve the bounded opening retry count; availability failures
                // are retried only on the next real opening/environment request.
                return;
            }
        }
    }

    private async Task<bool> WaitTickAsync(int milliseconds)
    {
        try { await Task.Delay(milliseconds, _stop.Token).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Dispose();
        _serial.Dispose();
    }
}
