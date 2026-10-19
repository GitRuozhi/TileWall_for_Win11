namespace TileWall.Core.Imaging;

/// <summary>Presentation timing only; no image identity or carousel state belongs here.</summary>
internal static class PartitionTransitionTiming
{
    internal const double PartitionDurationMilliseconds = 240;
    internal const double StartWindowMilliseconds = 360;

    internal static double DurationMilliseconds(int count) => count > 1
        ? PartitionDurationMilliseconds + StartWindowMilliseconds : PartitionDurationMilliseconds;

    internal static double DelayMilliseconds(int ordinal, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (ordinal < 0 || ordinal >= count) throw new ArgumentOutOfRangeException(nameof(ordinal));
        return count == 1 ? 0 : StartWindowMilliseconds * ordinal / (count - 1);
    }

    internal static double LocalProgress(double progress, int ordinal, int count)
        => Math.Clamp((Math.Clamp(progress, 0, 1) * DurationMilliseconds(count) -
            DelayMilliseconds(ordinal, count)) / PartitionDurationMilliseconds, 0, 1);

    /// <summary>Maps a fixed partition index to its shuffled start ordinal for this round.</summary>
    internal static int[] ShuffledOrdinals(int count, Random random)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentNullException.ThrowIfNull(random);
        var ordinals = Enumerable.Range(0, count).ToArray();
        for (var index = count - 1; index > 0; index--)
        {
            var other = random.Next(index + 1);
            (ordinals[index], ordinals[other]) = (ordinals[other], ordinals[index]);
        }
        return ordinals;
    }
}
