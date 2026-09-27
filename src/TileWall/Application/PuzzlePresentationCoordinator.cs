using System.Diagnostics;
using Microsoft.UI.Dispatching;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Resources;

namespace TileWall.ApplicationModel;

/// <summary>Owns derived frames and one animation clock per puzzle; persisted state belongs to WallSession.</summary>
internal sealed class PuzzlePresentationCoordinator : IDisposable
{
    private sealed class State(PuzzleMember member)
    {
        public PuzzleMember Member = member;
        public PuzzleFrame? Frame;
        public PuzzleFrame? NextFrame;
        public PuzzleFrame? PreviousFrame;
        public PuzzleSwitchCapture? Capture;
        public PuzzleCarouselRuntime Runtime = new(false, 0, [], null);
        public IReadOnlyList<string>? Candidates;
        public CancellationTokenSource? Cancellation;
        public long Generation;
        public long FlipStarted;
        public bool Flipping;
        public bool NeedsInitial = true;
        public bool Persisting;
        public double Progress = 1;
        public int TargetWidth;
        public int TargetHeight;
    }

    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly WallImageService _images;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly Func<string, PuzzleCarouselState, string?> _persist;
    private readonly Func<double> _scale;
    private readonly Action<string> _error;
    private readonly bool _trace;
    private PuzzleCarouselGate _gate = PuzzleCarouselGate.Hidden;
    private bool _disposed;
    private bool _queued;
    private bool _queuedExplicit;
    private readonly WallMetrics _metrics = new();
    private static readonly TimeSpan FlipDuration = TimeSpan.FromMilliseconds(600);

    public event Action<string, PuzzleFrame?, double>? FrameChanged;

    public PuzzlePresentationCoordinator(WallImageService images, DispatcherQueue dispatcher,
        Func<double> scale, Func<string, PuzzleCarouselState, string?> persist,
        Action<string> error, bool trace)
    {
        _images = images;
        _dispatcher = dispatcher;
        _scale = scale;
        _persist = persist;
        _error = error;
        _trace = trace;
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => { Trace("TimerFire"); Check(false); };
    }

    public PuzzleFrame? FrameFor(string id) => _states.TryGetValue(id, out var state)
        ? VisibleFrame(state) : null;
    public double ProgressFor(string id) => _states.TryGetValue(id, out var state) ? state.Progress : 1;

