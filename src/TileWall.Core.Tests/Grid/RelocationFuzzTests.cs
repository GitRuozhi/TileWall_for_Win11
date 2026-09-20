using TileWall.Core.Configuration;
using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;
using Xunit.Sdk;

namespace TileWall.Core.Tests.Grid;

/// <summary>
/// T-REL-F：种子化随机布局模糊测试（P1 §4.2 V-01「模糊 ≥1000 例零随机」）。
/// 固定种子 20260920——失败可复现；每例跑两遍逐字段比对（INV-9/12）+ 全部不变式断言（INV-1..11）。
/// 布局来源 =「L1–L4 风格混合」（设计 §9.1）：随机生成布局（组/变尺寸磁贴混排），
/// 外加每 5 例 1 例直接取 L2/L3/L4 参考夹具（含 L4 8×25 满栏二百分块组过引擎）。
/// 目标位三种形态：同尺寸对象原位（全重叠）、与随机对象部分重叠（链式挤压典型形态）、随机合法位。
/// </summary>
public class RelocationFuzzTests
{
    private const int Seed = 20260920;
    private const int CaseCount = 1200;

    [Fact]
    public void Fuzz_1200SeededCases_IncludingL2L3L4Fixtures_DeterministicAndInvariantClean()
    {
        Assert.True(CaseCount >= 1000, "设计要求模糊测试 ≥1000 例。");
        var random = new Random(Seed);
        var generated = 0;
        var fixtureCases = 0;
        var fixtureByLayout = new Dictionary<string, int>();
        var targetsByMode = new Dictionary<string, int>();

        for (var caseIndex = 0; caseIndex < CaseCount; caseIndex++)
        {
            WallGrid wall;
            List<LayoutObject> objects;
            var isFixture = caseIndex % 5 == 0;
            if (isFixture)
            {
                // 每 5 例 1 例：参考夹具布局轮换 L2 → L3 → L4（L4 = 8×25 满栏组 + 密铺条带）
                var fixtureName = ((caseIndex / 5) % 3) switch
                {
                    0 => "L2",
                    1 => "L3",
                    _ => "L4",
                };
                fixtureCases++;
                fixtureByLayout[fixtureName] = fixtureByLayout.GetValueOrDefault(fixtureName) + 1;
                switch (fixtureName)
                {
                    case "L2":
                        wall = Layouts.Wall2x9;
                        objects = Layouts.FullOnes();
                        break;
                    case "L3":
                        wall = Layouts.Wall2x9;
                        objects = Layouts.Mixed();
                        break;
                    default:
                        wall = Layouts.Wall2x25;
                        objects = Layouts.Extreme();
                        break;
                }
            }
            else
            {
                (wall, objects) = GenerateRandomLayout(random);
            }

            if (objects.Count == 0)
            {
                continue;
            }

            generated++;
            var draggedIndex = random.Next(objects.Count);
            var draggedId = objects[draggedIndex].Id;
            var (target, targetMode) = PickTarget(wall, objects, draggedIndex, random);
            targetsByMode[targetMode] = targetsByMode.GetValueOrDefault(targetMode) + 1;

            try
            {
                RunAndAssert(objects, wall, draggedId, target);
            }
            catch (XunitException ex)
            {
                var dump = ConfigJson.Serialize(Layouts.Config(wall, objects));
                throw new XunitException(
                    $"[seed={Seed} case={caseIndex} fixture={isFixture} dragged={draggedId} target={target}]\n{ex.Message}\n布局 JSON：{System.Text.Encoding.UTF8.GetString(dump)}");
            }
        }

        Assert.True(generated >= 1000, $"有效布局数 {generated} 少于 1000（种子 {Seed}）。");
        // 覆盖面自证（固定种子下可复现）：夹具例 = 240（L2/L3/L4 各 80），
        // 部分重叠目标 ≥100 例——链式挤压的随机覆盖面断言（评审修复项）。
        Assert.Equal(240, fixtureCases);
        Assert.Equal(80, fixtureByLayout.GetValueOrDefault("L2"));
        Assert.Equal(80, fixtureByLayout.GetValueOrDefault("L3"));
        Assert.Equal(80, fixtureByLayout.GetValueOrDefault("L4"));
        Assert.True(targetsByMode.GetValueOrDefault("partial") >= 100,
            $"部分重叠目标仅 {targetsByMode.GetValueOrDefault("partial")} 例，随机覆盖不足；分布：{string.Join(", ", targetsByMode.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))}");
    }

    /// <summary>INV-9：两次执行逐字段一致（含 Moves 次序）；成功则断言 INV-1..11，失败则断言零残留。</summary>
    private static void RunAndAssert(List<LayoutObject> objects, WallGrid wall, string draggedId, GridRect target)
    {
        var first = RelocationEngine.Relocate(objects, wall, draggedId, target);
        var second = RelocationEngine.Relocate(objects, wall, draggedId, target);

        RelocationInvariants.AssertSameResult(first, second);

        if (first.Success)
        {
            RelocationInvariants.AssertAll(objects, wall, draggedId, target, first);
        }
        else
        {
            // INV-8：失败返回不携带部分结果；record 不可变 → 输入快照零改动
            Assert.True(first.Reason is RelocationFailureReason.TargetInvalid
                or RelocationFailureReason.NoEvictionPossible);
            Assert.Null(first.ResultObjects);
            Assert.Empty(first.Moves);
        }
    }

