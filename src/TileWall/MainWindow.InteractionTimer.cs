using System.Runtime.InteropServices;
using TileWall.Core.Shell;

namespace TileWall;

public sealed partial class MainWindow
{
    private readonly bool _disableGestureTimer = Environment.GetEnvironmentVariable("TILEWALL_PERF") == "1" &&
        Environment.GetEnvironmentVariable("TILEWALL_DISABLE_GESTURE_TIMER") == "1";
    private bool _gestureTimerHeld;
    private uint? _gestureTimerBeginResult, _gestureTimerEndResult;

    private void BeginInteractionTimer()
    {
        if (_windowClosing) return;
        if (!DisplayPulseSource.Required || _disableGestureTimer)
        {
            _performance.TimerState(DisplayPulseSource.Required ? "diagnostic-disabled" : "not-required", null, null);
            return;
        }
        if (_gestureTimerHeld)
        {
            _performance.TimerState("held", _gestureTimerBeginResult, _gestureTimerEndResult);
            return;
        }
        _gestureTimerEndResult = null;
        try
        {
            _gestureTimerBeginResult = timeBeginPeriod(1);
            _gestureTimerHeld = _gestureTimerBeginResult == 0;
            if (!_gestureTimerHeld)
                AppLog.Warn("GestureTimer", $"timeBeginPeriod(1) failed: {_gestureTimerBeginResult}; continuing existing scheduler.");
            _performance.TimerState(_gestureTimerHeld ? "held" : "begin-failed", _gestureTimerBeginResult, null);
        }
        catch (Exception ex)
        {
            AppLog.Warn("GestureTimer", $"Timer request unavailable: {ex.GetType().Name}: {ex.Message}");
            _performance.TimerState("begin-error", null, null);
        }
    }

    private void EndInteractionTimer()
    {
        if (!_gestureTimerHeld) return;
        try
        {
            _gestureTimerEndResult = timeEndPeriod(1);
            if (_gestureTimerEndResult == 0) _gestureTimerHeld = false;
            else AppLog.Warn("GestureTimer", $"timeEndPeriod(1) failed: {_gestureTimerEndResult}.");
            _performance.TimerState(_gestureTimerHeld ? "end-failed" : "released", _gestureTimerBeginResult, _gestureTimerEndResult);
        }
        catch (Exception ex)
        {
            AppLog.Warn("GestureTimer", $"Timer release unavailable: {ex.GetType().Name}: {ex.Message}");
            _performance.TimerState("end-error", _gestureTimerBeginResult, null);
        }
        // Keep ownership on an unexpected release failure, so close retries without
        // acquiring another unmatched request. Successful release never runs twice.
    }

    [DllImport("winmm.dll", ExactSpelling = true)] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll", ExactSpelling = true)] private static extern uint timeEndPeriod(uint period);
}