    public void Sync(WallDocument document)
    {
        if (_disposed) return;
        var members = document.Lanes.SelectMany(l => l.Groups).SelectMany(g => g.Members)
            .OfType<PuzzleMember>().ToDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var id in _states.Keys.Except(members.Keys).ToArray())
        {
            Cancel(_states[id]);
            _states.Remove(id);
        }
        foreach (var (id, member) in members)
        {
            var (width, height) = TargetSize(member);
            if (!_states.TryGetValue(id, out var state))
                _states[id] = new State(member) { TargetWidth = width, TargetHeight = height };
            else
            {
                var changed = !SameSource(state.Member.Images, member.Images);
                var sizeChanged = state.TargetWidth != width || state.TargetHeight != height;
                state.TargetWidth = width;
                state.TargetHeight = height;
                var selectedChanged = !StringComparer.OrdinalIgnoreCase.Equals(
                    state.Member.Carousel?.CurrentImageId, member.Carousel?.CurrentImageId);
                state.Member = member;
                if (changed || sizeChanged || selectedChanged && !state.Persisting)
                {
                    Cancel(state);
                    state.Flipping = false;
                    state.Capture = null;
                    state.NextFrame = null;
                    state.PreviousFrame = null;
                    state.Progress = 1;
                    state.Candidates = null;
                    state.NeedsInitial = true;
                    state.Runtime = new(false, 0, [], null);
                    if (member.Images.Kind == PuzzleImageSourceKind.None) state.Frame = null;
                }
            }
        }
        QueueCheck(false);
    }

    public void SetGate(PuzzleCarouselGate gate)
    {
        if (_disposed || _gate == gate) return;
        _gate = gate;
        Trace($"Gate={gate}");
        _timer.Stop();
        if (gate != PuzzleCarouselGate.Ready)
        {
            foreach (var state in _states.Values.ToArray())
            {
                Cancel(state);
                if (state.Flipping) FinishFlip(state);
            }
            Trace("HideCancelOrSuspend");
        }
        else QueueCheck(true);
    }

    private void QueueCheck(bool explicitCheck)
    {
        _queuedExplicit |= explicitCheck;
        if (_queued || _disposed) return;
        _queued = true;
        _dispatcher.TryEnqueue(() =>
        {
            _queued = false;
            var explicitEvent = _queuedExplicit;
            _queuedExplicit = false;
            Check(explicitEvent);
        });
    }

    private void Check(bool explicitCheck)
    {
        if (_disposed || _gate != PuzzleCarouselGate.Ready) return;
        _timer.Stop();
        TimeSpan? wake = null;
        foreach (var state in _states.Values.ToArray())
        {
            if (state.Flipping)
            {
                state.Progress = Math.Clamp(Stopwatch.GetElapsedTime(state.FlipStarted).TotalMilliseconds /
                    FlipDuration.TotalMilliseconds, 0, 1);
                Publish(state);
                if (state.Progress >= 1) FinishFlip(state);
                else wake = Earlier(wake, TimeSpan.FromMilliseconds(16));
                continue;
            }
            if (state.Cancellation is not null) continue;
            if (explicitCheck && state.Member.Images.Kind == PuzzleImageSourceKind.Folder)
                state.NeedsInitial = true;
            if (state.NeedsInitial)
            {
                if (!state.Runtime.Passive || explicitCheck) _ = LoadInitialAsync(state);
                continue;
            }
            var decision = PuzzleCarouselRules.Evaluate(state.Candidates ?? [], state.Member.Carousel,
                state.Runtime, DateTimeOffset.UtcNow, _gate, explicitCheck, RandomIndex());
            state.Runtime = decision.Runtime;
            if (decision.Kind == PuzzleCarouselDecisionKind.Prepare) _ = LoadNextAsync(state, decision);
            else if (decision.Remaining is { } remaining) wake = Earlier(wake, remaining);
        }
        if (wake is { } delay && _gate == PuzzleCarouselGate.Ready)
        {
            _timer.Interval = delay < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : delay;
            _timer.Start();
            Trace($"TimerSchedule milliseconds={_timer.Interval.TotalMilliseconds:F0}");
        }
    }

    private async Task LoadInitialAsync(State state)
    {
        var (generation, token) = BeginRequest(state);
        try
        {
            var candidates = await _images.EnumeratePuzzleCandidatesAsync(state.Member.Images, token);
            if (!Current(state, generation)) return;
            state.Candidates = candidates.Paths;
            var current = state.Member.Carousel?.CurrentImageId;
            var ordered = candidates.Paths.OrderBy(p => StringComparer.OrdinalIgnoreCase.Equals(p, current) ? 0 : 1)
                .Take(3).ToArray();
            foreach (var candidate in ordered)
            {
                var result = await LoadAsync(state, candidate, token);
                if (!Current(state, generation)) return;
                if (result.Frame is not { } frame) continue;
                state.NeedsInitial = false;
                state.Progress = 1;
                state.Runtime = new(false, 0, [], null);
                if (state.Frame is { } previous &&
                    !StringComparer.OrdinalIgnoreCase.Equals(previous.CanonicalPath, frame.CanonicalPath))
                {
                    // Undo/restoration may need a visual flip from the previous frame without changing
                    // the time already committed for this exact image.
                    var restoring = StringComparer.OrdinalIgnoreCase.Equals(frame.CanonicalPath, current) &&
                        state.Member.Carousel?.LastSwitchUtc is not null;
                    StartFlip(state, frame, generation, captureSwitch: !restoring);
                    return;
                }
                state.PreviousFrame = state.Frame;
                state.Frame = frame;
                // Restoration of the same saved image must preserve its original time base.
                if (!StringComparer.OrdinalIgnoreCase.Equals(frame.CanonicalPath, current) ||
                    state.Member.Carousel?.LastSwitchUtc is null)
                {
                    state.Capture = PuzzleCarouselRules.EnterFlip(state.Member.Carousel, state.Runtime,
                        frame.CanonicalPath, DateTimeOffset.UtcNow);
                    Trace($"DirectCapture member={state.Member.Id} generation={generation} image={frame.CanonicalPath} utc={state.Capture.EnteredFlipUtc:O}");
                    PersistCapture(state);
                }
                Publish(state);
                if (state.Capture is null) state.PreviousFrame = null;
                return;
            }
            state.Runtime = state.Runtime with { Passive = true };
            if (state.Member.Images.Kind == PuzzleImageSourceKind.None) state.NeedsInitial = false;
            Trace($"InitialUnavailable member={state.Member.Id} generation={generation}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current(state, generation)) Fail(state, ex); }
        finally { EndRequest(state, generation); }
    }

    private async Task LoadNextAsync(State state, PuzzleCarouselDecision decision)
    {
        var (generation, token) = BeginRequest(state);
        try
        {
            // A folder is sampled at a due read, never polled while hidden or watched in the background.
            if (state.Member.Images.Kind == PuzzleImageSourceKind.Folder)
            {
                var candidates = await _images.EnumeratePuzzleCandidatesAsync(state.Member.Images, token);
                if (!Current(state, generation)) return;
                state.Candidates = candidates.Paths;
                state.Runtime = PuzzleCarouselRules.AbandonPending(state.Runtime);
                decision = PuzzleCarouselRules.Evaluate(state.Candidates, state.Member.Carousel,
                    state.Runtime, DateTimeOffset.UtcNow, _gate, false, RandomIndex());
                state.Runtime = decision.Runtime;
            }
            while (decision.Kind == PuzzleCarouselDecisionKind.Prepare && decision.CandidateImageId is { } path)
            {
                var result = await LoadAsync(state, path, token);
                if (!Current(state, generation)) return;
                if (result.Frame is { } frame)
                {
                    StartFlip(state, frame, generation);
                    return;
                }
                Trace($"LoadFailure member={state.Member.Id} generation={generation} image={path} status={result.Status}");
                decision = PuzzleCarouselRules.ReportLoadFailure(state.Candidates ?? [], state.Member.Carousel,
                    state.Runtime, RandomIndex());
                state.Runtime = decision.Runtime;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current(state, generation)) Fail(state, ex); }
        finally { EndRequest(state, generation); }
    }

    private Task<PuzzleFrameResult> LoadAsync(State state, string path, CancellationToken token) =>
        _images.LoadPuzzleFrameAsync(path, state.TargetWidth, state.TargetHeight, token);

    private (int Width, int Height) TargetSize(PuzzleMember member) =>
        (Math.Max(1, (int)Math.Ceiling((member.LocalBounds.Width * _metrics.Pitch - _metrics.Gap) * _scale())),
         Math.Max(1, (int)Math.Ceiling((member.LocalBounds.Height * _metrics.Pitch - _metrics.Gap) * _scale())));

    private void StartFlip(State state, PuzzleFrame frame, long generation, bool captureSwitch = true)
    {
        state.PreviousFrame = state.Frame;
        state.Capture = captureSwitch ? PuzzleCarouselRules.EnterFlip(state.Member.Carousel, state.Runtime,
            frame.CanonicalPath, DateTimeOffset.UtcNow) : null;
        if (state.Capture is { } capture) state.Runtime = capture.Runtime;
        state.NextFrame = frame;
        state.FlipStarted = Stopwatch.GetTimestamp();
        state.Progress = 0;
        state.Flipping = true;
        Trace($"EnterFlip member={state.Member.Id} generation={generation} image={frame.CanonicalPath} capture={captureSwitch} utc={state.Capture?.EnteredFlipUtc:O}");
        Publish(state);
    }

    private void FinishFlip(State state)
    {
        state.Flipping = false;
        state.Frame = state.NextFrame ?? state.Frame;
        state.NextFrame = null;
        state.Progress = 1;
        Trace($"CompleteFlip member={state.Member.Id} image={state.Frame?.CanonicalPath}");
        Publish(state);
        PersistCapture(state);
        if (state.Capture is null) state.PreviousFrame = null;
        QueueCheck(false);
    }

    private void PersistCapture(State state)
    {
        if (state.Capture is not { } capture) return;
        string? error;
        state.Persisting = true;
        try { error = _persist(state.Member.Id, capture.NextPersistedState); }
        catch (Exception ex) { error = ex.Message; }
        finally { state.Persisting = false; }
        if (error is not null)
        {
            // A failed switch never survives into a later edit/undo generation as a stale capture.
            state.Frame = state.PreviousFrame;
            state.PreviousFrame = null;
            state.Capture = null;
            state.Runtime = new(true, 0, [], null);
            state.NeedsInitial = state.Frame is null || state.Member.Carousel?.LastSwitchUtc is null;
            Publish(state);
            Trace($"PersistFailedReverted member={state.Member.Id}");
            _error(string.Format(ProductStrings.Get("Wall.PuzzlePersistFailed"), error));
            return;
        }
        state.Member = state.Member with { Carousel = capture.NextPersistedState };
        state.Capture = null;
        state.PreviousFrame = null;
        Trace($"Persisted member={state.Member.Id} image={capture.NextPersistedState.CurrentImageId} utc={capture.EnteredFlipUtc:O}");
    }

    private (long Generation, CancellationToken Token) BeginRequest(State state)
    {
        state.Cancellation = new CancellationTokenSource();
        var generation = ++state.Generation;
        Trace($"Request member={state.Member.Id} generation={generation}");
        return (generation, state.Cancellation.Token);
    }
    private bool Current(State state, long generation) => !_disposed && _gate == PuzzleCarouselGate.Ready &&
        state.Generation == generation && _states.TryGetValue(state.Member.Id, out var existing) &&
        ReferenceEquals(existing, state) && state.Cancellation?.IsCancellationRequested == false;
    private void EndRequest(State state, long generation)
    {
        if (state.Generation != generation) return;
        state.Cancellation?.Dispose();
        state.Cancellation = null;
        QueueCheck(false);
    }
    private void Cancel(State state)
    {
        state.Generation++;
        state.Cancellation?.Cancel();
        state.Cancellation?.Dispose();
        state.Cancellation = null;
        state.Runtime = PuzzleCarouselRules.AbandonPending(state.Runtime);
    }
    private void Fail(State state, Exception error)
    {
        state.Runtime = PuzzleCarouselRules.AbandonPending(state.Runtime) with { Passive = true };
        _error(string.Format(ProductStrings.Get("Wall.PuzzleLoadFailed"), error.Message));
    }
    private static PuzzleFrame? VisibleFrame(State state) => state.Flipping && state.Progress >= 0.5
        ? state.NextFrame : state.Frame;
    private void Publish(State state) => FrameChanged?.Invoke(state.Member.Id, VisibleFrame(state), state.Progress);
    private static TimeSpan Earlier(TimeSpan? first, TimeSpan second) => first is { } value && value < second ? value : second;
    private static uint RandomIndex() => (uint)Random.Shared.Next();
    private static bool SameSource(PuzzleImageSource left, PuzzleImageSource right) => left.Kind == right.Kind &&
        StringComparer.OrdinalIgnoreCase.Equals(left.FolderPath, right.FolderPath) &&
        left.ImagePaths.SequenceEqual(right.ImagePaths, StringComparer.OrdinalIgnoreCase);
    private void Trace(string message)
    {
        if (!_trace) return;
        AppLog.Warn("Phase3Puzzle", message);
        AppLog.Flush();
    }
    public void Dispose()
    {
        if (_disposed) return;
        SetGate(PuzzleCarouselGate.Hidden);
        _disposed = true;
        _timer.Stop();
        foreach (var state in _states.Values) Cancel(state);
        _states.Clear();
    }
}
