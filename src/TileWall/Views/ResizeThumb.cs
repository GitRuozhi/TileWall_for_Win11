using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace TileWall.Views;

/// <summary>Exposes the protected WinUI cursor for the permitted resize zones.</summary>
public sealed partial class ResizeThumb : Grid
{
    private Point _pressedAt;
    private InputSystemCursorShape? _cursorShape;
    private InputSystemCursor? _cursor;
    public ResizeThumb()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        PointerPressed += (_, e) =>
        {
            var root = XamlRoot;
            var pressed = e.GetCurrentPoint(this);
            if (root is null || !pressed.Properties.IsLeftButtonPressed) return;
            // Preserve the event point before capture/host callbacks can change layout.
            var rootPoint = e.GetCurrentPoint(root.Content).Position;
            var scale = root.RasterizationScale;
            if (!CapturePointer(e.Pointer)) return;
            IsDragging = true; _pressedAt = pressed.Position; e.Handled = true;
            DragStarted?.Invoke(this, new ResizeDragStartedEventArgs(rootPoint, scale));
        };
        PointerMoved += (_, e) =>
        {
            if (!IsDragging || e.GetCurrentPoint(this).Position == _pressedAt) return;
            e.Handled = true; DragDelta?.Invoke(this, EventArgs.Empty);
        };
        PointerReleased += (_, e) =>
        {
            if (!IsDragging) return;
            var root = XamlRoot;
            var completed = root is null ? new ResizeDragCompletedEventArgs(true) :
                new ResizeDragCompletedEventArgs(false, e.GetCurrentPoint(root.Content).Position, root.RasterizationScale);
            IsDragging = false; e.Handled = true;
            // Convert to screen coordinates in the host before capture-loss callbacks.
            try { DragCompleted?.Invoke(this, completed); }
            finally { ReleasePointerCapture(e.Pointer); }
        };
        PointerCaptureLost += (_, _) => CompleteCanceled();
        PointerCanceled += (_, _) => CompleteCanceled();
    }

    public event EventHandler<ResizeDragStartedEventArgs>? DragStarted;
    public event EventHandler? DragDelta;
    public event EventHandler<ResizeDragCompletedEventArgs>? DragCompleted;
    internal bool IsDragging { get; private set; }
    internal void CancelDrag() { CompleteCanceled(); ReleasePointerCaptures(); }
    private void CompleteCanceled()
    {
        if (!IsDragging) return;
        IsDragging = false; DragCompleted?.Invoke(this, new ResizeDragCompletedEventArgs(true));
    }

    internal void SetCursor(InputSystemCursorShape shape)
    {
        if (_cursorShape == shape) return;
        var previous = _cursor;
        _cursor = InputSystemCursor.Create(shape); _cursorShape = shape;
        ProtectedCursor = _cursor;
        previous?.Dispose();
    }
}

public sealed class ResizeDragStartedEventArgs(Point rootPoint, double rasterizationScale) : EventArgs
{
    public Point RootPoint { get; } = rootPoint;
    public double RasterizationScale { get; } = rasterizationScale;
}

public sealed class ResizeDragCompletedEventArgs(bool canceled, Point? rootPoint = null, double rasterizationScale = 1) : EventArgs
{
    public bool Canceled { get; } = canceled;
    public Point? RootPoint { get; } = rootPoint;
    public double RasterizationScale { get; } = rasterizationScale;
}
