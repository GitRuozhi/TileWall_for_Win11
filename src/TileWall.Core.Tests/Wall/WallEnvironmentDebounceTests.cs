using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class WallEnvironmentDebounceTests
{
    private static readonly WallDisplayEnvironment Original = new("screen-a", 1920, 1080, 1, "1");
    private static readonly WallDisplayEnvironment OtherScreen = Original with { Topology = "screen-b" };
    private static readonly WallDisplayEnvironment Smaller = Original with { Width = 1280 };

    [Fact]
    public void RepeatedParameterNotificationKeepsDeadline()
    {
        var debounce = new WallEnvironmentDebounce();
        debounce.Observe(Original, 0, initialFitRequired: false);
        debounce.Observe(Smaller, 1000, initialFitRequired: false);
        debounce.Observe(Smaller, 5000, initialFitRequired: false);
        Assert.Equal(5, debounce.DelaySeconds);
        Assert.Equal(6000L, debounce.Deadline);

        debounce.Observe(Smaller with { Scale = 1.25 }, 5500, initialFitRequired: false);
        Assert.Equal(10500L, debounce.Deadline);
    }

    [Fact]
    public void TopologyChangeKeepsLongDelayUntilOriginalEnvironmentReturns()
    {
        var debounce = new WallEnvironmentDebounce();
        debounce.Observe(Original, 0, initialFitRequired: false);
        debounce.Observe(OtherScreen, 1000, initialFitRequired: false);
        Assert.Equal(16000L, debounce.Deadline);
        debounce.Observe(Smaller, 5000, initialFitRequired: false);
        Assert.Equal(15, debounce.DelaySeconds);
        Assert.Equal(20000L, debounce.Deadline);
        debounce.Observe(Original, 8000, initialFitRequired: false);
        Assert.Null(debounce.Deadline);
    }

    [Fact]
    public void HideOrDisplayOffCancelsDeadlineAndResumeStartsAnotherWait()
    {
        var debounce = new WallEnvironmentDebounce();
        debounce.Observe(Original, 0, initialFitRequired: false);
        debounce.Observe(OtherScreen, 1000, initialFitRequired: false);
        debounce.Suspend();
        Assert.Null(debounce.Deadline);
        debounce.Observe(OtherScreen, 20000, initialFitRequired: false);
        Assert.Equal(35000L, debounce.Deadline);

        debounce.Suspend();
        debounce.Observe(Original, 21000, initialFitRequired: false);
        Assert.Null(debounce.Deadline);
    }

    [Fact]
    public void UnreadableSamplePausesWaitingWithoutShorteningMixedDelay()
    {
        var debounce = new WallEnvironmentDebounce();
        debounce.Observe(Original, 0, initialFitRequired: false);
        debounce.Observe(OtherScreen, 1000, initialFitRequired: false);
        debounce.Suspend(keepTopology: true);
        Assert.Null(debounce.Deadline);
        debounce.Observe(Smaller, 3000, initialFitRequired: false);
        Assert.Equal(18000L, debounce.Deadline);
    }

    [Fact]
    public void InitialOverflowAndAcceptedUserEditGetFullWait()
    {
        var debounce = new WallEnvironmentDebounce();
        debounce.Observe(Smaller, 1000, initialFitRequired: true);
        Assert.Equal(16000L, debounce.Deadline);
        var beforeEdit = debounce.Revision;

        debounce.Restart(14000);
        Assert.True(debounce.Revision > beforeEdit);
        Assert.Equal(29000L, debounce.Deadline);
        debounce.Finish(Smaller, beforeEdit, accepted: false);
        Assert.Equal(29000L, debounce.Deadline);
    }

    [Fact]
    public void AcceptedSaveKeepsNewerChangeAndPreventsDuplicateDeadline()
    {
        var debounce = new WallEnvironmentDebounce();
        debounce.Observe(Original, 0, initialFitRequired: false);
        debounce.Observe(OtherScreen, 1000, initialFitRequired: false);
        var committing = debounce.Revision;

        debounce.Observe(Original, 16500, initialFitRequired: false);
        Assert.Null(debounce.Deadline);
        debounce.Finish(OtherScreen, committing, accepted: true);
        Assert.Equal(31500L, debounce.Deadline);

        debounce.Finish(Original, debounce.Revision, accepted: true);
        debounce.Observe(Original, 32100, initialFitRequired: false);
        Assert.Null(debounce.Deadline);
    }
}
