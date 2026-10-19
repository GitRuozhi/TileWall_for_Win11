using TileWall.Core.Imaging;
using Xunit;

namespace TileWall.Core.Tests.Graphics;

public sealed class PartitionTransitionTimingTests
{
    [Fact]
    public void SinglePartitionUsesOneUnstaggered240MillisecondSwitch()
    {
        Assert.Equal(240, PartitionTransitionTiming.DurationMilliseconds(1));
        Assert.Equal(0, PartitionTransitionTiming.DelayMilliseconds(0, 1));
        Assert.Equal(0.5, PartitionTransitionTiming.LocalProgress(0.5, 0, 1));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(12)]
    [InlineData(96)]
    public void EveryPartitionFinishesWithin600MillisecondsRegardlessOfCount(int count)
    {
        Assert.Equal(600, PartitionTransitionTiming.DurationMilliseconds(count));
        Assert.Equal(360, PartitionTransitionTiming.DelayMilliseconds(count - 1, count));
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var delay = PartitionTransitionTiming.DelayMilliseconds(ordinal, count);
            Assert.Equal(0, PartitionTransitionTiming.LocalProgress(delay / 600, ordinal, count), 10);
            Assert.Equal(1, PartitionTransitionTiming.LocalProgress((delay + 240) / 600, ordinal, count), 10);
        }
    }

    [Fact]
    public void DelayedPartitionsHoldOldImageAndDoNotRestartFinishedPartitions()
    {
        Assert.Equal(0.5, PartitionTransitionTiming.LocalProgress(0.2, 0, 2));
        Assert.Equal(0, PartitionTransitionTiming.LocalProgress(0.2, 1, 2));
        Assert.Equal(1, PartitionTransitionTiming.LocalProgress(0.8, 0, 2));
        Assert.Equal(0.5, PartitionTransitionTiming.LocalProgress(0.8, 1, 2));
        Assert.Equal(0, PartitionTransitionTiming.LocalProgress(-1, 0, 2));
        Assert.Equal(1, PartitionTransitionTiming.LocalProgress(2, 1, 2));
    }

    [Fact]
    public void ShuffleOnlyChangesPresentationOrderAndIncludesEachPartitionOnce()
    {
        var first = PartitionTransitionTiming.ShuffledOrdinals(96, new Random(41));
        var second = PartitionTransitionTiming.ShuffledOrdinals(96, new Random(42));
        Assert.Equal(Enumerable.Range(0, 96), first.Order());
        Assert.Equal(Enumerable.Range(0, 96), second.Order());
        Assert.False(first.SequenceEqual(second));
    }
}
