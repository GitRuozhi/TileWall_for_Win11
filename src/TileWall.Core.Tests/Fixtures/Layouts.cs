using TileWall.Core.Configuration;
using TileWall.Core.Grid;

namespace TileWall.Core.Tests.Fixtures;

/// <summary>
/// L1–L4 参考布局（P1 §4.1，一次建立、T-REL/T-REL-F/T-CAP 复用）。
/// 全部为确定性构造器：Id 固定可排序（π 序稳定），对象两两不交、合法（跨栏/越墙均无）。
/// </summary>
internal static class Layouts
{
    /// <summary>1080p 满容量墙：2 栏 × 9 行（P1 §1.1）。</summary>
    public static WallGrid Wall2x9 { get; } = new(2, 9);

    /// <summary>极限墙：2 栏 × 25 行（L4 用）。</summary>
    public static WallGrid Wall2x25 { get; } = new(2, 25);

    /// <summary>单栏墙（1 栏 × 9 行）。</summary>
    public static WallGrid Wall1x9 { get; } = new(1, 9);

    /// <summary>L1 空墙（空状态、首次显示；设计 §17.3）。</summary>
    public static List<LayoutObject> Empty() => [];

    /// <summary>L2 满屏全 1×1 独立磁贴（2×9 = 144 块；最大对象数下的腾位与容量）。</summary>
    public static List<LayoutObject> FullOnes()
    {
        var objects = new List<LayoutObject>(144);
        for (var row = 0; row < 9; row++)
        {
            for (var col = 0; col < 16; col++)
            {
                objects.Add(Tile($"t{row:D2}{col:D2}", new GridRect(col, row, 1, 1)));
            }
        }

        return objects;
    }

    /// <summary>
    /// L3 混合：2 个 4×4 组 + 变尺寸独立磁贴，含贴边与贴角（组整体避让、矩形不拆分）。
    /// 条带 1（基础格列 8–15）内留有空穴，供腾位与容量测试。
    /// </summary>
    public static List<LayoutObject> Mixed()
    {
        List<LayoutObject> objects =
        [
            Group("g000-A", new GridRect(0, 0, 4, 4)),   // 贴左上角
            Group("g000-B", new GridRect(0, 5, 4, 4)),   // 贴左下角
            Tile("t000-1", new GridRect(8, 0, 3, 2)),    // 条带 1 顶边
            Tile("t000-2", new GridRect(11, 0, 4, 1)),   // 顶边
            Tile("t000-3", new GridRect(15, 0, 1, 1)),   // 右上角
            Tile("t000-4", new GridRect(11, 1, 2, 2)),
            Tile("t000-5", new GridRect(15, 1, 1, 4)),   // 右边
            Tile("t000-6", new GridRect(8, 5, 2, 4)),    // 底边
            Tile("t000-7", new GridRect(15, 6, 1, 3)),   // 右下角
            Tile("t000-8", new GridRect(12, 4, 2, 2)),
        ];
        return objects;
    }

    /// <summary>L4 极限：1 个 8×25 组（满一栏，二百块 1×1 分区）+ 第二栏 2×1 密铺（整墙无空位）。</summary>
    public static List<LayoutObject> Extreme()
    {
        var objects = new List<LayoutObject> { Group("g400-8x25", new GridRect(0, 0, 8, 25)) };
        var n = 0;
        for (var row = 0; row < 25; row++)
        {
            for (var col = 8; col < 16; col += 2)
            {
                objects.Add(Tile($"t4{n:D3}", new GridRect(col, row, 2, 1)));
                n++;
            }
        }

        return objects;
    }

    /// <summary>配置根快捷工厂（默认 schemaVersion=1、默认设置）。</summary>
    public static TileWallConfig Config(WallGrid wall, IReadOnlyList<LayoutObject> objects) =>
        new() { Wall = new WallState(wall.Columns, wall.Rows), Objects = objects };

    public static TileObject Tile(string id, GridRect bounds, EntryReference? entry = null, string? title = null) =>
        new()
        {
            Id = id,
            Bounds = bounds,
            Entry = entry,
            Visual = new ObjectVisual { TitleText = title },
        };

    /// <summary>默认 1×1 细分（设计 §7.1：默认 4×4 = 十六块 1×1）。</summary>
    public static GroupObject Group(string id, GridRect bounds)
    {
        var partitions = new List<GridRect>(bounds.Width * bounds.Height);
        for (var row = 0; row < bounds.Height; row++)
        {
            for (var col = 0; col < bounds.Width; col++)
            {
                partitions.Add(new GridRect(col, row, 1, 1));
            }
        }

        return new GroupObject { Id = id, Bounds = bounds, Partitions = partitions };
    }
}
