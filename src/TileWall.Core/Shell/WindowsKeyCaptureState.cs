namespace TileWall.Core.Shell;

public enum CapturedInputSource { Physical, OwnInjected, ExternalInjected, PromotedPointer }
public enum WindowsKeyCapturePhase { Stopped, Idle, Pending, Chord, Bypass, Faulted }
public enum WindowsKeyCaptureFailure { None, UnsupportedReplay, ReplayIncomplete, ReplayResultUnknown, ExternalInjectionInterrupted, UnconfirmedReplay }

/// <summary>Original hook metadata. IsReplayable must be false for keys the native adapter cannot reproduce reliably.</summary>
public readonly record struct WindowsKeyStroke(int VirtualKey, int ScanCode, bool Extended, bool IsUp = false, bool IsReplayable = true);
public readonly record struct WindowsKeyInput(WindowsKeyStroke Stroke, CapturedInputSource Source = CapturedInputSource.Physical);

/// <summary>Execute Inputs in ONE SendInput call, then synchronously report its result before handling another physical event.</summary>
public sealed record WindowsKeyReplay(long Id, IReadOnlyList<WindowsKeyStroke> Inputs, bool IsCleanup);
public readonly record struct WindowsKeyCaptureDecision(
    bool Suppress = false,
    bool Toggle = false,
    WindowsKeyReplay? Replay = null,
    WindowsKeyCaptureFailure Failure = WindowsKeyCaptureFailure.None);

/// <summary>
/// Hook-thread-only input accounting; owns no hook and does not inject input itself.
/// Stop closes admission but preserves pairing until physical releases arrive. The caller must retain
/// the hook while CanReleaseHook is false; a bounded shutdown timeout is a real fault, not successful cleanup.
/// </summary>
public sealed class WindowsKeyCaptureState
{
    public const int LeftWindowsKey = 0x5B;
    public const int RightWindowsKey = 0x5C;
    private readonly HashSet<int> _physicalHeld = [];
    private readonly HashSet<int> _externalHeld = [];
    private readonly HashSet<int> _observedHeld = [];
    private readonly HashSet<int> _swallowed = [];
    private readonly Dictionary<int, WindowsKeyStroke> _injectedDown = [];
    private WindowsKeyStroke _candidate;
    private WindowsKeyReplay? _replay;
    private long _nextReplayId;
    private bool _active;
    private bool _unknownReplay;

    public WindowsKeyCapturePhase Phase { get; private set; } = WindowsKeyCapturePhase.Stopped;
    public bool HasPendingRelease => _swallowed.Count != 0 || _injectedDown.Count != 0;
    public bool HasUnconfirmedReplay => _replay is not null || _unknownReplay;
    public bool CanReleaseHook => !HasPendingRelease && !HasUnconfirmedReplay;

    /// <summary>Sample initial held keys before activation, never with GetAsyncKeyState inside the callback.</summary>
    public void Activate(IEnumerable<int> initialHeldKeys)
    {
        ArgumentNullException.ThrowIfNull(initialHeldKeys);
        if (!CanReleaseHook || _active)
        {
            throw new InvalidOperationException("Previous input accounting has not completed.");
        }

        _physicalHeld.Clear();
        _externalHeld.Clear();
        _observedHeld.Clear();
        _observedHeld.UnionWith(initialHeldKeys);
        _active = true;
        Phase = _observedHeld.Count == 0 ? WindowsKeyCapturePhase.Idle : WindowsKeyCapturePhase.Bypass;
    }

    /// <summary>Bypass the active input round without releasing or forgetting its input pairings.</summary>
    public void CancelCurrentGesture()
    {
        if (_active && Phase != WindowsKeyCapturePhase.Faulted)
            Phase = WindowsKeyCapturePhase.Bypass;
    }

