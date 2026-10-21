using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Shell;
using Windows.Foundation;

namespace TileWall;

public sealed partial class MainWindow
{
    private DisplayPulseSource? _displayPulseSource;
    private bool _displayPulseActive, _displayPulseFailed;
    private string? _displayPulseFailure;
    private Visual? _ghostCompositionVisual;
    private double _ghostX, _ghostY;
    private IAsyncAction? _pendingCompositionCommit;
    private long _pendingCompositionStarted;
    private bool _pendingCompositionCancelRequested;
    private long _compositionCommitEpoch;
    private readonly CancellationTokenSource _interactionLifetime = new();
    private Task? _interactionCommitWork;
    private bool _interactionStopping;

    private bool TryStartDisplayPulses()
    {
        if (!DisplayPulseSource.Required)
        {
            _performance.Scheduler("xaml-rendering", null);
            return false;
        }
        if (!_displayPulseFailed)
        {
            _displayPulseSource ??= new DisplayPulseSource(DispatcherQueue, OnDisplayPulse, FallBackToRendering);
            if (_displayPulseSource.Start())
            {
                _displayPulseActive = true;
                _performance.Scheduler("dxgi-primary-vblank", null);
                return true;
            }
        }
        _performance.Scheduler("xaml-rendering-fallback", _displayPulseFailure);
        return false;
    }

    private void OnDisplayPulse(long timestamp)
    {
        if (_windowClosing || _interactionStopping || !_frameSubscribed || !_displayPulseActive) return;
        _performance.DisplayPulse(timestamp);
        OnInteractionFrame(null, EventArgs.Empty);
    }

    private void FallBackToRendering(string reason)
    {
        if (_windowClosing || _interactionStopping || _displayPulseFailed) return;
        _displayPulseFailed = true;
        _displayPulseFailure = reason;
        _displayPulseSource?.Stop();
        var wasActive = _displayPulseActive;
        _displayPulseActive = false;
        CancelInteractionCommit();
        if (_ghostCompositionVisual is { } visual)
        {
            try { visual.Properties.InsertVector3("Translation", Vector3.Zero); }
            catch { /* The window/compositor can be closing independently. */ }
            _ghostCompositionVisual = null;
        }
        if (_gesture.GhostHost?.RenderTransform is TranslateTransform transform)
        { transform.X = _ghostX; transform.Y = _ghostY; }
        _performance.Scheduler("xaml-rendering-fallback", reason);
        AppLog.Warn("GestureScheduler", reason);
        if (wasActive && _frameSubscribed) CompositionTarget.Rendering += OnInteractionFrame;
    }

    private void PositionGhost(Border ghost, double x, double y)
    {
        _ghostX = x; _ghostY = y;
        if (_displayPulseActive)
        {
            try
            {
                if (_ghostCompositionVisual is null)
                {
                    ElementCompositionPreview.SetIsTranslationEnabled(ghost, true);
                    _ghostCompositionVisual = ElementCompositionPreview.GetElementVisual(ghost);
                }
                // This Composition property is independent of XAML layout/RenderTransform.
                // x/y already include the existing viewport, scrolling and anchor conversion.
                _ghostCompositionVisual.Properties.InsertVector3("Translation", new Vector3((float)x, (float)y, 0));
                return;
            }
            catch (Exception ex) { FallBackToRendering($"Composition ghost update failed: {ex.GetType().Name}: {ex.Message}"); }
        }
        if (ghost.RenderTransform is TranslateTransform transform)
        { transform.X = x; transform.Y = y; }
    }

    private void RequestInteractionCommit()
    {
        if (_windowClosing || _interactionStopping || !_displayPulseActive || !_frameSubscribed || _interactionCommitWork is not null) return;
        var work = RequestInteractionCommitAsync();
        _interactionCommitWork = work.IsCompleted ? null : work;
    }

    private async Task RequestInteractionCommitAsync()
    {
        var epoch = _compositionCommitEpoch;
        var started = Stopwatch.GetTimestamp();
        IAsyncAction? request = null;
        try
        {
            var compositor = (_ghostCompositionVisual ?? ElementCompositionPreview.GetElementVisual(WallRoot)).Compositor;
            request = compositor.RequestCommitAsync();
            _pendingCompositionCommit = request;
            _pendingCompositionStarted = started;
            _pendingCompositionCancelRequested = false;
            _performance.CompositionCommitRequest();
            await request.AsTask(_interactionLifetime.Token);
            if (epoch == _compositionCommitEpoch) _performance.CompositionCommitCompleted(started);
        }
        catch (Exception ex)
        {
            if (!_windowClosing && !_interactionStopping && epoch == _compositionCommitEpoch)
                FallBackToRendering($"Composition commit request failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_pendingCompositionCommit, request))
            {
                _pendingCompositionCommit = null;
                _pendingCompositionStarted = 0;
                _pendingCompositionCancelRequested = false;
            }
            _interactionCommitWork = null;
            // No continuation-driven commits: the next display pulse adopts the latest
            // values. A delayed completion cannot enqueue a chain of stale submissions.
        }
    }

    private void InvalidateInteractionCommit()
    {
        // RequestCommitAsync belongs to the compositor, not to a gesture's lifetime.
        // Ignore its old telemetry, but retain the single in-flight action until its
        // completion. Cancel can report Canceled before its completion callback runs.
        _compositionCommitEpoch++;
    }

    private void CancelInteractionCommit()
    {
        InvalidateInteractionCommit();
        var request = _pendingCompositionCommit;
        if (request is not null) _pendingCompositionCancelRequested = true;
        try { request?.Cancel(); }
        catch { /* Cancellation must tolerate E_RO_CLOSED during native shutdown. */ }
    }

    private void ClearGhostComposition()
    {
        _ghostCompositionVisual = null;
        InvalidateInteractionCommit();
    }

    private void RecordCompositionGateAtBegin()
    {
        if (!_performance.Enabled) return;
        var request = _pendingCompositionCommit;
        var status = "none";
        if (request is not null)
        {
            try { status = request.Status.ToString(); }
            catch (Exception ex) { status = $"unavailable:0x{ex.HResult:X8}"; }
        }
        var age = _pendingCompositionStarted == 0 ? 0 : Stopwatch.GetElapsedTime(_pendingCompositionStarted).TotalMilliseconds;
        _performance.CompositionGateAtBegin(request is not null, status, age, _pendingCompositionCancelRequested);
    }

    private void DisposeInteractionScheduling()
    {
        _interactionStopping = true;
        _interactionLifetime.Cancel();
        EndInteractionTimer();
        _displayPulseSource?.Dispose();
        CancelInteractionCommit();
        _ghostCompositionVisual = null;
    }

    private async Task ShutdownInteractionSchedulingAsync()
    {
        DisposeInteractionScheduling();
        if (_interactionCommitWork is { } pending) await pending;
    }
}
