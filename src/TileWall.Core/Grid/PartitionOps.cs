namespace TileWall.Core.Grid;

/// <summary>纯函数分区操作结果状态（M5 设计 §4–§7）。非 Applied 时 <see cref="PartitionOpResult.Layout"/> 为 null 且布局逐字段不变。</summary>
public enum PartitionOpStatus
{
    Applied,
    SolidForDraw,
    DashedForErase,
    OutOfRange,
    AtLimit,
}

/// <summary>纯函数操作结果：状态 + 结果布局（不可变 record；失败输入返回原因，调用方保留原布局）。</summary>
public sealed record PartitionOpResult(PartitionOpStatus Status, PartitionLayout? Layout)
{
    public static PartitionOpResult Applied(PartitionLayout layout) => new(PartitionOpStatus.Applied, layout);

    public static PartitionOpResult Fail(PartitionOpStatus status) => new(status, null);
}

/// <summary>
/// 分区纯函数算法（M5 设计 §4–§7；同输入必同输出，无时钟/随机/IO/线程状态）：
/// <see cref="DrawWall"/>（W-1 画墙=分块内贯穿整条线）、<see cref="EraseWall"/>（D-1 拆墙=最小合法矩形合并）、
/// <see cref="AddColumn"/>/<see cref="RemoveColumn"/>/<see cref="AddRow"/>/<see cref="RemoveRow"/>（R-1/R-2 行列增减）。
/// </summary>
public static class PartitionOps
{
    // ————————————————————————————— 画墙 W-1（设计 §4；B03） —————————————————————————————

