// Adapted from ReStart (MIT), commits 297c8f5 / b9c2718 and their base.
// Copyright (c) 2026 ReStart contributors. See Licenses/ReStartLicense.txt.
using TileWall.ApplicationModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TileWall.Shell.Interop;

/// <summary>Applies temporary window effects only to revalidated Explorer taskbars; never writes auto-hide configuration or injects Explorer code.</summary>
internal sealed class TaskbarActivationBackend(Func<IEnumerable<TaskbarWindowState>>? observe = null,
    Action<nint, uint, nint, nint>? sendMessage = null, Func<nint, bool>? menuActive = null) : ITaskbarLeaseBackend
{
    internal TaskbarBounds Scope { get; set; }
    internal nint LauncherWindow { get; set; }
    internal Func<bool>? StillRequested { get; set; }
    private void RequireRequest()
    {
        if (StillRequested?.Invoke() == false) throw new IOException("The taskbar request was superseded.");
    }
    private string? _appliedToken;
    private string? _releasedToken;
    /// <summary>Gets the last anonymous native recovery decision for explicit local verification.</summary>
    internal string? LastRecoveryDecision { get; private set; }

    internal static bool IsCurrentTaskbar(nint window) => window != 0 && window == FindWindow("Shell_TrayWnd", null)
        && Identity(window, out _, out _);

    internal bool MenuIsActive()
    {
        var window = FindWindow("Shell_TrayWnd", null);
        return window != 0 && (menuActive ?? NativeMenuActive)(window);
    }

    /// <summary>Observes native primary-screen geometry only; no UIA or synchronous Explorer messages.</summary>
    internal static TaskbarActivationObservation ObservePrimary()
    {
        try
        {
            var data = new MonitorData { Size = (uint)Marshal.SizeOf<MonitorData>() };
            if (!GetMonitorInfo(MonitorFromPoint(new Point(), 1), ref data))
                return new(false, false, 0, default, default, default, "The primary display is unavailable.");
            var screen = new TaskbarArea(data.Monitor.Left, data.Monitor.Top,
                data.Monitor.Right - data.Monitor.Left, data.Monitor.Bottom - data.Monitor.Top);
            var work = new TaskbarArea(data.Work.Left, data.Work.Top,
                data.Work.Right - data.Work.Left, data.Work.Bottom - data.Work.Top);
            var window = FindWindow("Shell_TrayWnd", null);
            if (!IsCurrentTaskbar(window) || !Bounds(window, out var original))
                return new(false, false, 0, screen, work, screen, "The primary Explorer taskbar is unavailable.");
            if (!AutoHideEnabled) return new(true, false, window, screen, work, work);
            var monitor = new TaskbarBounds(screen.X, screen.Y, screen.Width, screen.Height);
            if (AutoHideEdge(window, monitor) is not { } edge)
                return new(false, true, window, screen, work, screen, "The automatic taskbar edge could not be identified.");
            if (edge is 0 or 2 ? original.Width >= screen.Width / 2 : original.Height >= screen.Height / 2)
                return new(false, true, window, screen, work, screen, "The automatic taskbar thickness is invalid.");
            var target = VisibleBounds(monitor, original, edge);
            var effective = ExcludeEdge(work, target, edge);
            if (effective.Width <= 0 || effective.Height <= 0)
                return new(false, true, window, screen, work, screen, "The automatic taskbar bounds are invalid.");
            return new(true, true, window, screen, work, effective);
        }
        catch (Exception error) when (TaskbarLease.Expected(error))
        { return new(false, false, 0, default, default, default, error.Message); }
    }

    internal static TaskbarArea ExcludeEdge(TaskbarArea work, TaskbarBounds bar, int edge)
    {
        var left = work.X; var top = work.Y; var right = work.X + work.Width; var bottom = work.Y + work.Height;
        switch (edge)
        {
            case 0: left = Math.Max(left, bar.X + bar.Width); break;
            case 1: top = Math.Max(top, bar.Y + bar.Height); break;
            case 2: right = Math.Min(right, bar.X); break;
            case 3: bottom = Math.Min(bottom, bar.Y); break;
            default: throw new ArgumentOutOfRangeException(nameof(edge));
        }
        return new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
    /// <summary>Reports whether a top-level window precedes another in the current bounded desktop stacking order.</summary>
    /// <param name="window">Existing top-level window expected to be in front.</param>
    /// <param name="other">Existing top-level window expected to be behind it.</param>
    /// <returns>True only when both distinct handles are found and window is encountered first; false for missing or uncertain ordering.</returns>
    internal static bool IsAbove(nint window, nint other)
    {
        if (window == 0 || other == 0 || window == other) return false;
        var first = 0;
        var current = GetTopWindow(0);
        for (var count = 0; current != 0 && count < 4096; count++, current = GetWindow(current, 2))
        {
            if (current == window) { if (first == 2) return false; first = 1; }
            if (current == other) { if (first == 1) return true; first = 2; }
        }
        return false;
    }
    private static readonly int ParentId = Environment.ProcessId;
    private static readonly long ParentStarted = CurrentStarted();
    private static long CurrentStarted() { using var process = Process.GetCurrentProcess(); return process.StartTime.ToUniversalTime().Ticks; }

    public TaskbarLeaseState? Capture(TaskbarLeaseMode mode)
    {
        var windows = new List<TaskbarWindowState>();
        foreach (var item in (observe ?? ObserveWindows)())
        {
            // This preference never overrides a fixed taskbar or creates a new
            // hide operation. Hide remains readable for recovery of old leases.
            if (mode != TaskbarLeaseMode.Reveal || !item.AutoHide || item.Monitor != Scope) continue;
            windows.Add(item);
        }
        return windows.Count == 0 ? null : new(windows.Any(item => item.HoldAutoHide) ? 2 : 1,
            Guid.NewGuid().ToString("N"), ParentId, ParentStarted, mode, windows.ToArray());
    }

    private static IEnumerable<TaskbarWindowState> ObserveWindows()
    {
        if (!AutoHideEnabled) yield break;
        var monitors = MonitorBounds();
        if (monitors.Length == 0) yield break;
        foreach (var window in Taskbars())
        {
            if (!Identity(window, out var pid, out var started) || !Bounds(window, out var original) || !Monitor(window, out var monitor)) continue;
            // An auto-hidden rectangle can sit mostly on a neighboring display.
            // Its appbar registration identifies the owner more reliably than
            // MonitorFromWindow's geometric overlap in that state.
            var resolved = ResolveMonitor(monitor, monitors, bounds => AutoHideEdge(window, bounds));
            monitor = resolved.Monitor; var edge = resolved.Edge;
            var target = edge == null ? original : VisibleBounds(monitor, original, edge.Value);
            yield return new(window, pid, started, IsWindowVisible(window), edge != null, monitor, original, target, IsTopmost(window), ContentWindows(window, pid), TaskbarNativeRegion.Read(window),
                HoldAutoHide: window == FindWindow("Shell_TrayWnd", null));
        }
    }

    /// <summary>Prefers appbar ownership over a hidden window's overlap with a neighboring display.</summary>
    /// <param name="geometric">Monitor selected by the current window rectangle, used only for an unregistered fixed taskbar.</param>
    /// <param name="monitors">Current bounded physical monitor inventory.</param>
    /// <param name="registeredEdge">Read-only monitor registration lookup returning the edge, or null when this taskbar is not registered there.</param>
    /// <returns>The registered owner and edge, or the geometric monitor and null for a fixed taskbar.</returns>
    internal static (TaskbarBounds Monitor, int? Edge) ResolveMonitor(TaskbarBounds geometric, IEnumerable<TaskbarBounds> monitors, Func<TaskbarBounds, int?> registeredEdge)
    {
        foreach (var monitor in monitors)
            if (registeredEdge(monitor) is { } edge) return (monitor, edge);
        return (geometric, null);
    }

    private static TaskbarBounds[] MonitorBounds()
    {
        var result = new List<TaskbarBounds>();
        bool Add(nint monitor, nint hdc, ref Rect rect, nint data)
        {
            if (rect.Right > rect.Left && rect.Bottom > rect.Top) result.Add(new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
            return result.Count < 32;
        }
        return EnumDisplayMonitors(0, 0, Add, 0) ? result.ToArray() : [];
    }

    public bool IsCurrent(TaskbarLeaseState state)
    {
        var current = Capture(state.Mode);
        return current != null && current.Windows.Length == state.Windows.Length
            && state.Windows.All(saved => current.Windows.Any(observed => observed.Window == saved.Window && observed.ProcessId == saved.ProcessId
                && observed.ProcessStarted == saved.ProcessStarted && observed.Monitor == saved.Monitor && observed.AutoHide == saved.AutoHide
                && (observed.Content ?? []).Select(child => child.Window).Order().SequenceEqual((saved.Content ?? []).Select(child => child.Window).Order())
                && ClipCurrent(saved, observed)
                && (state.Mode != TaskbarLeaseMode.Reveal || observed.Target == saved.Target)));
    }

    private static bool ClipCurrent(TaskbarWindowState saved, TaskbarWindowState current) => saved.Clip == null
        || TaskbarNativeRegion.Same(saved.Clip, current.Clip)
        || SecondaryEdge(saved.Monitor, saved.Target) is { } edge
            && TaskbarNativeRegion.AutoHideStrip(saved.Clip, saved.Target, edge)
            && (current.Clip is { HasRegion: false }
                || saved.HoldAutoHide && current.HoldAutoHide && TaskbarNativeRegion.FullSurface(current.Clip, current.Target));

    public void Apply(TaskbarLeaseState state, Action armRecovery)
    {
        RequireRequest();
        var firstReveal = _appliedToken != state.Token;
        foreach (var item in state.Windows)
        {
            if (!Matches(item)) throw new IOException("Taskbar identity changed.");
            var window = (nint)item.Window;
            if (state.Mode == TaskbarLeaseMode.Hide) throw new InvalidOperationException("New taskbar hide leases are not supported.");
            else
            {
                if (!item.AutoHide || !AutoHideEnabled || AutoHideEdge(window, item.Monitor) is not { } edge) throw new IOException("Taskbar preference changed.");
                if (LauncherWindow == 0) throw new IOException("Taskbar reveal requires a launcher window.");
                if (item.HoldAutoHide)
                {
                    _releasedToken = null;
                    // Moving the HWND alone leaves Explorer's hidden state intact.
                    // Ask it to unhide first. The minimized notification flag
                    // suppresses DefWindowProc's focus transfer; no window is minimized.
                    HoldTimer(window, firstReveal || Bounds(window, out var observed) && HiddenAtEdge(item.Monitor, observed, edge), armRecovery);
                    WaitForNativeReveal(item, edge);
                }
                else armRecovery();
                var changed = false;
                if (TaskbarNativeRegion.AutoHideStrip(item.Clip, item.Target, edge))
                {
                    var currentClip = TaskbarNativeRegion.Read(window);
                    if (TaskbarNativeRegion.AutoHideStrip(currentClip, item.Target, edge))
                    {
                        // A secondary tray can retain its two-pixel region even
                        // after its HWND and content are visible. Persisted
                        // recovery precedes this scoped, reversible correction.
                        RequireRequest();
                        TaskbarNativeRegion.Write(window, new(false, []));
                        changed = true;
                    }
                    else if (!TaskbarNativeRegion.FullSurface(currentClip, item.Target)) throw new IOException("Taskbar clipping changed.");
                }
                if (firstReveal && !item.HoldAutoHide || changed || !Bounds(window, out var current) || current != item.Target || !IsWindowVisible(window) || !IsAbove(window, LauncherWindow))
                {
                    RequireRequest();
                    Position(window, item.Target, show: true, topmost: true);
                    if (item.HoldAutoHide) HoldTimer(window, reveal: false, armRecovery);
                    changed = true;
                }
                // On Windows 11 the tray HWND can be visible and correctly
                // positioned while its composition content is still hidden.
                // Recoverable ownership of each exact child was saved before
                // this point; only direct children of this Explorer tray qualify.
                foreach (var child in item.Content ?? [])
                {
                    if (!MatchesContent(item, child)) throw new IOException("Taskbar content identity changed.");
                    if (!IsWindowVisible((nint)child.Window)) { RequireRequest(); ShowWindow((nint)child.Window, 8); changed = true; }
                    if (!IsWindowVisible((nint)child.Window)) throw new IOException("Taskbar content reveal failed.");
                }
                if (!Bounds(window, out var applied) || applied != item.Target) throw new IOException("Taskbar reveal geometry was not applied.");
                if (!IsWindowVisible(window)) throw new IOException("Taskbar reveal visibility was not applied.");
                if (!IsAbove(window, LauncherWindow)) throw new IOException("Taskbar reveal stacking was not applied.");
                if (changed) RedrawWindow(window, 0, 0, 0x0585); // INVALIDATE | ERASE | ALLCHILDREN | UPDATENOW | FRAME.
                if (TaskbarNativeRegion.AutoHideStrip(item.Clip, item.Target, edge) && !TaskbarNativeRegion.FullSurface(TaskbarNativeRegion.Read(window), item.Target))
                    throw new IOException("Taskbar content remains clipped.");
            }
        }
        _appliedToken = state.Token;
    }

    private void WaitForNativeReveal(TaskbarWindowState item, int edge)
    {
        // Explorer owns more than its HWND rectangle: its hidden flag, clipping
        // and auto-hide timer must transition together. Installing a visible
        // rectangle or removing the hidden strip before its native transition
        // completes can leave a visible tray that Explorer still considers hidden.
        var window = (nint)item.Window;
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            RequireRequest();
            RequireMenuIdle(window);
            if (!Matches(item) || !AutoHideEnabled || AutoHideEdge(window, item.Monitor) != edge)
                throw new IOException("Taskbar identity or preference changed during native reveal.");
            if (Bounds(window, out var current) && current == item.Target && IsWindowVisible(window)
                && TaskbarNativeRegion.FullSurface(TaskbarNativeRegion.Read(window), current)) return;
            if (deadline.Elapsed >= TimeSpan.FromSeconds(2))
                throw new IOException("Explorer did not complete the native taskbar reveal.");
            Thread.Sleep(20);
        }
    }

    public void Restore(TaskbarLeaseState state)
    {
        foreach (var item in state.Windows)
        {
            if (!Matches(item)) continue; // Dead Explorer / reused HWND is not owned.
            var window = (nint)item.Window;
            if (item.HoldAutoHide && window == FindWindow("Shell_TrayWnd", null) && _releasedToken != state.Token)
            {
                ReleaseTimer(window, TaskbarInteraction(window, item.Target, item.Monitor), GetForegroundWindow());
                _releasedToken = state.Token;
            }
            if (state.Mode == TaskbarLeaseMode.Reveal && item.HoldAutoHide && window == FindWindow("Shell_TrayWnd", null))
            {
                // A foreign WINDOWPOSCHANGING can restore Explorer's docked
                // visible rectangle even after its own auto-hide has completed.
                // Release our timer and tier only; never reposition this tray.
                if (RestoreTopmost(item.Topmost, IsTopmost(window)) == false) ReturnToOrdinaryTier(window);
                // Explorer may legitimately retain it for another application's
                // attention request or other native policy. A confirmed release
                // returns that decision to Shell; full geometry is not a failure.
                LastRecoveryDecision = "native-policy";
                RestoreContent(item);
                RestoreClip(item);
                continue;
            }
            if (state.Mode == TaskbarLeaseMode.Hide)
            {
                if (item.Visible && !IsWindowVisible(window)) ShowWindow(window, 8);
                if (item.Visible && !IsWindowVisible(window)) throw new IOException("Taskbar visibility recovery failed.");
            }
            else if (Bounds(window, out var current))
            {
                // Never replay a captured visible/hidden rectangle. Explorer
                // must receive WINDOWPOSCHANGING and reconcile its *current*
                // preference, monitor and auto-hide state, including after a
                // setting change, a display change or recovery of an old lease.
                Handoff(window, current, RestoreTopmost(item.Topmost, IsTopmost(window)));
                LastRecoveryDecision = ReturnOwnedTrayToHiddenEdge(item);
                RestoreContent(item);
                RestoreClip(item);
            }
        }
    }

    private static void ReturnToOrdinaryTier(nint window)
    {
        if (!SetWindowPos(window, new nint(-2), 0, 0, 0, 0, 0x0013)) // NOACTIVATE | NOMOVE | NOSIZE
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    internal void HoldTimer(nint window, bool reveal, Action armRecovery)
    {
        RequireMenuIdle(window);
        RequireRequest();
        armRecovery();
        if (reveal) TrayMessage(window, 0x0006, 0x10001, LauncherWindow);
        RequireMenuIdle(window);
        RequireRequest();
        TrayMessage(window, 0x0211, 1, 0);
    }

    internal void ReleaseTimer(nint window, bool interacting, nint foreground)
    {
        RequireMenuIdle(window);
        if (!interacting)
        {
            TrayMessage(window, 0x0006, 0, foreground);
            RequireMenuIdle(window);
        }
        TrayMessage(window, 0x0212, 1, 0);
    }

    private void RequireMenuIdle(nint window)
    {
        if ((menuActive ?? NativeMenuActive)(window)) throw new TaskbarMenuBusyException();
    }

    private static bool NativeMenuActive(nint window)
    {
        var thread = GetWindowThreadProcessId(window, out _);
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (thread == 0 || !GetGUIThreadInfo(thread, ref info)) throw new IOException("Explorer menu state is unavailable.");
        return (info.Flags & 0x1c) != 0; // GUI_INMENUMODE | GUI_SYSTEMMENUMODE | GUI_POPUPMENUMODE
    }

    private void TrayMessage(nint window, uint message, nint wParam, nint lParam)
        => (sendMessage ?? SendTrayMessage)(window, message, wParam, lParam);

    private static void SendTrayMessage(nint window, uint message, nint wParam, nint lParam)
    {
        if (SendMessageTimeout(window, message, wParam, lParam, 0x0022, 250, out _) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 0) throw new IOException("Explorer did not acknowledge the taskbar lifecycle message.");
            throw new System.ComponentModel.Win32Exception(error);
        }
    }

    /// <summary>Reverts only an owned promotion from the ordinary window tier; old records and external demotion retain their current tier.</summary>
    /// <param name="original">Captured tier, or null for a recovery record created before tier capture was supported.</param>
    /// <param name="current">Current topmost tier after identity and geometry validation.</param>
    /// <returns>False to undo an owned promotion, or null to leave the current tier unchanged.</returns>
    internal static bool? RestoreTopmost(bool? original, bool current) => original == false && current ? false : null;

    private static bool IsTopmost(nint window) => (GetWindowLongPtr(window, -20).ToInt64() & 8) != 0;

    /// <summary>Reads Windows' current global automatic-hiding preference without changing it.</summary>
    internal static bool AutoHideEnabled
    {
        get
        {
            var query = new AppBarData { Size = (uint)Marshal.SizeOf<AppBarData>() };
            return (SHAppBarMessage(4, ref query) & 1) != 0;
        }
    }

    private static IEnumerable<nint> Taskbars()
    {
        var primary = FindWindow("Shell_TrayWnd", null);
        if (primary != 0) yield return primary;
    }
    private static bool Identity(nint window, out int pid, out long started)
    {
        pid = 0; started = 0;
        var name = new StringBuilder(64); GetClassName(window, name, name.Capacity);
        if (name.ToString() is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd")) return false;
        GetWindowThreadProcessId(window, out var id);
        try
        {
            using var process = Process.GetProcessById((int)id);
            using var self = Process.GetCurrentProcess();
            if (!process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) || process.SessionId != self.SessionId
                || !string.Equals(process.MainModule?.FileName, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase)) return false;
            pid = (int)id; started = process.StartTime.ToUniversalTime().Ticks;
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    private static bool Matches(TaskbarWindowState state) => Identity((nint)state.Window, out var pid, out var started)
        && pid == state.ProcessId && started == state.ProcessStarted;

    private static TaskbarContentState[] ContentWindows(nint parent, int pid)
    {
        var children = new List<TaskbarContentState>();
        nint previous = 0;
        for (var count = 0; count < 4; count++)
        {
            var child = FindWindowEx(parent, previous, "Windows.UI.Composition.DesktopWindowContentBridge", null);
            if (child == 0) break;
            GetWindowThreadProcessId(child, out var owner);
            if (owner == pid) children.Add(new(child, IsWindowVisible(child)));
            previous = child;
        }
        return children.ToArray();
    }

    private static bool MatchesContent(TaskbarWindowState parent, TaskbarContentState child)
    {
        if (!Matches(parent) || GetParent((nint)child.Window) != (nint)parent.Window) return false;
        var name = new StringBuilder(128);
        GetClassName((nint)child.Window, name, name.Capacity);
        GetWindowThreadProcessId((nint)child.Window, out var pid);
        return pid == parent.ProcessId && name.ToString() == "Windows.UI.Composition.DesktopWindowContentBridge";
    }

    private static void RestoreContent(TaskbarWindowState item)
    {
        // Do not hide content if Windows now wants a visible/fixed taskbar, or
        // while a pointer-triggered reveal/animation is still on screen. Native
        // Shell transitions keep control in those cases. This restores only our
        // temporary child reveal after the tray has actually returned offscreen.
        if (!AutoHideEnabled || !Bounds((nint)item.Window, out var current) || !Monitor((nint)item.Window, out var geometric)) return;
        var owner = ResolveMonitor(geometric, MonitorBounds(), monitor => AutoHideEdge((nint)item.Window, monitor));
        if (owner.Edge is not { } edge || !HiddenAtEdge(owner.Monitor, current, edge)) return;
        foreach (var child in item.Content ?? [])
        {
            if (child.Visible || !MatchesContent(item, child)) continue;
            if (IsWindowVisible((nint)child.Window)) ShowWindow((nint)child.Window, 0);
            if (IsWindowVisible((nint)child.Window)) throw new IOException("Taskbar content recovery failed.");
        }
    }

    private static void RestoreClip(TaskbarWindowState item)
    {
        if (!AutoHideEnabled || !Matches(item) || !Bounds((nint)item.Window, out var current)
            || AutoHideEdge((nint)item.Window, item.Monitor) is not { } edge || !HiddenAtEdge(item.Monitor, current, edge)
            || !TaskbarNativeRegion.AutoHideStrip(item.Clip, item.Target, edge)) return;
        // Native transitions, a new region, a visible tray or a changed Windows
        // preference take precedence. Undo only our removal of this exact strip.
        if (TaskbarNativeRegion.Read((nint)item.Window).HasRegion) return;
        TaskbarNativeRegion.Write((nint)item.Window, item.Clip!);
        if (!TaskbarNativeRegion.Same(TaskbarNativeRegion.Read((nint)item.Window), item.Clip)) throw new IOException("Taskbar clipping recovery failed.");
    }

    private string ReturnOwnedTrayToHiddenEdge(TaskbarWindowState item)
    {
        // Some Explorer versions retain the exact visible rectangle installed
        // by reveal after their menu timer is released. Undo only that owned
        // rectangle, using today's registered monitor and edge. The normal
        // WINDOWPOSCHANGING remains enabled so Explorer retains the final say.
        if (!AutoHideEnabled) return "windows-preference";
        var window = (nint)item.Window;
        if (!Matches(item) || !Bounds(window, out var current)) return "identity-changed";
        if (current != item.Target) return "native-position";
        if (!MonitorBounds().Contains(item.Monitor) || AutoHideEdge(window, item.Monitor) is not { } edge)
            return "registration-changed";
        RequireMenuIdle(window);
        if (TaskbarInteraction(window, current, item.Monitor)) return "taskbar-interaction";
        Handoff(window, HiddenBounds(item.Monitor, current, edge), null);
        if (!Bounds(window, out var after)) throw new IOException("Taskbar recovery geometry is unavailable.");
        if (HiddenAtEdge(item.Monitor, after, edge)) return "edge-restored";
        // Do not checkpoint recovery while our unchanged reveal is still on
        // screen. Keep the durable obligation available to the crash helper.
        if (after == item.Target) throw new IOException("Explorer retained the temporary taskbar reveal.");
        return "native-position";
    }

    private static bool TaskbarInteraction(nint taskbar, TaskbarBounds target, TaskbarBounds monitor)
    {
        bool Related(nint window)
        {
            var root = GetAncestor(window, 2);
            if (root == taskbar) return true;
            var name = new StringBuilder(128); GetClassName(root, name, name.Capacity);
            return name.ToString() is "TaskListThumbnailWnd" or "NotifyIconOverflowWindow" or "XamlExplorerHostIslandWindow"
                && Monitor(root, out var owner) && owner == monitor;
        }
        if (GetCursorPos(out var point) && (point.X >= target.X && point.X < target.X + target.Width
            && point.Y >= target.Y && point.Y < target.Y + target.Height || Related(WindowFromPoint(point)))) return true;
        return Related(GetForegroundWindow());
    }

    /// <summary>Computes the current monitor's hidden edge position without reusing captured coordinates.</summary>
    /// <param name="monitor">Current registered display in physical pixels.</param>
    /// <param name="bar">Fresh tray dimensions; the long axis is fitted to the current monitor.</param>
    /// <param name="edge">Registered edge: left 0, top 1, right 2 or bottom 3.</param>
    /// <returns>A rectangle leaving only the two-pixel Windows reveal strip on the registered edge.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The edge is not a supported appbar edge.</exception>
    internal static TaskbarBounds HiddenBounds(TaskbarBounds monitor, TaskbarBounds bar, int edge) => edge switch
    {
        0 => new(monitor.X - bar.Width + 2, monitor.Y, bar.Width, monitor.Height),
        1 => new(monitor.X, monitor.Y - bar.Height + 2, monitor.Width, bar.Height),
        2 => new(monitor.X + monitor.Width - 2, monitor.Y, bar.Width, monitor.Height),
        3 => new(monitor.X, monitor.Y + monitor.Height - 2, monitor.Width, bar.Height),
        _ => throw new ArgumentOutOfRangeException(nameof(edge))
    };

    /// <summary>Recognizes an auto-hidden taskbar with at most its two-pixel reveal strip inside the registered display.</summary>
    /// <param name="monitor">Current registered monitor in physical pixels, including negative desktop coordinates.</param>
    /// <param name="bar">Fresh tray rectangle after Explorer has reconciled its own position.</param>
    /// <param name="edge">Registered appbar edge: left 0, top 1, right 2 or bottom 3.</param>
    /// <returns>True only for a valid edge whose visible thickness is at most two physical pixels; false for a visible or partially revealing taskbar.</returns>
    internal static bool HiddenAtEdge(TaskbarBounds monitor, TaskbarBounds bar, int edge) => edge switch
    {
        0 => bar.X < monitor.X && bar.X + bar.Width <= monitor.X + 2,
        1 => bar.Y < monitor.Y && bar.Y + bar.Height <= monitor.Y + 2,
        2 => bar.X >= monitor.X + monitor.Width - 2,
        3 => bar.Y >= monitor.Y + monitor.Height - 2,
        _ => false
    };
    private static bool Bounds(nint window, out TaskbarBounds bounds)
    {
        var success = GetWindowRect(window, out var rect);
        bounds = new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return success && bounds.Width > 0 && bounds.Height > 0;
    }
    private static bool Monitor(nint window, out TaskbarBounds bounds)
    {
        var data = new MonitorData { Size = (uint)Marshal.SizeOf<MonitorData>() };
        var success = GetMonitorInfo(MonitorFromWindow(window, 2), ref data);
        var rect = data.Monitor; bounds = new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return success;
    }
    private static int? AutoHideEdge(nint window, TaskbarBounds monitor)
    {
        for (uint edge = 0; edge < 4; edge++)
        {
            var query = new AppBarData
            {
                Size = (uint)Marshal.SizeOf<AppBarData>(),
                Edge = edge,
                Rect = new() { Left = monitor.X, Top = monitor.Y, Right = monitor.X + monitor.Width, Bottom = monitor.Y + monitor.Height }
            };
            if ((nint)SHAppBarMessage(11, ref query) == window) return (int)edge;
        }
        // Some secondary Explorer trays do not appear in the appbar registry,
        // despite Windows' global auto-hide preference being enabled. Accept
        // only their exact visible/hidden edge geometry, never an arbitrary
        // nearby window or a partially animated rectangle.
        var name = new StringBuilder(64); GetClassName(window, name, name.Capacity);
        return AutoHideEnabled && name.ToString() == "Shell_SecondaryTrayWnd" && Bounds(window, out var bar)
            ? SecondaryEdge(monitor, bar) : null;
    }

    /// <summary>Resolves an unregistered secondary tray only at an exact full-length visible or two-pixel hidden edge.</summary>
    /// <param name="monitor">Candidate monitor in physical desktop pixels.</param>
    /// <param name="bar">Fresh secondary Explorer tray rectangle in physical desktop pixels.</param>
    /// <returns>The unique docking edge, or null for partial movement, ambiguous or unrelated geometry.</returns>
    internal static int? SecondaryEdge(TaskbarBounds monitor, TaskbarBounds bar)
    {
        int? result = null;
        for (var edge = 0; edge < 4; edge++)
        {
            var horizontal = edge is 1 or 3;
            if (horizontal ? bar.Width != monitor.Width || bar.Height >= monitor.Height / 2
                : bar.Height != monitor.Height || bar.Width >= monitor.Width / 2) continue;
            if (bar != VisibleBounds(monitor, bar, edge) && bar != HiddenBounds(monitor, bar, edge)) continue;
            if (result != null) return null;
            result = edge;
        }
        return result;
    }
    private static TaskbarBounds VisibleBounds(TaskbarBounds monitor, TaskbarBounds bar, int edge) => edge switch
    {
        0 => new(monitor.X, monitor.Y, bar.Width, monitor.Height),
        1 => new(monitor.X, monitor.Y, monitor.Width, bar.Height),
        2 => new(monitor.X + monitor.Width - bar.Width, monitor.Y, bar.Width, monitor.Height),
        _ => new(monitor.X, monitor.Y + monitor.Height - bar.Height, monitor.Width, bar.Height)
    };
    /// <summary>Applies a temporary reveal without activation or position coercion; release must use Handoff instead.</summary>
    /// <param name="window">Revalidated taskbar HWND or a native verification window owned by this process.</param>
    /// <param name="bounds">Exact visible physical screen rectangle for this active lease.</param>
    /// <param name="show">Whether to show without activating; false retains current visibility.</param>
    /// <param name="topmost">True raises above the launcher, false returns to the ordinary tier; null retains stacking order.</param>
    internal static void Position(nint window, TaskbarBounds bounds, bool show, bool? topmost = null)
    {
        // Suppress WINDOWPOSCHANGING only during reveal so Explorer cannot coerce the requested
        // visible rectangle back to its auto-hidden edge during this call.
        var after = topmost.HasValue ? new nint(topmost.Value ? -1 : -2) : 0;
        var flags = (uint)(0x0430 | (topmost.HasValue ? 0 : 0x0004) | (show ? 0x0040 : 0));
        if (!SetWindowPos(window, after, bounds.X, bounds.Y, bounds.Width, bounds.Height, flags))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>Returns position decisions to the owning window procedure without replaying saved geometry or changing visibility.</summary>
    /// <param name="window">Revalidated Explorer taskbar, or an owned native test window.</param>
    /// <param name="current">Fresh physical screen rectangle read immediately before this call; never a saved recovery rectangle.</param>
    /// <param name="topmost">False only to undo an owned promotion; null preserves the current tier.</param>
    internal static void Handoff(nint window, TaskbarBounds current, bool? topmost)
    {
        var after = topmost.HasValue ? new nint(topmost.Value ? -1 : -2) : 0;
        var flags = (uint)(0x0010 | (topmost.HasValue ? 0 : 0x0004));
        if (!SetWindowPos(window, after, current.X, current.Y, current.Width, current.Height, flags))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>Opts an owned launcher out of Shell full-screen occlusion before it is shown; does not change any Explorer window or preference.</summary>
    /// <param name="window">Launcher HWND owned by this process.</param>
    /// <param name="showTaskbar">True opts out of automatic full-screen classification; false removes the owned property.</param>
    internal static void PrepareWall(nint window, bool showTaskbar)
    {
        // Documented by ITaskbarList2::MarkFullscreenWindow. Without this,
        // Explorer can lower its taskbar after our full-screen window opens.
        if (showTaskbar)
        {
            if (!SetProp(window, "NonRudeHWND", new nint(1)))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        else RemoveProp(window, "NonRudeHWND");
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorData { public uint Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct AppBarData { public uint Size; public nint Window; public uint Callback, Edge; public Rect Rect; public nint Parameter; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string? className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    private delegate bool MonitorCallback(nint monitor, nint hdc, ref Rect rect, nint data);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint parent);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretBounds;
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)] private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool RedrawWindow(nint window, nint rect, nint region, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorData data);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("shell32.dll")] private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetProp(nint window, string name, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint window, string name);
}
