using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TileWall.Core.Settings;
using TileWall.Resources;
using Windows.System;

namespace TileWall.Views;

/// <summary>Records a valid key chord; cancellation never becomes a settings edit.</summary>
internal sealed partial class HotKeyRecorder : TextBox
{
    private string _configured = string.Empty;
    // Keep the native callback alive for the lifetime of this control.
    private readonly KeyboardHook _keyboardHook;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _focusWatch;
    private IntPtr _hook;
    private IntPtr _recordingWindow;
    private uint _modifierKeys;
    private uint _suppressedModifiers;
    private int _recordingGeneration;
    private bool _inputPending;
    internal bool IsRecording { get; private set; }
    internal event Action<bool>? RecordingChanged;
    internal event Action<HotKeyGesture>? GestureRecorded;
    internal event Action<string>? RecordingFailed;

    internal HotKeyRecorder()
    {
        _keyboardHook = OnKeyboardHook;
        // XAML can retain its logical focus when another window is activated.
        // Watch only during recording so deactivation also releases the hook.
        _focusWatch = DispatcherQueue.CreateTimer();
        _focusWatch.Interval = TimeSpan.FromMilliseconds(100);
        _focusWatch.IsRepeating = true;
        _focusWatch.Tick += (_, _) => { if (!HasRecordingFocus()) CancelRecording(); };
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
        if (!IsRecording) Text = _configured;
    }

    internal void CancelRecording()
    {
        _focusWatch.Stop();
        RemoveHook();
        if (!IsRecording) return;
        IsRecording = false; _inputPending = false; _modifierKeys = 0; _suppressedModifiers = 0; _recordingWindow = IntPtr.Zero;
        _recordingGeneration++;
        Text = _configured; RecordingChanged?.Invoke(false);
    }

