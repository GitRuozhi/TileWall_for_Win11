using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using TileWall.Core.Shell;

namespace TileWall.Shell.Interop;

/// <summary>
/// Owns Start-entry capture. Prepare is pass-through; only Activate admits new gestures.
/// One thread owns hooks and all input ledgers.
/// The toggle delegate MUST only enqueue to its owner; the owner must recheck IsCurrentActivation on delivery.
/// </summary>
internal sealed class StartReplacementService(Action<long> enqueueToggle) : IAsyncDisposable
{
    private const uint WakeMessage = 0x8000 + 74;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly WindowsKeyCaptureState _keyboard = new();
    private readonly StartButtonPressState _mouse = new();
    private StartButtonLocator _locator = new();
    private readonly nuint _inputMarker = (nuint)Random.Shared.Next(1, int.MaxValue);
    private TaskCompletionSource _ready = Completion();
    private TaskCompletionSource _finished = Completion();
    private TaskCompletionSource? _stopAttempt;
    private Win32Api.LowLevelHookProc? _keyboardCallback, _mouseCallback;
    private InputDesktopState.WinEventProc? _desktopCallback;
    private Thread? _thread;
    private nint _keyboardHook, _mouseHook, _desktopHook;
    private GCHandle _nativeLifetime;
    private uint _threadId;
    private long _generation;
    private long _locatorGeneration;
    private volatile bool _admission;
    private bool _stopping, _quit, _prepared;
    private volatile bool _desktopRecoveryPending;
    private bool _desktopRecoveryQueued;
    private string? _fault;

    internal string? Fault => Volatile.Read(ref _fault);
    internal bool IsActive => _admission && !_desktopRecoveryPending;
    internal bool HasRetainedResources => _thread is not null
        && (!_finished.Task.IsCompletedSuccessfully || _keyboardHook != 0 || _mouseHook != 0 || _desktopHook != 0);
    internal bool IsCurrentActivation(long generation) => IsActive && Interlocked.Read(ref _generation) == generation;
    internal StartButtonSnapshot ButtonSnapshot => _locator.CurrentSnapshot;
    internal event Action<string>? Faulted;