    /// <summary>目标位选取：25% 同尺寸对象原位（全重叠）、35% 部分重叠、其余随机合法位；兜底拖回原位。返回目标与实际命中形态。</summary>
    private static (GridRect Target, string Mode) PickTarget(WallGrid wall, List<LayoutObject> objects, int draggedIndex, Random random)
    {
        var dragged = objects[draggedIndex];
        var mode = random.Next(100);

        if (mode < 25)
        {
            var sameSize = objects.Where((o, i) => i != draggedIndex
                && o.Bounds.Width == dragged.Bounds.Width && o.Bounds.Height == dragged.Bounds.Height).ToList();
            if (sameSize.Count > 0)
            {
                return (sameSize[random.Next(sameSize.Count)].Bounds, "same-size");
            }
        }
        else if (mode < 60 && TryPartialOverlapTarget(wall, objects, draggedIndex, random, out var partial))
        {
            return (partial, "partial");
        }

        var occupied = OccupancyMap.Build(wall, objects.Select(o => o.Bounds));
        if (TryRandomRect(wall, occupied, dragged.Bounds.Size, random, out var randomRect))
        {
            return (randomRect, "random-free");
        }

        return (dragged.Bounds, "fallback-own");
    }

    /// <summary>
    /// 部分重叠目标：以随机其他对象原位为锚，随机偏移（偏移范围保证相交），要求结果
    /// 墙内 + 单竖栏 + 与某对象相交但非其原位全等——产生「2×2 拖到 3×3 角上」类样本。
    /// </summary>
    private static bool TryPartialOverlapTarget(
        WallGrid wall, List<LayoutObject> objects, int draggedIndex, Random random, out GridRect target)
    {
        target = default;
        var dragged = objects[draggedIndex];
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var anchor = objects[random.Next(objects.Count)];
            if (anchor.Id == dragged.Id)
            {
                continue;
            }

            // dColumn ∈ [1−w, W−1] 时两列区间必相交；行同理
            var dColumn = random.Next(1 - dragged.Bounds.Width, anchor.Bounds.Width);
            var dRow = random.Next(1 - dragged.Bounds.Height, anchor.Bounds.Height);
            var candidate = new GridRect(
                anchor.Bounds.Column + dColumn,
                anchor.Bounds.Row + dRow,
                dragged.Bounds.Width,
                dragged.Bounds.Height);

            if (!wall.Contains(candidate) || !wall.InSingleColumn(candidate) || candidate == dragged.Bounds)
            {
                continue;
            }

            var intersectsSome = objects.Any(o => o.Id != dragged.Id && candidate.Intersects(o.Bounds));
            var identicalToSome = objects.Any(o => o.Id != dragged.Id && candidate == o.Bounds);
            if (intersectsSome && !identicalToSome)
            {
                target = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>确定性生成随机布局：随机墙（1–2 栏 × 4–9 行），组（2–4×2–4）与变尺寸磁贴（1–4×1–3）混排。</summary>
    private static (WallGrid Wall, List<LayoutObject> Objects) GenerateRandomLayout(Random random)
    {
        var wall = new WallGrid(1 + random.Next(2), 4 + random.Next(6));
        var occupied = new OccupancyMap(wall);
        var objects = new List<LayoutObject>();

        var attempts = 40;
        while (objects.Count < 12 && attempts-- > 0)
        {
            GridRect rect;
            string id;
            if (random.Next(100) < 35)
            {
                // 组：2–4 × 2–4（L3/L4 风格）
                var size = new GridSize(2 + random.Next(3), 2 + random.Next(3));
                if (!TryRandomRect(wall, occupied, size, random, out rect))
                {
                    continue;
                }

                id = $"g{objects.Count:D3}";
                var partitions = new List<GridRect>(size.Columns * size.Rows);
                for (var row = 0; row < size.Rows; row++)
                {
                    for (var col = 0; col < size.Columns; col++)
                    {
                        partitions.Add(new GridRect(col, row, 1, 1));
                    }
                }

                objects.Add(new GroupObject { Id = id, Bounds = rect, Partitions = partitions });
            }
            else
            {
                // 独立磁贴：1–4 × 1–3（L2 风格的 1×1 占多数）
                var size = new GridSize(1 + random.Next(4), 1 + random.Next(3));
                if (!TryRandomRect(wall, occupied, size, random, out rect))
                {
                    continue;
                }

                id = $"t{objects.Count:D3}";
                objects.Add(Layouts.Tile(id, rect));
            }

            occupied.Fill(rect);
        }

        return (wall, objects);
    }

    /// <summary>随机挑一个墙内、单竖栏、与当前占用无交的矩形；失败返回 false。</summary>
    private static bool TryRandomRect(WallGrid wall, OccupancyMap occupied, GridSize size, Random random, out GridRect rect)
    {
        rect = default;
        if (size.Columns > GridMetrics.CellsPerColumn || size.Columns > wall.CellColumns || size.Rows > wall.Rows)
        {
            return false;
        }

        var strip = random.Next(wall.Columns);
        var column = (strip * GridMetrics.CellsPerColumn) + random.Next(GridMetrics.CellsPerColumn - size.Columns + 1);
        var row = random.Next(wall.Rows - size.Rows + 1);
        rect = new GridRect(column, row, size.Columns, size.Rows);
        return occupied.IsFree(rect);
    }
}
