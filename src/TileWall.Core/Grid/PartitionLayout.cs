namespace TileWall.Core.Grid;

/// <summary>
/// 内部边规范标识（M5 设计 §3.1）：竖边 V(X,Y)=格(X−1,Y)|(X,Y)，定义域 X∈[1,W−1)、Y∈[0,H)；
/// 横边 H(X,Y)=格(X,Y−1)|(X,Y)，定义域 X∈[0,W)、Y∈[1,H−1)。渲染/命中/去抖的唯一边界身份；
/// 组最外侧边框从不是可操作边——由类型域而非运行时检查保证。
/// </summary>
public readonly record struct WallEdge(bool IsVertical, int X, int Y);

/// <summary>分区不变式违规码（与落盘闸门 ConfigValidator 的 PARTITION_* 同名，双保险不另设第二套码表）。</summary>
public static class PartitionErrorCodes
{
    public const string OutOfBounds = "PARTITION_OUT_OF_BOUNDS";
    public const string Overlap = "PARTITION_OVERLAP";
    public const string Hole = "PARTITION_HOLE";
    public const string Cover = "PARTITION_COVER";
}

/// <summary>分区不变式检查结果（M5 设计 §3.3 INV-P1–P5）：违规码列表，空 = 全部成立。</summary>
public sealed record PartitionInvariants(IReadOnlyList<string> Violations)
{
    /// <summary>无违规的共享实例。</summary>
    public static readonly PartitionInvariants Valid = new([]);

    public bool IsValid => Violations.Count == 0;

    public bool Contains(string code) => Violations.Contains(code);
}

/// <summary>
/// 分区布局不可变值（M5 设计 §3）：权威表示 = 分区矩形列表（承 M2 §4.4 决策），
/// 「墙」为纯派生视图（<see cref="Edges"/>：实线 ⇔ 两侧格属不同分区）。
/// 构造期建 cell→分区索引表（≤ 8×25=200 格）→ <see cref="PartitionAt"/> O(1)。
/// record 值相等按 Size + 分区逐条比较（INV-P11 撤销快照往返的判等基础）。
/// 允许构造不变式非法的实例（Check 负例测试需要）；引擎操作只在合法布局上进行。
/// </summary>
public sealed record PartitionLayout
{
    private readonly int[] _cellIndex;

