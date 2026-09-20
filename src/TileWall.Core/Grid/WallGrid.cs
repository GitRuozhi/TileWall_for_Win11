namespace TileWall.Core.Grid;

/// <summary>
/// 墙容量边界（栏数 × 行数）+ 结构性限制常量（设计 §2.4、§4.4）。
/// 不可变值记录；只描述容量，不含任何 DIP 尺寸（视觉事实不入引擎，拍板 Q2）。
/// </summary>
public sealed record WallGrid(int Columns, int Rows)
{
    /// <summary>拼图组尺寸界限：列 2—8、行 2—25（设计 §4.4）。</summary>
    public const int GroupMinColumns = 2;
    public const int GroupMaxColumns = 8;
    public const int GroupMinRows = 2;
    public const int GroupMaxRows = 25;

    /// <summary>独立磁贴最大宽 = 一栏八格（设计 §4.4）。</summary>
    public const int TileMaxCells = GridMetrics.CellsPerColumn;

    /// <summary>整墙基础格总列数 = Columns × 8。</summary>
    public int CellColumns => Columns * GridMetrics.CellsPerColumn;

    /// <summary>矩形完整位于墙内（0 ≤ Column、Row，Right ≤ 8×Columns，Bottom ≤ Rows；宽高 ≥ 1）。</summary>
    public bool Contains(GridRect r) =>
        r.Column >= 0 && r.Row >= 0 &&
        r.Width >= 1 && r.Height >= 1 &&
        r.Right <= CellColumns && r.Bottom <= Rows;

    /// <summary>矩形不跨竖栏：r.Column/8 == (r.Right−1)/8（设计 §2.4）。仅对 Width ≥ 1 的矩形有意义。</summary>
    public bool InSingleColumn(GridRect r) =>
        r.Column / GridMetrics.CellsPerColumn == (r.Right - 1) / GridMetrics.CellsPerColumn;
}