    /// <summary>
    /// 对候选边定位含左/上格的分块 p：两侧同块（虚线）→ 在 p 内开一条贯穿 p 整条高/宽的线
    /// （p → 两个矩形，其余分区逐条不变，分块数 +1）；实线 → SolidForDraw 忽略；越出定义域 → OutOfRange。
    /// 线终止于分块边界、不贯穿整组、无半截悬空墙（剖分不变式在结果上成立即等价，§4.2）。
    /// </summary>
    public static PartitionOpResult DrawWall(PartitionLayout layout, WallEdge edge)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!InDomain(layout.Size, edge))
        {
            return PartitionOpResult.Fail(PartitionOpStatus.OutOfRange);
        }

        var (a, b) = CellsAcross(layout, edge);
        if (a != b)
        {
            return PartitionOpResult.Fail(PartitionOpStatus.SolidForDraw); // 两侧异块=实线：画墙不处理（§8.4）
        }

        var p = a; // 两侧同块（虚线），在 p 内开线
        if (edge.IsVertical)
        {
            var left = new GridRect(p.Column, p.Row, edge.X - p.Column, p.Height);
            var right = new GridRect(edge.X, p.Row, p.Right - edge.X, p.Height);
            return PartitionOpResult.Applied(ReplaceOne(layout, p, left, right));
        }

        var top = new GridRect(p.Column, p.Row, p.Width, edge.Y - p.Row);
        var bottom = new GridRect(p.Column, edge.Y, p.Width, p.Bottom - edge.Y);
        return PartitionOpResult.Applied(ReplaceOne(layout, p, top, bottom));
    }

    // ————————————————————————————— 拆墙 D-1（设计 §5；B04/B05） —————————————————————————————

    /// <summary>
    /// 以两侧分块包围盒起步，循环「边界切穿旁块 → 完整纳入再扩大」至收敛，最终移除 R 内全部墙（合并为一块）。
    /// R 单调严格增大且 ⊆ Bounds ⇒ 必终止（≤200 块）；收敛时所有与 R 相交的分区都被 R 完整包含（无半切块）；
    /// 最小性：每个纳入决策都是覆盖合法性的必要条件。虚线 → DashedForErase 忽略。
    /// </summary>
    public static PartitionOpResult EraseWall(PartitionLayout layout, WallEdge edge)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!InDomain(layout.Size, edge))
        {
            return PartitionOpResult.Fail(PartitionOpStatus.OutOfRange);
        }

        var extent = EraseExtent(layout, edge);
        if (extent is null)
        {
            return PartitionOpResult.Fail(PartitionOpStatus.DashedForErase); // 虚线：拆墙不处理（§8.4）
        }

        var r = extent.Value;
        var merged = layout.Partitions.Where(p => r.Contains(p)).ToList();
        return PartitionOpResult.Applied(MergeMany(layout, merged, r));
    }

    /// <summary>
    /// 拆墙预览（§5.1/§6.2）：返回最终合并矩形 R；边越出定义域或为虚线（两侧同块）→ null。
    /// </summary>
    internal static GridRect? EraseExtent(PartitionLayout layout, WallEdge edge)
    {
        if (!InDomain(layout.Size, edge))
        {
            return null;
        }

        var (a, b) = CellsAcross(layout, edge);
        if (a == b)
        {
            return null; // 虚线：拆墙不处理（§8.4）
        }

        var r = GridRect.BoundingBox(a, b);
        while (true)
        {
            var expanded = r;
            foreach (var p in layout.Partitions)
            {
                if (p.Intersects(r) && !r.Contains(p))
                {
                    expanded = GridRect.BoundingBox(expanded, p); // 旁块完整纳入后再次扩大
                }
            }

            if (expanded == r)
            {
                return r; // 收敛：所有与 R 相交的分区都已被 R 完整包含
            }

            r = expanded; // R 严格增大且 ⊆ Bounds ⇒ 必终止
        }
    }

    // ————————————————————————————— 行列增减 R-1/R-2（设计 §7；B09） —————————————————————————————

    /// <summary>右扩一列：旧分区逐条不变 + 右侧整列 1×1（INV-P8「扩大不清空布局」）；列已到 8 → AtLimit。</summary>
    public static PartitionOpResult AddColumn(PartitionLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Size.Columns >= WallGrid.GroupMaxColumns)
        {
            return PartitionOpResult.Fail(PartitionOpStatus.AtLimit);
        }

        var additions = new GridRect[layout.Size.Rows];
        for (var row = 0; row < layout.Size.Rows; row++)
        {
            additions[row] = new GridRect(layout.Size.Columns, row, 1, 1);
        }

        return PartitionOpResult.Applied(new PartitionLayout(
            new GridSize(layout.Size.Columns + 1, layout.Size.Rows),
            [.. layout.Partitions, .. additions]));
    }

    /// <summary>
    /// 缩列：越界整块丢弃、被截分区截短仍矩形、覆盖完备（INV-P9，归纳可证）；列已到 2 → AtLimit。
    /// 被裁内容即丢、再次扩大不恢复（撤销是唯一回退）。
    /// </summary>
    public static PartitionOpResult RemoveColumn(PartitionLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Size.Columns <= WallGrid.GroupMinColumns)
        {
            return PartitionOpResult.Fail(PartitionOpStatus.AtLimit);
        }

        var keepColumn = layout.Size.Columns - 1; // 该列即被移除列
        var kept = new List<GridRect>(layout.Partitions.Count);
        foreach (var p in layout.Partitions)
        {
            if (p.Right <= keepColumn)
            {
                kept.Add(p); // 原样保留
            }
            else if (p.Column < keepColumn)
            {
                kept.Add(new GridRect(p.Column, p.Row, keepColumn - p.Column, p.Height)); // 截短仍矩形
            }
            // else p.Column ≥ keepColumn：整块被裁掉（丢弃）
        }

        return PartitionOpResult.Applied(new PartitionLayout(
            new GridSize(keepColumn, layout.Size.Rows),
            kept));
    }

    /// <summary>下扩一行：旧分区逐条不变 + 底部整行 1×1（INV-P8）；行已到 25 → AtLimit。</summary>
    public static PartitionOpResult AddRow(PartitionLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Size.Rows >= WallGrid.GroupMaxRows)
        {
            return PartitionOpResult.Fail(PartitionOpStatus.AtLimit);
        }

        var additions = new GridRect[layout.Size.Columns];
        for (var col = 0; col < layout.Size.Columns; col++)
        {
            additions[col] = new GridRect(col, layout.Size.Rows, 1, 1);
        }

        return PartitionOpResult.Applied(new PartitionLayout(
            new GridSize(layout.Size.Columns, layout.Size.Rows + 1),
            [.. layout.Partitions, .. additions]));
    }

    /// <summary>缩行：与缩列对称（INV-P9）；行已到 2 → AtLimit。</summary>
    public static PartitionOpResult RemoveRow(PartitionLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Size.Rows <= WallGrid.GroupMinRows)
        {
            return PartitionOpResult.Fail(PartitionOpStatus.AtLimit);
        }

        var keepRow = layout.Size.Rows - 1;
        var kept = new List<GridRect>(layout.Partitions.Count);
        foreach (var p in layout.Partitions)
        {
            if (p.Bottom <= keepRow)
            {
                kept.Add(p);
            }
            else if (p.Row < keepRow)
            {
                kept.Add(new GridRect(p.Column, p.Row, p.Width, keepRow - p.Row));
            }
        }

        return PartitionOpResult.Applied(new PartitionLayout(
            new GridSize(layout.Size.Columns, keepRow),
            kept));
    }

    // ————————————————————————————— 内部件（PartitionEditSession 共用） —————————————————————————————

    /// <summary>边是否在内部边定义域内（V: X∈[1,W−1]、Y∈[0,H)；H: X∈[0,W)、Y∈[1,H−1)）。</summary>
    internal static bool InDomain(GridSize size, WallEdge edge) => edge.IsVertical
        ? edge.X >= 1 && edge.X < size.Columns && edge.Y >= 0 && edge.Y < size.Rows
        : edge.X >= 0 && edge.X < size.Columns && edge.Y >= 1 && edge.Y < size.Rows;

    /// <summary>边两侧的格所属分区 (左|上, 右|下)；仅在定义域内调用。</summary>
    internal static (GridRect A, GridRect B) CellsAcross(PartitionLayout layout, WallEdge edge) => edge.IsVertical
        ? (layout.PartitionAt(edge.X - 1, edge.Y), layout.PartitionAt(edge.X, edge.Y))
        : (layout.PartitionAt(edge.X, edge.Y - 1), layout.PartitionAt(edge.X, edge.Y));

    /// <summary>恰一个分区 p 被替换为两个矩形（其余逐条不变、分块数 +1；INV-P6 的实现直译）。</summary>
    private static PartitionLayout ReplaceOne(PartitionLayout layout, GridRect old, GridRect first, GridRect second)
    {
        var list = new List<GridRect>(layout.Partitions.Count + 1);
        foreach (var p in layout.Partitions)
        {
            if (p == old)
            {
                list.Add(first);
                list.Add(second);
            }
            else
            {
                list.Add(p);
            }
        }

        return new PartitionLayout(layout.Size, list);
    }

    /// <summary>合并集替换为单一矩形 R（插入位置 = 首个被移除分区的原位置，渲染序稳定）。</summary>
    private static PartitionLayout MergeMany(PartitionLayout layout, IReadOnlyList<GridRect> merged, GridRect replacement)
    {
        var list = new List<GridRect>(layout.Partitions.Count - merged.Count + 1);
        var inserted = false;
        foreach (var p in layout.Partitions)
        {
            if (merged.Contains(p))
            {
                if (!inserted)
                {
                    list.Add(replacement);
                    inserted = true;
                }
            }
            else
            {
                list.Add(p);
            }
        }

        return new PartitionLayout(layout.Size, list);
    }
}
