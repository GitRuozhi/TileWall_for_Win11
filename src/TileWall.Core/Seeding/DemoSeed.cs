using TileWall.Core.Configuration;
using TileWall.Core.Grid;

namespace TileWall.Core.Seeding;

/// <summary>种子对象规格（M3 设计 §8.2 标准布置表；Id 为固定字面量 → AutomationId 确定性）。</summary>
public sealed record DemoSeedSpec(string Id, int Column, int Row, int Width, int Height, string Title, string BackgroundColor);

/// <summary>
/// 开发种子（M3 设计 §8）：给墙 → 给合法配置的纯映射。
/// 零环境读取、零时钟、零随机（承 Core 第一原则）：同墙尺寸两次调用输出逐字段相等。
/// 标准布置要求墙 ≥ 2 栏 × ≥ 4 行（12 基础格列 × 4 行）；放不下时按表序
/// 逐项 CanFitRect first-fit 尝试、放不下的项跳过（§8.3 降级，仍确定性）。
/// 环境变量触发判定（TILEWALL_SEED）留在应用层，本类不感知。
/// </summary>
public static class DemoSeed
{
    /// <summary>标准布置可用的最小墙：2 栏（16 基础格列，需 12）× 4 行。</summary>
    public const int StandardMinColumns = 2;
    public const int StandardMinRows = 4;

    /// <summary>组标题（M3 设计 §8.2 表未列；组横幅与 UIA Name 需要稳定标题，故固定字面量）。</summary>
    public const string GroupTitle = "演示 · 组";

    public static IReadOnlyList<DemoSeedSpec> StandardSpecs { get; } =
    [
        new("seed-tile-1", 0, 0, 2, 2, "演示 · 笔记", "#0078D4"),
        new("seed-tile-2", 2, 0, 1, 1, "演示 · 计算器", "#00B294"),
        new("seed-tile-3", 3, 0, 2, 1, "演示 · 邮件", "#CA5010"),
        new("seed-tile-4", 2, 1, 1, 1, "演示 · 日历", "#744DA9"),
        new("seed-tile-5", 4, 1, 2, 2, "演示 · 照片", "#C30052"),
        new("seed-tile-6", 0, 2, 1, 2, "演示 · 音乐", "#498205"),
        new("seed-group-1", 8, 0, 4, 4, GroupTitle, "#5D5D5D"),
    ];

    /// <summary>按 §8.2 标准布置（或 §8.3 first-fit 降级）生成种子配置；全部对象空目标、固定调色板。</summary>
    public static TileWallConfig Create(WallGrid wall)
    {
        ArgumentNullException.ThrowIfNull(wall);

        var objects = new List<LayoutObject>(StandardSpecs.Count);
        if (wall.Columns >= StandardMinColumns && wall.Rows >= StandardMinRows)
        {
            foreach (var spec in StandardSpecs)
            {
                objects.Add(Build(spec, new GridRect(spec.Column, spec.Row, spec.Width, spec.Height)));
            }
        }
        else
        {
            var occupied = new OccupancyMap(wall);
            foreach (var spec in StandardSpecs)
            {
                if (!GridPlacement.CanFitRect(wall, new GridSize(spec.Width, spec.Height), occupied, out var fit))
                {
                    continue; // §8.3：放不下的项跳过
                }

                occupied.Fill(fit);
                objects.Add(Build(spec, fit));
            }
        }

        return new TileWallConfig
        {
            Wall = new WallState(wall.Columns, wall.Rows),
            Objects = objects,
        };
    }

    private static LayoutObject Build(DemoSeedSpec spec, GridRect bounds) =>
        spec.Id == "seed-group-1"
            ? new GroupObject
            {
                Id = spec.Id,
                Bounds = bounds,
                Partitions = DefaultPartitions(bounds),
                Visual = new ObjectVisual { ShowTitle = true, TitleText = spec.Title, BackgroundColor = spec.BackgroundColor },
            }
            : new TileObject
            {
                Id = spec.Id,
                Bounds = bounds,
                Visual = new ObjectVisual { ShowTitle = true, TitleText = spec.Title, BackgroundColor = spec.BackgroundColor },
            };

    /// <summary>默认组细分（设计 §7.1：4×4 = 十六块 1×1；降级后组尺寸不变，分区随 Bounds 相对坐标）。</summary>
    private static IReadOnlyList<GridRect> DefaultPartitions(GridRect bounds)
    {
        var partitions = new List<GridRect>(bounds.Width * bounds.Height);
        for (var row = 0; row < bounds.Height; row++)
        {
            for (var col = 0; col < bounds.Width; col++)
            {
                partitions.Add(new GridRect(col, row, 1, 1));
            }
        }

        return partitions;
    }
}
