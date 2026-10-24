namespace TileWall.Core.Shell;

/// <summary>Validated locator identity; changes to Explorer, taskbar, button or discovery generation invalidate the press.</summary>
public readonly record struct StartButtonIdentity(nint TaskbarWindow, int ExplorerProcessId, long DiscoveryGeneration, string ButtonId);
public enum StartButtonInputKind { LeftDown, LeftUp, Move, Other }
public readonly record struct StartButtonPressDecision(bool Suppress = false, bool Toggle = false);

/// <summary>
/// Physical mouse click pairing only. Hit must come from a fresh, independently validated physical-pixel locator
/// snapshot and WindowFromPoint check; null means outside, covered, expired or ambiguous. Own/external injected,
/// touch and pen promoted mouse input must be classified before calling this state machine.
/// </summary>
public sealed class StartButtonPressState
{
    private StartButtonIdentity? _pressed;
    private bool _cancelled;
    private bool _active;
    private bool _bypassUntilUp;

    public bool HasPendingRelease => _pressed is not null;

    public void Activate(bool physicalLeftButtonAlreadyDown = false)
    {
        if (HasPendingRelease || _active) throw new InvalidOperationException("Previous press has not completed.");
        _bypassUntilUp = physicalLeftButtonAlreadyDown;
        _active = true;
    }

    public void Stop()
    {
        _active = false;
        _cancelled = true;
    }

    /// <summary>A desktop switch invalidates a gesture but cannot erase its swallowed down/up pair.</summary>
    public void CancelCurrentPress() => _cancelled = true;

    /// <summary>Use the current locator generation when delivered; a delayed notification must not cancel a newer press.</summary>
    public void InvalidateSnapshot(long currentGeneration)
    {
        if (_pressed is { } pressed && pressed.DiscoveryGeneration != currentGeneration) _cancelled = true;
    }

    public StartButtonPressDecision Handle(StartButtonInputKind kind, CapturedInputSource source, StartButtonIdentity? hit)
    {
        if (kind == StartButtonInputKind.Other) return default;
        if (source != CapturedInputSource.Physical)
        {
            // An injected/promoted up cannot balance the captured physical down.
            if (_pressed is not null) _cancelled = true;
            return default;
        }

        if (kind == StartButtonInputKind.LeftUp)
        {
            _bypassUntilUp = false;
            if (_pressed is null) return default;
            var toggle = _active && !_cancelled && hit == _pressed;
            _pressed = null;
            _cancelled = false;
            return new(Suppress: true, Toggle: toggle);
        }

        if (_pressed is not null)
        {
            if (hit != _pressed) _cancelled = true;
            return new(Suppress: kind == StartButtonInputKind.LeftDown);
        }

        if (kind != StartButtonInputKind.LeftDown || !_active || _bypassUntilUp) return default;
        if (hit is null)
        {
            _bypassUntilUp = true;
            return default;
        }

        _pressed = hit;
        _cancelled = false;
        return new(Suppress: true);
    }

    /// <summary>MS documented touch/pen promoted mouse marker, independent of the native injected flag.</summary>
    public static bool IsPromotedPointer(nuint extraInformation) => ((uint)extraInformation & 0xFFFFFF00u) == 0xFF515700u;
}
