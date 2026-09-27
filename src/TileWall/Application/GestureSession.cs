using TileWall.Core.Wall;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace TileWall.ApplicationModel;

internal enum GesturePhase { Idle, Pressed, Dragging }

/// <summary>One pointer's press, capture, preview, and idempotent termination.</summary>
internal sealed class GestureSession
{
    public GesturePhase Phase { get; private set; }
    public uint PointerId { get; private set; }
    public string? MemberId { get; private set; }
    public string? GroupId { get; private set; }
    public string NewGroupId { get; private set; } = string.Empty;
    public Point Start { get; private set; }
    public Point GrabOffset { get; private set; }
    public Point WindowOrigin { get; private set; }
    public FrameworkElement? GhostVisual { get; private set; }
    public Border? GhostHost { get; private set; }
    public WallLayoutResult? Preview { get; private set; }

    public bool Begin(uint pointerId, Point position, Point grabOffset, Point windowOrigin,
        FrameworkElement? ghostVisual, string? memberId, string? groupId)
    {
        if (Phase != GesturePhase.Idle || memberId is null && groupId is null) return false;
        PointerId = pointerId;
        Start = position;
        GrabOffset = grabOffset;
        WindowOrigin = windowOrigin;
        GhostVisual = ghostVisual;
        MemberId = memberId;
        GroupId = memberId is null ? groupId : null;
        NewGroupId = $"group-{Guid.NewGuid():N}";
        Phase = GesturePhase.Pressed;
        return true;
    }

    public bool Advance(Point current, double thresholdDip)
    {
        if (Phase == GesturePhase.Idle) return false;
        if (Phase == GesturePhase.Pressed &&
            Math.Abs(current.X - Start.X) + Math.Abs(current.Y - Start.Y) >= thresholdDip)
            Phase = GesturePhase.Dragging;
        return Phase == GesturePhase.Dragging;
    }

    public void SetPreview(WallLayoutResult? result) => Preview = result;
    public void SetGhostHost(Border host) => GhostHost = host;
    public Point ToCommittedPoint(Point localPoint, Point currentWindowOrigin, double scale) =>
        new(localPoint.X + (currentWindowOrigin.X - WindowOrigin.X) / scale,
            localPoint.Y + (currentWindowOrigin.Y - WindowOrigin.Y) / scale);

    public bool End()
    {
        var wasDragging = Phase == GesturePhase.Dragging;
        Phase = GesturePhase.Idle;
        PointerId = 0;
        MemberId = null;
        GroupId = null;
        NewGroupId = string.Empty;
        GhostVisual = null;
        GhostHost = null;
        Preview = null;
        return wasDragging;
    }
}
