using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TileWall.Core.Shell;

namespace TileWall.Shell.Interop;

internal readonly record struct PhysicalRect(int Left, int Top, int Right, int Bottom)
{
    public bool IsValid => Right > Left && Bottom > Top;
    public bool Contains(int x, int y) => IsValid && x >= Left && x < Right && y >= Top && y < Bottom;
    public bool Contains(PhysicalRect other) => IsValid && other.IsValid && other.Left >= Left && other.Top >= Top
        && other.Right <= Right && other.Bottom <= Bottom;
}

internal sealed record StartButtonTarget(StartButtonIdentity Identity, string AutomationId, string ClassName,
    nint NativeButtonWindow, PhysicalRect ButtonBounds, PhysicalRect TaskbarBounds, bool NativeWholeButton);

internal sealed record StartButtonSnapshot(long Generation, long ReadTick, int DiscoveredTaskbarCount,
    int RecognizedTaskbarCount, IReadOnlyList<StartButtonTarget> Targets)
{
    public bool IsReady => DiscoveredTaskbarCount > 0 && RecognizedTaskbarCount == DiscoveredTaskbarCount;
}

internal enum StartButtonLocatorFailure { AutomationUnavailable, TaskbarUnavailable, ButtonUnrecognized, AmbiguousButton, NativeQueryFailed, TaskbarTopologyChanged }
internal sealed class StartButtonLocatorException : Exception
{
    public StartButtonLocatorException(StartButtonLocatorFailure code, int nativeResult = 0, string? context = null)
        : base($"Start button discovery failed: {code}.{(context is null ? string.Empty : " " + context)}")
    {
        Code = code;
        if (nativeResult < 0) HResult = nativeResult;
    }
    public StartButtonLocatorFailure Code { get; }
}

/// <summary>
/// Owns one windowless MTA and its native UIA objects. Events contain immutable values, never UIA pointers;
/// subscribers marshal to their own thread and reject notifications whose generation is no longer current.
/// Read-only hit validation never invokes UIA or waits for the worker. Instances are one-shot; prepare a new locator after Stop.
/// </summary>
internal sealed class StartButtonLocator : IAsyncDisposable
{
    public const int RefreshIntervalMilliseconds = 500;
    public const int SnapshotLifetimeMilliseconds = 1500;
    private readonly object _lifecycle = new();
    private readonly AutoResetEvent _refresh = new(false);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private StartButtonSnapshot _snapshot = new(0, 0, 0, 0, Array.Empty<StartButtonTarget>());
    private Thread? _thread;
    private long _epoch;
    private long _generation;
    private long _lastRefreshRequestTick = Environment.TickCount64 - RefreshIntervalMilliseconds;
    private int _stopping;
    private bool _disposed;
    private CancellationTokenRegistration _startupCancellation;

    public StartButtonSnapshot CurrentSnapshot => Volatile.Read(ref _snapshot);
    public event Action<StartButtonSnapshot>? SnapshotChanged;
    public event Action<StartButtonLocatorException>? Faulted;

