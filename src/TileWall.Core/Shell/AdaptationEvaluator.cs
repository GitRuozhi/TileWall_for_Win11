using TileWall.Core.Grid;

namespace TileWall.Core.Shell;

/// <summary>适配判定快照（M8 设计 §6.1/§6.4）：提示窗两组数字（需求 vs 容量）+ 判定结果的纯数据载体。</summary>
public sealed record AdaptationSnapshot(
    bool Fits,
    int WallColumns,
    int WallRows,
    int RequiredColumns,
    int RequiredRows,
    int RequiredBaseCells,
    int CapacityColumns,
    int CapacityRows)
{
    /// <summary>「需求 X 栏 × Y 行（Z 基础格）」文案（P1 §3.6 线框逐字）。</summary>
    public string RequirementText => $"需要 {RequiredColumns} 栏 × {RequiredRows} 行（{RequiredBaseCells} 基础格）";

    /// <summary>「当前 A 栏 × B 行」容量文案（P1 §3.6 线框逐字；A=0 表示整屏容不下一栏八格，A18）。</summary>
    public string CapacityText => $"当前可容纳 {CapacityColumns} 栏 × {CapacityRows} 行";
}

/// <summary>
/// 显示适配判定规则（M8 设计 §6.1；纯函数，屏幕数值由调用方传入，Core 不查屏幕）：
/// fit = 墙宽/墙高 ≤ 工作区 + 0.5 DIP（与 MainWindow 既有启动分支逐字同容差）；墙级判定即对象级判定——
/// 对象恒在墙内，墙放得下 ⇒ 所有对象完整可见；墙放不下 ⇒ 不渲染（不做对象级裁切判定）。
/// capacity = (MaxColumnsFor(workW), MaxRowsFor(workH)) 即 A 栏 × B 行；
/// requirement = (Columns, Rows, CellColumns×Rows) 即 X 栏 × Y 行（Z 基础格）。
/// </summary>
public static class AdaptationEvaluator
{
    /// <summary>工作区容差（DIP）；与 MainWindow 启动分支的 +0.5 逐字一致。</summary>
    public const double WorkAreaToleranceDip = 0.5;

    public static bool Fits(WallGrid wall, double workWidthDip, double workHeightDip, GridMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(wall);
        ArgumentNullException.ThrowIfNull(metrics);
        return metrics.WallWidth(wall.Columns) <= workWidthDip + WorkAreaToleranceDip
               && metrics.WallHeight(wall.Rows) <= workHeightDip + WorkAreaToleranceDip;
    }

    public static (int MaxColumns, int MaxRows) CapacityFor(double workWidthDip, double workHeightDip, GridMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        return (metrics.MaxColumnsFor(workWidthDip), metrics.MaxRowsFor(workHeightDip));
    }

    public static AdaptationSnapshot Snapshot(WallGrid wall, double workWidthDip, double workHeightDip, GridMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(wall);
        ArgumentNullException.ThrowIfNull(metrics);
        var (maxColumns, maxRows) = CapacityFor(workWidthDip, workHeightDip, metrics);
        return new AdaptationSnapshot(
            Fits: Fits(wall, workWidthDip, workHeightDip, metrics),
            WallColumns: wall.Columns,
            WallRows: wall.Rows,
            RequiredColumns: wall.Columns,
            RequiredRows: wall.Rows,
            RequiredBaseCells: wall.CellColumns * wall.Rows,
            CapacityColumns: maxColumns,
            CapacityRows: maxRows);
    }
}
