namespace TileWall.Core.Grid;

/// <summary>布局子页画笔工具（§10.3：必须先选工具，画布不响应未选工具的笔画由 UI 守卫）。</summary>
public enum EditTool
{
    Draw,
    Erase,
}

/// <summary>笔画裁决结果（§6.2 固定次序：越界 → 去抖 → 工具不匹配忽略 → 应用）。</summary>
public enum StrokeOutcome
{
    Applied,
    Debounced,
    SolidForDraw,
    DashedForErase,
    OutOfRange,
}

/// <summary>落笔前预览（纯函数；状态行/画布叠加）：Draw 适用 → NewLineExtent=将新增完整线段矩形；Erase 适用 → MergeExtent=最终合并矩形 R。</summary>
public sealed record StrokePreview(bool Applicable, StrokeOutcome Outcome, GridRect? NewLineExtent, GridRect? MergeExtent);

/// <summary>
/// 布局子草稿会话（M5 设计 §6）：确定性有状态——无时钟/随机/IO（类型级保证 INV-P12 草稿隔离）。
/// 去抖集合以 WallEdge 规范标识判等（同一边界一次笔画仅首次生效、不反向切换，B06）；
/// 一次连续笔画（含单击 Begin+Stroke+End）= 一个撤销单元；行列按钮各自一单元；
/// 会话内快照撤销/重做栈（容量默认 100，关窗即弃），不与主墙单槽撤销互通。
/// </summary>
public sealed class PartitionEditSession
{
    private readonly int _undoCapacity;
    private readonly List<PartitionLayout> _undo = [];
    private readonly List<PartitionLayout> _redo = [];
    private readonly HashSet<WallEdge> _strokeEdges = [];
    private PartitionLayout _strokeStart;

