using TileWall.Core.Configuration;

namespace TileWall.Core.Grid;

/// <summary>一次腾位移动：对象自 From 平移至 To（组同时整体平移其分区）。</summary>
public sealed record RelocationMove(string ObjectId, GridRect From, GridRect To);

/// <summary>腾位失败原因（UI 显示「不可放置」）。</summary>
public enum RelocationFailureReason
{
    None,
    TargetInvalid,
    NoEvictionPossible,
}

/// <summary>
/// 腾位结果。<see cref="ResultObjects"/> 即提交载荷——预览=提交的 API 契约（P1 §2.2 状态机、设计 §5.2）：
/// 引擎只产出这一份新布局；悬停预览渲染它，松手提交把同一实例交给 ConfigStore.Save。
/// </summary>
public sealed record RelocationResult(
    bool Success,
    RelocationFailureReason Reason,
    IReadOnlyList<LayoutObject>? ResultObjects,
    IReadOnlyList<RelocationMove> Moves);

/// <summary>
/// 自动腾位算法 E-1「定向挤压」（设计 §5.2–5.3、P1 §2.2）。
/// 纯函数：不修改入参；无时钟、无随机、无环境读取——同输入必同输出（§5.3 可预测性）。
/// </summary>
/// <remarks>
/// 确定性五规则（§7.2）：D-1 纯函数；D-2 全序 π=行→列→Id 消除遍历歧义；
/// D-3 方向次序固定 [Down,Right,Up,Left]（内部常量，不可注入）且每方向取最小整格位移；
/// D-4 冲突队列恒取 π 最小；D-5 Moves 与 ResultObjects 顺序由 π/处理序决定。
///
/// 挤压语义：被弹出对象的候选位只需避开「目标位 + 已放置新位」；若候选位与尚未移动的对象相交，
/// 该对象按 π 入队被继续挤压（§7.2「与 cand 新相交且未入队未放置者按 π 序入队」）——
/// 这正是链式挤压（Win10 磁贴下移让位观感）的来源，也是 INV-11「原位与某已放置新位相交」的情形。
/// 已移动对象的原位视为腾空（不构成障碍）。
/// </remarks>
public static class RelocationEngine
{
    /// <summary>方向固定次序：Down, Right, Up, Left（§7.2；Down/Up 保列——「尽量保留所在竖栏」）。不可注入。</summary>
    private static readonly (int DColumn, int DRow)[] EvictionDirectionOrder =
    [
        (0, 1),  // Down
        (1, 0),  // Right
        (0, -1), // Up
        (-1, 0), // Left
    ];

    /// <summary>
    /// 把 <paramref name="draggedId"/> 对象拖放到 <paramref name="target"/>，自动挤压冲突对象。
    /// draggedId 不在 objects 中 → ArgumentException（编程错误）；目标非法 → Fail(TargetInvalid)；
    /// 冲突链无法疏解 → Fail(NoEvictionPossible)，两种失败均不携带部分结果（INV-8）。
    /// </summary>
    public static RelocationResult Relocate(
        IReadOnlyList<LayoutObject> objects,
        WallGrid wall,
        string draggedId,
        GridRect target)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentException.ThrowIfNullOrEmpty(draggedId);

