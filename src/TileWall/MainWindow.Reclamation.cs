using Microsoft.UI.Dispatching;
using TileWall.Core.Shell;

namespace TileWall;

public sealed partial class MainWindow
{
    private bool _reclamationRequested, _reclamationWakeQueued;
    private CancellationTokenSource? _reclamationStop;
    private Task<bool>? _reclamationWorker;

    private bool CanRunDeferredReclamation =>
        _reclamationRequested && _reclamationWorker is null && CanAcceptBackgroundWrite;

    private void QueueDeferredReclamation()
    {
        if (_reclamationWakeQueued || !CanRunDeferredReclamation) return;
        _reclamationWakeQueued = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _reclamationWakeQueued = false;
            if (CanRunDeferredReclamation) _ = ReclaimWhenIdleAsync();
        });
    }

    private async Task ReclaimWhenIdleAsync()
    {
        if (_storageExecutor is not { } executor) return;
        using var stop = new CancellationTokenSource();
        _reclamationStop = stop;
        _reclamationRequested = false;
        var work = executor.ExecuteAsync(() =>
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                _session.ReclaimUnreferencedResources(stop.Token);
                return true;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return false; }
            finally
            {
                if (_traceInput)
                    TraceInput($"DeferredReclamation elapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} yielded={stop.IsCancellationRequested}");
            }
        });
        _reclamationWorker = work;
        try { if (!await work) _reclamationRequested = true; }
        catch (Exception ex) { AppLog.Warn("ResourceReclamation", ex.Message); }
        finally
        {
            _reclamationWorker = null;
            _reclamationStop = null;
            QueueDeferredReclamation();
        }
    }

    private void YieldDeferredReclamation() => _reclamationStop?.Cancel();

    private void SettleReclamationBeforeSynchronousWrite()
    {
        YieldDeferredReclamation();
        // This is the worker task. Its completion does not require the UI dispatcher.
        try { _reclamationWorker?.GetAwaiter().GetResult(); }
        catch (Exception) { /* The maintenance continuation reports failures. */ }
    }
}