    internal async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_prepared) return;
            if (_thread is not null && (!_finished.Task.IsCompletedSuccessfully || _keyboardHook != 0 || _mouseHook != 0 || _desktopHook != 0))
                throw new InvalidOperationException("Previous input resources have not finished stopping.");
            await _locator.DisposeAsync().ConfigureAwait(false);
            _locator = new StartButtonLocator();
            _locator.Faulted += LocatorFaulted;
            _locator.SnapshotChanged += SnapshotChanged;
            Volatile.Write(ref _fault, null);
            await _locator.StartAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_locator.CurrentSnapshot.IsReady) throw new InvalidOperationException("Start buttons are not all recognized.");
            _ready = Completion();
            _finished = Completion();
            _stopAttempt = null;
            while (_commands.TryDequeue(out _)) { }
            _quit = _stopping = false;
            _desktopRecoveryPending = _desktopRecoveryQueued = false;
            _thread = new Thread(Run) { Name = "TileWall Start input", IsBackground = true };
            _thread.Start();
            await _ready.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_locator.CurrentSnapshot.IsReady || Fault is not null)
                throw new InvalidOperationException("Input preparation became unavailable.");
            _prepared = true;
        }
        catch (Exception preparationError)
        {
            await _locator.StopAsync().ConfigureAwait(false);
            if (_thread is not null && !_finished.Task.IsCompletedSuccessfully)
            {
                if (_threadId != 0)
                {
                    try { Post(BeginStop); }
                    catch (InvalidOperationException) { }
                }
                try { await _finished.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Input preparation failed and cleanup is incomplete; retain this service.",
                        preparationError, cleanupError);
                }
            }
            throw;
        }
        finally { _transition.Release(); }
    }

    internal async Task<long> ActivateAsync()
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_prepared || Fault is not null) throw new InvalidOperationException("Input preparation is not healthy.");
            var expectedGeneration = Interlocked.Read(ref _generation);
            var completed = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                try
                {
                    if (_stopping || Fault is not null)
                        throw new InvalidOperationException("Activation was superseded by stop or failure.");
                    if (_admission && _desktopRecoveryPending)
                    {
                        completed.SetException(new InvalidOperationException(
                            "Input desktop recovery is still pending; wait for recovery before requesting activation."));
                        return;
                    }
                    if (!_locator.CurrentSnapshot.IsReady) throw new InvalidOperationException("Start-button discovery is unavailable.");
                    if (IsActive)
                    {
                        // Repeated requests refer to the existing activation, including its held-key ledgers.
                        completed.SetResult(Interlocked.Read(ref _generation));
                        return;
                    }
                    if (Interlocked.Read(ref _generation) != expectedGeneration)
                        throw new InvalidOperationException("Activation was superseded by stop or failure.");
                    // Mouse VKs and generic aliases for left/right modifiers never arrive as keyboard releases.
                    var held = Enumerable.Range(8, 247).Where(key => key is not (0x10 or 0x11 or 0x12)
                        && (Win32Api.GetAsyncKeyState(key) & 0x8000) != 0).ToArray();
                    _keyboard.Activate(held);
                    _mouse.Activate((Win32Api.GetAsyncKeyState(1) & 0x8000) != 0);
                    var generation = Interlocked.Increment(ref _generation);
                    _admission = true;
                    completed.SetResult(generation);
                }
                catch (Exception ex) { SetFault("activate-failed"); completed.SetException(ex); }
            });
            return await completed.Task.ConfigureAwait(false);
        }
        finally { _transition.Release(); }
    }

    /// <summary>
    /// Await genuine releases and Unhook success. A caller may time out its WAIT, but must keep this service alive
    /// and may not tear down the application or start another hook owner while this task is incomplete.
    /// No guessed key-up is injected to manufacture a successful shutdown.
    /// </summary>
    internal async Task StopAsync()
    {
        _admission = false;
        Interlocked.Increment(ref _generation);
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            _prepared = false;
            if (_thread is not null && !_finished.Task.IsCompletedSuccessfully)
            {
                if (_finished.Task.IsFaulted) await _finished.Task.ConfigureAwait(false);
                var stopped = Completion();
                Post(() => { _stopAttempt = stopped; BeginStop(); });
                await stopped.Task.ConfigureAwait(false);
                await _finished.Task.ConfigureAwait(false);
            }
            await _locator.StopAsync().ConfigureAwait(false);
        }
        finally { _transition.Release(); }
    }

    internal Task SuspendAsync() => StopAsync();
    internal async Task<long> ResumeAsync()
    {
        await PrepareAsync().ConfigureAwait(false);
        return await ActivateAsync().ConfigureAwait(false);
    }

    internal void InvalidateButtons() => _locator.Invalidate();

    private void Run()
    {
        _threadId = Win32Api.GetCurrentThreadId();
        _ = Win32Api.PeekMessageW(out _, 0, 0, 0, 0); // Establish this thread's queue BEFORE publishing Ready.
        _keyboardCallback = KeyboardHook;
        _mouseCallback = MouseHook;
        _desktopCallback = DesktopChanged;
        // Native callbacks must stay rooted even if the message loop faults before Unhook succeeds.
        _nativeLifetime = GCHandle.Alloc(this);
        try
        {
            var module = Win32Api.GetModuleHandleW(null);
            _keyboardHook = Win32Api.SetWindowsHookExW(13, _keyboardCallback, module, 0);
            if (_keyboardHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _mouseHook = Win32Api.SetWindowsHookExW(14, _mouseCallback, module, 0);
            if (_mouseHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _desktopHook = InputDesktopState.SetWinEventHook(InputDesktopState.DesktopSwitchEvent,
                InputDesktopState.DesktopSwitchEvent, 0, _desktopCallback, 0, 0, 0); // OUTOFCONTEXT; this message thread owns delivery.
            if (_desktopHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _ready.TrySetResult();
        }
        catch (Exception ex)
        {
            SetFault("hook-prepare-failed");
            _ready.TrySetException(ex);
            BeginStop();
        }

        while (!_quit)
        {
            var result = Win32Api.GetMessageW(out var message, 0, 0, 0);
            if (result <= 0)
            {
                SetFault(result == 0 ? "unexpected-thread-quit" : "get-message-failed");
                BeginStop();
                // The OS may have stopped delivery. Keep the ledger/handles truthful; no restart is permitted.
                if (!_quit)
                {
                    var error = new InvalidOperationException("Input loop ended before cleanup.");
                    _stopAttempt?.TrySetException(error);
                    _finished.TrySetException(error);
                    _threadId = 0;
                    return;
                }
                break;
            }
            if (message.Message == WakeMessage)
            {
                while (_commands.TryDequeue(out var command)) command();
                TryFinishStop();
            }
            else _ = Win32Api.DispatchMessageW(ref message);
        }
        _stopAttempt?.TrySetResult();
        if (_nativeLifetime.IsAllocated) _nativeLifetime.Free();
        _keyboardCallback = _mouseCallback = null;
        _desktopCallback = null;
        _threadId = 0;
        _finished.TrySetResult();
    }

    private nint KeyboardHook(int code, nint message, nint data)
    {
        if (code < 0) return Win32Api.CallNextHookEx(_keyboardHook, code, message, data);
        var suppress = false;
        try
        {
            if (!_admission) _keyboard.Stop();
            if (_desktopRecoveryPending) _keyboard.CancelCurrentGesture();
            var native = Marshal.PtrToStructure<Win32Api.LowLevelKeyboardInput>(data);
            var injected = (native.Flags & 0x12) != 0;
            var source = !injected ? CapturedInputSource.Physical : native.ExtraInformation == _inputMarker
                ? CapturedInputSource.OwnInjected : CapturedInputSource.ExternalInjected;
            var stroke = new WindowsKeyStroke((int)native.VirtualKey, (int)native.ScanCode,
                (native.Flags & 1) != 0, (native.Flags & 0x80) != 0,
                native.ScanCode is > 0 and <= 0xff && native.VirtualKey is not (0x13 or 0x2c or 0xe7));
            var decision = _keyboard.Handle(new(stroke, source));
            suppress = decision.Suppress;
            if (decision.Replay is { } replay)
            {
                var inputs = replay.Inputs.Select(key => new Win32Api.SyntheticInput
                {
                    Type = 1,
                    Data = new Win32Api.InputUnion { Keyboard = new Win32Api.KeyboardInput
                    {
                        ScanCode = checked((ushort)key.ScanCode),
                        Flags = 8u | (key.Extended ? 1u : 0u) | (key.IsUp ? 2u : 0u),
                        ExtraInformation = _inputMarker
                    }}
                }).ToArray();
                int? inserted = null;
                try { inserted = checked((int)Win32Api.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32Api.SyntheticInput>())); }
                finally
                {
                    var result = _keyboard.CompleteReplay(replay.Id, inserted);
                    if (result.Failure != WindowsKeyCaptureFailure.None) SetFault("key-replay-failed");
                }
            }
            if (decision.Failure != WindowsKeyCaptureFailure.None) SetFault("key-capture-failed");
            if (decision.Toggle && IsActive) enqueueToggle(Interlocked.Read(ref _generation));
        }
        catch (Exception) { SetFault("keyboard-callback-failed"); }
        WakeForCleanup();
        return suppress ? 1 : Win32Api.CallNextHookEx(_keyboardHook, code, message, data);
    }

    private nint MouseHook(int code, nint message, nint data)
    {
        if (code < 0) return Win32Api.CallNextHookEx(_mouseHook, code, message, data);
        var suppress = false;
        try
        {
            if (!_admission) _mouse.Stop();
            var kind = (uint)message switch
            {
                Win32Api.WmLbuttondown => StartButtonInputKind.LeftDown,
                Win32Api.WmLbuttonup => StartButtonInputKind.LeftUp,
                Win32Api.WmMousemove => StartButtonInputKind.Move,
                _ => StartButtonInputKind.Other
            };
            if (kind == StartButtonInputKind.Other) return Win32Api.CallNextHookEx(_mouseHook, code, message, data);
            var native = Marshal.PtrToStructure<Win32Api.LowLevelMouseInput>(data);
            var source = StartButtonPressState.IsPromotedPointer(native.ExtraInformation) ? CapturedInputSource.PromotedPointer
                : (native.Flags & 3) != 0 ? CapturedInputSource.ExternalInjected : CapturedInputSource.Physical;
            StartButtonIdentity? hit = null;
            if (source == CapturedInputSource.Physical && (IsActive || _mouse.HasPendingRelease)
                && _locator.TryHitTest(native.Point.X, native.Point.Y, out var target)) hit = target!.Identity;
            var decision = _mouse.Handle(kind, source, hit);
            suppress = decision.Suppress;
            if (decision.Toggle && IsActive) enqueueToggle(Interlocked.Read(ref _generation));
        }
        catch (Exception) { SetFault("mouse-callback-failed"); }
        WakeForCleanup();
        return suppress ? 1 : Win32Api.CallNextHookEx(_mouseHook, code, message, data);
    }

    private void BeginStop()
    {
        _admission = false;
        _stopping = true;
        TryFinishStop();
    }

    private void TryFinishStop()
    {
        if (!_stopping) return;
        // Fault-driven cleanup can reach here without BeginStop. Close both state machines
        // before releasing hooks; Stop preserves any down/up accounting still owed.
        _keyboard.Stop();
        _mouse.Stop();
        if (!_keyboard.CanReleaseHook || _mouse.HasPendingRelease) return;
        if (_mouseHook != 0)
        {
            if (Win32Api.UnhookWindowsHookEx(_mouseHook)) _mouseHook = 0;
            else SetFault("mouse-unhook-failed");
        }
        if (_keyboardHook != 0)
        {
            if (Win32Api.UnhookWindowsHookEx(_keyboardHook)) _keyboardHook = 0;
            else SetFault("keyboard-unhook-failed");
        }
        if (_desktopHook != 0)
        {
            if (InputDesktopState.UnhookWinEvent(_desktopHook)) _desktopHook = 0;
            else SetFault("desktop-unhook-failed");
        }
        _quit = _mouseHook == 0 && _keyboardHook == 0 && _desktopHook == 0;
        if (!_quit) _stopAttempt?.TrySetException(new InvalidOperationException("Native hook release failed; resources are retained."));
    }

    private void WakeForCleanup()
    {
        QueueDesktopRecovery();
        if ((_stopping || Fault is not null) && _keyboard.CanReleaseHook && !_mouse.HasPendingRelease)
        {
            _stopping = true;
            if (!Win32Api.PostThreadMessageW(_threadId, WakeMessage, 0, 0)) SetFault("cleanup-post-failed");
        }
    }

    private void DesktopChanged(nint hook, uint eventType, nint window, int objectId, int childId, uint eventThread, uint eventTime)
    {
        try
        {
            if (_quit) return;
            Interlocked.Increment(ref _generation); // A pre-switch queued toggle must never fire on return.
            _keyboard.CancelCurrentGesture();
            _mouse.CancelCurrentPress();
            _desktopRecoveryPending = true;
            QueueDesktopRecovery();
        }
        catch (Exception) { SetFault("desktop-switch-failed"); }
    }

    private void QueueDesktopRecovery()
    {
        if (!_desktopRecoveryPending || _desktopRecoveryQueued || _quit) return;
        _desktopRecoveryQueued = true;
        try
        {
            Post(() =>
            {
                _desktopRecoveryQueued = false;
                if (!_desktopRecoveryPending) return;
                try
                {
                    // This command runs after the low-level callback has returned. An inaccessible/unstable
                    // desktop is not evidence of released keys; retain the request until a later event/input.
                    var generation = Interlocked.Read(ref _generation);
                    if (!InputDesktopState.TryReadHeldKeys(_threadId, out var held)
                        || generation != Interlocked.Read(ref _generation)) return;
                    if (!_keyboard.ReconcileAfterDesktopSwitch(held))
                    {
                        SetFault("desktop-replay-unconfirmed");
                        BeginStop();
                        return;
                    }
                    _desktopRecoveryPending = false;
                    TryFinishStop();
                }
                catch (Exception)
                {
                    SetFault("desktop-state-query-failed");
                    BeginStop();
                }
            });
        }
        catch (InvalidOperationException) { _desktopRecoveryQueued = false; }
    }

    private void SnapshotChanged(StartButtonSnapshot snapshot)
    {
        if (Interlocked.Exchange(ref _locatorGeneration, snapshot.Generation) == snapshot.Generation || _threadId == 0) return;
        // The notification may wait behind physical input. Compare with the snapshot
        // that is current when dispatched, not an older queued notification.
        try { Post(() => _mouse.InvalidateSnapshot(_locator.CurrentSnapshot.Generation)); }
        catch (InvalidOperationException) { }
    }

    private void LocatorFaulted(StartButtonLocatorException error)
    {
        SetFault($"start-button-{error.Code}: {error.Message}");
        if (_threadId != 0)
        {
            try { Post(BeginStop); }
            catch (InvalidOperationException) { /* Post records the concrete failure and keeps admission closed. */ }
        }
    }

    private void SetFault(string code)
    {
        _admission = false;
        var generation = Interlocked.Increment(ref _generation);
        if (Interlocked.CompareExchange(ref _fault, code, null) is not null) return;
        // Never run logging or an arbitrary subscriber on the hook thread.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (Fault == code && Interlocked.Read(ref _generation) == generation) Faulted?.Invoke(code);
            }
            catch (Exception) { /* Subscriber failure cannot terminate the input owner. Fault remains readable. */ }
        });
    }

    private void Post(Action command)
    {
        _commands.Enqueue(command);
        if (_threadId != 0 && Win32Api.PostThreadMessageW(_threadId, WakeMessage, 0, 0)) return;
        SetFault("thread-post-failed");
        throw new InvalidOperationException("Could not dispatch to the input thread.");
    }

    private static TaskCompletionSource Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _locator.Faulted -= LocatorFaulted;
        _locator.SnapshotChanged -= SnapshotChanged;
        await _locator.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
