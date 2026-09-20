namespace TileWall.Core.Grid;

/// <summary>
/// 合法放置判定与容量（设计 §5.4 统一入口）。全部纯函数。
/// 判定次序固定：OutOfWall → CrossesColumn → TileTooWide / GroupSizeOutOfRange → OverlapsOccupied（首错返回）。
/// </summary>
public static class GridPlacement
{
    /// <summary>
    /// 单矩形合法性（§5.4「完整对象有合法位置后才提交」的第一半）。
    /// 注意：栏宽固定八格下，宽 &gt; 8 的矩形必然跨栏，会先返回 <see cref="PlacementError.CrossesColumn"/>
    /// （次序固定所致）；<see cref="PlacementError.TileTooWide"/> 为按 §6.2 次序保留的显式错误码。
    /// </summary>
    public static PlacementError Validate(WallGrid wall, GridRect rect, ObjectKind kind, OccupancyMap occupied)
    {
        ArgumentNullException.ThrowIfNull(occupied);
        if (!wall.Contains(rect))
        {
            return PlacementError.OutOfWall;
        }

        if (!wall.InSingleColumn(rect))
        {
            return PlacementError.CrossesColumn;
        }

        var sizeError = kind switch
        {
            ObjectKind.Tile => rect.Width > WallGrid.TileMaxCells
                ? PlacementError.TileTooWide
                : PlacementError.None,
            ObjectKind.Group => IsGroupSizeInRange(rect.Size)
                ? PlacementError.None
                : PlacementError.GroupSizeOutOfRange,
            _ => PlacementError.None,
        };

        if (sizeError != PlacementError.None)
        {
            return sizeError;
        }

        return occupied.IsFree(rect) ? PlacementError.None : PlacementError.OverlapsOccupied;
    }

    /// <summary>组尺寸界限谓词（列 2—8、行 2—25，设计 §4.4）。</summary>
    public static bool IsGroupSizeInRange(GridSize size) =>
        size.Columns >= WallGrid.GroupMinColumns && size.Columns <= WallGrid.GroupMaxColumns &&
        size.Rows >= WallGrid.GroupMinRows && size.Rows <= WallGrid.GroupMaxRows;

    /// <summary>
    /// 容量判断：当前占用下能否容纳 size 矩形；firstFit = 行优先扫描的首个合法空位（确定性）。
    /// 扫描只落在单竖栏内的起点上，绝不返回跨栏矩形。
    /// </summary>
    public static bool CanFitRect(WallGrid wall, GridSize size, OccupancyMap occupied, out GridRect firstFit)
    {
        ArgumentNullException.ThrowIfNull(occupied);
        firstFit = default;
        if (size.Columns < 1 || size.Rows < 1 || size.Columns > wall.CellColumns || size.Rows > wall.Rows)
        {
            return false;
        }

        for (var row = 0; row + size.Rows <= wall.Rows; row++)
        {
            for (var col = 0; col + size.Columns <= wall.CellColumns; col++)
            {
                var candidate = new GridRect(col, row, size.Columns, size.Rows);
                if (!wall.InSingleColumn(candidate) || !occupied.IsFree(candidate))
                {
                    continue;
                }

                firstFit = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 批量全有或全无（§5.4「批量导入先检查完整选择」、验收 A15）。
    /// 顺序 first-fit（不放回试探）；任一失败 → false，调用方不得采用部分结果。
    /// </summary>
    public static bool CanPlaceBatch(WallGrid wall, IReadOnlyList<GridRect> existing, IReadOnlyList<GridSize> additions)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(additions);
        var occupied = OccupancyMap.Build(wall, existing);
        foreach (var size in additions)
        {
            if (!CanFitRect(wall, size, occupied, out var fit))
            {
                return false;
            }

            occupied.Fill(fit);
        }

        return true;
    }
}
