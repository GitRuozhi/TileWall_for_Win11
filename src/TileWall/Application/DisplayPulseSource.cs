using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace TileWall;

/// <summary>
/// Win10 display pacing when lifted XAML falls back to simulated-vblank Sleep.
/// One MTA worker owns all DXGI pointers; UI callbacks carry only an epoch and timestamp.
/// </summary>
internal sealed class DisplayPulseSource : IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<long> _pulse;
    private readonly Action<string> _failure;
    private Thread? _worker;
    private bool _active, _disposed, _failed, _workerExited;
    private long _epoch, _pendingEpoch;

    internal static bool Required { get; } = !HasCompositorClock();

    internal DisplayPulseSource(DispatcherQueue dispatcher, Action<long> pulse, Action<string> failure)
    { _dispatcher = dispatcher; _pulse = pulse; _failure = failure; }

    internal bool Start()
    {
        lock (_gate)
        {
            if (_disposed || _failed) return false;
            if (_active) return true;
            _active = true;
            _epoch++;
            if (_worker is null)
            {
                _worker = new Thread(Run) { IsBackground = true, Name = "TileWall display pulse" };
                _worker.Start();
            }
            _wake.Set();
            return true;
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            if (_disposed || _workerExited) return;
            _active = false;
            _epoch++;
            _wake.Set();
        }
    }

    private bool IsCurrent(long epoch)
    { lock (_gate) return !_disposed && !_failed && _active && _epoch == epoch; }

    private void Run()
    {
        var initialized = false;
        try
        {
            Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 0)); // COINIT_MULTITHREADED
            initialized = true;
            while (true)
            {
                _wake.WaitOne(); // No refresh wait, polling or CPU usage while idle.
                long epoch;
                lock (_gate)
                {
                    if (_disposed || _failed) break;
                    if (!_active) continue;
                    epoch = _epoch;
                }
                var output = FindPrimaryOutput();
                try
                {
                    var wait = Method<WaitForVBlank>(output, 10);
                    long previous = 0;
                    var immediateReturns = 0;
                    while (IsCurrent(epoch))
                    {
                        // DXGI has no cancellation argument. Stop/close invalidate the
                        // epoch immediately; only this background worker may await a driver.
                        Marshal.ThrowExceptionForHR(wait(output));
                        var now = Stopwatch.GetTimestamp();
                        if (!IsCurrent(epoch)) break;
                        if (previous != 0 && Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds < 1)
                        {
                            if (++immediateReturns >= 3)
                                throw new InvalidOperationException("DXGI vblank returned repeatedly without waiting.");
                            continue;
                        }
                        previous = now;
                        immediateReturns = 0;
                        Queue(epoch, now);
                    }
                }
                finally { Marshal.Release(output); }
            }
        }
        catch (Exception ex) { Fail($"DXGI display pulse unavailable: {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            if (initialized) CoUninitialize();
            // Dispose only after this thread has left WaitForVBlank and released DXGI.
            lock (_gate) { _workerExited = true; _wake.Dispose(); }
        }
    }

    private void Queue(long epoch, long timestamp)
    {
        if (Interlocked.CompareExchange(ref _pendingEpoch, epoch, 0) != 0) return;
        if (_dispatcher.TryEnqueue(() =>
        {
            if (Interlocked.CompareExchange(ref _pendingEpoch, 0, epoch) != epoch || !IsCurrent(epoch)) return;
            _pulse(timestamp);
        })) return;
        Interlocked.CompareExchange(ref _pendingEpoch, 0, epoch);
        throw new InvalidOperationException("The UI dispatcher rejected the display pulse.");
    }

    private void Fail(string reason)
    {
        lock (_gate)
        {
            if (_disposed || _failed) return;
            _failed = true; _active = false; _epoch++;
        }
        _dispatcher.TryEnqueue(() =>
        {
            lock (_gate) { if (_disposed) return; }
            _failure(reason);
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _active = false; _epoch++;
            if (_worker is null) { _workerExited = true; _wake.Dispose(); }
            else if (!_workerExited) _wake.Set();
        }
        // Never join the display/driver wait from the UI thread.
    }

    private static bool HasCompositorClock()
    {
        if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "dcomp.dll"), out var module)) return false;
        try { return NativeLibrary.TryGetExport(module, "DCompositionWaitForCompositorClock", out _); }
        finally { NativeLibrary.Free(module); }
    }

    private static IntPtr FindPrimaryOutput()
    {
        // The primary display contains desktop (0,0). Match HMONITOR, never assume
        // the primary display belongs to the first adapter or its first output.
        var primary = MonitorFromPoint(default, 1); // MONITOR_DEFAULTTOPRIMARY
        if (primary == IntPtr.Zero) throw new InvalidOperationException("No primary monitor.");
        var iid = new Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369");
        Marshal.ThrowExceptionForHR(CreateDXGIFactory(ref iid, out var factory));
        try
        {
            var adapters = Method<Enumerate>(factory, 7);
            for (uint a = 0; ; a++)
            {
                var hr = adapters(factory, a, out var adapter);
                if (hr == unchecked((int)0x887A0002)) break; // DXGI_ERROR_NOT_FOUND
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    var outputs = Method<Enumerate>(adapter, 7);
                    for (uint o = 0; ; o++)
                    {
                        hr = outputs(adapter, o, out var output);
                        if (hr == unchecked((int)0x887A0002)) break;
                        Marshal.ThrowExceptionForHR(hr);
                        var selected = false;
                        try
                        {
                            Marshal.ThrowExceptionForHR(Method<GetOutputDescription>(output, 7)(output, out var description));
                            selected = description.AttachedToDesktop != 0 && description.Monitor == primary;
                            if (selected) return output;
                        }
                        finally { if (!selected) Marshal.Release(output); }
                    }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        throw new InvalidOperationException("No attached DXGI output matches the primary monitor.");
    }

    // Slots/signatures/layout verified against local Windows SDK 10.0.26100 dxgi.h.
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Enumerate(IntPtr instance, uint index, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetOutputDescription(IntPtr instance, out OutputDescription description);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int WaitForVBlank(IntPtr instance);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OutputDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public NativeRect DesktopCoordinates;
        public int AttachedToDesktop;
        public uint Rotation;
        public IntPtr Monitor;
    }
    [DllImport("dxgi.dll", ExactSpelling = true)] private static extern int CreateDXGIFactory(ref Guid iid, out IntPtr factory);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
}
