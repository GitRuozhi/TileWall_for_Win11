using TileWall.Core.Carousel;
using TileWall.Core.Configuration;
using TileWall.Core.Grid;
using TileWall.Core.Imaging;
using TileWall.Shell.Imaging;

namespace TileWall.Shell;

/// <summary>
/// 轮播引擎的 Shell 适配器（M6 设计 §2.2 装配）：Core <see cref="CarouselCoordinator"/> 的
/// <see cref="ICarouselImageGate"/>（T1 解码，经 <see cref="ImageLoader"/>，异步不阻塞 UI 线程）
/// 与 <see cref="ICarouselDriveHost"/>（T3 = LayoutCommitService.SaveWithoutUndo 既有通道；
/// ShowImage/StartFlip = GroupVisualHost 上墙）双实现。位图资产按组缓存一份（RenderAll 重建后重放）。
/// </summary>
public sealed class WallCarouselEngine : ICarouselImageGate, ICarouselDriveHost
{
    private readonly LayoutCommitService _commit;
    private readonly ImageLoader _loader;
    private readonly Imaging.GroupVisualHost _visuals;
    private readonly GridMetrics _metrics;
    private readonly Func<double> _rasterScale;
    private readonly Dictionary<string, (string ImageId, GroupImageAssets Assets)> _prepared = new(StringComparer.Ordinal);

    public WallCarouselEngine(LayoutCommitService commit, ImageLoader loader, Imaging.GroupVisualHost visuals, GridMetrics metrics, Func<double> rasterScale)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(visuals);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(rasterScale);
        _commit = commit;
        _loader = loader;
        _visuals = visuals;
        _metrics = metrics;
        _rasterScale = rasterScale;
    }

    public TileWallConfig CurrentConfig => _commit.Current;

    async Task<bool> ICarouselImageGate.PrepareAsync(string groupId, string imageId)
    {
        var group = FindGroup(groupId);
        if (group is null)
        {
            return false;
        }

        var canvas = SharedCanvas.CanvasSize(new GridSize(group.Bounds.Width, group.Bounds.Height), _metrics);
        var assets = await _loader.LoadAsync(imageId, canvas, _rasterScale());
        if (assets is null)
        {
            // 加载失败保留既有缓存资产（§6.2/B19「失败保留旧图」的 UI 侧）：缓存条目恒为最近一次
            // 成功上墙的图，失败链后任何 RenderAll → ReapplyRenderedImages 仍能重放当前显示图，
            // 而不是整组跌回兜底底色
            return false;
        }

        _prepared[groupId] = (imageId, assets);
        return true;
    }

    public bool PersistWithoutUndo(TileWallConfig config) => _commit.SaveWithoutUndo(config, out _); // 不触碰撤销槽（M5 §9.5）

    public void ShowImage(string groupId, string imageId)
    {
        if (TryGetPrepared(groupId, imageId, out var assets))
        {
            _visuals.ApplyImage(groupId, imageId, assets); // 首图/恢复：直显无翻转
        }
    }

    public void StartFlip(string groupId, string imageId)
    {
        if (TryGetPrepared(groupId, imageId, out var assets))
        {
            _visuals.StartFlip(groupId, imageId, assets); // T4：组内全部块同帧启动
        }
    }

    /// <summary>M6 设计 §7.6 第 2 行：动画中收起 → 全组跳终态（front=新图，不从半张翻转继续）。</summary>
    public void SettleFlips() => _visuals.SettleAll();

    private bool TryGetPrepared(string groupId, string imageId, out GroupImageAssets assets)
    {
        if (_prepared.TryGetValue(groupId, out var entry) && string.Equals(entry.ImageId, imageId, StringComparison.OrdinalIgnoreCase))
        {
            assets = entry.Assets;
            return true;
        }

        assets = null!;
        return false;
    }

    private GroupObject? FindGroup(string groupId) =>
        _commit.Current.Objects.OfType<GroupObject>().FirstOrDefault(g => g.Id == groupId);
}
