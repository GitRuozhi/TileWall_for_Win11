using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Grid;

/// <summary>
/// T-FUZZ（P1 §4.2 V-02「随机笔画序列模糊测试」）：种子化 Random ≥1000 例——
/// 随机初始剖分（FullGrid + 随机合法操作）× 随机笔画序列（工具/边界/行列/撤销重做混合）。
/// 每次操作后断言 INV-P1–P5（Check）/P10（失败输入布局逐字段不变）；笔画结束断言撤销栈深度；
/// 失败打印种子与操作序列（确定性复跑）。
/// </summary>
public sealed class PartitionFuzzTests
{
    private const int CaseCount = 1200; // ≥1000（任务书硬性要求）
    private const int SeedBase = 20260921;

    [Fact]
    public void RandomStrokeSequences_PreserveInvariants_AcrossThousandSeededCases()
    {
        for (var caseIndex = 0; caseIndex < CaseCount; caseIndex++)
        {
            var seed = SeedBase + caseIndex;
            var log = new List<string>();
            try
            {
                RunCase(seed, log);
            }
            catch (Exception ex)
            {
                throw new Exception(
                    $"T-FUZZ 失败：seed={seed}（复跑：new Random({seed})）\n操作序列：\n{string.Join(Environment.NewLine, log)}",
                    ex);
            }
        }
    }

    private static void RunCase(int seed, List<string> log)
    {
        var random = new Random(seed);

        // —— 随机初始剖分（合法构造器路径：FullGrid + 随机适用操作）——
        var size = new GridSize(2 + random.Next(7), 2 + random.Next(24)); // 列 2–8、行 2–25
        var layout = PartitionLayout.FullGrid(size);
        log.Add($"init FullGrid({size.Columns},{size.Rows})");
        var initOps = random.Next(8);
        for (var i = 0; i < initOps; i++)
        {
            var edge = RandomEdge(random, layout.Size);
            var tool = random.Next(2) == 0 ? EditTool.Draw : EditTool.Erase;
            var result = tool == EditTool.Draw
                ? PartitionOps.DrawWall(layout, edge)
                : PartitionOps.EraseWall(layout, edge);
            log.Add($"init {tool} {edge} → {result.Status}");
            if (result.Status == PartitionOpStatus.Applied)
            {
                layout = result.Layout!;
            }

            PartitionAssert.AssertValid(layout, $"fuzz#{seed} 初始剖分第 {i} 步后");
        }

        var session = new PartitionEditSession(layout.Size, layout.Partitions);

        // —— 随机笔画序列 ——
        var strokes = 1 + random.Next(10);
        for (var s = 0; s < strokes; s++)
        {
            session.BeginStroke();
            var events = 1 + random.Next(6);
            var appliedAny = false;
            for (var e = 0; e < events; e++)
            {
                var tool = random.Next(2) == 0 ? EditTool.Draw : EditTool.Erase;
                var edge = RandomEdge(random, session.Current.Size); // 含越域候选（X/ Y 可取到 0 或 W/H）
                var before = session.Current;
                var outcome = session.Stroke(edge, tool);
                log.Add($"stroke[{s}].{e} {tool} {edge} → {outcome}");
                switch (outcome)
                {
                    case StrokeOutcome.Applied:
                        appliedAny = true;
                        Assert.False(before.Equals(session.Current), $"Applied 后布局必须变化（seed={seed}）");
                        break;
                    default:
                        Assert.True(before.Equals(session.Current),
                            $"非Applied（{outcome}）后布局逐字段不变（INV-P10，seed={seed}）");
                        break;
                }

                PartitionAssert.AssertValid(session.Current, $"fuzz#{seed} stroke[{s}].{e} {tool} {edge} → {outcome} 后");
            }

            var depthBefore = session.UndoDepth;
            var changed = session.EndStroke();
            log.Add($"stroke[{s}] end → changed={changed}");
            Assert.Equal(appliedAny, changed);
            Assert.Equal(depthBefore + (changed ? 1 : 0), session.UndoDepth);
            PartitionAssert.AssertValid(session.Current, $"fuzz#{seed} stroke[{s}] 结束后");

            // 混入撤销/重做
            if (random.Next(4) == 0 && session.Undo())
            {
                PartitionAssert.AssertValid(session.Current, $"fuzz#{seed} stroke[{s}] 后 Undo");
            }

            if (random.Next(4) == 0 && session.Redo())
            {
                PartitionAssert.AssertValid(session.Current, $"fuzz#{seed} stroke[{s}] 后 Redo");
            }
        }

        // —— 混入行列增减（单击即单元）——
        var resizes = random.Next(4);
        for (var r = 0; r < resizes; r++)
        {
            var which = random.Next(4);
            var before = session.Current;
            var depthBefore = session.UndoDepth;
            var ok = which switch
            {
                0 => session.AddColumn(),
                1 => session.RemoveColumn(),
                2 => session.AddRow(),
                _ => session.RemoveRow(),
            };
            log.Add($"resize {which} → {ok}");
            var check = session.Current.Check();
            Assert.True(check.IsValid, $"fuzz#{seed} resize[{which}]={ok} 后不变式违规：{string.Join(",", check.Violations)}");
            Assert.Equal(ok, session.UndoDepth == depthBefore + 1);
            if (!ok)
            {
                Assert.True(before.Equals(session.Current), "AtLimit 时布局不变");
            }

            // INV-P5 尺寸界限恒成立
            Assert.InRange(session.Current.Size.Columns, WallGrid.GroupMinColumns, WallGrid.GroupMaxColumns);
            Assert.InRange(session.Current.Size.Rows, WallGrid.GroupMinRows, WallGrid.GroupMaxRows);
        }
    }

    private static WallEdge RandomEdge(Random random, GridSize size) => new(
        IsVertical: random.Next(2) == 0,
        X: random.Next(size.Columns + 1),
        Y: random.Next(size.Rows + 1));
}
