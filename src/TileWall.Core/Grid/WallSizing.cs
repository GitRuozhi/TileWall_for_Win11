namespace TileWall.Core.Grid;

/// <summary>
/// 手动拉伸 / 缩小与初始吸附（设计 §4.2、验收 A03；拍板 Q3、P1 §1.2-6）。
/// 全部纯函数；横向扩容只增不改既有对象、恒合法（§4.3），故无对应引擎函数。
/// </summary>
public static class WallSizing
{
    private const double AreaTieTolerance = 1e-6;

    /// <summary>
    /// §4.2：缩小时不能把任何现有对象裁到墙外；不合法 → false（UI 预览阶段阻止）。
    /// 候选不得超过当前墙（本函数只服务缩小方向）。
    /// </summary>
    public static bool CanShrinkTo(WallGrid current, WallGrid candidate, IReadOnlyList<GridRect> occupiedRects)
    {
        ArgumentNullException.ThrowIfNull(occupiedRects);
        if (candidate.Columns < 1 || candidate.Rows < 1)
        {
            return false;
        }

        if (candidate.Columns > current.Columns || candidate.Rows > current.Rows)
        {
            return false;
        }

        foreach (var r in occupiedRects)
        {
            if (!candidate.Contains(r))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A03：给定占用，返回最大可缩小目标（列向收缩至最右占用列——按整栏八格向上取整，行向同）。
    /// 无占用 → 1×1；结果永不大于当前墙。
    /// </summary>
    public static WallGrid MinShrink(WallGrid current, IReadOnlyList<GridRect> occupiedRects)
    {
        ArgumentNullException.ThrowIfNull(occupiedRects);
        var maxRight = 0;
        var maxBottom = 0;
        foreach (var r in occupiedRects)
        {
            if (r.Right > maxRight)
            {
                maxRight = r.Right;
            }

            if (r.Bottom > maxBottom)
            {
                maxBottom = r.Bottom;
            }
        }

        var columns = (maxRight + GridMetrics.CellsPerColumn - 1) / GridMetrics.CellsPerColumn;
        var rows = maxBottom;
        columns = Math.Clamp(columns, 1, current.Columns);
        rows = Math.Clamp(rows, 1, current.Rows);
        return new WallGrid(columns, rows);
    }

    /// <summary>
    /// Q3：在合法 (栏数 n, 行数 r) 组合中取 |墙面积 − 工作区面积×50%| 最小者；
    /// 并列取墙面积较小者（宁小勿大），仍并列取枚举序（n 升 → r 升）。
    /// 合法 = WallWidth(n) ≤ workWidth ∧ WallHeight(r) ≤ workHeight ∧ n ≥ 1 ∧ r ≥ 1。
    /// 无任何合法组合（连一栏八格都放不下）→ columns=0（UI 进入显示适配提示，设计 §4.5、A18）。
    /// </summary>
    public static (int Columns, int Rows) InitialForWorkArea(double workWidth, double workHeight, GridMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var maxColumns = metrics.MaxColumnsFor(workWidth);
        var maxRows = metrics.MaxRowsFor(workHeight);
        if (maxColumns < 1 || maxRows < 1)
        {
            return (0, 0);
        }

        var halfArea = workWidth * workHeight * 0.5;
        var bestColumns = 1;
        var bestRows = 1;
        var bestDiff = double.MaxValue;
        var bestArea = double.MaxValue;
        for (var n = 1; n <= maxColumns; n++)
        {
            for (var r = 1; r <= maxRows; r++)
            {
                var width = metrics.WallWidth(n);
                var height = metrics.WallHeight(r);
                var area = width * height;
                var diff = Math.Abs(area - halfArea);
                var better = diff < bestDiff - AreaTieTolerance
                    || (diff <= bestDiff + AreaTieTolerance && area < bestArea);
                if (better)
                {
                    bestColumns = n;
                    bestRows = r;
                    bestDiff = diff;
                    bestArea = area;
                }
            }
        }

        return (bestColumns, bestRows);
    }
}
