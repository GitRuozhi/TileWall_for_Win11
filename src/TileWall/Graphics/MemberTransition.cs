using TileWall.Core.Imaging;

namespace TileWall.Graphics;

/// <summary>Presentation-only effects; selection never consumes the image shuffle sequence.</summary>
public enum MemberTransitionEffect
{
    None, Crossfade, SlideLeft, SlideRight, SlideUp, SlideDown,
    FlipHorizontal, FlipVertical, ZoomFade,
}

/// <summary>One frame pair and clock, with presentation-only start ordinals for fixed partitions.</summary>
public sealed record MemberTransition(PuzzleFrame? PreviousFrame, PuzzleFrame? NextFrame,
    MemberTransitionEffect Effect, double Progress, IReadOnlyList<int>? PartitionOrdinals = null)
{
    public double ProgressForPartition(int index) => PartitionOrdinals is { Count: > 0 } ordinals &&
        index >= 0 && index < ordinals.Count
        ? PartitionTransitionTiming.LocalProgress(Progress, ordinals[index], ordinals.Count)
        : Math.Clamp(Progress, 0, 1);
}
