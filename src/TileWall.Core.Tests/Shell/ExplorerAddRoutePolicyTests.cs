using Xunit;

using TileWall.Core.Shell;

namespace TileWall.Core.Tests.Shell;

/// <summary>C2/F-03：进程间送达路由的纯判定——有宿主投递、packaged 失败不直启。</summary>
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
}
