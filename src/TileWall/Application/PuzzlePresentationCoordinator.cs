using System.Diagnostics;
using Microsoft.UI.Dispatching;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Core.Imaging;
using TileWall.Graphics;
using TileWall.Resources;
using Windows.Foundation.Metadata;
using Windows.UI.ViewManagement;

namespace TileWall.ApplicationModel;

/// <summary>Owns shared tile/puzzle frames and animation clocks; persisted state belongs to WallSession.</summary>
internal sealed class PuzzlePresentationCoordinator : IDisposable
{
    private sealed class State(WallMember member)
    {
        public WallMember Member = member;
        public PuzzleFrame? Frame;
        public PuzzleFrame? NextFrame;
        public PuzzleFrame? PreviousFrame;
        public MemberSwitchCapture? Capture;
        public MemberCarouselRuntime Runtime = new(false, 0, [], null);
        public IReadOnlyList<string>? Candidates;
        public CancellationTokenSource? Cancellation;
        public long Generation;
        public long FlipStarted;
        public bool Flipping;
        public bool NeedsInitial = true;
        public bool Persisting;
        public int PendingPages;
        public bool ManualLoading;
        public double Progress = 1;
        public MemberTransitionEffect Effect;
        public IReadOnlyList<int>? PartitionOrdinals;
        public int TargetWidth;
        public int TargetHeight;
    }

    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly WallImageService _images;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueueTimer _animationTimer;
    private readonly Func<string, ImageCarouselState, Func<bool>, Task<string?>> _persist;
    private readonly Func<double> _scale;
    private readonly Action<string> _error;
    private readonly bool _trace;
    private readonly Random _effectRandom = new();
    private readonly UISettings _uiSettings = new();
    private bool _animationsEnabled = true;
    private bool _animationEventSubscribed;
    private MemberCarouselGate _gate = MemberCarouselGate.Hidden;
    private bool _disposed;
    private bool _queued;
    private bool _queuedExplicit;
    private bool _persistenceBusy;
    private bool _animationClockRunning;
    private readonly WallMetrics _metrics = new();

    public event Action<string, PuzzleFrame?, double>? FrameChanged;
    public Func<WallMember, string?>? ResolveEntryPath { get; set; }

