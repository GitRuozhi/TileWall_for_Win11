using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TileWall.Core.Settings;
using TileWall.Resources;
using TileWall.Shell.Interop;
using Windows.System;

namespace TileWall.Views;

/// <summary>Records a chord only after the host has released its global input hooks.</summary>
internal sealed partial class HotKeyRecorder : TextBox
{
    private string _configured = string.Empty;
    private readonly Win32Api.LowLevelHookProc _keyboardHook;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _focusWatch;
    private readonly HashSet<uint> _suppressedKeys = [];
    private readonly HashSet<uint> _bypassKeys = [];
    private Task _transition = Task.CompletedTask;
    private Task _gestureCompletion = Task.CompletedTask;
    private TaskCompletionSource? _pairedReleases;
    private IntPtr _hook, _recordingWindow;
    private uint _modifierKeys, _suppressedModifiers;
    private int _recordingGeneration;
    private bool _requested, _admitted, _inputPending;
    internal bool IsRecording { get; private set; }
    internal bool HasPendingTransition => _requested || !_transition.IsCompleted || !_gestureCompletion.IsCompleted || _hook != IntPtr.Zero || _admitted;
    internal event Func<bool, Task>? RecordingChanged;
    internal event Action<HotKeyGesture>? GestureRecorded;
    internal event Action<string>? RecordingFailed;