    /// <summary>
    /// Reconcile held-key ledgers against a stable native sample taken outside the hook callback after a desktop switch.
    /// Swallowed keys remain owned until their matching hook up arrives; this method never injects releases.
    /// </summary>
    public bool ReconcileAfterDesktopSwitch(IReadOnlySet<int> nativeHeldKeys)
    {
        ArgumentNullException.ThrowIfNull(nativeHeldKeys);
        if (_replay is not null || _unknownReplay) return false;

        CancelCurrentGesture();
        _physicalHeld.RemoveWhere(key => !nativeHeldKeys.Contains(key) && !_swallowed.Contains(key));
        _externalHeld.RemoveWhere(key => !nativeHeldKeys.Contains(key) && !_swallowed.Contains(key));
        foreach (var key in _injectedDown.Keys
                     .Where(key => !nativeHeldKeys.Contains(key) && !_swallowed.Contains(key)).ToArray())
            _injectedDown.Remove(key);

        _observedHeld.Clear();
        foreach (var key in nativeHeldKeys)
        {
            if (!_physicalHeld.Contains(key) && !_externalHeld.Contains(key)
                && !_injectedDown.ContainsKey(key) && !_swallowed.Contains(key))
                _observedHeld.Add(key);
        }

        if (_active && Phase == WindowsKeyCapturePhase.Idle
            && (_physicalHeld.Count != 0 || _externalHeld.Count != 0 || _observedHeld.Count != 0
                || _swallowed.Count != 0 || _injectedDown.Count != 0))
            Phase = WindowsKeyCapturePhase.Bypass;

        ResetWhenReleased();
        return true;
    }

    public WindowsKeyCaptureDecision Handle(WindowsKeyInput input)
    {
        // The adapter must require BOTH its private marker and the native injected flag for OwnInjected.
        if (input.Source == CapturedInputSource.OwnInjected) return default;
        var key = input.Stroke.VirtualKey;
        if (input.Stroke.IsUp) _observedHeld.Remove(key);
        if (input.Source != CapturedInputSource.Physical)
        {
            if (input.Stroke.IsUp) _externalHeld.Remove(key);
            else _externalHeld.Add(key);
            if (Phase == WindowsKeyCapturePhase.Pending)
            {
                return Fail(WindowsKeyCaptureFailure.ExternalInjectionInterrupted);
            }

            if (_active && Phase == WindowsKeyCapturePhase.Idle && _externalHeld.Count != 0)
                Phase = WindowsKeyCapturePhase.Bypass;
            ResetWhenReleased();
            return default;
        }

        var wasHeld = _physicalHeld.Contains(key);
        if (input.Stroke.IsUp) _physicalHeld.Remove(key);
        else _physicalHeld.Add(key);

        if (_replay is not null)
        {
            // Self-injected callbacks above are expected; unrelated physical reentry violates the adapter contract.
            var suppress = _swallowed.Contains(key);
            if (input.Stroke.IsUp && suppress) _swallowed.Remove(key);
            return Fail(WindowsKeyCaptureFailure.UnconfirmedReplay, suppress);
        }

        if (input.Stroke.IsUp)
        {
            var suppress = _swallowed.Remove(key);
            _injectedDown.Remove(key); // A real up balances a confirmed replayed down.
            var toggle = _active && Phase == WindowsKeyCapturePhase.Pending && key == _candidate.VirtualKey && suppress;
            ResetWhenReleased();
            return new(Suppress: suppress, Toggle: toggle);
        }

        if (_swallowed.Contains(key)) return new(Suppress: true);
        if (!_active) return default;
        if (Phase == WindowsKeyCapturePhase.Idle && IsWindowsKey(key) && !wasHeld
            && _physicalHeld.Count == 1 && _externalHeld.Count == 0 && _observedHeld.Count == 0)
        {
            _candidate = input.Stroke;
            _swallowed.Add(key);
            Phase = WindowsKeyCapturePhase.Pending;
            return new(Suppress: true);
        }

        if (Phase == WindowsKeyCapturePhase.Pending)
        {
            Phase = WindowsKeyCapturePhase.Chord; // Before SendInput can reenter the hook.
            _swallowed.Add(key);
            if (!_candidate.IsReplayable || !input.Stroke.IsReplayable)
                return Fail(WindowsKeyCaptureFailure.UnsupportedReplay, suppress: true);
            _replay = new(++_nextReplayId, Array.AsReadOnly(new[] { _candidate, input.Stroke }), IsCleanup: false);
            return new(Suppress: true, Replay: _replay);
        }

        if (Phase == WindowsKeyCapturePhase.Idle) Phase = WindowsKeyCapturePhase.Bypass;
        return default;
    }