    public PuzzlePresentationCoordinator(WallImageService images, DispatcherQueue dispatcher,
        Func<double> scale, Func<string, ImageCarouselState, Func<bool>, Task<string?>> persist,
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
        // Rendering notifications alone do not keep a static XAML scene advancing.
        // Run a clock only while a visual transition is active, then stop it.
        _animationTimer = dispatcher.CreateTimer();
        _animationTimer.Interval = TimeSpan.FromMilliseconds(16);
        _animationTimer.IsRepeating = true;
        _animationTimer.Tick += OnAnimationTick;
        _animationsEnabled = ReadAnimationsEnabled();
        // This event was added in Windows 10 2004; the property is available on our minimum OS.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) &&
            ApiInformation.IsEventPresent("Windows.UI.ViewManagement.UISettings", "AnimationsEnabledChanged"))
        {
            _uiSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
            _animationEventSubscribed = true;
        }
    }

    public PuzzleFrame? FrameFor(string id) => _states.TryGetValue(id, out var state)
        ? VisibleFrame(state) : null;
    public double ProgressFor(string id) => _states.TryGetValue(id, out var state) ? state.Progress : 1;
    public MemberTransition? TransitionFor(string id) => _states.TryGetValue(id, out var state)
        ? state.Flipping
            ? new(state.PreviousFrame, state.NextFrame, state.Effect, state.Progress, state.PartitionOrdinals)
            : new(null, state.Frame, MemberTransitionEffect.None, 1)
        : null;

    private bool ReadAnimationsEnabled()
    {
        try { return _uiSettings.AnimationsEnabled; }
        catch { return false; }
    }

    private void OnAnimationsEnabledChanged(UISettings sender, object args)
        => _dispatcher.TryEnqueue(() =>
        {
            if (_disposed) return;
            ApplyAnimationPreference();
            QueueCheck(false);
        });

    private void ApplyAnimationPreference()
    {
        _animationsEnabled = ReadAnimationsEnabled();
        if (_animationsEnabled) return;
        foreach (var state in _states.Values.ToArray())
            if (state.Flipping) FinishFlip(state);
    }

    public void AdvanceAll()
    {
        if (_disposed) return;
        foreach (var state in _states.Values)
            if (state.Member is PuzzleMember && CouldAdvance(state)) state.PendingPages++;
        QueueCheck(false);
    }

    private static bool CouldAdvance(State state) => state.Member.Images.Kind == MemberImageSourceKind.Folder;

    public void Sync(WallDocument document)
    {
        if (_disposed) return;
        var members = document.Lanes.SelectMany(l => l.Groups).SelectMany(g => g.Members)
            .Where(m => m is TileMember or PuzzleMember).ToDictionary(p => p.Id, StringComparer.Ordinal);
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
                var changed = !SameSource(state.Member.Images, member.Images) ||
                    state.Member.Images.Kind == MemberImageSourceKind.Automatic &&
                    (state.Member.TargetInput != member.TargetInput || state.Member.Entry != member.Entry);
                var qualityChanged = state.Member.ImageDisplay != member.ImageDisplay;
                var sizeChanged = state.TargetWidth != width || state.TargetHeight != height;
                var partitionsChanged = state.Member is PuzzleMember previousPuzzle && member is PuzzleMember puzzle &&
                    !previousPuzzle.Partitions.SequenceEqual(puzzle.Partitions);
                state.TargetWidth = width;
                state.TargetHeight = height;
                var selectedChanged = !StringComparer.OrdinalIgnoreCase.Equals(
                    state.Member.Carousel?.CurrentImageId, member.Carousel?.CurrentImageId);
                state.Member = member;
                if (partitionsChanged && state.Flipping) FinishFlip(state);
                if (changed || sizeChanged || qualityChanged || selectedChanged && !state.Persisting)
                {
                    Cancel(state);
                    // A captured next image is already the committed carousel image. Never
                    // leave its old half-frame behind when editing or replacing a source.
                    state.Frame = changed ? null : state.NextFrame ?? state.Frame;
                    state.Flipping = false;
                    state.Capture = null;
                    state.NextFrame = null;
                    state.PreviousFrame = null;
                    state.Progress = 1;
                    state.Effect = MemberTransitionEffect.None;
                    state.PartitionOrdinals = null;
                    state.Candidates = null;
                    state.NeedsInitial = true;
                    state.Runtime = new(false, 0, [], null);
                    Publish(state);
                }
            }
        }
        UpdateAnimationClock();
        QueueCheck(false);
    }

    public void SetGate(MemberCarouselGate gate)
    {
        if (_disposed || _gate == gate) return;
        _gate = gate;
        Trace($"Gate={gate}");
        _timer.Stop();
        if (gate != MemberCarouselGate.Ready)
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

    public void SetPersistenceBusy(bool busy)
    {
        if (_disposed || _persistenceBusy == busy) return;
        _persistenceBusy = busy;
        if (busy) _timer.Stop();
        else QueueCheck(false);
        // Unlike a visibility/gesture gate, this does not cancel a captured save.
    }

    private void UpdateAnimationClock()
    {
        var needed = !_disposed && _gate == MemberCarouselGate.Ready && _states.Values.Any(s => s.Flipping);
        if (needed == _animationClockRunning) return;
        _animationClockRunning = needed;
        if (needed) _animationTimer.Start();
        else _animationTimer.Stop();
    }

    private void OnAnimationTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || _gate != MemberCarouselGate.Ready) return;
        foreach (var state in _states.Values.ToArray())
        {
            if (!state.Flipping) continue;
            state.Progress = Math.Clamp(Stopwatch.GetElapsedTime(state.FlipStarted).TotalMilliseconds /
                PartitionTransitionTiming.DurationMilliseconds(state.PartitionOrdinals?.Count ?? 1), 0, 1);
            if (state.Progress >= 1) FinishFlip(state);
            else Publish(state);
        }
        UpdateAnimationClock();
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
        if (_persistenceBusy) return;
        if (_disposed || _gate != MemberCarouselGate.Ready) return;
        // Also covers OS versions without the preference-change event.
        ApplyAnimationPreference();
        _timer.Stop();
        TimeSpan? wake = null;
        foreach (var state in _states.Values.ToArray())
        {
            if (state.Flipping)
            {
                // The bounded animation clock advances active transitions; this
                // deadline timer only wakes idle carousel deadlines.
                continue;
            }
            if (state.Cancellation is not null) continue;
            if (state.PendingPages > 0)
            {
                state.PendingPages--;
                state.Runtime = new(false, 0, [], null);
                _ = LoadNextAsync(state, NextPageDecision(state), manual: true);
                continue;
            }
            if (explicitCheck && state.Member.Images.Kind == MemberImageSourceKind.Folder)
                state.NeedsInitial = true;
            if (state.NeedsInitial)
            {
                if (!state.Runtime.Passive || explicitCheck) _ = LoadInitialAsync(state);
                continue;
            }
            if (state.Member.Images.Kind != MemberImageSourceKind.Folder) continue;
            var decision = MemberCarouselRules.Evaluate(state.Candidates ?? [], state.Member.Carousel,
                state.Runtime, DateTimeOffset.UtcNow, _gate, explicitCheck, RandomIndex(), state.Member.RotationInterval);
            state.Runtime = decision.Runtime;
            if (decision.Kind == MemberCarouselDecisionKind.Prepare) _ = LoadNextAsync(state, decision);
            else if (decision.Remaining is { } remaining) wake = Earlier(wake, remaining);
        }
        if (wake is { } delay && _gate == MemberCarouselGate.Ready)
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
            if (state.Member.Images.Kind != MemberImageSourceKind.Folder)
            {
                var staticFrame = await _images.LoadMemberFrameAsync(state.Member,
                    ResolveEntryPath?.Invoke(state.Member) ?? state.Member.TargetInput,
                    state.TargetWidth, state.TargetHeight, ct: token);
                if (!Current(state, generation)) return;
                if (staticFrame.Frame is { } loaded) state.Frame = loaded;
                else if (staticFrame.Status == WallImageStatus.None) state.Frame = null;
                state.NeedsInitial = false;
                state.Progress = 1;
                state.Runtime = new(false, 0, [], null);
                Publish(state);
                return;
            }
            var candidates = await _images.EnumerateMemberCandidatesAsync(state.Member.Images, token);
            if (!Current(state, generation)) return;
            state.Candidates = candidates.Paths;
            var current = state.Member.Carousel?.CurrentImageId;
            var restoring = candidates.Paths.FirstOrDefault(path => StringComparer.OrdinalIgnoreCase.Equals(path, current));
            var tentative = state.Member.Carousel;
            for (var attempt = 0; attempt < Math.Min(3, candidates.Paths.Count); attempt++)
            {
                var restore = attempt == 0 && restoring is not null;
                if (!restore) tentative = ImageCarouselSequence.Next(candidates.Paths, tentative,
                    DateTimeOffset.UtcNow);
                var candidate = restore ? restoring! : tentative?.CurrentImageId;
                if (candidate is null) break;
                var result = await LoadAsync(state, candidate, token);
                if (!Current(state, generation)) return;
                if (result.Frame is not { } frame) continue;
                state.NeedsInitial = false;
                state.Progress = 1;
                state.Runtime = new(false, 0, [], null);
                if (!restore && tentative is not null)
                    state.Runtime = state.Runtime with { PendingImageId = candidate, PendingState = tentative };
                if (state.Frame is { } previous &&
                    !StringComparer.OrdinalIgnoreCase.Equals(previous.CanonicalPath, frame.CanonicalPath))
                {
                    // Undo/restoration may need a visual flip from the previous frame without changing
                    // the time already committed for this exact image.
                    var restoreSaved = StringComparer.OrdinalIgnoreCase.Equals(frame.CanonicalPath, current) &&
                        state.Member.Carousel?.LastSwitchUtc is not null;
                    await StartFlipAsync(state, frame, generation, captureSwitch: !restoreSaved);
                    return;
                }
                // Restoration of the same saved image must preserve its original time base.
                if (!StringComparer.OrdinalIgnoreCase.Equals(frame.CanonicalPath, current) ||
                    state.Member.Carousel?.LastSwitchUtc is null)
                {
                    state.PreviousFrame = state.Frame;
                    state.Capture = MemberCarouselRules.EnterFlip(state.Member.Carousel, state.Runtime,
                        frame.CanonicalPath, DateTimeOffset.UtcNow);
                    Trace($"DirectCapture member={state.Member.Id} generation={generation} image={frame.CanonicalPath} utc={state.Capture.EnteredFlipUtc:O}");
                    if (!await PersistCaptureAsync(state)) return;
                }
                state.Frame = frame;
                state.PreviousFrame = null;
                Publish(state);
                return;
            }
            state.Runtime = state.Runtime with { Passive = true };
            Trace($"InitialUnavailable member={state.Member.Id} generation={generation}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current(state, generation)) Fail(state, ex); }
        finally { EndRequest(state, generation); }
    }

    private async Task LoadNextAsync(State state, MemberCarouselDecision decision, bool manual = false)
    {
        state.ManualLoading = manual;
        var (generation, token) = BeginRequest(state);
        try
        {
            // A folder is sampled at a due read, never polled while hidden or watched in the background.
            if (state.Member.Images.Kind == MemberImageSourceKind.Folder || manual && state.Candidates is null)
            {
                var candidates = await _images.EnumerateMemberCandidatesAsync(state.Member.Images, token);
                if (!Current(state, generation)) return;
                state.Candidates = candidates.Paths;
                state.Runtime = MemberCarouselRules.AbandonPending(state.Runtime);
                decision = manual ? NextPageDecision(state)
                    : MemberCarouselRules.Evaluate(state.Candidates, state.Member.Carousel,
                        state.Runtime, DateTimeOffset.UtcNow, _gate, false, RandomIndex(), state.Member.RotationInterval);
                state.Runtime = decision.Runtime;
            }
            else state.Runtime = decision.Runtime;
            while (decision.Kind == MemberCarouselDecisionKind.Prepare && decision.CandidateImageId is { } path)
            {
                var result = await LoadAsync(state, path, token);
                if (!Current(state, generation)) return;
                if (result.Frame is { } frame)
                {
                    if (manual) state.NeedsInitial = false;
                    await StartFlipAsync(state, frame, generation);
                    return;
                }
                Trace($"LoadFailure member={state.Member.Id} generation={generation} image={path} status={result.Status}");
                decision = MemberCarouselRules.ReportLoadFailure(state.Candidates ?? [], state.Member.Carousel,
                    state.Runtime, RandomIndex());
                state.Runtime = decision.Runtime;
            }

            // A manual next-page request with no alternative must not suppress the initial
            // frame load when the puzzle has not displayed any image yet.
            if (manual && state.NeedsInitial && decision.Kind != MemberCarouselDecisionKind.Prepare)
                state.Runtime = state.Runtime with { Passive = false };
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current(state, generation)) Fail(state, ex); }
        finally { EndRequest(state, generation); }
    }

    private static MemberCarouselDecision NextPageDecision(State state)
        => MemberCarouselRules.Evaluate(state.Candidates ?? [], state.Member.Carousel,
            state.Runtime, DateTimeOffset.UtcNow, MemberCarouselGate.Ready, false, RandomIndex(),
            state.Member.RotationInterval, manual: true);

    private Task<PuzzleFrameResult> LoadAsync(State state, string path, CancellationToken token) =>
        _images.LoadMemberFrameAsync(state.Member, ResolveEntryPath?.Invoke(state.Member) ?? state.Member.TargetInput,
            state.TargetWidth, state.TargetHeight, path, token);

    private (int Width, int Height) TargetSize(WallMember member) =>
        (Math.Clamp((int)Math.Ceiling((member.LocalBounds.Width * _metrics.Pitch - _metrics.Gap) * _scale()), 1, 4096),
         Math.Clamp((int)Math.Ceiling((member.LocalBounds.Height * _metrics.Pitch - _metrics.Gap) * _scale()), 1, 4096));

    private async Task StartFlipAsync(State state, PuzzleFrame frame, long generation, bool captureSwitch = true)
    {
        state.PreviousFrame = state.Frame;
        state.Capture = captureSwitch ? MemberCarouselRules.EnterFlip(state.Member.Carousel, state.Runtime,
            frame.CanonicalPath, DateTimeOffset.UtcNow) : null;
        if (state.Capture is { } capture) state.Runtime = capture.Runtime;
        if (!await PersistCaptureAsync(state) || !Current(state, generation)) return;
        state.NextFrame = frame;
        _animationsEnabled = ReadAnimationsEnabled();
        if (state.PreviousFrame is null || !_animationsEnabled)
        {
            FinishFlip(state);
            return;
        }
        // A separate draw is independent of ImageCarouselSequence and its persisted queue.
        state.Effect = (MemberTransitionEffect)_effectRandom.Next(1, 9);
        state.PartitionOrdinals = PartitionTransitionTiming.ShuffledOrdinals(
            state.Member is PuzzleMember puzzle ? puzzle.Partitions.Count : 1, _effectRandom);
        state.FlipStarted = Stopwatch.GetTimestamp();
        state.Progress = 0;
        state.Flipping = true;
        UpdateAnimationClock();
        Trace($"EnterFlip member={state.Member.Id} generation={generation} image={frame.CanonicalPath} effect={state.Effect} capture={captureSwitch}");
        Publish(state);
    }

    private void FinishFlip(State state)
    {
        state.Flipping = false;
        state.Frame = state.NextFrame ?? state.Frame;
        state.NextFrame = null;
        state.PreviousFrame = null;
        state.Effect = MemberTransitionEffect.None;
        state.PartitionOrdinals = null;
        state.Progress = 1;
        Trace($"CompleteFlip member={state.Member.Id} image={state.Frame?.CanonicalPath}");
        Publish(state);
        UpdateAnimationClock();
        QueueCheck(false);
    }

    private async Task<bool> PersistCaptureAsync(State state)
    {
        if (state.Capture is not { } capture) return true;
        var generation = state.Generation;
        string? error;
        state.Persisting = true;
        try { error = await _persist(state.Member.Id, capture.NextPersistedState,
            () => Current(state, generation) && ReferenceEquals(state.Capture, capture)); }
        catch (Exception ex) { error = ex.Message; }
        finally { state.Persisting = false; }
        if (!Current(state, generation) || !ReferenceEquals(state.Capture, capture)) return false;
        if (error is not null)
        {
            // A failed switch never survives into a later edit/undo generation as a stale capture.
            state.Frame = state.PreviousFrame;
            state.PreviousFrame = null;
            state.NextFrame = null;
            state.Flipping = false;
            state.Progress = 1;
            state.Effect = MemberTransitionEffect.None;
            state.PartitionOrdinals = null;
            state.Capture = null;
            state.Runtime = new(true, 0, [], null);
            state.NeedsInitial = state.Frame is null || state.Member.Carousel?.LastSwitchUtc is null;
            Publish(state);
            UpdateAnimationClock();
            Trace($"PersistFailedReverted member={state.Member.Id}");
            _error(string.Format(ProductStrings.Get("Wall.PuzzlePersistFailed"), error));
            return false;
        }
        state.Member = state.Member with { Carousel = capture.NextPersistedState };
        state.Capture = null;
        Trace($"Persisted member={state.Member.Id} image={capture.NextPersistedState.CurrentImageId} utc={capture.EnteredFlipUtc:O}");
        return true;
    }

    private (long Generation, CancellationToken Token) BeginRequest(State state)
    {
        state.Cancellation = new CancellationTokenSource();
        var generation = ++state.Generation;
        Trace($"Request member={state.Member.Id} generation={generation}");
        return (generation, state.Cancellation.Token);
    }
    private bool Current(State state, long generation) => !_disposed && _gate == MemberCarouselGate.Ready &&
        state.Generation == generation && _states.TryGetValue(state.Member.Id, out var existing) &&
        ReferenceEquals(existing, state) && state.Cancellation?.IsCancellationRequested == false;
    private void EndRequest(State state, long generation)
    {
        if (state.Generation != generation) return;
        state.Cancellation?.Dispose();
        state.Cancellation = null;
        state.ManualLoading = false;
        QueueCheck(false);
    }
    private void Cancel(State state)
    {
        // Gate changes can interrupt a load before a valid frame is ready. Keep that click queued.
        if (state.ManualLoading && !state.Flipping) state.PendingPages++;
        state.ManualLoading = false;
        state.Generation++;
        state.Cancellation?.Cancel();
        state.Cancellation?.Dispose();
        state.Cancellation = null;
        state.Runtime = MemberCarouselRules.AbandonPending(state.Runtime);
    }
    private void Fail(State state, Exception error)
    {
        state.Runtime = MemberCarouselRules.AbandonPending(state.Runtime) with { Passive = true };
        _error(string.Format(ProductStrings.Get("Wall.PuzzleLoadFailed"), error.Message));
    }
    private static PuzzleFrame? VisibleFrame(State state) => state.Flipping && state.Progress >= 0.5
        ? state.NextFrame : state.Frame;
    private void Publish(State state) => FrameChanged?.Invoke(state.Member.Id, VisibleFrame(state), state.Progress);
    private static TimeSpan Earlier(TimeSpan? first, TimeSpan second) => first is { } value && value < second ? value : second;
    private static uint RandomIndex() => (uint)Random.Shared.Next();
    private static bool SameSource(MemberImageSource left, MemberImageSource right) => left.Kind == right.Kind &&
        StringComparer.OrdinalIgnoreCase.Equals(left.Path, right.Path);
    private void Trace(string message)
    {
        if (!_trace) return;
        AppLog.Warn("Phase3Puzzle", message);
        AppLog.Flush();
    }
    public void Dispose()
    {
        if (_disposed) return;
        SetGate(MemberCarouselGate.Hidden);
        _disposed = true;
        if (_animationEventSubscribed && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            _uiSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
        _timer.Stop();
        _animationTimer.Stop();
        foreach (var state in _states.Values) Cancel(state);
        _states.Clear();
    }
}