    /// <summary>Completes only after all current Explorer taskbars have exactly one recognized button (hidden is allowed).</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Volatile.Read(ref _stopping) != 0) throw new InvalidOperationException("The locator has stopped.");
            if (_thread is not null) return _ready.Task;
            cancellationToken.ThrowIfCancellationRequested();
            _startupCancellation = cancellationToken.Register(RequestStop);
            _thread = new Thread(Run) { IsBackground = true, Name = "TileWall Start button UIA" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
            return _ready.Task;
        }
    }

    public void RequestRefresh()
    {
        // Hook-safe signal only; no monitor/COM/worker wait. Dispose may race the signal after stopping.
        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref _lastRefreshRequestTick);
        if (now - previous < RefreshIntervalMilliseconds
            || Interlocked.CompareExchange(ref _lastRefreshRequestTick, now, previous) != previous) return;
        if (!Volatile.Read(ref _disposed) && Volatile.Read(ref _stopping) == 0)
        {
            try { _refresh.Set(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Call on display/taskbar geometry changes or Explorer replacement. In-flight UIA results cannot republish.</summary>
    public void Invalidate()
    {
        StartButtonSnapshot empty;
        lock (_lifecycle)
        {
            if (_disposed) return;
            Interlocked.Increment(ref _epoch);
            empty = new(++_generation, Environment.TickCount64, 0, 0, Array.Empty<StartButtonTarget>());
            Volatile.Write(ref _snapshot, empty);
            if (Volatile.Read(ref _stopping) == 0) _refresh.Set();
        }

        SnapshotChanged?.Invoke(empty);
    }

    /// <summary>Known target only: rejects stale, covered, moved, hidden, ambiguous or replaced taskbars and requests refresh.</summary>
    public bool TryHitTest(int physicalX, int physicalY, out StartButtonTarget? target)
    {
        target = null;
        var snapshot = CurrentSnapshot;
        var age = Environment.TickCount64 - snapshot.ReadTick;
        if (Volatile.Read(ref _stopping) != 0 || !snapshot.IsReady || age < 0 || age > SnapshotLifetimeMilliseconds)
        {
            RequestRefresh();
            return false;
        }

        foreach (var candidate in snapshot.Targets)
        {
            if (!candidate.ButtonBounds.Contains(physicalX, physicalY)) continue;
            using var dpi = new Native.PhysicalDpiScope();
            if (!dpi.IsSet) return false;
            if (target is not null || !Native.ValidateHit(candidate, physicalX, physicalY))
            {
                target = null;
                RequestRefresh();
                return false;
            }

            target = candidate;
        }

        // Invalidation/stop racing native checks may not deliver an old generation.
        if (snapshot.Generation != CurrentSnapshot.Generation || Volatile.Read(ref _stopping) != 0) target = null;
        return target is not null;
    }

    public Task StopAsync()
    {
        RequestStop();
        lock (_lifecycle)
        {
            if (_thread is null)
            {
                _ready.TrySetCanceled();
                _finished.TrySetResult();
            }
        }

        return _finished.Task;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            _refresh.Dispose();
        }

        _startupCancellation.Dispose();
    }

    private void RequestStop()
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 0) Invalidate();
        lock (_lifecycle) { if (!_disposed) _refresh.Set(); }
    }

    private void Run()
    {
        try
        {
            using var dpi = new Native.PhysicalDpiScope();
            if (!dpi.IsSet) throw new StartButtonLocatorException(StartButtonLocatorFailure.NativeQueryFailed);
            using var automation = new StartButtonAutomationInterop.Session();
            var startupTimeoutRetried = false;
            while (Volatile.Read(ref _stopping) == 0)
            {
                try
                {
                    var epoch = Interlocked.Read(ref _epoch);
                    // The oldest observation sets freshness; a slow COM pass cannot make old geometry fresh at publication.
                    var readTick = Environment.TickCount64;
                    var taskbars = Native.FindExplorerTaskbars();
                    ValidateTaskbarContinuity(taskbars);
                    var candidates = new List<Candidate>();
                    foreach (var taskbar in taskbars)
                    {
                        bool Cancelled() => Volatile.Read(ref _stopping) != 0 || Interlocked.Read(ref _epoch) != epoch;
                        if (Cancelled()) throw new OperationCanceledException();
                        IReadOnlyList<StartButtonAutomationInterop.ButtonData> buttons;
                        var previous = _previousCandidates.FirstOrDefault(candidate => candidate.Window == taskbar.Window
                            && candidate.ProcessId == taskbar.ProcessId);
                        var nativeRefresh = previous?.NativeWholeButton == true;
                        try
                        {
                            if (nativeRefresh)
                            {
                                if (!Native.TryRefreshStartChild(taskbar, previous!.Button, out var refreshed))
                                {
                                    // An observed identity mismatch permanently discards the cached UIA seed.
                                    // The next observation must recognize all taskbars again before publishing.
                                    _previousCandidates = [];
                                    throw new OperationCanceledException();
                                }
                                buttons = [refreshed];
                            }
                            else buttons = automation.ReadButtons(taskbar.Window, taskbar.ProcessId, Cancelled);
                            if (buttons.Count == 0)
                            {
                                // A single empty UIA observation is not proof that Explorer no longer has
                                // a Start button. Confirm once, without publishing partial/guessed targets.
                                // The existing snapshot still has its original expiry and native hit guards.
                                _refresh.WaitOne(100);
                                if (Cancelled()) throw new OperationCanceledException();
                                buttons = automation.ReadButtons(taskbar.Window, taskbar.ProcessId, Cancelled);
                            }
                        }
                        catch (StartButtonLocatorException error)
                        {
                            throw new StartButtonLocatorException(error.Code, error.HResult,
                                $"Taskbar 0x{taskbar.Window:X}: {error.Message}");
                        }
                        if (!Native.TryGetTaskbar(taskbar.Window, taskbar.ProcessId, out var current)
                            || current.Bounds != taskbar.Bounds) throw new OperationCanceledException();
                        if (buttons.Count == 0) throw new StartButtonLocatorException(StartButtonLocatorFailure.ButtonUnrecognized,
                            context: $"Taskbar 0x{taskbar.Window:X}; UIA buttons={automation.LastReadElementCount}; bounds={current.Bounds}.");
                        if (buttons.Count != 1) throw new StartButtonLocatorException(StartButtonLocatorFailure.AmbiguousButton);
                        var button = buttons[0];
                        // Recognize a hidden auto-hide button, but never turn its remaining screen-edge pixels into a target.
                        var canCapture = button.IsEnabled && !button.IsOffscreen && taskbar.Bounds.Contains(button.Bounds)
                            && Native.IsFullyOnMonitor(taskbar.Window, taskbar.Bounds) && Native.IsWindowVisible(taskbar.Window);
                        // Only a UIA-confirmed whole, unique native Start child can bypass later COM scans.
                        // Windowless controls and HWNDs whose UIA element covers only a subregion stay on UIA.
                        var nativeWholeButton = nativeRefresh || Native.TryRefreshStartChild(taskbar, button, out var nativeButton)
                            && nativeButton.Bounds == button.Bounds;
                        candidates.Add(new(taskbar.Window, taskbar.ProcessId, button, taskbar.Bounds, canCapture, nativeWholeButton));
                    }

                    Publish(epoch, readTick, taskbars.Count, candidates);
                }
                catch (StartButtonLocatorException error) when (error.HResult == unchecked((int)0x80131505)
                    && !_ready.Task.IsCompletedSuccessfully && !startupTimeoutRetried && Volatile.Read(ref _stopping) == 0)
                {
                    // One initial UIA timeout discards the whole unpublished observation. The next normal
                    // interval rereads every taskbar and samples epoch/readTick afresh; no partial result is reused.
                    startupTimeoutRetried = true;
                    _previousCandidates = [];
                }
                catch (OperationCanceledException) when (Volatile.Read(ref _stopping) == 0)
                {
                    // A moved/replaced/expanding taskbar is a cancelled observation, not a failed UIA capability.
                    Invalidate();
                }

                _refresh.WaitOne(RefreshIntervalMilliseconds);
            }
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _stopping) != 0)
        {
            _ready.TrySetCanceled();
        }
        catch (Exception error)
        {
            var fault = error as StartButtonLocatorException ?? new StartButtonLocatorException(StartButtonLocatorFailure.AutomationUnavailable, error.HResult);
            RequestStop();
            _ready.TrySetException(fault);
            Faulted?.Invoke(fault);
        }
        finally
        {
            _ready.TrySetCanceled();
            _finished.TrySetResult();
        }
    }

    private sealed record Candidate(nint Window, int ProcessId, StartButtonAutomationInterop.ButtonData Button,
        PhysicalRect TaskbarBounds, bool CanCapture, bool NativeWholeButton);
    private Candidate[] _previousCandidates = [];
    private Native.Taskbar[] _publishedTaskbars = [];

    private void ValidateTaskbarContinuity(IReadOnlyList<Native.Taskbar> taskbars)
    {
        if (_publishedTaskbars.Length == 0 || _publishedTaskbars.Length == taskbars.Count
            && _publishedTaskbars.All(previous => taskbars.Any(taskbar => taskbar.Window == previous.Window
                && taskbar.ProcessId == previous.ProcessId))) return;

        // FindWindowEx walks Z-order across calls. A still-valid previous window that was missed
        // makes this observation incomplete; keep the existing cancellation behavior for that race.
        if (_publishedTaskbars.Any(previous => !taskbars.Any(taskbar => taskbar.Window == previous.Window
                && taskbar.ProcessId == previous.ProcessId)
            && Native.TryGetTaskbar(previous.Window, previous.ProcessId, out _)))
            throw new OperationCanceledException();

        throw new StartButtonLocatorException(StartButtonLocatorFailure.TaskbarTopologyChanged,
            context: "Explorer taskbar windows or processes changed after discovery; explicit retry is required.");
    }

    private void Publish(long epoch, long readTick, int discoveredCount, List<Candidate> candidates)
    {
        StartButtonSnapshot snapshot;
        lock (_lifecycle)
        {
            if (Volatile.Read(ref _stopping) != 0 || Interlocked.Read(ref _epoch) != epoch) return;
            if (candidates.Count != discoveredCount) return;
            // A locator lifetime belongs to this confirmed set. Geometry may change, but Explorer
            // replacement or monitor/taskbar additions/removals require a fresh explicit preparation.
            if (_publishedTaskbars.Length == 0)
                _publishedTaskbars = candidates.Select(candidate => new Native.Taskbar(
                    candidate.Window, candidate.ProcessId, candidate.TaskbarBounds)).ToArray();
            if (!_previousCandidates.SequenceEqual(candidates)) _generation++;
            _previousCandidates = candidates.ToArray();
            var targets = candidates.Where(candidate => candidate.CanCapture).Select(candidate => new StartButtonTarget(
                new(candidate.Window, candidate.ProcessId, _generation, candidate.Button.RuntimeId),
                candidate.Button.AutomationId, candidate.Button.ClassName, candidate.Button.NativeWindow,
                candidate.Button.Bounds, candidate.TaskbarBounds, candidate.NativeWholeButton)).ToArray();
            snapshot = new(_generation, readTick, discoveredCount, candidates.Count, Array.AsReadOnly(targets));
            Volatile.Write(ref _snapshot, snapshot);
            if (_ready.TrySetResult()) _startupCancellation.Unregister();
        }

        if (snapshot.Generation == CurrentSnapshot.Generation && Volatile.Read(ref _stopping) == 0) SnapshotChanged?.Invoke(snapshot);
    }

    /// <summary>Win32 declarations for taskbar discovery and physical hit validation.</summary>
    internal static class Native
    {
        internal sealed record Taskbar(nint Window, int ProcessId, PhysicalRect Bounds);
        internal static IReadOnlyList<Taskbar> FindExplorerTaskbars()
        {
            var found = new List<Taskbar>();
            var matchingClasses = 0;
            var rejected = new List<string>();
            var seen = new HashSet<nint>();
            var expectedExplorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            // Task View can exclude existing Shell taskbars from EnumWindows/EnumDesktopWindows.
            // Class lookup still discovers their current HWNDs; never substitute cached taskbars.
            foreach (var className in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            {
                nint window = 0;
                while ((window = FindWindowExW(0, window, className, null)) != 0)
                {
                    // Z-order/identity changes during a traversal must not loop or publish duplicates.
                    if (!seen.Add(window)) throw new OperationCanceledException();
                    matchingClasses++;
                    GetWindowThreadProcessId(window, out var pid);
                    using var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
                    if (process.IsInvalid)
                    {
                        rejected.Add($"0x{window:X}/PID {pid}: OpenProcess error={Marshal.GetLastWin32Error()}");
                        continue;
                    }
                    var image = new StringBuilder(32768);
                    var length = image.Capacity;
                    if (!QueryFullProcessImageNameW(process, 0, image, ref length))
                        rejected.Add($"0x{window:X}/PID {pid}: QueryFullProcessImageName error={Marshal.GetLastWin32Error()}");
                    else if (!string.Equals(image.ToString(), expectedExplorer, StringComparison.OrdinalIgnoreCase))
                        rejected.Add($"0x{window:X}/PID {pid}: image={image}; expected={expectedExplorer}");
                    else if (TryGetTaskbar(window, checked((int)pid), out var taskbar, out var failure)) found.Add(taskbar);
                    else rejected.Add($"0x{window:X}/PID {pid}: {failure}");
                }
            }
            if (found.Count == 0) throw new StartButtonLocatorException(StartButtonLocatorFailure.TaskbarUnavailable,
                context: $"FindWindowEx taskbarClasses={matchingClasses}; {string.Join("; ", rejected)}");
            return found.OrderBy(taskbar => taskbar.Window).ToArray();
        }

        internal static bool TryGetTaskbar(nint window, int processId, out Taskbar taskbar)
            => TryGetTaskbar(window, processId, out taskbar, out _);

        private static bool TryGetTaskbar(nint window, int processId, out Taskbar taskbar, out string? failure)
        {
            taskbar = new(window, processId, default);
            failure = null;
            if (!IsWindow(window)) { failure = "window no longer exists"; return false; }
            if (!IsTaskbarClass(window)) { failure = "taskbar class changed or unreadable"; return false; }
            GetWindowThreadProcessId(window, out var currentPid);
            if (currentPid != processId) { failure = $"owner changed to {currentPid}"; return false; }
            if (!GetWindowRect(window, out var bounds)) { failure = $"GetWindowRect error={Marshal.GetLastWin32Error()}"; return false; }
            if (!bounds.ToPhysical().IsValid) { failure = $"invalid bounds={bounds.ToPhysical()}"; return false; }
            taskbar = new(window, processId, bounds.ToPhysical());
            return true;
        }

        internal static bool IsStartChild(nint window, nint taskbar, int processId)
            => ReadClass(window) == "Start" && BelongsToTaskbar(window, taskbar, processId);

        private static bool IsUniqueStartChild(nint window, nint taskbar, int processId) =>
            window != 0 && window != taskbar && IsStartChild(window, taskbar, processId)
            && GetParent(window) == taskbar && FindWindowExW(taskbar, 0, "Start", null) == window
            && FindWindowExW(taskbar, window, "Start", null) == 0;

        internal static bool TryRefreshStartChild(Taskbar taskbar, StartButtonAutomationInterop.ButtonData seed,
            out StartButtonAutomationInterop.ButtonData refreshed)
        {
            refreshed = seed;
            if (seed.ClassName != "Start" || !IsUniqueStartChild(seed.NativeWindow, taskbar.Window, taskbar.ProcessId)
                || !GetWindowRect(seed.NativeWindow, out var bounds) || !bounds.ToPhysical().IsValid) return false;
            // RuntimeId remains the initial UIA identity seed; native ownership/uniqueness is checked every time.
            refreshed = seed with { Bounds = bounds.ToPhysical(),
                IsEnabled = IsWindowEnabled(taskbar.Window) && IsWindowEnabled(seed.NativeWindow),
                IsOffscreen = !IsWindowVisible(seed.NativeWindow) };
            return true;
        }

        internal static bool BelongsToTaskbar(nint window, nint taskbar, int processId)
        {
            GetWindowThreadProcessId(window, out var pid);
            return IsWindow(window) && pid == processId && GetAncestor(window, 2) == taskbar; // GA_ROOT
        }

        internal static bool ValidateHit(StartButtonTarget target, int x, int y)
        {
            if (!TryGetTaskbar(target.Identity.TaskbarWindow, target.Identity.ExplorerProcessId, out var taskbar)
                || taskbar.Bounds != target.TaskbarBounds || !IsWindowVisible(taskbar.Window) || !IsWindowEnabled(taskbar.Window)
                || !IsFullyOnMonitor(taskbar.Window, taskbar.Bounds)) return false;
            var hitWindow = WindowFromPoint(new() { X = x, Y = y });
            if (hitWindow == 0 || !BelongsToTaskbar(hitWindow, taskbar.Window, taskbar.ProcessId)) return false;
            if (target.NativeButtonWindow == 0) return true;
            if (!BelongsToTaskbar(target.NativeButtonWindow, taskbar.Window, taskbar.ProcessId)) return false;
            if (target.NativeButtonWindow == taskbar.Window) return true; // Windowless UIA element hosted in the taskbar HWND.
            if (!IsWindowVisible(target.NativeButtonWindow) || !IsWindowEnabled(target.NativeButtonWindow)) return false;
            if (target.NativeWholeButton && !IsUniqueStartChild(target.NativeButtonWindow, taskbar.Window, taskbar.ProcessId)) return false;
            return (hitWindow == target.NativeButtonWindow || IsChild(target.NativeButtonWindow, hitWindow))
                && GetWindowRect(target.NativeButtonWindow, out var buttonBounds) && buttonBounds.ToPhysical() == target.ButtonBounds;
        }

        internal static bool IsFullyOnMonitor(nint window, PhysicalRect rectangle)
        {
            var monitor = MonitorFromWindow(window, 2); // MONITOR_DEFAULTTONEAREST
            var info = new MonitorInformation { Size = (uint)Marshal.SizeOf<MonitorInformation>() };
            return monitor != 0 && GetMonitorInfoW(monitor, ref info) && info.Monitor.ToPhysical().Contains(rectangle);
        }

        private static bool IsTaskbarClass(nint window) => ReadClass(window) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
        private static string ReadClass(nint window)
        {
            var name = new StringBuilder(256);
            return GetClassNameW(window, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
        }

        internal readonly struct PhysicalDpiScope : IDisposable
        {
            private readonly nint _previous;
            public PhysicalDpiScope() => _previous = SetThreadDpiAwarenessContext((nint)(-4)); // PER_MONITOR_AWARE_V2
            public bool IsSet => _previous != 0;
            public void Dispose() { if (_previous != 0) SetThreadDpiAwarenessContext(_previous); }
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MonitorInformation
        {
            public uint Size;
            public StartButtonAutomationInterop.NativeRect Monitor, WorkArea;
            public uint Flags;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder name, int maximum);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsChild(nint parent, nint child);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(nint window);
        [DllImport("user32.dll")] private static extern nint GetParent(nint window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowExW(nint parent, nint after, string className, string? windowName);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out StartButtonAutomationInterop.NativeRect bounds);
        [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
        [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
        [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInformation information);
        [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
    }
}
