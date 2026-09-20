namespace TileWall.Core.Grid;

/// <summary>
/// 占用表：bool 网格，引擎内部状态。Clone 后变更，绝不共享可变状态。
/// 单元索引 = Row × 整墙格列数 + Column。
/// </summary>
public sealed class OccupancyMap
{
    private readonly bool[] _cells;

    public OccupancyMap(WallGrid wall)
    {
        Wall = wall;
        _cells = new bool[wall.CellColumns * wall.Rows];
    }

    public WallGrid Wall { get; }

    /// <summary>矩形在墙内且每个基础格均未被占用 → true；越墙矩形一律 false。</summary>
    public bool IsFree(GridRect r)
    {
        if (!Wall.Contains(r))
        {
            return false;
        }

        for (var row = r.Row; row < r.Bottom; row++)
        {
            var baseIndex = row * Wall.CellColumns;
            for (var col = r.Column; col < r.Right; col++)
            {
                if (_cells[baseIndex + col])
                {
                    return false;
                }
            }
        }

        return true;
    }

    public void Fill(GridRect r)
    {
        EnsureInside(r);
        for (var row = r.Row; row < r.Bottom; row++)
        {
            var baseIndex = row * Wall.CellColumns;
            for (var col = r.Column; col < r.Right; col++)
            {
                _cells[baseIndex + col] = true;
            }
        }
    }

    public void Clear(GridRect r)
    {
        EnsureInside(r);
        for (var row = r.Row; row < r.Bottom; row++)
        {
            var baseIndex = row * Wall.CellColumns;
            for (var col = r.Column; col < r.Right; col++)
            {
                _cells[baseIndex + col] = false;
            }
        }
    }

    public OccupancyMap Clone()
    {
        var copy = new OccupancyMap(Wall);
        Array.Copy(_cells, copy._cells, _cells.Length);
        return copy;
    }

    /// <summary>按矩形集合构建占用表；矩形必须合法（墙内、宽高 ≥ 1）。</summary>
    public static OccupancyMap Build(WallGrid wall, IEnumerable<GridRect> rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        var map = new OccupancyMap(wall);
        foreach (var r in rects)
        {
            map.Fill(r);
        }

        return map;
    }

    private void EnsureInside(GridRect r)
    {
        if (!Wall.Contains(r))
        {
            throw new ArgumentOutOfRangeException(nameof(r), r,
                $"矩形 {r} 超出墙容量 {Wall.Columns} 栏 × {Wall.Rows} 行。");
        }
    }
}
