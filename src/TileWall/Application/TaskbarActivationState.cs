namespace TileWall.ApplicationModel;

internal readonly record struct TaskbarArea(int X, int Y, int Width, int Height);
internal sealed record TaskbarActivationObservation(bool Available, bool AutoHide, nint Window,
    TaskbarArea Screen, TaskbarArea WorkArea, TaskbarArea EffectiveArea, string? Failure = null);
internal sealed record TaskbarActivationResult(bool Active, string? Failure = null);

