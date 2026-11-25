using System.Diagnostics;
using TileWall.Core.Shell;

namespace TileWall.ApplicationModel;

internal sealed partial class WallApplicationLifetime
{
    private static readonly TimeSpan HiddenMemoryIdleDelay = TimeSpan.FromSeconds(30);
    private CancellationTokenSource? _hiddenMemoryRequest;
    private Task? _hiddenMemoryWork;
    private long _hiddenMemoryActivity;

    private void ScheduleHiddenMemoryCollection()
    {
        if (!_started || _exitPending || _tearingDown || _hiddenMemoryRequest is { IsCancellationRequested: false }) return;
        _hiddenMemoryActivity = Stopwatch.GetTimestamp();
        var request = new CancellationTokenSource();
        _hiddenMemoryRequest = request;
        _hiddenMemoryWork = CollectMemoryWhenHiddenAsync(request, _hiddenMemoryWork);
    }

    private void NoteHiddenMemoryActivity()
    {
        if (_hiddenMemoryRequest is not null) _hiddenMemoryActivity = Stopwatch.GetTimestamp();
    }

    private void CancelHiddenMemoryCollection() => _hiddenMemoryRequest?.Cancel();

    private async Task CollectMemoryWhenHiddenAsync(CancellationTokenSource request, Task? previous)
    {
        var token = request.Token;
        try
        {
            // Capture the UI context. Eligibility and normal application activity
            // are checked on the same dispatcher as visibility changes.
            // Preserve ownership until a previous, already-admitted collection
            // settles, even if this new hidden period is canceled first.
            if (previous is not null) await previous;
            await Task.Delay(HiddenMemoryIdleDelay, token);
            while (!token.IsCancellationRequested && _show.State == WallShowState.Hidden && !_exitPending)
            {
                var (busy, lastActivity) = _window.MemoryCollectionActivity();
                if (busy || _pendingAdds.Count != 0 || _addDrain is { IsCompleted: false } ||
                    _taskbarWork is { IsCompleted: false } || _explorerMenuSynchronization is { IsCompleted: false })
                {
                    NoteHiddenMemoryActivity();
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                    continue;
                }
                var remaining = HiddenMemoryIdleDelay - Stopwatch.GetElapsedTime(Math.Max(lastActivity, _hiddenMemoryActivity));
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, token);
                    continue;
                }
                var admittedActivity = _hiddenMemoryActivity;
                var collected = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref _hiddenMemoryActivity) != admittedActivity) return false;
                    var started = Stopwatch.GetTimestamp();
                    AppLog.Info("HiddenMemory", $"started={started} frequency={Stopwatch.Frequency}");
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    var finished = Stopwatch.GetTimestamp();
                    AppLog.Info("HiddenMemory", $"finished={finished} elapsedMs={Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds:F2}");
                    return true;
                }, token);
                if (collected) return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { AppLog.Warn("HiddenMemory", ex.GetType().Name); }
        finally
        {
            if (ReferenceEquals(_hiddenMemoryRequest, request))
            {
                _hiddenMemoryRequest = null;
                _hiddenMemoryWork = null;
            }
            request.Dispose();
        }
    }
}