    public PartitionLayout(GridSize size, IReadOnlyList<GridRect> partitions)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        if (size.Columns < 1 || size.Rows < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "组尺寸必须 ≥ 1×1。");
        }

        Size = size;
        Partitions = [.. partitions];
        _cellIndex = new int[size.Columns * size.Rows];
        _cellIndex.AsSpan().Fill(-1);
        for (var i = 0; i < Partitions.Count; i++)
        {
            var p = Partitions[i];
            for (var row = Math.Max(0, p.Row); row < Math.Min(size.Rows, p.Bottom); row++)
            {
                var baseIndex = row * size.Columns;
                for (var col = Math.Max(0, p.Column); col < Math.Min(size.Columns, p.Right); col++)
                {
                    if (_cellIndex[baseIndex + col] < 0)
                    {
                        _cellIndex[baseIndex + col] = i; // 重叠（非法布局）时先到者优先
                    }
                }
            }
        }
    }

    /// <summary>组总行列（列 2–8、行 2–25，设计 §4.4；界限谓词在 Check/GroupDraftValidator）。</summary>
    public GridSize Size { get; }

    /// <summary>组内相对坐标分区列表（M2 §4.4 权威表示）。</summary>
    public IReadOnlyList<GridRect> Partitions { get; }

    public int PartitionCount => Partitions.Count;

    /// <summary>包含格 (column,row) 的分区矩形；O(1)。越界格或孔洞格（非法布局）上抛。</summary>
    public GridRect PartitionAt(int column, int row)
    {
        if (column < 0 || column >= Size.Columns || row < 0 || row >= Size.Rows)
        {
            throw new ArgumentOutOfRangeException(nameof(column), (column, row), $"格 ({column},{row}) 越出布局 {Size.Columns}×{Size.Rows}。");
        }

        var index = _cellIndex[(row * Size.Columns) + column];
        if (index < 0)
        {
            throw new InvalidOperationException($"格 ({column},{row}) 无分区覆盖（布局存在孔洞，Check() 应报 {PartitionErrorCodes.Hole}）。");
        }

        return Partitions[index];
    }

    /// <summary>全 1×1 工厂（默认 4×4=十六块，设计 §7.1/B02）。</summary>
    public static PartitionLayout FullGrid(GridSize size)
    {
        if (size.Columns < 1 || size.Rows < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "组尺寸必须 ≥ 1×1。");
        }

        var partitions = new GridRect[size.Columns * size.Rows];
        var i = 0;
        for (var row = 0; row < size.Rows; row++)
        {
            for (var col = 0; col < size.Columns; col++)
            {
                partitions[i++] = new GridRect(col, row, 1, 1);
            }
        }

        return new PartitionLayout(size, partitions);
    }

    /// <summary>
    /// 派生：当前实线内部边集合（渲染/命中专用，从不回写）。
    /// V(x,y) 实线 ⇔ PartitionAt(x−1,y) ≠ PartitionAt(x,y)；H(x,y) 对称（设计 §3.1）。
    /// </summary>
    public IEnumerable<WallEdge> Edges()
    {
        var w = Size.Columns;
        var h = Size.Rows;
        for (var y = 0; y < h; y++)
        {
            for (var x = 1; x < w; x++)
            {
                if (_cellIndex[(y * w) + x - 1] != _cellIndex[(y * w) + x])
                {
                    yield return new WallEdge(IsVertical: true, x, y);
                }
            }
        }

        for (var y = 1; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (_cellIndex[((y - 1) * w) + x] != _cellIndex[(y * w) + x])
                {
                    yield return new WallEdge(IsVertical: false, x, y);
                }
            }
        }
    }

    /// <summary>该内部边当前是否为实线（两侧格属不同分区）；越出定义域抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
    public bool IsSolid(WallEdge edge)
    {
        if (!PartitionOps.InDomain(Size, edge))
        {
            throw new ArgumentOutOfRangeException(nameof(edge), edge, $"边 {edge} 越出内部边定义域（布局 {Size.Columns}×{Size.Rows}）。");
        }

        return PartitionOps.CellsAcross(this, edge).A != PartitionOps.CellsAcross(this, edge).B;
    }

    /// <summary>不变式谓词（INV-P1–P5；错误码与 ConfigValidator.ValidatePartitions 同名）。</summary>
    public PartitionInvariants Check()
    {
        var violations = new List<string>();
        var w = Size.Columns;
        var h = Size.Rows;

        // INV-P4：每分区为宽高 ≥1 的矩形且整体在 [0,W)×[0,H) 内
        foreach (var p in Partitions)
        {
            if (p.Width < 1 || p.Height < 1 || p.Column < 0 || p.Row < 0 || p.Right > w || p.Bottom > h)
            {
                violations.Add(PartitionErrorCodes.OutOfBounds);
            }
        }

        // INV-P1：两两不交
        for (var i = 0; i < Partitions.Count; i++)
        {
            for (var j = i + 1; j < Partitions.Count; j++)
            {
                if (Partitions[i].Intersects(Partitions[j]))
                {
                    violations.Add(PartitionErrorCodes.Overlap);
                }
            }
        }

        // INV-P2：完整覆盖（格覆盖表；越界部分裁剪后统计，与落盘闸门同一口径）
        var covered = new bool[w * h];
        foreach (var p in Partitions)
        {
            for (var row = Math.Max(0, p.Row); row < Math.Min(h, p.Bottom); row++)
            {
                var baseIndex = row * w;
                for (var col = Math.Max(0, p.Column); col < Math.Min(w, p.Right); col++)
                {
                    covered[baseIndex + col] = true;
                }
            }
        }

        var coveredCount = 0;
        foreach (var cell in covered)
        {
            if (cell)
            {
                coveredCount++;
            }
        }

        if (coveredCount != covered.Length)
        {
            violations.Add(PartitionErrorCodes.Cover);
        }

        // INV-P3：无孔洞（未覆盖格在某行/列上两侧均有覆盖格；独立谓词仅为落盘诊断保留）
        var hasHole = false;
        for (var row = 0; row < h && !hasHole; row++)
        {
            for (var col = 0; col < w && !hasHole; col++)
            {
                if (covered[(row * w) + col])
                {
                    continue;
                }

                var coveredLeft = false;
                var coveredRight = false;
                for (var c = 0; c < w; c++)
                {
                    if (covered[(row * w) + c])
                    {
                        coveredLeft |= c < col;
                        coveredRight |= c > col;
                    }
                }

                var coveredAbove = false;
                var coveredBelow = false;
                for (var r = 0; r < h; r++)
                {
                    if (covered[(r * w) + col])
                    {
                        coveredAbove |= r < row;
                        coveredBelow |= r > row;
                    }
                }

                hasHole = (coveredLeft && coveredRight) || (coveredAbove && coveredBelow);
            }
        }

        if (hasHole)
        {
            violations.Add(PartitionErrorCodes.Hole);
        }

        return violations.Count == 0 ? PartitionInvariants.Valid : new PartitionInvariants(violations);
    }

    public bool Equals(PartitionLayout? other) =>
        other is not null && Size.Equals(other.Size) && Partitions.SequenceEqual(other.Partitions);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Size);
        foreach (var p in Partitions)
        {
            hash.Add(p);
        }

        return hash.ToHashCode();
    }
}