    internal HotKeyRecorder()
    {
        _keyboardHook = OnKeyboardHook;
        _focusWatch = DispatcherQueue.CreateTimer();
        _focusWatch.Interval = TimeSpan.FromMilliseconds(100);
        _focusWatch.IsRepeating = true;
        _focusWatch.Tick += (_, _) => { if (_requested && !HasRecordingFocus()) CancelRecording(); };
        IsReadOnly = true; IsSpellCheckEnabled = false; IsTextPredictionEnabled = false;
        Width = 220; MinHeight = 32; CornerRadius = new CornerRadius(0); FontSize = 14; Padding = new Thickness(8, 4, 8, 4);
        HorizontalAlignment = HorizontalAlignment.Left;
        GotFocus += (_, _) => BeginRecording();
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => BeginRecording()), true);
        AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        LostFocus += (_, _) => CancelRecording();
        Unloaded += (_, _) => CancelRecording();
    }

    internal void SetConfigured(string? configured)
    {
        _configured = Display(configured);
        if (!_requested && !IsRecording) Text = _configured;
    }

    internal void CancelRecording() => _ = CancelRecordingAsync();

    internal Task CancelRecordingAsync()
    {
        _requested = false; IsRecording = false; _inputPending = false;
        _recordingGeneration++;
        _focusWatch.Stop(); Text = _configured;
        if (_transition.IsCompleted && !_admitted && _hook == IntPtr.Zero) return _transition;
        return QueueTransition(EndRecordingCoreAsync);
    }

    internal async Task WaitForIdleAsync()
    {
        Task observed;
        do { observed = _transition; await observed; await _gestureCompletion; } while (!ReferenceEquals(observed, _transition));
    }

    private Task QueueTransition(Func<Task> operation)
    {
        var previous = _transition;
        return _transition = RunAsync();
        async Task RunAsync()
        {
            try { await previous; } catch { /* A new cancellation can retry native teardown. */ }
            await operation();
        }
    }

    private void BeginRecording()
    {
        if (HasPendingTransition || !CanRecord()) return;
        _requested = true;
        var generation = ++_recordingGeneration;
        _ = QueueTransition(() => BeginRecordingCoreAsync(generation));
    }

    private async Task BeginRecordingCoreAsync(int generation)
    {
        if (!_requested || generation != _recordingGeneration || !CanRecord()) return;
        try
        {
            // Pair admission even when a partially completed host pause throws.
            _admitted = true;
            if (RecordingChanged is { } changed) await changed(true);
            if (!_requested || generation != _recordingGeneration || !CanRecord())
            { await EndRecordingCoreAsync(); return; }
            _recordingWindow = GetForegroundWindow();
            Win32Api.GetWindowThreadProcessId(_recordingWindow, out var processId);
            if (_recordingWindow == IntPtr.Zero || processId != Environment.ProcessId)
            { _requested = false; await EndRecordingCoreAsync(); return; }
            if (_hook != IntPtr.Zero) throw new InvalidOperationException();
            _modifierKeys = 0;
            foreach (var key in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
                if (Win32Api.GetAsyncKeyState(key) < 0) _modifierKeys |= ModifierKeyBit((uint)key);
            _bypassKeys.Clear();
            for (uint key = 1; key < 255; key++)
                if (ModifierKeyBit(key) == 0 && Win32Api.GetAsyncKeyState((int)key) < 0) _bypassKeys.Add(key);
            _hook = Win32Api.SetWindowsHookExW(13, _keyboardHook, Win32Api.GetModuleHandleW(null), 0);
            if (_hook == IntPtr.Zero) throw new InvalidOperationException();
            IsRecording = true; _focusWatch.Start(); Text = "…";
        }
        catch
        {
            _requested = false; IsRecording = false;
            await EndRecordingCoreAsync();
            RecordingFailed?.Invoke(ProductStrings.Get("Settings.HotKeyRecordingFailed"));
        }
    }

    private async Task EndRecordingCoreAsync()
    {
        IsRecording = false;
        // Only consume real releases paired with swallowed downs while stopping.
        if (_suppressedModifiers != 0 || _suppressedKeys.Count != 0)
        {
            _pairedReleases ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _pairedReleases.Task;
        }
        if (_hook != IntPtr.Zero)
        {
            if (!Win32Api.UnhookWindowsHookEx(_hook))
            {
                RecordingFailed?.Invoke(ProductStrings.Get("Settings.HotKeyRecordingFailed"));
                throw new InvalidOperationException(ProductStrings.Get("Settings.HotKeyRecordingFailed"));
            }
            _hook = IntPtr.Zero;
        }
        _modifierKeys = 0; _bypassKeys.Clear(); _recordingWindow = IntPtr.Zero; _pairedReleases = null;
        if (!_admitted) return;
        _admitted = false;
        try { if (RecordingChanged is { } changed) await changed(false); }
        catch
        {
            RecordingFailed?.Invoke(ProductStrings.Get("Settings.HotKeyRecordingFailed"));
            throw;
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_requested) return;
        if (e.Key == VirtualKey.Tab) { CancelRecording(); return; }
        e.Handled = true;
        if (e.Key == VirtualKey.Escape) CancelRecording();
    }

    private IntPtr OnKeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code < 0) return Win32Api.CallNextHookEx(_hook, code, message, data);
        try
        {
            var down = message.ToInt64() is 0x0100 or 0x0104;
            var up = message.ToInt64() is 0x0101 or 0x0105;
            if (!down && !up) return Win32Api.CallNextHookEx(_hook, code, message, data);
            var input = Marshal.PtrToStructure<Win32Api.LowLevelKeyboardInput>(data);
            // Injected releases cannot settle swallowed physical downs.
            if ((input.Flags & 0x12) != 0) return Win32Api.CallNextHookEx(_hook, code, message, data);
            var key = input.VirtualKey;
            var bit = ModifierKeyBit(key);
            if (_bypassKeys.Contains(key))
            {
                if (up) _bypassKeys.Remove(key);
                return Win32Api.CallNextHookEx(_hook, code, message, data);
            }
            var paired = bit != 0 ? (_suppressedModifiers & bit) != 0 : _suppressedKeys.Contains(key);
            if (paired)
            {
                if (up)
                {
                    _suppressedModifiers &= ~bit; _modifierKeys &= ~bit; _suppressedKeys.Remove(key);
                    if (_suppressedModifiers == 0 && _suppressedKeys.Count == 0) _pairedReleases?.TrySetResult();
                }
                return new IntPtr(1);
            }
            if (!HasRecordingFocus())
            {
                if (_requested) QueueInput(CancelRecording);
                return Win32Api.CallNextHookEx(_hook, code, message, data);
            }
            if (bit != 0)
            {
                if (down)
                {
                    var heldBeforeRecording = (_modifierKeys & bit) != 0;
                    _modifierKeys |= bit;
                    if (!heldBeforeRecording) { _suppressedModifiers |= bit; return new IntPtr(1); }
                }
                if (up) _modifierKeys &= ~bit;
                return Win32Api.CallNextHookEx(_hook, code, message, data);
            }
            if (key == (uint)VirtualKey.Tab)
            {
                if (down) QueueInput(CancelRecording);
                return Win32Api.CallNextHookEx(_hook, code, message, data);
            }
            if (up) return Win32Api.CallNextHookEx(_hook, code, message, data);
            _suppressedKeys.Add(key);
            if (!_inputPending)
            {
                if (key == (uint)VirtualKey.Escape) QueueInput(CancelRecording);
                else
                {
                    uint modifiers = 0;
                    if ((_modifierKeys & 0x600) != 0) modifiers |= HotKeyGesture.ModWin;
                    if ((_modifierKeys & 0x038) != 0) modifiers |= HotKeyGesture.ModCtrl;
                    if ((_modifierKeys & 0x1C0) != 0) modifiers |= HotKeyGesture.ModAlt;
                    if ((_modifierKeys & 0x007) != 0) modifiers |= HotKeyGesture.ModShift;
                    QueueInput(() => RecordGesture(new HotKeyGesture(modifiers, key)));
                }
            }
            return new IntPtr(1);
        }
        catch
        {
            // Keep paired-release ownership; never throw across the native boundary.
            try { DispatcherQueue.TryEnqueue(() => { CancelRecording(); RecordingFailed?.Invoke(ProductStrings.Get("Settings.HotKeyRecordingFailed")); }); }
            catch { }
            return Win32Api.CallNextHookEx(_hook, code, message, data);
        }
    }

    private bool CanRecord() => IsEnabled && XamlRoot is not null
        && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), this);
    private bool HasRecordingFocus() => IsRecording && _requested && CanRecord() && GetForegroundWindow() == _recordingWindow;

    private void QueueInput(Action action)
    {
        if (_inputPending) return;
        _inputPending = true;
        var generation = _recordingGeneration;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsRecording || generation != _recordingGeneration) return;
            _inputPending = false;
            if (!HasRecordingFocus()) { CancelRecording(); return; }
            action();
        })) CancelRecording();
    }

    private void RecordGesture(HotKeyGesture gesture)
    {
        if (HotKeyGesture.Validate(gesture) is { } error) { RecordingFailed?.Invoke(error); return; }
        _gestureCompletion = CompleteGestureAsync(gesture);
    }

    private async Task CompleteGestureAsync(HotKeyGesture gesture)
    {
        var completion = CancelRecordingAsync();
        var generation = _recordingGeneration;
        try { await completion; }
        catch { return; }
        if (generation != _recordingGeneration || _hook != IntPtr.Zero || _admitted) return;
        Text = Display(gesture.ToConfigString());
        GestureRecorded?.Invoke(gesture);
    }

    private static uint ModifierKeyBit(uint key) => key switch
    {
        0x10 => 0x001, 0xA0 => 0x002, 0xA1 => 0x004,
        0x11 => 0x008, 0xA2 => 0x010, 0xA3 => 0x020,
        0x12 => 0x040, 0xA4 => 0x080, 0xA5 => 0x100,
        0x5B => 0x200, 0x5C => 0x400,
        _ => 0,
    };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private static string Display(string? configured) =>
        HotKeyGesture.TryParse(configured, out var gesture)
            ? gesture!.ToDisplayString() : configured ?? string.Empty;
}
