namespace TileWall.Core.Grid;

/// <summary>
/// 基础格坐标矩形（0 基整数四元组；设计 §2.4「尺寸统一写作列×行」：
/// <see cref="Width"/>=列数、<see cref="Height"/>=行数）。全部引擎 API 只用此坐标系。
/// </summary>
public readonly record struct GridRect(int Column, int Row, int Width, int Height)
{
    /// <summary>右边界（排他）：Column + Width。</summary>
    public int Right => Column + Width;

    /// <summary>下边界（排他）：Row + Height。</summary>
    public int Bottom => Row + Height;

    /// <summary>尺寸（列数 × 行数）。</summary>
    public GridSize Size => new(Width, Height);

    /// <summary>半开区间相交判定：两矩形共享至少一个基础格。</summary>
    public bool Intersects(GridRect other) =>
        Column < other.Right && other.Column < Right &&
        Row < other.Bottom && other.Row < Bottom;

    /// <summary>平移（不改变尺寸）。</summary>
    public GridRect Translate(int dColumn, int dRow) =>
        new(Column + dColumn, Row + dRow, Width, Height);

    /// <summary>other 完整位于本矩形内（边界可重合；M5 拆墙合并集判定）。</summary>
    public bool Contains(GridRect other) =>
        Column <= other.Column && Row <= other.Row &&
        other.Right <= Right && other.Bottom <= Bottom;

    /// <summary>两矩形的轴对齐包围盒（同时覆盖两者的最小矩形；M5 拆墙扩张循环）。</summary>
    public static GridRect BoundingBox(GridRect a, GridRect b)
    {
        var column = Math.Min(a.Column, b.Column);
        var row = Math.Min(a.Row, b.Row);
        return new GridRect(
            column,
            row,
            Math.Max(a.Right, b.Right) - column,
            Math.Max(a.Bottom, b.Bottom) - row);
    }
}

/// <summary>基础格容量尺寸（列数 × 行数）。</summary>
public readonly record struct GridSize(int Columns, int Rows);

/// <summary>DIP 坐标点（相对墙窗口客户区左上角；仅由 <see cref="GridMetrics"/> 换算）。</summary>
public readonly record struct DipPoint(double X, double Y);

/// <summary>DIP 矩形（相对墙窗口客户区左上角）。</summary>
public readonly record struct DipRect(double X, double Y, double Width, double Height);
