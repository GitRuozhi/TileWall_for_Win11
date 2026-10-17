using System.Diagnostics;
using System.Text.Json;
using TileWall.Core.Wall;

namespace TileWall;

/// <summary>Optional bounded in-memory CPU/submit diagnostics; never persisted in wall settings.</summary>
internal sealed class GesturePerformance
{
    private readonly bool _enabled = Environment.GetEnvironmentVariable("TILEWALL_PERF") == "1";
    internal bool Enabled => _enabled;
    private readonly List<double> _frames = [], _ages = [], _proposals = [], _views = [];
    private readonly List<double> _callbackIntervals = [], _renderIntervals = [], _dispatchAges = [];
    private readonly List<double> _windowMoves = [], _windowAges = [];
    private readonly List<double> _windowChanged = [], _backgroundErase = [];
    private readonly List<double> _compositionCommitLatency = [];
    private readonly Dictionary<uint, List<double>> _nativeMessageTimings = [];
    private readonly Dictionary<string, int> _windowSubmissionBackends = [];
    private int _windowRectAckMatches, _windowRectAckMismatches, _windowRectAckFailures;
    private long _inputTime;
    private long _lastFrame, _lastRendering, _visualStart, _gridStart;
    private long _nestedChangedTicks, _nestedEraseTicks;
    private double _commitMs;
    private int _commits, _frameGhosts, _frameMoves, _maxFrameGhosts, _maxFrameMoves;
    private int _inputs, _ghosts, _cacheHits, _moves;
    private int _callbacks, _idleCallbacks;
    private int _renderingCallbacks, _idleRenderingCallbacks, _compositionCommitRequests;
    private int _compositionCommitsCompleted;
    private string _scheduler = "unassigned";
    private string? _schedulerFailure;
    private string _timerState = "unreported";
    private uint? _timerBeginResult, _timerEndResult;
    private bool _nativeSubmissionActive;
    private object? _context;
    private object? _compositionGateAtBegin;
    private bool _active;
    public void Begin(string kind, WallDocument doc, double scale, long visuals, long grids)
    {
        if (!_enabled) return;
        _frames.Clear(); _ages.Clear(); _proposals.Clear(); _views.Clear();
        _callbackIntervals.Clear(); _renderIntervals.Clear(); _dispatchAges.Clear();
        _windowChanged.Clear(); _backgroundErase.Clear();
        _compositionCommitLatency.Clear(); _nativeMessageTimings.Clear(); _compositionCommitsCompleted = 0;
        _windowSubmissionBackends.Clear();
        _windowRectAckMatches = _windowRectAckMismatches = _windowRectAckFailures = 0;
        _timerState = "unreported"; _timerBeginResult = _timerEndResult = null;
        _compositionGateAtBegin = null;
        _windowMoves.Clear(); _windowAges.Clear(); _callbacks = _idleCallbacks = 0;
        _renderingCallbacks = _idleRenderingCallbacks = _compositionCommitRequests = 0;
        _scheduler = "unassigned"; _schedulerFailure = null; _nativeSubmissionActive = false;
        _inputs = _ghosts = _cacheHits = _moves = 0; _inputTime = Stopwatch.GetTimestamp(); _active = true;
        _visualStart = visuals; _gridStart = grids; _lastFrame = _lastRendering = 0; _commitMs = 0; _commits = 0;
        _frameGhosts = _frameMoves = _maxFrameGhosts = _maxFrameMoves = 0;
        _context = new { kind, doc.Generation, doc.Columns, doc.Rows, scale,
            members = doc.Lanes.SelectMany(l => l.Groups).Sum(g => g.Members.Count), doc.Settings.ShowGrid,
            doc.Settings.BackgroundMode, os = Environment.OSVersion.VersionString };
    }
    private static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;
    private static void Add(List<double> list, double value) { if (list.Count < 16384) list.Add(value); }
    public void Input() { if (_active) { _inputs++; _inputTime = Stopwatch.GetTimestamp(); } }
    public void Scheduler(string source, string? failure)
    { if (_active) { _scheduler = source; _schedulerFailure = failure; } }
    public void DisplayPulse(long timestamp)
    { if (_active) Add(_dispatchAges, Ms(Stopwatch.GetTimestamp() - timestamp)); }
    public void CompositionCommitRequest() { if (_active) _compositionCommitRequests++; }
    public void CompositionGateAtBegin(bool pending, string status, double ageMs, bool cancellationRequested)
    { if (_active) _compositionGateAtBegin = new { pending, status, ageMs, cancellationRequested }; }
    public void CompositionCommitCompleted(long started)
    {
        if (!_active) return;
        _compositionCommitsCompleted++;
        Add(_compositionCommitLatency, Ms(Stopwatch.GetTimestamp() - started));
    }
    public void TimerState(string state, uint? beginResult, uint? endResult)
    { if (_active) { _timerState = state; _timerBeginResult = beginResult; _timerEndResult = endResult; } }
    public void Frame(long started, bool activeWork, bool displayPulse)
    {
        if (!_active) return;
        _callbacks++;
        if (_lastFrame != 0) Add(_callbackIntervals, Ms(started - _lastFrame));
        _lastFrame = started;
        if (!displayPulse)
        {
            _renderingCallbacks++;
            if (!activeWork) _idleRenderingCallbacks++;
            if (_lastRendering != 0) Add(_renderIntervals, Ms(started - _lastRendering));
            _lastRendering = started;
        }
        // Continuous gesture subscription includes stationary-pointer callbacks. Keep
        // their cadence, but never let idle frames dilute the active-work percentiles.
        if (activeWork) Add(_frames, Ms(Stopwatch.GetTimestamp() - started));
        else _idleCallbacks++;
        _maxFrameGhosts = Math.Max(_maxFrameGhosts, _frameGhosts);
        _maxFrameMoves = Math.Max(_maxFrameMoves, _frameMoves);
        _frameGhosts = _frameMoves = 0;
    }
    public void Proposal(long ticks) { if (_active) Add(_proposals, Ms(ticks)); }
    public void View(long ticks) { if (_active) Add(_views, Ms(ticks)); }
    public void Ghost() { if (_active) { _ghosts++; _frameGhosts++; Add(_ages, Ms(Stopwatch.GetTimestamp() - _inputTime)); } }
    public void CacheHit() { if (_active) _cacheHits++; }
    public void BeginWindowSubmission()
    {
        if (!_active) return;
        _nativeSubmissionActive = true;
        _nestedChangedTicks = _nestedEraseTicks = 0;
    }
    public long NativeStageStart() => _active && _nativeSubmissionActive ? Stopwatch.GetTimestamp() : 0;
    public void WindowChanged(long started)
    { if (started != 0 && _active && _nativeSubmissionActive) _nestedChangedTicks += Stopwatch.GetTimestamp() - started; }
    public void NativeErase(long ticks)
    { if (_active && _nativeSubmissionActive) _nestedEraseTicks += ticks; }
    public void NativeMessage(uint message, long ticks)
    {
        if (!_active || !_nativeSubmissionActive) return;
        if (!_nativeMessageTimings.TryGetValue(message, out var timings))
            _nativeMessageTimings.Add(message, timings = []);
        Add(timings, Ms(ticks));
    }
    public void AbortWindowSubmission() => _nativeSubmissionActive = false;
    public void WindowMove(long ticks, string backend = "appwindow", bool? actualRectMatches = null)
    {
        if (!_active) return;
        _moves++; _frameMoves++;
        _windowSubmissionBackends[backend] = _windowSubmissionBackends.GetValueOrDefault(backend) + 1;
        if (backend.StartsWith("setwindowpos", StringComparison.Ordinal))
        {
            if (actualRectMatches is true) _windowRectAckMatches++;
            else if (actualRectMatches is false) _windowRectAckMismatches++;
            else _windowRectAckFailures++;
        }
        Add(_windowMoves, Ms(ticks));
        Add(_windowAges, Ms(Stopwatch.GetTimestamp() - _inputTime));
        Add(_windowChanged, Ms(_nestedChangedTicks));
        Add(_backgroundErase, Ms(_nestedEraseTicks));
        _nativeSubmissionActive = false;
    }
    public void Commit(long ticks) { if (_active) { _commits++; _commitMs += Ms(ticks); } }
    public void End(string outcome, long visualCreations, long gridCreations)
    {
        if (!_active) return;
        _active = false;
        var report = JsonSerializer.Serialize(new { schemaVersion = 4, context = _context, outcome,
            frameSource = _scheduler, frameSourceFallbackReason = _schedulerFailure,
            interactionTimer = new { periodMs = 1, state = _timerState, beginResult = _timerBeginResult, endResult = _timerEndResult },
            inputs = _inputs, ghostSubmissions = _ghosts, proposalCount = _proposals.Count,
            cacheHits = _cacheHits, windowSubmissions = _moves,
            windowSubmissionBackend = _windowSubmissionBackends.Count switch
            {
                0 => "none",
                1 => _windowSubmissionBackends.Keys.Single(),
                _ => "mixed",
            },
            windowSubmissionBackends = _windowSubmissionBackends,
            setWindowPosRectAck = new { matches = _windowRectAckMatches, mismatches = _windowRectAckMismatches, readFailures = _windowRectAckFailures },
            visualCreations = visualCreations - _visualStart, gridCreations = gridCreations - _gridStart,
            commits = _commits, commitAndViewCpuMs = _commitMs,
            maxGhostSubmissionsPerFrameCallback = _maxFrameGhosts, maxWindowSubmissionsPerFrameCallback = _maxFrameMoves,
            frameCallbacks = _callbacks, idleFrameCallbacks = _idleCallbacks,
            renderingCallbacks = _renderingCallbacks, idleRenderingCallbacks = _idleRenderingCallbacks,
            dxgiDisplayCallbacks = _callbacks - _renderingCallbacks,
            compositionCommitRequests = _compositionCommitRequests,
            compositionGateAtBegin = _compositionGateAtBegin,
            compositionCommitsCompleted = _compositionCommitsCompleted,
            compositionCommitCompletionLatencyMs = Summary(_compositionCommitLatency),
            frameCpuMs = Summary(_frames), proposalCpuMs = Summary(_proposals), viewCpuMs = Summary(_views),
            nativeWindowSubmitCpuMs = Summary(_windowMoves),
            nestedWindowChangedCpuMs = Summary(_windowChanged), nestedBackgroundEraseCpuMs = Summary(_backgroundErase),
            nativeMessageChainCpuMs = _nativeMessageTimings.ToDictionary(p => p.Key switch
            {
                0x0003 => "WM_MOVE",
                0x0005 => "WM_SIZE",
                0x0046 => "WM_WINDOWPOSCHANGING",
                0x0047 => "WM_WINDOWPOSCHANGED",
                0x0083 => "WM_NCCALCSIZE",
                _ => $"0x{p.Key:X4}",
            }, p => Summary(p.Value)),
            frameCallbackIntervalMs = Summary(_callbackIntervals),
            renderingCallbackIntervalMs = Summary(_renderIntervals),
            displayPulseDispatchDelayMs = Summary(_dispatchAges),
            latestInputToGhostSubmitMs = Summary(_ages),
            latestInputToWindowSubmitMs = Summary(_windowAges),
            frameCpuDefinition = "Active callbacks from frameSource consuming input, advancing a transition or submitting a rectangle; idle callbacks excluded. Intervals use callback start times. Native submit is inclusive API wall time; nested message stages may overlap and cannot be subtracted as percentiles. SetWindowPos RECT readback is included in UI-frame and input-to-submit latency, outside the API interval. Commit completion latency includes UI continuation dispatch and is not presentation latency.",
            measurement = "CPU and UI submission only; not physical display latency or presented frame time." },
            new JsonSerializerOptions { WriteIndented = true });
        // One write after the gesture. No pointer event logs, flushes or per-frame I/O.
        _ = Task.Run(() =>
        {
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "TileWall-performance");
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, $"gesture-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.json"), report);
            }
            catch { /* Optional diagnostics must never affect input or commit. */ }
        });
    }
    private static object Summary(List<double> values)
    {
        var ordered = values.Order().ToArray();
        double P(double p) => ordered.Length == 0 ? 0 : ordered[Math.Clamp((int)Math.Ceiling(ordered.Length * p) - 1, 0, ordered.Length - 1)];
        return new { count = ordered.Length, p50 = P(.5), p95 = P(.95), p99 = P(.99), max = P(1) };
    }
}
