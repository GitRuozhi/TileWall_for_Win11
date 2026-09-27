using Xunit;

using TileWall.Core.Shell;

namespace TileWall.Core.Tests.Shell;

/// <summary>C2/F-03：进程间送达路由的纯判定——有宿主投递、packaged 失败不直启、失败一律放弃。</summary>
public sealed class ExplorerAddRoutePolicyTests
{
    [Fact]
    public void 宿主窗存在_一律投递_与打包模式无关()
    {
        Assert.Equal(ExplorerAddRouteAction.DeliverToHost, ExplorerAddRoutePolicy.Plan(packaged: false, hostWindowFound: true));
        Assert.Equal(ExplorerAddRouteAction.DeliverToHost, ExplorerAddRoutePolicy.Plan(packaged: true, hostWindowFound: true));
    }

    [Fact]
    public void 无宿主_按打包模式选冷启动通道()
    {
        Assert.Equal(ExplorerAddRouteAction.ActivatePackagedSelf, ExplorerAddRoutePolicy.Plan(packaged: true, hostWindowFound: false));
        Assert.Equal(ExplorerAddRouteAction.LaunchUnpackagedSelf, ExplorerAddRoutePolicy.Plan(packaged: false, hostWindowFound: false));
    }

    [Fact]
    public void packaged_激活失败_放弃且绝不退化直启()
    {
        var outcome = ExplorerAddRoutePolicy.ResolveStepFailure(ExplorerAddRouteAction.ActivatePackagedSelf);
        Assert.Equal(ExplorerAddRouteAction.AbandonPackagedActivationFailed, outcome);
        Assert.NotEqual(ExplorerAddRouteAction.LaunchUnpackagedSelf, outcome); // F-03 影子实例的根因正是这条退化路径
        Assert.NotEqual(ExplorerAddRouteAction.ActivatePackagedSelf, outcome);
    }

    [Fact]
    public void unpackaged_冷启动失败_放弃()
    {
        Assert.Equal(
            ExplorerAddRouteAction.AbandonUnpackagedLaunchFailed,
            ExplorerAddRoutePolicy.ResolveStepFailure(ExplorerAddRouteAction.LaunchUnpackagedSelf));
    }

    [Fact]
    public void 投递失败_放弃_不再冷启动()
    {
        // 宿主存在但未送达（拒绝/超时）时冷启动无意义：二次启动只会再投一次并再次失败
        Assert.Equal(
            ExplorerAddRouteAction.AbandonDeliveryFailed,
            ExplorerAddRoutePolicy.ResolveStepFailure(ExplorerAddRouteAction.DeliverToHost));
    }

    [Fact]
    public void 放弃态幂等()
    {
        foreach (var abandoned in new[]
                 {
                     ExplorerAddRouteAction.AbandonDeliveryFailed,
                     ExplorerAddRouteAction.AbandonPackagedActivationFailed,
                     ExplorerAddRouteAction.AbandonUnpackagedLaunchFailed,
                 })
        {
            Assert.Equal(abandoned, ExplorerAddRoutePolicy.ResolveStepFailure(abandoned));
        }
    }
}
