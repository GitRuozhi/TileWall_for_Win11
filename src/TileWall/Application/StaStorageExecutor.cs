using System.ComponentModel;
using System.Runtime.InteropServices;
using TileWall.Core.Application;
using TileWall.Core.Shell;

namespace TileWall.ApplicationModel;

/// <summary>One idle message-pumped COM apartment for serialized synchronous storage work.</summary>
internal sealed class StaStorageExecutor : IWallStorageExecutor
{
    private const uint WorkMessage = 0x8001, StopMessage = 0x8002;
    private readonly object _gate = new();
    private readonly Dictionary<nuint, (Action Run, Action<Exception> Fail)> _jobs = [];
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nuint _nextJob;
    private uint _threadId;
    private bool _stopping;

    internal StaStorageExecutor()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "TileWall.Storage" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public async Task<T> ExecuteAsync<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        await _ready.Task.ConfigureAwait(false);
        var completed = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_stopping) throw new ObjectDisposedException(nameof(StaStorageExecutor));
            var id = ++_nextJob;
            _jobs.Add(id, (() =>
            {
                try { completed.TrySetResult(work()); }
                catch (Exception ex) { completed.TrySetException(ex); }
            }, ex => completed.TrySetException(ex)));
            if (!PostThreadMessageW(_threadId, WorkMessage, id, IntPtr.Zero))
            {
                _jobs.Remove(id);
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Storage work could not be queued.");
            }
        }
        return await completed.Task.ConfigureAwait(false);
    }

    public async Task ShutdownAsync()
    {
        try { await _ready.Task.ConfigureAwait(false); }
        catch { await _stopped.Task.ConfigureAwait(false); return; }
        lock (_gate)
        {
            if (!_stopping)
            {
                // Session admission is already closed and accepted work has drained.
                if (!PostThreadMessageW(_threadId, StopMessage, 0, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Storage shutdown could not be queued.");
                _stopping = true;
            }
        }
        await _stopped.Task.ConfigureAwait(false);
    }

    private void Run()
    {
        var initialized = false;
        Exception? failure = null;
        try
        {
            var result = CoInitializeEx(IntPtr.Zero, 2); // COINIT_APARTMENTTHREADED
            Marshal.ThrowExceptionForHR(result);
            initialized = true;
            _threadId = GetCurrentThreadId();
            PeekMessageW(out _, IntPtr.Zero, 0, 0, 0); // Create this thread's message queue.
            _ready.TrySetResult();
            while (true)
            {
                var received = GetMessageW(out var message, IntPtr.Zero, 0, 0);
                if (received == 0) break;
                if (received < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (message.MessageId == StopMessage) break;
                if (message.MessageId == WorkMessage)
                {
                    Action? run = null;
                    lock (_gate)
                        if (_jobs.Remove(message.WParam, out var job)) run = job.Run;
                    run?.Invoke();
                }
                else
                {
                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            _ready.TrySetException(ex);
            AppLog.Warn("StorageWorker", ex.Message);
        }
        finally
        {
            (Action Run, Action<Exception> Fail)[] pending;
            lock (_gate)
            {
                _stopping = true;
                pending = _jobs.Values.ToArray();
                _jobs.Clear();
            }
            var error = failure ?? new ObjectDisposedException(nameof(StaStorageExecutor));
            foreach (var job in pending) job.Fail(error);
            if (initialized) CoUninitialize();
            _stopped.TrySetResult();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal IntPtr Hwnd;
        internal uint MessageId;
        internal nuint WParam;
        internal IntPtr LParam;
        internal uint Time;
        internal int X, Y;
        internal uint Private;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint apartment);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int GetMessageW(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint thread, uint message, nuint wParam, IntPtr lParam);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr DispatchMessageW(ref Message message);
}