    /// <summary>
    /// insertedCount is the returned SendInput count; null means the result is unknown (for example an exception).
    /// Only a confirmed ordered prefix changes the ledger. Unknown results deliberately cannot claim successful teardown.
    /// </summary>
    public WindowsKeyCaptureDecision CompleteReplay(long requestId, int? insertedCount)
    {
        if (_replay is null || _replay.Id != requestId)
            throw new InvalidOperationException("Replay confirmation does not match the outstanding request.");
        var replay = _replay;
        _replay = null;
        if (insertedCount is null || insertedCount < 0 || insertedCount > replay.Inputs.Count)
        {
            _unknownReplay = true;
            return Fail(WindowsKeyCaptureFailure.ReplayResultUnknown);
        }

        for (var index = 0; index < insertedCount.Value; index++)
        {
            var stroke = replay.Inputs[index];
            if (stroke.IsUp)
            {
                _injectedDown.Remove(stroke.VirtualKey);
                if (_physicalHeld.Contains(stroke.VirtualKey)) _swallowed.Add(stroke.VirtualKey);
            }
            else
            {
                _injectedDown[stroke.VirtualKey] = stroke;
                _swallowed.Remove(stroke.VirtualKey); // Native input now owns the down; let the real up through.
            }
        }

        if (insertedCount != replay.Inputs.Count) return Fail(WindowsKeyCaptureFailure.ReplayIncomplete);
        ResetWhenReleased();
        return default;
    }

    /// <summary>No synthetic release by default: pending Win is swallowed through up, replayed keys finish with their real up.</summary>
    public WindowsKeyCaptureDecision Stop()
    {
        _active = false;
        if (Phase != WindowsKeyCapturePhase.Faulted) Phase = WindowsKeyCapturePhase.Stopped;
        return default;
    }

    /// <summary>
    /// Optional explicit fault recovery, limited to confirmed self-inserted downs. May invoke native Start
    /// after a partial Win-only insertion; this is NOT evidence that the native feasibility gate passed.
    /// Never releases arbitrary physical modifiers. Repeated physical downs/up remain swallowed afterwards.
    /// </summary>
    public WindowsKeyCaptureDecision ReleaseKnownInjectedDowns()
    {
        Stop();
        if (_replay is not null || _unknownReplay || _injectedDown.Count == 0) return default;
        var releases = _injectedDown.Values.Reverse().Select(stroke => stroke with { IsUp = true }).ToArray();
        _replay = new(++_nextReplayId, Array.AsReadOnly(releases), IsCleanup: true);
        return new(Replay: _replay);
    }

    private WindowsKeyCaptureDecision Fail(WindowsKeyCaptureFailure failure, bool suppress = false)
    {
        _active = false;
        Phase = WindowsKeyCapturePhase.Faulted;
        return new(Suppress: suppress, Failure: failure);
    }

    private void ResetWhenReleased()
    {
        if (_active && _physicalHeld.Count == 0 && _externalHeld.Count == 0 && _observedHeld.Count == 0
            && _swallowed.Count == 0 && _injectedDown.Count == 0 && _replay is null)
            Phase = WindowsKeyCapturePhase.Idle;
    }

    private static bool IsWindowsKey(int key) => key is LeftWindowsKey or RightWindowsKey;
}
