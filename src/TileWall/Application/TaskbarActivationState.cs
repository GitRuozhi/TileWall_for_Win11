namespace TileWall.ApplicationModel;

internal readonly record struct TaskbarArea(int X, int Y, int Width, int Height);
internal readonly record struct TaskbarTarget(TaskbarBounds Monitor, nint Window, nint LauncherWindow);
internal readonly record struct TaskbarWindowAnchor(nint Window, uint ProcessId);
internal sealed record TaskbarActivationObservation(bool Available, bool AutoHide, TaskbarWindowAnchor Anchor,
    TaskbarArea Screen, TaskbarArea WorkArea, TaskbarArea EffectiveArea, string? Failure = null);
internal sealed record TaskbarActivationResult(bool Active, string? Failure = null);