    private void BeginRecording()
    {
        if (IsRecording || !IsEnabled || XamlRoot is null || !ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), this)) return;
        try
        {
            // Do not overwrite a handle if native teardown ever failed.
            RemoveHook();
            if (_hook != IntPtr.Zero) { FailRecording(ProductStrings.Get("Settings.HotKeyRecordingFailed")); return; }
            _recordingWindow = GetForegroundWindow();
            GetWindowThreadProcessId(_recordingWindow, out var processId);
            if (_recordingWindow == IntPtr.Zero || processId != Environment.ProcessId) return;
            // LL hooks run before asynchronous key state changes. Track modifier
            // transitions ourselves, including keys already held when recording starts.
            _modifierKeys = 0; _suppressedModifiers = 0;
            foreach (var key in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
                if (GetAsyncKeyState(key) < 0) _modifierKeys |= ModifierKeyBit((uint)key);
            _hook = SetWindowsHookEx(13, _keyboardHook, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) { FailRecording(ProductStrings.Get("Settings.HotKeyRecordingFailed")); return; }
            _recordingGeneration++;
            IsRecording = true; RecordingChanged?.Invoke(true);
            _focusWatch.Start();
            Text = "…";
        }
        catch { FailRecording(ProductStrings.Get("Settings.HotKeyRecordingFailed")); }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsRecording) return;
        if (e.Key == VirtualKey.Tab) { CancelRecording(); return; }
        e.Handled = true;
        if (e.Key == VirtualKey.Escape) { CancelRecording(); return; }
        // The hook owns chord capture, including registered hotkeys that never
        // produce a routed KeyDown. Do not submit the same chord a second time.
    }

    private IntPtr OnKeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code < 0) return CallNextHookEx(_hook, code, message, data);
        try
        {
            if (!HasRecordingFocus())
            {
                if (IsRecording) QueueInput(CancelRecording);
                return CallNextHookEx(_hook, code, message, data);
            }
            var down = message.ToInt64() is 0x0100 or 0x0104; // WM_KEYDOWN / WM_SYSKEYDOWN
            var up = message.ToInt64() is 0x0101 or 0x0105;
            if (!down && !up) return CallNextHookEx(_hook, code, message, data);
            // Only the VK is needed; do not inspect/store text, scan codes or extra
            // input metadata. SendInput is intentionally accepted for accessibility.
            var key = unchecked((uint)Marshal.ReadInt32(data));
            var bit = ModifierKeyBit(key);
            if (bit != 0)
            {
                if (down)
                {
                    if ((_modifierKeys & bit) == 0) _suppressedModifiers |= bit;
                    _modifierKeys |= bit;
                }
                var suppress = (_suppressedModifiers & bit) != 0;
                if (up) { _modifierKeys &= ~bit; _suppressedModifiers &= ~bit; }
                // Suppress Win down as well as the chord so releasing Win cannot
                // open Start after completion. A modifier held before recording
                // must still deliver its release, avoiding a stuck system key.
                return suppress ? new IntPtr(1) : CallNextHookEx(_hook, code, message, data);
            }
            if (key == (uint)VirtualKey.Tab)
            {
                if (down) QueueInput(CancelRecording);
                return CallNextHookEx(_hook, code, message, data);
            }
            if (down && !_inputPending)
            {
                if (key == (uint)VirtualKey.Escape) QueueInput(CancelRecording);
                else
                {
                    uint modifiers = 0;
                    if ((_modifierKeys & 0x600) != 0) modifiers |= HotKeyGesture.ModWin;
                    if ((_modifierKeys & 0x038) != 0) modifiers |= HotKeyGesture.ModCtrl;
                    if ((_modifierKeys & 0x1C0) != 0) modifiers |= HotKeyGesture.ModAlt;
                    if ((_modifierKeys & 0x007) != 0) modifiers |= HotKeyGesture.ModShift;
                    var gesture = new HotKeyGesture(modifiers, key);
                    QueueInput(() => RecordGesture(gesture));
                }
            }
            return new IntPtr(1);
        }
        catch
        {
            // Never allow managed exceptions to cross the native callback boundary.
            var generation = _recordingGeneration;
            RemoveHook();
            try
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (IsRecording && generation == _recordingGeneration)
                        FailRecording(ProductStrings.Get("Settings.HotKeyRecordingFailed"));
                });
            }
            catch { }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        }
    }

    private bool HasRecordingFocus() => IsRecording && IsEnabled && XamlRoot is not null
        && GetForegroundWindow() == _recordingWindow
        && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), this);

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
            try { action(); }
            catch { FailRecording(ProductStrings.Get("Settings.HotKeyRecordingFailed")); }
        })) FailRecording(ProductStrings.Get("Settings.HotKeyRecordingFailed"));
    }

    private void RecordGesture(HotKeyGesture gesture)
    {
        if (HotKeyGesture.Validate(gesture) is { } error) { RecordingFailed?.Invoke(error); return; }
        CancelRecording();
        Text = Display(gesture.ToConfigString());
        GestureRecorded?.Invoke(gesture);
    }

    private void FailRecording(string error)
    {
        CancelRecording(); Text = _configured; RecordingFailed?.Invoke(error);
    }

    private void RemoveHook()
    {
        if (_hook == IntPtr.Zero) return;
        if (UnhookWindowsHookEx(_hook)) _hook = IntPtr.Zero;
    }

    private static uint ModifierKeyBit(uint key) => key switch
    {
        0x10 => 0x001, 0xA0 => 0x002, 0xA1 => 0x004,
        0x11 => 0x008, 0xA2 => 0x010, 0xA3 => 0x020,
        0x12 => 0x040, 0xA4 => 0x080, 0xA5 => 0x100,
        0x5B => 0x200, 0x5C => 0x400,
        _ => 0,
    };

    private delegate IntPtr KeyboardHook(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, KeyboardHook callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    private static string Display(string? configured) =>
        HotKeyGesture.TryParse(configured, out var gesture) && gesture!.VirtualKey == 0xC0
            ? gesture.ToConfigString().Replace("Oem3", "~", StringComparison.Ordinal) : configured ?? string.Empty;
}
