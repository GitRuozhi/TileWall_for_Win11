using System.Globalization;
using Microsoft.UI.Dispatching;
using TileWall.Core.Carousel;
using TileWall.Core.Components;

namespace TileWall.Shell;

/// <summary>
/// 时间日期 1 Hz 刷新驱动（M8 设计 §4.1，C16）：计时器只在墙 Shown/Hidden 之间切换——
/// Hidden 即 Stop()（隐藏期零重绘零计时，§14.5 同款语义，与轮播停表同一钩子、零新增状态源）；
/// Shown 先即时求值当前时间再续 1 Hz（再显即当前值，无「错过时长」状态，结构上不可能补播）。
/// 模态会话期间不暂停：墙仍物理可见（遮罩之下），组件继续走时（§4.1 表）。
/// </summary>
public sealed class ClockTickDriver
{
    private readonly WallPresenter _presenter;
    private readonly IWallVisibility _visibility;
    private readonly IClock _clock;
    private readonly CultureInfo _culture;
    private readonly DispatcherQueueTimer _tickTimer;

    public ClockTickDriver(
        WallPresenter presenter,
        IWallVisibility visibility,
        IClock clock,
        CultureInfo culture,
        DispatcherQueue dispatcherQueue)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(visibility);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(dispatcherQueue);
        _presenter = presenter;
        _visibility = visibility;
        _clock = clock;
        _culture = culture;

        _tickTimer = dispatcherQueue.CreateTimer();
        _tickTimer.Interval = TimeSpan.FromSeconds(1);
        _tickTimer.IsRepeating = true;
        _tickTimer.Tick += (_, _) => Refresh();
    }

    /// <summary>启动（Bootstrap 完成后调用一次）：初始可见 → 立即刷新再续表；初始隐藏 → 保持停表。</summary>
    public void Start()
    {
        _visibility.Shown += OnWallShown;
        _visibility.Hidden += OnWallHidden;
        if (_visibility.IsVisible)
        {
            Refresh();
            _tickTimer.Start();
        }
    }

    private void OnWallShown()
    {
        Refresh(); // 先即时求值当前时间（C16：再显即当前值）
        _tickTimer.Start();
    }

    private void OnWallHidden()
    {
        _tickTimer.Stop(); // 隐藏期：无计时器、无重算、无重绘
    }

    private void Refresh()
    {
        try
        {
            var now = _clock.UtcNow;
            foreach (var id in _presenter.ClockIds)
            {
                var (timeLine, dateLine) = ClockTextFormatter.Format(now, _culture);
                _presenter.UpdateClockText(id, timeLine, dateLine);
            }
        }
        catch (Exception)
        {
            // 刷新为非关键路径：单 tick 异常不终止计时（下一 tick 自然重试，轮播驱动器同款）
        }
    }
}
