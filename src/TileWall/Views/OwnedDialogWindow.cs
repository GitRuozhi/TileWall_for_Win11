using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TileWall.Shell.Interop;
using Windows.Foundation;
using Windows.Graphics;

namespace TileWall.Views;

/// <summary>A content-sized modal window; the wall remains in its anchored viewport.</summary>
internal sealed class OwnedDialogWindow : Window
{
    private readonly Window _owner;
    private readonly DialogRoot _root;
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ContentDialog? _dialog;
    private FrameworkElement? _title, _contentPresenter;
    private bool _releasing, _fitQueued, _fitting, _shown, _opened;
    private uint? _dragPointer;
    private NativePoint _dragStart;
    private PointInt32 _dragOrigin;
    private (int Width, int Height, int Radius) _regionSize;
    private ElementTheme _pendingTheme;
    private ComboBox? _themeWaitCombo;
    private bool _themeQueued;

    internal OwnedDialogWindow(Window owner, Action dismiss)
    {
        _owner = owner;
        _root = new DialogRoot(this);
        _root.RequestedTheme = (owner.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;
        Title = "TileWall";
        Content = _root;
        _root.Loaded += (_, _) => _loaded.TrySetResult();
        _root.LayoutUpdated += (_, _) => QueueFit();
        // The stock card wraps its title in a ScrollViewer, which may handle pointer
        // events before they bubble here. Observe the complete captured gesture route.
        _root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        _root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerMoved), true);
        _root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => EndDrag(e)), true);
        _root.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, e) => EndDrag(e)), true);
        _root.PointerCaptureLost += (_, _) => _dragPointer = null;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // Cloaking permits the bootstrap XamlRoot layout without displaying an empty HWND.
        var cloak = 1;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(hwnd, 13, ref cloak, sizeof(int)));
        // GWLP_HWNDPARENT establishes an owned top-level window, not a child HWND.
        Win32Api.SetWindowLongPtrW(hwnd, -8, WinRT.Interop.WindowNative.GetWindowHandle(owner));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = owner.AppWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: true };
        }
        RemoveNativeFrame(hwnd);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Changed += (_, e) => { if (e.DidPositionChange && !_fitting) QueueFit(); };
        AppWindow.Closing += (_, e) =>
        {
            if (_releasing) return;
            e.Cancel = true;
            dismiss(); // ContentDialog.Closing retains its existing draft/discard policy.
        };
    }

    internal async Task InitializeAsync()
    {
        var work = WorkArea;
        var scale = (_owner.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1;
        // Constructors use XamlRoot.Size as their initial page budget. Keep that budget
        // independent of the final card size, including when a small exit prompt replaces it.
        var width = Math.Min(work.Width, (int)Math.Ceiling(800 * scale));
        var height = Math.Min(work.Height, (int)Math.Ceiling(780 * scale));
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2, width, height));
        // Bootstrap XAML without activating an invisible window or disabling the current
        // foreground owner. Transfer activation only once the fitted card is visible.
        AppWindow.Show(false);
        await _loaded.Task;
        _root.XamlRoot!.Changed += (_, _) => { _root.InvalidateMeasure(); QueueFit(); };
    }

    internal void ApplyTheme(ElementTheme theme)
    {
        _pendingTheme = theme;
        if (_releasing || _root.RequestedTheme == theme || _themeQueued) return;
        // A system-theme notification may arrive while a ComboBox popup is open.
        // Change its root only after the popup closes and the selection route unwinds.
        _themeQueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ApplyPendingTheme);
    }

    private void ApplyPendingTheme()
    {
        _themeQueued = false;
        if (_releasing) return;
        var open = FindOpenCombo(_root);
        if (open is not null)
        {
            if (ReferenceEquals(_themeWaitCombo, open)) return;
            DetachThemeWait();
            _themeWaitCombo = open;
            open.DropDownClosed += OnThemeDropDownClosed;
            return;
        }
        DetachThemeWait();
        if (_root.RequestedTheme != _pendingTheme) _root.RequestedTheme = _pendingTheme;
    }

    private void OnThemeDropDownClosed(object? sender, object args)
    {
        DetachThemeWait();
        ApplyTheme(_pendingTheme);
    }

    private void DetachThemeWait()
    {
        if (_themeWaitCombo is null) return;
        _themeWaitCombo.DropDownClosed -= OnThemeDropDownClosed;
        _themeWaitCombo = null;
    }

    private static ComboBox? FindOpenCombo(DependencyObject parent)
    {
        if (parent is ComboBox { IsDropDownOpen: true } combo) return combo;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindOpenCombo(VisualTreeHelper.GetChild(parent, i)) is { } open) return open;
        return null;
    }

    internal static Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (dialog.XamlRoot?.Content is not DialogRoot root)
            throw new InvalidOperationException("The dialog requires its owned modal root.");
        return root.Host.ShowDialogAsync(dialog);
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        _dialog = dialog;
        dialog.HorizontalAlignment = HorizontalAlignment.Left;
        dialog.VerticalAlignment = VerticalAlignment.Top;
        dialog.Opened += OnDialogOpened;
        _root.Children.Add(dialog);
        try
        {
            // InPlace keeps the stock card and ContentDialog behavior without a popup
            // canvas or dimming background surrounding the independently modal HWND.
            var pending = dialog.ShowAsync(ContentDialogPlacement.InPlace);
            dialog.ApplyTemplate();
            _title = FindPart(dialog, "Title");
            _root.UpdateLayout();
            FitToContent();
            return await pending;
        }
        finally
        {
            dialog.Opened -= OnDialogOpened;
            _dialog = null; _title = null; _contentPresenter = null; _root.Children.Clear();
        }
    }

    private void OnDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        _opened = true;
        _root.UpdateLayout();
        _title = FindPart(sender, "Title");
        _contentPresenter = FindPart(sender, "Content");
        FitToContent();
    }

    private static void RemoveNativeFrame(IntPtr hwnd)
    {
        const long frameStyles = 0x00C00000L | 0x00040000L; // WS_CAPTION | WS_THICKFRAME
        var style = Win32Api.GetWindowLongPtrW(hwnd, -16).ToInt64();
        Win32Api.SetWindowLongPtrW(hwnd, -16, new IntPtr(style & ~frameStyles));
        Win32Api.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0020 | 0x0001 | 0x0002 | 0x0004 | 0x0010);
        // Suppress only DWM's independent frame. The ContentDialog border and its stock
        // rounded corners remain; this attribute is simply unsupported on Windows 10.
        uint border = 0xFFFFFFFE;
        Win32Api.DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(uint));
    }

    private RectInt32 WorkArea => DisplayArea.GetFromWindowId(
        _shown ? AppWindow.Id : _owner.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;

    private double Scale => _root.XamlRoot?.RasterizationScale ?? 1;

    private void QueueFit()
    {
        if (_releasing || _dialog is null || _fitQueued || _fitting) return;
        _fitQueued = true;
        DispatcherQueue.TryEnqueue(() => { _fitQueued = false; FitToContent(); });
    }

    private void FitToContent()
    {
        if (_releasing || _dialog is null || _fitting) return;
        var desired = _dialog.DesiredSize;
        if (desired.Width <= 0 || desired.Height <= 0) return;
        _fitting = true;
        try
        {
            var work = WorkArea;
            var width = Math.Clamp((int)Math.Ceiling(desired.Width * Scale), 1, work.Width);
            var height = Math.Clamp((int)Math.Ceiling(desired.Height * Scale), 1, work.Height);
            var position = AppWindow.Position;
            var x = _shown ? position.X : work.X + (work.Width - width) / 2;
            var y = _shown ? position.Y : work.Y + (work.Height - height) / 2;
            x = Math.Clamp(x, work.X, work.X + work.Width - width);
            y = Math.Clamp(y, work.Y, work.Y + work.Height - height);
            if (AppWindow.Size.Width != width || AppWindow.Size.Height != height || position.X != x || position.Y != y)
                AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
            ApplyCardRegion(width, height);
            if (!_shown && _opened)
            {
                _root.UpdateLayout();
                var cloak = 0;
                Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 13, ref cloak, sizeof(int)));
                _shown = true;
                Activate();
                Win32Api.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
                Win32Api.EnableWindow(WinRT.Interop.WindowNative.GetWindowHandle(_owner), false);
                // Initial Opened handlers ran while cloaked. Reapply their selected XAML
                // focus after native activation so text input reaches the intended control.
                if (FocusManager.GetFocusedElement(_root.XamlRoot) is Control focused)
                    focused.Focus(FocusState.Programmatic);
            }
        }
        finally { _fitting = false; }
    }

    private void ApplyCardRegion(int width, int height)
    {
        // Shape the HWND to the stock card radius, also on Win10, so no native background
        // pixels remain outside its rounded corners. Windows owns the HRGN on success.
        var radius = (int)Math.Ceiling((_dialog?.CornerRadius.TopLeft ?? 0) * Scale);
        if (_regionSize == (width, height, radius)) return;
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        if (region == IntPtr.Zero) return;
        if (SetWindowRgn(WinRT.Interop.WindowNative.GetWindowHandle(this), region, true) == 0)
            DeleteObject(region);
        else _regionSize = (width, height, radius);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_title is null || _dragPointer is not null) return;
        var point = e.GetCurrentPoint(_root);
        var titleTop = _title.TransformToVisual(_root).TransformPoint(new Point());
        var titleBottom = titleTop.Y + _title.ActualHeight + _title.Margin.Bottom;
        if (_contentPresenter is not null)
            titleBottom = Math.Min(titleBottom, _contentPresenter.TransformToVisual(_root).TransformPoint(new Point()).Y);
        // This band contains only the title and its top/side padding. Fields, scrolling,
        // preview gestures and commands below it retain their normal input route.
        if (!point.Properties.IsLeftButtonPressed || point.Position.Y < 0 || point.Position.Y >= titleBottom ||
            !GetCursorPos(out _dragStart) || !_root.CapturePointer(e.Pointer)) return;
        _dragPointer = e.Pointer.PointerId;
        _dragOrigin = AppWindow.Position;
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointer != e.Pointer.PointerId || !GetCursorPos(out var cursor)) return;
        var work = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest).WorkArea;
        var size = AppWindow.Size;
        var x = Math.Clamp(_dragOrigin.X + cursor.X - _dragStart.X, work.X, work.X + Math.Max(0, work.Width - size.Width));
        var y = Math.Clamp(_dragOrigin.Y + cursor.Y - _dragStart.Y, work.Y, work.Y + Math.Max(0, work.Height - size.Height));
        AppWindow.Move(new PointInt32(x, y));
        e.Handled = true;
    }

    private void EndDrag(PointerRoutedEventArgs e)
    {
        if (_dragPointer != e.Pointer.PointerId) return;
        _dragPointer = null;
        _root.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private static FrameworkElement? FindPart(DependencyObject parent, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element && element.Name == name) return element;
            if (FindPart(child, name) is { } found) return found;
        }
        return null;
    }

    internal void Release()
    {
        if (_releasing) return;
        _releasing = true;
        DetachThemeWait();
        Win32Api.EnableWindow(WinRT.Interop.WindowNative.GetWindowHandle(_owner), true);
        Close();
        if (_owner.AppWindow.IsVisible) _owner.Activate();
    }

    /// <summary>Measure at the screen budget so a small host cannot lock out larger pages.</summary>
    private sealed class DialogRoot(OwnedDialogWindow host) : Panel
    {
        internal OwnedDialogWindow Host => host;

        protected override Size MeasureOverride(Size availableSize)
        {
            var work = host.WorkArea;
            var budget = new Size(work.Width / host.Scale, work.Height / host.Scale);
            var desired = new Size();
            foreach (var child in Children)
            {
                child.Measure(budget);
                desired = child.DesiredSize;
            }
            return new Size(Math.Min(availableSize.Width, desired.Width), Math.Min(availableSize.Height, desired.Height));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (var child in Children) child.Arrange(new Rect(new Point(), child.DesiredSize));
            return finalSize;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