    public PartitionEditSession(GridSize initialSize, IReadOnlyList<GridRect> initialPartitions, int undoCapacity = 100)
    {
        ArgumentNullException.ThrowIfNull(initialPartitions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(undoCapacity);
        _undoCapacity = undoCapacity;
        Current = new PartitionLayout(initialSize, initialPartitions);
        _strokeStart = Current;
    }

    /// <summary>布局子草稿的唯一事实（不可变快照；确认编辑时整批回主页草稿）。</summary>
    public PartitionLayout Current { get; private set; }

    /// <summary>撤销栈深度（测试与 UI 到界禁用断言用）。</summary>
    public int UndoDepth => _undo.Count;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    // ————————————————————————————— 笔画（§6.1/§6.2） —————————————————————————————

    /// <summary>开始一轮笔画：清空本轮去抖集合并记住笔画前快照（单击 = Begin+Stroke+End 同样适用）。</summary>
    public void BeginStroke()
    {
        _strokeEdges.Clear();
        _strokeStart = Current;
    }

    /// <summary>
    /// 边界事件裁决（次序固定，§6.2）：1 越界 → OutOfRange；2 本轮已生效 → Debounced；
    /// 3 画墙遇实线 → SolidForDraw；4 拆墙遇虚线 → DashedForErase；5 应用 W-1/D-1 并记入去抖集合 → Applied。
    /// 非Applied 一律不改布局。
    /// </summary>
    public StrokeOutcome Stroke(WallEdge edge, EditTool tool)
    {
        if (!PartitionOps.InDomain(Current.Size, edge))
        {
            return StrokeOutcome.OutOfRange; // 1. 越出定义域（不改布局）
        }

        if (_strokeEdges.Contains(edge))
        {
            return StrokeOutcome.Debounced; // 2. 同一边界一次笔画仅首次生效；不反向切换（B06）
        }

        if (tool == EditTool.Draw && Current.IsSolid(edge))
        {
            return StrokeOutcome.SolidForDraw; // 3. 画墙经过实线不处理（§8.4）
        }

        if (tool == EditTool.Erase && !Current.IsSolid(edge))
        {
            return StrokeOutcome.DashedForErase; // 4. 拆墙经过虚线不处理（§8.4）
        }

        var result = tool == EditTool.Draw
            ? PartitionOps.DrawWall(Current, edge)
            : PartitionOps.EraseWall(Current, edge);
        if (result.Status != PartitionOpStatus.Applied || result.Layout is null)
        {
            // 防御：与上方虚实判定应一致；出现即按原因返回、布局不变
            return result.Status switch
            {
                PartitionOpStatus.SolidForDraw => StrokeOutcome.SolidForDraw,
                PartitionOpStatus.DashedForErase => StrokeOutcome.DashedForErase,
                _ => StrokeOutcome.OutOfRange,
            };
        }

        Current = result.Layout; // 5. 应用
        _strokeEdges.Add(edge);
        return StrokeOutcome.Applied;
    }

    /// <summary>结束笔画：本轮有生效边界 → 压入一个撤销单元并返回 true；否则 false（栈不动，INV-P11）。</summary>
    public bool EndStroke()
    {
        if (_strokeEdges.Count == 0)
        {
            _strokeEdges.Clear();
            return false;
        }

        PushUndo(_strokeStart);
        _strokeEdges.Clear();
        return true;
    }

    /// <summary>悬停预览（未按下）：只出预览不改状态（§6.1）。</summary>
    public StrokePreview Preview(WallEdge edge, EditTool tool)
    {
        if (!PartitionOps.InDomain(Current.Size, edge))
        {
            return new StrokePreview(false, StrokeOutcome.OutOfRange, null, null);
        }

        var solid = Current.IsSolid(edge);
        if (tool == EditTool.Draw)
        {
            if (solid)
            {
                return new StrokePreview(false, StrokeOutcome.SolidForDraw, null, null);
            }

            var p = PartitionOps.CellsAcross(Current, edge).A; // 两侧同块，取任一
            var line = edge.IsVertical
                ? new GridRect(edge.X, p.Row, 1, p.Height)      // 新竖线段：贯穿 p 整条高
                : new GridRect(p.Column, edge.Y, p.Width, 1);   // 新横线段：贯穿 p 整条宽
            return new StrokePreview(true, StrokeOutcome.Applied, line, null);
        }

        if (!solid)
        {
            return new StrokePreview(false, StrokeOutcome.DashedForErase, null, null);
        }

        var extent = PartitionOps.EraseExtent(Current, edge);
        return new StrokePreview(true, StrokeOutcome.Applied, null, extent);
    }

    // ————————————————————————————— 行列增减（单击即一个撤销单元，§7） —————————————————————————————

    /// <summary>右扩一列（INV-P8）；已到 8 列返回 false 且布局不变。</summary>
    public bool AddColumn() => ApplyResize(PartitionOps.AddColumn(Current));

    /// <summary>缩列（INV-P9）；已到 2 列返回 false 且布局不变。</summary>
    public bool RemoveColumn() => ApplyResize(PartitionOps.RemoveColumn(Current));

    /// <summary>下扩一行（INV-P8）；已到 25 行返回 false 且布局不变。</summary>
    public bool AddRow() => ApplyResize(PartitionOps.AddRow(Current));

    /// <summary>缩行（INV-P9）；已到 2 行返回 false 且布局不变。</summary>
    public bool RemoveRow() => ApplyResize(PartitionOps.RemoveRow(Current));

    private bool ApplyResize(PartitionOpResult result)
    {
        if (result.Status != PartitionOpStatus.Applied || result.Layout is null)
        {
            return false; // AtLimit：布局逐字段不变
        }

        PushUndo(Current);
        Current = result.Layout;
        return true;
    }

    // ————————————————————————————— 轻量撤销/重做（INV-P11） —————————————————————————————

    /// <summary>栈空 → false；否则 Current ← 上一快照、当前态入重做栈。</summary>
    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        _redo.Add(Current);
        Current = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    /// <summary>Undo 的对称操作。</summary>
    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        _undo.Add(Current);
        Current = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        return true;
    }

    /// <summary>新撤销单元：入栈、清重做栈、容量超出丢最旧（§6.2）。</summary>
    private void PushUndo(PartitionLayout snapshot)
    {
        _undo.Add(snapshot);
        if (_undo.Count > _undoCapacity)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
    }
}
