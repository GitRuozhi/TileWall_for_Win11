using Microsoft.UI.Dispatching;
using TileWall.Core.Carousel;
using TileWall.Shell.Imaging;

namespace TileWall.Shell;

/// <summary>
/// 轮播接线中枢的 Shell 面（M6 设计 §2.1 WallCarouselDriver / §8）：
/// 唯一哑闹钟（DispatcherQueueTimer 1 s tick，IsRepeating）→ Core <see cref="CarouselCoordinator.TickAsync"/>
/// 轮询收敛（M5 §9.2 设计意图）；墙可见性 / 模态 / 拖动全部收敛为协调器的输入（§7.5）。
/// 隐藏 = 停闹钟 + 协调器停检查（真实时间不作废，引擎零改动）；显示 = 恢复闹钟 + 显式检查（§8.1）。
/// </summary>
public sealed class WallCarouselDriver
{
    private readonly CarouselCoordinator _coordinator;
    private readonly WallCarouselEngine _engine;
    private readonly IWallVisibility _visibility;
    private readonly ModalSessionService _modal;
    private readonly DispatcherQueueTimer _tickTimer;

    public WallCarouselDriver(
        CarouselCoordinator coordinator,
        WallCarouselEngine engine,
        IWallVisibility visibility,
        ModalSessionService modal,
        WallPresenter presenter,
        DispatcherQueue dispatcherQueue)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(visibility);
        ArgumentNullException.ThrowIfNull(modal);
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(dispatcherQueue);
        _coordinator = coordinator;
        _engine = engine;
        _visibility = visibility;
        _modal = modal;

        _tickTimer = dispatcherQueue.CreateTimer();
        _tickTimer.Interval = TimeSpan.FromSeconds(1);
        _tickTimer.IsRepeating = true;
        _tickTimer.Tick += async (_, _) =>
        {
            try
            {
                await _coordinator.TickAsync();
            }
            catch (Exception)
            {
                // 轮播为非关键路径：单 tick 异常不终止哑闹钟（下一 tick 自然重试）
            }
        };

        // 挂接点（全部既有事件/机制，§2.2）：
        _visibility.Shown += OnWallShown;
        _visibility.Hidden += OnWallHidden;
        _modal.SessionOpened += OnModalOpened;
        _modal.SessionClosed += OnModalClosed;
        presenter.RenderedAll += ReapplyRenderedImages; // RenderAll 重建视觉树后重放已解码的当前图
    }

    /// <summary>启动哑闹钟（Bootstrap 完成后调用一次）。</summary>
    public void Start() => _tickTimer.Start();

    private async void OnWallShown()
    {
        _tickTimer.Start();
        try
        {
            await _coordinator.OnWallShownAsync(); // 重开已到期只切一次（B14）/未到期等剩余（B15）
        }
        catch (Exception)
        {
            // 非关键路径
        }
    }

    private void OnWallHidden()
    {
        _tickTimer.Stop(); // §14.5 隐藏期零计时任务；真实时间不作废（引擎零改动）
        _coordinator.OnWallHidden();
    }

    private void OnModalOpened() => _coordinator.ModalActive = true; // 属性窗模态期暂缓切图（§7.5）

    private async void OnModalClosed()
    {
        _coordinator.ModalActive = false;
        try
        {
            await _coordinator.ExplicitCheckAsync(); // 恢复事件 → 到期只切一次
        }
        catch (Exception)
        {
            // 非关键路径
        }
    }

    /// <summary>RenderAll 重建后：把引擎缓存的当前图重放到新视觉树（无动画）。</summary>
    public void ReapplyRenderedImages()
    {
        var config = _engine.CurrentConfig;
        foreach (var group in config.Objects.OfType<TileWall.Core.Configuration.GroupObject>())
        {
            var current = group.Carousel?.CurrentImageId;
            if (current is null)
            {
                continue;
            }

            _engine.ShowImage(group.Id, current); // 引擎仅命中已缓存资产（避免渲染路径再解码）
        }
    }
}