        LayoutObject? dragged = null;
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in objects)
        {
            if (!seenIds.Add(o.Id))
            {
                throw new ArgumentException($"对象 Id 重复：{o.Id}", nameof(objects));
            }

            if (o.Id == draggedId)
            {
                dragged = o;
            }
        }

        if (dragged is null)
        {
            throw new ArgumentException($"未找到被拖对象：{draggedId}", nameof(draggedId));
        }

        var failure = new RelocationResult(false, RelocationFailureReason.TargetInvalid, null, []);

        // §5.1：拖动不改变尺寸
        if (dragged.Bounds.Width != target.Width || dragged.Bounds.Height != target.Height)
        {
            return failure;
        }

        // 几何合法性（越界/跨栏/尺寸越限 → TargetInvalid；与占用无关，传空占用表）
        var geometry = GridPlacement.Validate(wall, target, KindOf(dragged), new OccupancyMap(wall));
        if (geometry is not (PlacementError.None or PlacementError.OverlapsOccupied))
        {
            return failure;
        }

        var others = objects.Where(o => !string.Equals(o.Id, draggedId, StringComparison.Ordinal)).ToArray();

        // 目标空位：零移动直接成功
        var placed = new OccupancyMap(wall);
        placed.Fill(target);
        if (TargetIsFree(placed, others))
        {
            return Success(objects, draggedId, target, [], []);
        }

        // 冲突链挤压：初始队列 = 与 target 相交的对象（π 序由取最小保证）
        var queue = new List<LayoutObject>();
        var enqueuedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in others)
        {
            if (o.Bounds.Intersects(target))
            {
                queue.Add(o);
                enqueuedIds.Add(o.Id);
            }
        }

        var moved = new List<(LayoutObject Obj, GridRect To)>();
        while (queue.Count > 0)
        {
            // D-4：恒取 π 最小（π = 行→列→Id，取输入 Bounds——对象至多移动一次，键恒定）
            var best = 0;
            for (var i = 1; i < queue.Count; i++)
            {
                if (SortKey(queue[i]).CompareTo(SortKey(queue[best])) < 0)
                {
                    best = i;
                }
            }

            var current = queue[best];
            queue.RemoveAt(best);

            if (!TryFindCandidate(wall, placed, current.Bounds, out var candidate))
            {
                return new RelocationResult(false, RelocationFailureReason.NoEvictionPossible, null, []);
            }

            placed.Fill(candidate);
            moved.Add((current, candidate));

            // 与 cand 新相交且未入队未放置者入队（每个对象至多入队一次）
            foreach (var z in others)
            {
                if (!enqueuedIds.Contains(z.Id) && z.Bounds.Intersects(candidate))
                {
                    queue.Add(z);
                    enqueuedIds.Add(z.Id);
                }
            }
        }

        var toById = new Dictionary<string, GridRect>(moved.Count, StringComparer.Ordinal);
        foreach (var (obj, to) in moved)
        {
            toById.Add(obj.Id, to);
        }

        var moves = new List<RelocationMove>(moved.Count);
        foreach (var (obj, to) in moved)
        {
            moves.Add(new RelocationMove(obj.Id, obj.Bounds, to));
        }

        return Success(objects, draggedId, target, toById, moves);
    }

    private static RelocationResult Success(
        IReadOnlyList<LayoutObject> objects,
        string draggedId,
        GridRect target,
        Dictionary<string, GridRect> toById,
        List<RelocationMove> moves)
    {
        // 组装（纯映射，不改输入记录；未移动对象保留原实例——INV-6）
        var resultObjects = new List<LayoutObject>(objects.Count);
        foreach (var o in objects)
        {
            if (string.Equals(o.Id, draggedId, StringComparison.Ordinal))
            {
                resultObjects.Add(WithBounds(o, target));
            }
            else if (toById.TryGetValue(o.Id, out var to))
            {
                resultObjects.Add(WithBounds(o, to));
            }
            else
            {
                resultObjects.Add(o);
            }
        }

        return new RelocationResult(true, RelocationFailureReason.None, resultObjects, moves);
    }

    /// <summary>D-3：方向次序固定，每方向取最小整格位移 d ≥ 1；墙内 ∧ 单竖栏 ∧ 与已放置矩形无交。</summary>
    private static bool TryFindCandidate(WallGrid wall, OccupancyMap placed, GridRect bounds, out GridRect candidate)
    {
        foreach (var (dColumn, dRow) in EvictionDirectionOrder)
        {
            for (var d = 1; ; d++)
            {
                var moved = bounds.Translate(dColumn * d, dRow * d);
                if (!wall.Contains(moved))
                {
                    break; // 该方向越界，更大 d 只会更远 → 换下一方向
                }

                if (!wall.InSingleColumn(moved))
                {
                    continue; // 跨栏，尝试更大 d
                }

                if (!placed.IsFree(moved))
                {
                    continue;
                }

                candidate = moved;
                return true;
            }
        }

        candidate = default;
        return false;
    }

    /// <summary>
    /// 组整体平移 = 仅改 Bounds：分区是 Bounds 的相对坐标（TileWallConfig.cs「坐标相对 Bounds 左上角」、设计 §2.4，
    /// ConfigValidator.ValidatePartitions 按 [0,W)×[0,H) 强制），Bounds 平移即整体刚性平移、分区永不拆分（INV-5）。
    /// 若按墙格位移平移分区，任何位移 ≠ 0 的组移动都会产出 PARTITION_OUT_OF_BOUNDS 的非法配置（保存必败）。
    /// </summary>
    private static LayoutObject WithBounds(LayoutObject o, GridRect bounds) => o switch
    {
        GroupObject g => g with { Bounds = bounds },
        TileObject t => t with { Bounds = bounds },
        _ => throw new InvalidOperationException($"未知布局对象类型：{o.GetType().Name}"),
    };

    /// <summary>target 与任何其他对象的原位都不相交 → 零移动直接成功。</summary>
    private static bool TargetIsFree(OccupancyMap placed, LayoutObject[] others)
    {
        foreach (var o in others)
        {
            if (!placed.IsFree(o.Bounds))
            {
                return false;
            }
        }

        return true;
    }

    private static ObjectKind KindOf(LayoutObject o) => o is GroupObject ? ObjectKind.Group : ObjectKind.Tile;

    private static (int Row, int Column, string Id) SortKey(LayoutObject o) =>
        (o.Bounds.Row, o.Bounds.Column, o.Id);
}
