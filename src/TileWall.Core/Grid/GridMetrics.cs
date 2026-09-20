namespace TileWall.Core.Grid;

/// <summary>
/// 网格几何常量与换算（P1 §1.1 公式的代码化）。全部常量可注入；
/// 占位默认值 96/8/24/24 为拍板 Q2 的临时参数，后期校准只改此处常量。
/// 屏幕工作区数值由 UI 层传入（Core 不查屏幕，设计 §18.1）。
/// </summary>
public sealed record GridMetrics
{
    // —— 占位默认值（P1 §1.1；拍板 Q2：临时占位，校准轮只改这些常量）——
    public const double DefaultCellCore = 96;   // s  格芯
    public const double DefaultGap = 8;         // g  磁贴间隙
    public const double DefaultColumnGap = 24;  // G  栏间距
    public const double DefaultMargin = 24;     // m  墙外边距

    /// <summary>竖栏固定八格（设计 §2.1），非占位参数，不随校准变化。</summary>
    public const int CellsPerColumn = 8;

    public double CellCore { get; init; } = DefaultCellCore;
    public double Gap { get; init; } = DefaultGap;
    public double ColumnGap { get; init; } = DefaultColumnGap;
    public double Margin { get; init; } = DefaultMargin;

    /// <summary>单点校准入口（默认实例）。</summary>
    public static GridMetrics Default { get; } = new();

    /// <summary>格步距 p = s + g。</summary>
    public double Pitch => CellCore + Gap;

    /// <summary>栏宽 = 8s + 7g。</summary>
    public double ColumnWidth => CellsPerColumn * CellCore + (CellsPerColumn - 1) * Gap;

    // —— 墙外形（P1 §1.1 公式，运行时计算、不硬编码结果）——

    /// <summary>墙宽（n 栏）= 2m + n×(8s+7g) + (n−1)×G。</summary>
    public double WallWidth(int columns) =>
        2 * Margin + columns * ColumnWidth + (columns - 1) * ColumnGap;

    /// <summary>墙高（r 行）= 2m + r×s + (r−1)×g。</summary>
    public double WallHeight(int rows) =>
        2 * Margin + rows * CellCore + (rows - 1) * Gap;

    // —— 对象矩形尺寸（n 格宽 = n×s + (n−1)×g）——

    public double WidthOf(int cells) => cells * CellCore + (cells - 1) * Gap;

    public double HeightOf(int cells) => WidthOf(cells);

    public double RectWidth(GridRect r) => WidthOf(r.Width);

    public double RectHeight(GridRect r) => HeightOf(r.Height);

    // —— 原点换算（格 → DIP；栏内步距 Pitch，跨栏加 ColumnWidth+ColumnGap）——

    public double CellX(int column) => Margin
        + (column / CellsPerColumn) * (ColumnWidth + ColumnGap)
        + (column % CellsPerColumn) * Pitch;

    public double CellY(int row) => Margin + row * Pitch;

    public DipPoint OriginOf(GridRect r) => new(CellX(r.Column), CellY(r.Row));

    // —— 反向换算（M3 命中/拖动用；内容区外返回 -1）——
    // 语义：格芯取半开区间 [原点, 原点+CellCore)；落在格间隙 / 栏间隙 / 外边距上返回 -1。

    /// <summary>x → 基础格列号；不在任何格芯内（边距、格间隙、栏间隙、越界、非法值）返回 -1。</summary>
    public int ColumnFromX(double x)
    {
        if (!double.IsFinite(x) || x < Margin)
        {
            return -1;
        }

        var band = ColumnWidth + ColumnGap;
        var strip = (int)((x - Margin) / band);
        if (strip > int.MaxValue / 4)
        {
            return -1;
        }

        var local = x - Margin - strip * band;
        if (local >= ColumnWidth)
        {
            return -1; // 栏间隙（含末栏之后的右边距）
        }

        var cell = (int)(local / Pitch);
        if (cell >= CellsPerColumn)
        {
            return -1;
        }

        return local - (cell * Pitch) >= CellCore ? -1 : (strip * CellsPerColumn) + cell; // 格间隙 / 命中
    }

    /// <summary>y → 基础格行号；不在任何格芯内（边距、格间隙、越界、非法值）返回 -1。行数不限（墙行数由调用方约束）。</summary>
    public int RowFromY(double y)
    {
        if (!double.IsFinite(y) || y < Margin)
        {
            return -1;
        }

        var local = y - Margin;
        var cell = (int)(local / Pitch);
        if (cell < 0)
        {
            return -1;
        }

        return local - (cell * Pitch) >= CellCore ? -1 : cell;
    }

    // —— 容量上限（工作区由 UI 传入，Core 不查屏幕）——

    /// <summary>最大 n 使 WallWidth(n) ≤ workWidth；一栏都放不下 → 0。</summary>
    public int MaxColumnsFor(double workWidth)
    {
        var n = (int)Math.Floor((workWidth - (2 * Margin) + ColumnGap) / (ColumnWidth + ColumnGap));
        if (n < 0)
        {
            n = 0;
        }

        while (n > 0 && WallWidth(n) > workWidth)
        {
            n--;
        }

        while (WallWidth(n + 1) <= workWidth)
        {
            n++;
        }

        return n;
    }

    /// <summary>最大 r 使 WallHeight(r) ≤ workHeight；一行都放不下 → 0。</summary>
    public int MaxRowsFor(double workHeight)
    {
        var r = (int)Math.Floor((workHeight - (2 * Margin) + Gap) / Pitch);
        if (r < 0)
        {
            r = 0;
        }

        while (r > 0 && WallHeight(r) > workHeight)
        {
            r--;
        }

        while (WallHeight(r + 1) <= workHeight)
        {
            r++;
        }

        return r;
    }
}
