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
        public bool RefreshCandidatesOnResume;
        public string? SourceVersion;
        public PuzzleCandidateResult? PendingGallery;
        public CancellationTokenSource? Cancellation;
        public long Generation;
        public long FlipStarted;
        public bool Flipping;
        public bool NeedsInitial = true;
        public bool RestoreEvictedFrame;
        public bool FrameLoadUnavailable;
        public bool ValidateRetainedFrame;
        public LinkedListNode<State>? RetainedNode;
        public long RetainedBytes;
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
    private const long OffscreenIconLimitBytes = 64L * 1024 * 1024;
    private readonly LinkedList<State> _retainedIcons = new();
    private long _retainedIconBytes;
    private HashSet<string> _visibleMembers = new(StringComparer.Ordinal);
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
    private bool _stopping;
    private Task? _shutdown;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _work = [];
    private bool _queued;
    private bool _queuedExplicit;
    private bool _persistenceBusy;
    private bool _animationClockRunning;
    private readonly WallMetrics _metrics = new();
    private MemberImageSource? _editorGallery;
    private sealed class GalleryRequest(MemberImageSource source, CancellationTokenSource cancellation)
    {
        public MemberImageSource Source { get; } = source;
        public CancellationTokenSource Cancellation { get; } = cancellation;
    }
    private readonly Dictionary<string, GalleryRequest> _galleryRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _galleryDiagnostics = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string, PuzzleFrame?, double>? FrameChanged;
    public event Action<MemberImageSource, PuzzleCandidateResult>? GalleryCandidatesChanged;
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

    public PuzzleFrame? FrameFor(string id) => IsResident(id) && _states.TryGetValue(id, out var state)
        ? VisibleFrame(state) : null;
    public double ProgressFor(string id) => _states.TryGetValue(id, out var state) ? state.Progress : 1;
    public MemberTransition? TransitionFor(string id) => IsResident(id) && _states.TryGetValue(id, out var state)
        ? state.Flipping
            ? new(state.PreviousFrame, state.NextFrame, state.Effect, state.Progress, state.PartitionOrdinals)
            : new(null, state.Frame, MemberTransitionEffect.None, 1)
        : null;

    /// <summary>The caller supplies the viewport plus its preload margin. Until supplied, no wall frames load.</summary>
    public void SetVisibleMembers(IReadOnlySet<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (_disposed || _stopping || _visibleMembers.SetEquals(ids)) return;
        var previous = _visibleMembers;
        _visibleMembers = new HashSet<string>(ids, StringComparer.Ordinal);
        foreach (var state in _states.Values)
            if (previous.Contains(state.Member.Id) && !IsResident(state.Member.Id)) ReleaseFrames(state, retainStaticIcon: true);
        foreach (var state in _states.Values)
            if (!previous.Contains(state.Member.Id) && IsResident(state.Member.Id)) RestoreRetainedIcon(state);
        CancelUnusedGalleryRequests();
        UpdateAnimationClock();
        QueueCheck(false);
    }

    private bool IsResident(string id) => _gate != MemberCarouselGate.Hidden && _visibleMembers.Contains(id);

    private bool ReadAnimationsEnabled()
    {
        try { return _uiSettings.AnimationsEnabled; }
        catch { return false; }
    }

    private void OnAnimationsEnabledChanged(UISettings sender, object args)
    {
        if (_disposed || _stopping) return;
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed || _stopping) return;
            ApplyAnimationPreference();
            QueueCheck(false);
        });
    }

    private void ApplyAnimationPreference()
    {
        _animationsEnabled = ReadAnimationsEnabled();
        if (_animationsEnabled) return;
        foreach (var state in _states.Values.ToArray())
            if (state.Flipping) FinishFlip(state);
    }

    public void AdvanceAll()
    {
        if (_disposed || _stopping) return;
        foreach (var state in _states.Values)
            if (IsResident(state.Member.Id) && state.Member is PuzzleMember && CouldAdvance(state)) state.PendingPages++;
        QueueCheck(false);
    }

    private static bool CouldAdvance(State state) => state.Member.Images.Kind == MemberImageSourceKind.Folder;

    public void SetEditorGallerySource(MemberImageSource? source)
    {
        if (_disposed || _stopping) return;
        _editorGallery = source?.Kind == MemberImageSourceKind.Folder ? source : null;
        CancelUnusedGalleryRequests();
        QueueCheck(false);
    }

    public void Sync(WallDocument document)
    {
        if (_disposed || _stopping) return;
        var members = document.Lanes.SelectMany(l => l.Groups).SelectMany(g => g.Members)
            .Where(m => m is TileMember or PuzzleMember).ToDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var id in _states.Keys.Except(members.Keys).ToArray())
        {
            ForgetRetainedIcon(_states[id]);
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
                var blurChanged = (state.Member.Visual.BackgroundMode == MemberBackgroundMode.Blur)
                    != (member.Visual.BackgroundMode == MemberBackgroundMode.Blur);
                state.Member = member;
                if (partitionsChanged && state.Flipping) FinishFlip(state);
                if (changed || sizeChanged || qualityChanged || blurChanged || selectedChanged && !state.Persisting)
                {
                    ForgetRetainedIcon(state);
                    Cancel(state);
                    // A captured next image is already the committed carousel image. Never
                    // leave its old half-frame behind when editing or replacing a source.
                    // A blur-demand change cannot keep a frame decoded for the other demand.
                    state.Frame = changed || blurChanged || !IsResident(id) ? null : state.NextFrame ?? state.Frame;
                    state.ValidateRetainedFrame = false;
                    state.Flipping = false;
                    state.Capture = null;
                    state.NextFrame = null;
                    state.PreviousFrame = null;
                    state.Progress = 1;
                    state.Effect = MemberTransitionEffect.None;
                    state.PartitionOrdinals = null;
                    // Only a different image source invalidates the enumerated snapshot.
                    // Size, display quality, blur demand and the selected image reload frames from it.
                    if (changed) { state.Candidates = null; state.SourceVersion = null; state.PendingGallery = null; }
                    state.NeedsInitial = true;
                    state.FrameLoadUnavailable = false;
                    state.Runtime = new(false, 0, [], null);
                    Publish(state);
                }
            }
        }
        UpdateAnimationClock();
        CancelUnusedGalleryRequests();
        QueueCheck(false);
    }

    public void SetGate(MemberCarouselGate gate)
    {
        if (_disposed || _stopping || _gate == gate) return;
        var previousGate = _gate;
        _gate = gate;
        Trace($"Gate={gate}");
        _timer.Stop();
        if (gate != MemberCarouselGate.Ready)
        {
            foreach (var state in _states.Values.ToArray())
            {
                if (gate == MemberCarouselGate.Hidden) ReleaseFrames(state, retainStaticIcon: true);
                else
                {
                    Cancel(state);
                    if (state.Flipping) FinishFlip(state);
                }
            }
            Trace("HideCancelOrSuspend");
        }
        else if (previousGate == MemberCarouselGate.Hidden)
            foreach (var state in _states.Values.Where(state => IsResident(state.Member.Id))) RestoreRetainedIcon(state);
        CancelUnusedGalleryRequests();
        UpdateAnimationClock();
        QueueCheck(gate == MemberCarouselGate.Ready && previousGate == MemberCarouselGate.Hidden);
    }

    public void SetPersistenceBusy(bool busy)
    {
        if (_disposed || _stopping || _persistenceBusy == busy) return;
        _persistenceBusy = busy;
        QueueCheck(false);
        // Unlike a visibility/gesture gate, this does not cancel a captured save.
    }

    private void UpdateAnimationClock()
    {
        var needed = !_disposed && !_stopping && _gate == MemberCarouselGate.Ready &&
            _states.Values.Any(s => IsResident(s.Member.Id) && s.Flipping);
        if (needed == _animationClockRunning) return;
        _animationClockRunning = needed;
        if (needed) _animationTimer.Start();
        else _animationTimer.Stop();
    }

    private void OnAnimationTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || _stopping || _gate != MemberCarouselGate.Ready) return;
        foreach (var state in _states.Values.ToArray())
        {
            if (!IsResident(state.Member.Id) || !state.Flipping) continue;
            state.Progress = Math.Clamp(Stopwatch.GetElapsedTime(state.FlipStarted).TotalMilliseconds /
                PartitionTransitionTiming.DurationMilliseconds(state.PartitionOrdinals?.Count ?? 1), 0, 1);
            if (state.Progress >= 1) FinishFlip(state);
            else Publish(state);
        }
        UpdateAnimationClock();
    }

    private void QueueCheck(bool explicitCheck)
    {
        if (_disposed || _stopping) return;
        _queuedExplicit |= explicitCheck;
        if (_queued) return;
        _queued = _dispatcher.TryEnqueue(() =>
        {
            _queued = false;
            Check(false);
        });
    }

    private void Check(bool explicitCheck)
    {
        _queuedExplicit |= explicitCheck;
        if (_disposed || _stopping) return;
        _timer.Stop();
        // Directory deadlines belong to visible sources, independently of the
        // carousel's modal/gesture/save gate and of empty/passive shuffle queues.
        var wake = CheckGallerySources();
        if (_persistenceBusy || _gate != MemberCarouselGate.Ready) { ScheduleWake(wake); return; }
        explicitCheck = _queuedExplicit;
        _queuedExplicit = false;
        // Also covers OS versions without the preference-change event.
        ApplyAnimationPreference();
        foreach (var state in _states.Values.ToArray())
        {
            if (!IsResident(state.Member.Id)) continue;
            if (state.PendingGallery is { } snapshot && state.Cancellation is null && !state.Flipping)
            { state.PendingGallery = null; ApplyGallerySnapshot(state, snapshot); }
            if (state.Flipping)
            {
                // The bounded animation clock advances active transitions; this
                // deadline timer only wakes idle carousel deadlines.
                continue;
            }
            if (state.Cancellation is not null) continue;
            if (state.ValidateRetainedFrame && state.Frame is not null)
            {
                TrackWork(ValidateRetainedIconAsync(state));
                continue;
            }
            if (state.PendingPages > 0)
            {
                state.PendingPages--;
                state.Runtime = new(false, 0, [], null);
                TrackWork(LoadNextAsync(state, NextPageDecision(state), manual: true));
                continue;
            }
            if (explicitCheck && state.Member.Images.Kind == MemberImageSourceKind.Folder)
                state.NeedsInitial = true;
            if (state.NeedsInitial)
            {
                if (!state.Runtime.Passive || state.RestoreEvictedFrame || explicitCheck) TrackWork(LoadInitialAsync(state));
                continue;
            }
            if (state.Member.Images.Kind != MemberImageSourceKind.Folder) continue;
            var decision = MemberCarouselRules.Evaluate(state.Candidates ?? [], state.Member.Carousel,
                state.Runtime, DateTimeOffset.UtcNow, _gate, explicitCheck, RandomIndex(), state.Member.RotationInterval);
            state.Runtime = decision.Runtime;
            if (decision.Kind == MemberCarouselDecisionKind.Prepare) TrackWork(LoadNextAsync(state, decision));
            else if (decision.Remaining is { } remaining) wake = Earlier(wake, remaining);
        }
        ScheduleWake(wake);
    }

    private void ScheduleWake(TimeSpan? wake)
    {
        if (wake is { } delay && !_disposed && !_stopping)
        {
            _timer.Interval = delay < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : delay;
            _timer.Start();
            Trace($"TimerSchedule milliseconds={_timer.Interval.TotalMilliseconds:F0}");
        }
    }

    private TimeSpan? CheckGallerySources()
    {
        IEnumerable<MemberImageSource> wallSources = _states.Values.Where(state => IsResident(state.Member.Id)).Select(state => state.Member.Images)
            .Where(source => source.Kind == MemberImageSourceKind.Folder);
        IEnumerable<MemberImageSource> editorSources = _editorGallery is { } editor ? [editor] : [];
        var sources = wallSources.Concat(editorSources)
            .Where(source => !string.IsNullOrWhiteSpace(source.Path)).DistinctBy(source => Path.GetFullPath(source.Path!), StringComparer.OrdinalIgnoreCase).ToArray();
        TimeSpan? wake = null;
        foreach (var source in sources)
        {
            var key = Path.GetFullPath(source.Path!);
            if (_galleryRequests.ContainsKey(key)) continue;
            var remaining = _images.GalleryCheckDue(source) - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                var request = new GalleryRequest(source, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
                _galleryRequests.Add(key, request);
                TrackWork(RefreshGallerySourceAsync(request, key));
            }
            else wake = Earlier(wake, remaining);
        }
        return wake;
    }

    private bool HasResidentSource(MemberImageSource source) => _states.Values.Any(state =>
        IsResident(state.Member.Id) && SameSource(state.Member.Images, source));

    private void CancelUnusedGalleryRequests()
    {
        foreach (var request in _galleryRequests.Values.ToArray())
            if (!HasResidentSource(request.Source) &&
                !(_editorGallery is { } editor && SameSource(editor, request.Source))) request.Cancellation.Cancel();
    }

    private async Task RefreshGallerySourceAsync(GalleryRequest request, string key)
    {
        var source = request.Source;
        try
        {
            var snapshot = await _images.RefreshGalleryAsync(source, request.Cancellation.Token);
            if (_disposed || _stopping || request.Cancellation.IsCancellationRequested || snapshot.Status == WallImageStatus.Cancelled) return;
            var wallVisible = HasResidentSource(source);
            if (_editorGallery is { } editor && SameSource(editor, source)) GalleryCandidatesChanged?.Invoke(source, snapshot);
            if (wallVisible)
                foreach (var state in _states.Values.Where(state => IsResident(state.Member.Id) && SameSource(state.Member.Images, source)))
                    state.PendingGallery = snapshot;
            if (WallImageService.DescribeCandidateDiagnostic(snapshot) is { Length: > 0 } diagnostic && wallVisible && _galleryDiagnostics.GetValueOrDefault(key) != diagnostic)
                _error(diagnostic);
            _galleryDiagnostics[key] = WallImageService.DescribeCandidateDiagnostic(snapshot);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed && !_stopping && HasResidentSource(source)) _error(ex.Message); }
        finally
        {
            if (_galleryRequests.TryGetValue(key, out var current) && ReferenceEquals(current, request)) _galleryRequests.Remove(key);
            request.Cancellation.Dispose();
            QueueCheck(false);
        }
    }

    private void ApplyGallerySnapshot(State state, PuzzleCandidateResult snapshot)
    {
        // A complete check may have repaired a disposable package without changing
        // its source identity. Only failed frame reads earn this periodic retry;
        // a rejected formal carousel save must stay passive until explicit input.
        var retryFrame = snapshot.IsComplete && snapshot.Paths.Count > 0 && state.FrameLoadUnavailable;
        if (snapshot.SourceVersion is not null && snapshot.SourceVersion == state.SourceVersion &&
            state.Candidates?.SequenceEqual(snapshot.Paths, StringComparer.OrdinalIgnoreCase) == true && !retryFrame) return;
        if (!snapshot.IsComplete && snapshot.Paths.Count == 0) return;
        state.SourceVersion = snapshot.SourceVersion; state.Candidates = snapshot.Paths;
        state.RefreshCandidatesOnResume = false;
        if (snapshot.IsComplete && snapshot.Paths.Count == 0)
        {
            state.Frame = state.NextFrame = state.PreviousFrame = null;
            state.FrameLoadUnavailable = false;
            state.NeedsInitial = false; state.Runtime = new(true, 0, [], null); state.Progress = 1;
            Publish(state); return;
        }
        var current = state.Frame?.CanonicalPath;
        if (retryFrame)
        {
            state.FrameLoadUnavailable = false;
            state.Runtime = new(false, 0, [], null);
        }
        else state.Runtime = state.Runtime with { Passive = false };
        if (current is null || !snapshot.Paths.Contains(current, StringComparer.OrdinalIgnoreCase) || snapshot.ChangedPaths.Contains(current))
        { state.NeedsInitial = true; state.Runtime = new(false, 0, [], null); }
    }

    private async Task LoadInitialAsync(State state)
    {
        var (generation, token) = BeginRequest(state);
        var resumePassive = state.RestoreEvictedFrame && state.Runtime.Passive;
        var resumedRuntime = state.Runtime;
        try
        {
            if (state.Member.Images.Kind != MemberImageSourceKind.Folder)
            {
                var staticFrame = await _images.LoadMemberFrameAsync(state.Member,
                    ResolveEntryPath?.Invoke(state.Member) ?? state.Member.TargetInput,
                    state.TargetWidth, state.TargetHeight, ct: token);
                Trace($"StaticFrame member={state.Member.Id} status={staticFrame.Status} diagnostic={staticFrame.Diagnostic}");
                if (!Current(state, generation)) return;
                if (staticFrame.Frame is { } loaded) state.Frame = loaded;
                else if (staticFrame.Status == WallImageStatus.None) state.Frame = null;
                state.ValidateRetainedFrame = false;
                var available = staticFrame.Frame is not null || staticFrame.Status == WallImageStatus.None;
                state.NeedsInitial = !available;
                state.FrameLoadUnavailable = !available;
                state.RestoreEvictedFrame = false;
                state.Progress = 1;
                state.Runtime = new(!available, 0, [], null);
                Publish(state);
                return;
            }
            // Sync clears Candidates when the folder source changes. Reuse the
            // ordered snapshot for display/next-page requests instead of re-probing
            // every file in the folder each time.
            var candidateSnapshot = state.Candidates is null || state.RefreshCandidatesOnResume
                ? await _images.EnumerateMemberCandidatesAsync(state.Member.Images, token) : null;
            var candidates = candidateSnapshot?.Paths ?? state.Candidates!;
            if (!Current(state, generation)) return;
            state.Candidates = candidates;
            state.RefreshCandidatesOnResume = false;
            if (candidateSnapshot is not null) state.SourceVersion = candidateSnapshot.SourceVersion;
            if (candidates.Count == 0)
            {
                if (candidateSnapshot?.IsComplete != false) { state.Frame = null; Publish(state); }
                state.NeedsInitial = candidateSnapshot?.IsComplete == false;
                state.FrameLoadUnavailable = state.NeedsInitial;
                state.RestoreEvictedFrame = false;
                state.Runtime = new(true, 0, [], null); return;
            }
            var current = state.Member.Carousel?.CurrentImageId;
            var restoring = candidates.FirstOrDefault(path => StringComparer.OrdinalIgnoreCase.Equals(path, current));
            var tentative = state.Member.Carousel;
            // Rehydrating an evicted passive member may restore its committed image,
            // but does not retry a rejected formal switch or consume a different queue entry.
            var attempts = resumePassive ? (restoring is null ? 0 : 1) : Math.Min(3, candidates.Count);
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                var restore = attempt == 0 && restoring is not null;
                if (!restore) tentative = ImageCarouselSequence.Next(candidates, tentative,
                    DateTimeOffset.UtcNow);
                var candidate = restore ? restoring! : tentative?.CurrentImageId;
                if (candidate is null) break;
                var result = await LoadAsync(state, candidate, token);
                if (!Current(state, generation)) return;
                if (result.Frame is not { } frame) continue;
                state.FrameLoadUnavailable = false;
                state.NeedsInitial = false;
                state.RestoreEvictedFrame = false;
                state.Progress = 1;
                state.Runtime = resumePassive ? resumedRuntime : new(false, 0, [], null);
                if (!restore && tentative is not null)
                    state.Runtime = state.Runtime with { PendingImageId = candidate, PendingState = tentative };
                if (state.Frame is { } previous &&
                    !StringComparer.OrdinalIgnoreCase.Equals(previous.CanonicalPath, frame.CanonicalPath))
                {
                    // Restoration may need a visual flip from the previous frame without changing
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
            state.FrameLoadUnavailable = true;
            state.RestoreEvictedFrame = false;
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
            // Sync discards the candidate snapshot on source changes; until then, reuse it.
            if ((state.Candidates is null || state.RefreshCandidatesOnResume) &&
                (state.Member.Images.Kind == MemberImageSourceKind.Folder || manual))
            {
                var candidates = await _images.EnumerateMemberCandidatesAsync(state.Member.Images, token);
                if (!Current(state, generation)) return;
                state.Candidates = candidates.Paths;
                state.SourceVersion = candidates.SourceVersion;
                state.RefreshCandidatesOnResume = false;
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
                    state.FrameLoadUnavailable = false;
                    if (manual) state.NeedsInitial = false;
                    await StartFlipAsync(state, frame, generation);
                    return;
                }
                state.FrameLoadUnavailable = true;
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
            // A failed switch never survives into a later edit generation as a stale capture.
            state.Frame = state.PreviousFrame;
            state.PreviousFrame = null;
            state.NextFrame = null;
            state.Flipping = false;
            state.Progress = 1;
            state.Effect = MemberTransitionEffect.None;
            state.PartitionOrdinals = null;
            state.Capture = null;
            state.FrameLoadUnavailable = false;
            state.Runtime = new(true, 0, [], null);
            state.NeedsInitial = state.Frame is null || state.Member.Carousel?.LastSwitchUtc is null;
            Publish(state);
            UpdateAnimationClock();
            Trace($"PersistFailedReverted member={state.Member.Id}");
            _error(string.Format(ProductStrings.Get("Wall.PuzzlePersistFailed"), error));
            return false;
        }
        state.Member = state.Member with { Carousel = capture.NextPersistedState };
        // Both direct initial presentation and animated switches finish their pending
        // queue operation here. Keeping PendingImageId after the save suppresses every
        // later deadline in Evaluate, even though the captured image is already committed.
        state.Runtime = capture.Runtime;
        state.Capture = null;
        Trace($"Persisted member={state.Member.Id} image={capture.NextPersistedState.CurrentImageId} utc={capture.EnteredFlipUtc:O}");
        return true;
    }

    private (long Generation, CancellationToken Token) BeginRequest(State state)
    {
        state.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var generation = ++state.Generation;
        Trace($"Request member={state.Member.Id} generation={generation}");
        return (generation, state.Cancellation.Token);
    }
    private bool Current(State state, long generation) => !_disposed && !_stopping && _gate == MemberCarouselGate.Ready &&
        IsResident(state.Member.Id) &&
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

    private async Task ValidateRetainedIconAsync(State state)
    {
        var (generation, token) = BeginRequest(state);
        var frame = state.Frame!;
        try
        {
            var current = await _images.IsAutomaticFrameCurrentAsync(frame,
                ResolveEntryPath?.Invoke(state.Member) ?? state.Member.TargetInput, token);
            if (!Current(state, generation) || !ReferenceEquals(frame, state.Frame)) return;
            state.ValidateRetainedFrame = false;
            // Keep the visible previous frame while an expired/changed source
            // prepares its replacement; revalidation itself never clears it.
            if (!current) state.NeedsInitial = true;
            Trace($"RetainedStaticFrame member={state.Member.Id} current={current}");
        }
        catch (OperationCanceledException) { }
        finally { EndRequest(state, generation); }
    }

    private void RestoreRetainedIcon(State state)
    {
        if (state.RetainedNode is null) return;
        ForgetRetainedIcon(state);
        Publish(state);
        Trace($"ReuseStaticFrame member={state.Member.Id}");
    }

    private void ForgetRetainedIcon(State state)
    {
        if (state.RetainedNode is not { } node) return;
        _retainedIcons.Remove(node);
        _retainedIconBytes -= state.RetainedBytes;
        state.RetainedNode = null;
        state.RetainedBytes = 0;
    }

    private bool TryRetainStaticIcon(State state)
    {
        if (state.Member.Images.Kind != MemberImageSourceKind.Automatic || state.NeedsInitial ||
            state.Frame is not { MemoryCacheAllowed: true } frame) return false;
        var bytes = 4L * ((long)frame.SharpDecodedPixels.Width * (long)frame.SharpDecodedPixels.Height +
            (long)frame.BlurDecodedPixels.Width * (long)frame.BlurDecodedPixels.Height);
        if (bytes <= 0 || bytes > OffscreenIconLimitBytes) return false;
        if (state.RetainedNode is null)
        {
            state.RetainedNode = _retainedIcons.AddLast(state);
            state.RetainedBytes = bytes;
            _retainedIconBytes += bytes;
        }
        while (_retainedIconBytes > OffscreenIconLimitBytes && _retainedIcons.First is { } oldest)
            ReleaseFrames(oldest.Value);
        state.ValidateRetainedFrame = true;
        return true;
    }

    private void ReleaseFrames(State state, bool retainStaticIcon = false)
    {
        if (retainStaticIcon && TryRetainStaticIcon(state))
        {
            Cancel(state);
            Publish(state); // The offscreen view drops its references; State owns the retained frame.
            return;
        }
        ForgetRetainedIcon(state);
        state.RestoreEvictedFrame |= state.Frame is not null || state.NextFrame is not null || state.PreviousFrame is not null;
        Cancel(state);
        state.Frame = state.NextFrame = state.PreviousFrame = null;
        state.Capture = null;
        state.Flipping = false;
        state.Progress = 1;
        state.Effect = MemberTransitionEffect.None;
        state.PartitionOrdinals = null;
        state.PendingPages = 0;
        state.NeedsInitial = true;
        state.ValidateRetainedFrame = false;
        state.RefreshCandidatesOnResume = state.Member.Images.Kind == MemberImageSourceKind.Folder;
        Publish(state); // Existing views drop both transition layers and their ImageSource references.
    }
    private void Fail(State state, Exception error)
    {
        state.FrameLoadUnavailable = state.Member.Images.Kind == MemberImageSourceKind.Folder;
        state.Runtime = MemberCarouselRules.AbandonPending(state.Runtime) with { Passive = true };
        _error(string.Format(ProductStrings.Get("Wall.PuzzleLoadFailed"), error.Message));
    }
    private static PuzzleFrame? VisibleFrame(State state) => state.Flipping && state.Progress >= 0.5
        ? state.NextFrame : state.Frame;
    private void Publish(State state) => FrameChanged?.Invoke(state.Member.Id,
        IsResident(state.Member.Id) ? VisibleFrame(state) : null, state.Progress);
    private static TimeSpan Earlier(TimeSpan? first, TimeSpan second) => first is { } value && value < second ? value : second;
    private static uint RandomIndex() => (uint)Random.Shared.Next();
    private static bool SameSource(MemberImageSource left, MemberImageSource right) => left.Kind == right.Kind &&
        StringComparer.OrdinalIgnoreCase.Equals(left.Path, right.Path) && left.Media == right.Media && left.ManagedGallery == right.ManagedGallery;
    private void Trace(string message)
    {
        if (!_trace) return;
        AppLog.Warn("Phase3Puzzle", message);
        AppLog.Flush();
    }

    private void TrackWork(Task task)
    {
        // Includes each request's generation checks, publishing and persisted flip,
        // not only the service task it awaited. All access is on the UI thread.
        _work.RemoveWhere(completed => completed.IsCompletedSuccessfully || completed.IsCanceled);
        _work.Add(task);
    }

    public Task ShutdownAsync() => _shutdown ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        StopRequests();
        try { await Task.WhenAll(_work); }
        catch (Exception ex) { AppLog.Warn("PuzzleShutdown", ex.Message); }
        finally { _work.Clear(); _lifetime.Dispose(); }
    }

    private void StopRequests()
    {
        if (_stopping) return;
        _stopping = true;
        _gate = MemberCarouselGate.Hidden;
        _editorGallery = null;
        _timer.Stop();
        _animationTimer.Stop();
        _animationClockRunning = false;
        _lifetime.Cancel();
        foreach (var state in _states.Values) ReleaseFrames(state);
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopRequests();
        _disposed = true;
        if (_animationEventSubscribed && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            _uiSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
        _states.Clear();
    }
}
