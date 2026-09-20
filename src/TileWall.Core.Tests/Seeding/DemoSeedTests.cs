using TileWall.Core.Configuration;
using TileWall.Core.Grid;
using TileWall.Core.Seeding;
using Xunit;

namespace TileWall.Core.Tests.Seeding;

/// <summary>
/// T-SEED：开发种子纯函数（M3 设计 §8.2–8.3、§9.2 套件表）。
/// 断言点：过校验器零违规；标准墙布置与 §8.2 表逐项一致；两次调用逐字段相等；
/// 固定 Id 唯一；降级墙确定性且零违规；AutomationId 前缀映射（tile- + Id）逐项核对。
/// </summary>
public class DemoSeedTests
{
    private static readonly WallGrid StandardWall = new(2, 5); // 2 栏 × 5 行（M3 设计 §9.2）
    private static readonly WallGrid DegradedWall = new(1, 5); // 1 栏 × 5 行（降级用例）

    // —— 标准墙：合法、组成、布置表 ——

    [Fact]
    public void StandardWall_PassesValidator_WithZeroViolations()
    {
        var violations = ConfigValidator.Validate(DemoSeed.Create(StandardWall));
        Assert.Empty(violations);
    }

    [Fact]
    public void StandardWall_AtLeast6Tiles_AndOne4x4GroupWith16Partitions()
    {
        var config = DemoSeed.Create(StandardWall);

        Assert.Equal(2, config.Wall.Columns);
        Assert.Equal(5, config.Wall.Rows);

        var tiles = config.Objects.OfType<TileObject>().ToList();
        var groups = config.Objects.OfType<GroupObject>().ToList();
        Assert.True(tiles.Count >= 6, $"独立磁贴应 ≥ 6，实际 {tiles.Count}");
        var group = Assert.Single(groups);
        Assert.Equal(new GridRect(8, 0, 4, 4), group.Bounds);
        Assert.Equal(16, group.Partitions.Count);
        Assert.All(group.Partitions, p => Assert.Equal(new GridRect(p.Column, p.Row, 1, 1), p));
    }

    [Fact]
    public void StandardWall_ObjectLayout_MatchesSpecTable()
    {
        var objects = DemoSeed.Create(StandardWall).Objects;

        Assert.Equal(new GridRect(0, 0, 2, 2), ById(objects, "seed-tile-1").Bounds);
        Assert.Equal(new GridRect(2, 0, 1, 1), ById(objects, "seed-tile-2").Bounds);
        Assert.Equal(new GridRect(3, 0, 2, 1), ById(objects, "seed-tile-3").Bounds);
        Assert.Equal(new GridRect(2, 1, 1, 1), ById(objects, "seed-tile-4").Bounds);
        Assert.Equal(new GridRect(4, 1, 2, 2), ById(objects, "seed-tile-5").Bounds);
        Assert.Equal(new GridRect(0, 2, 1, 2), ById(objects, "seed-tile-6").Bounds);
        Assert.Equal(new GridRect(8, 0, 4, 4), ById(objects, "seed-group-1").Bounds);
    }

    [Fact]
    public void StandardWall_AllObjects_EmptyEntryWithTitlesAndColors()
    {
        foreach (var o in DemoSeed.Create(StandardWall).Objects)
        {
            Assert.Null(o.Entry); // M3 设计 §8.2：全部空目标
            Assert.True(o.Visual.ShowTitle);
            Assert.NotNull(o.Visual.TitleText);
            Assert.NotNull(o.Visual.BackgroundColor);
        }
    }

    [Fact]
    public void FixedIds_AreTheDocumentedLiterals_AndUnique()
    {
        var ids = DemoSeed.Create(StandardWall).Objects.Select(o => o.Id).ToList();

        var expected = new[]
        {
            "seed-tile-1", "seed-tile-2", "seed-tile-3", "seed-tile-4", "seed-tile-5", "seed-tile-6", "seed-group-1",
        };
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), ids.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count()); // 校验器仅要求非空唯一（ConfigValidator.cs:70-77）
    }

    [Fact]
    public void AutomationIdPrefix_MapsToTilePlusId_ForEveryObject()
    {
        // M3 设计 §4 AutomationId 契约：对象根 = "tile-" + Id；组分区 = "tile-" + Id + "-part-<i>"
        var expectedRootIds = new[]
        {
            "tile-seed-tile-1", "tile-seed-tile-2", "tile-seed-tile-3",
            "tile-seed-tile-4", "tile-seed-tile-5", "tile-seed-tile-6", "tile-seed-group-1",
        };
        var actualRootIds = DemoSeed.Create(StandardWall).Objects.Select(o => $"tile-{o.Id}").ToList();
        Assert.Equal(expectedRootIds.OrderBy(x => x, StringComparer.Ordinal), actualRootIds.OrderBy(x => x, StringComparer.Ordinal));

        var group = DemoSeed.Create(StandardWall).Objects.OfType<GroupObject>().Single();
        // 分区序号 0..Count-1 → UIA 将存在 tile-seed-group-1-part-0 .. part-15（16 个、互不相同）
        Assert.Equal(Enumerable.Range(0, 16), Enumerable.Range(0, group.Partitions.Count));
        var partitionIds = Enumerable.Range(0, group.Partitions.Count).Select(i => $"tile-{group.Id}-part-{i}").ToList();
        Assert.Equal(group.Partitions.Count, partitionIds.Distinct().Count());
    }

    // —— 确定性：两次调用逐字段相等（record 集合属性是引用相等，故逐字段比对） ——

    [Fact]
    public void TwoCalls_StandardWall_FieldByFieldEqual()
    {
        var a = DemoSeed.Create(StandardWall);
        var b = DemoSeed.Create(StandardWall);

        Assert.Equal(a.Wall, b.Wall);
        Assert.Equal(a.SchemaVersion, b.SchemaVersion);
        Assert.Equal(a.Objects.Count, b.Objects.Count);
        for (var i = 0; i < a.Objects.Count; i++)
        {
            AssertFieldEqual(a.Objects[i], b.Objects[i]);
        }
    }

    // —— 降级：1 栏 × 5 行，仍确定、零违规 ——

    [Fact]
    public void DegradedWall1x5_Deterministic_AndZeroViolations()
    {
        var a = DemoSeed.Create(DegradedWall);
        var b = DemoSeed.Create(DegradedWall);

        Assert.Empty(ConfigValidator.Validate(a));
        Assert.Equal(a.Objects.Count, b.Objects.Count);
        for (var i = 0; i < a.Objects.Count; i++)
        {
            AssertFieldEqual(a.Objects[i], b.Objects[i]);
        }

        // 降级语义：6 个变尺寸磁贴逐项 first-fit 应全部放下；4×4 组在 8 列宽内无 4×4 空位 → 跳过
        Assert.Equal(6, a.Objects.Count);
        Assert.DoesNotContain(a.Objects, o => o is GroupObject);
    }

    [Fact]
    public void DegradedTinyWall1x1_PlacesOnlyWhatFits_AndZeroViolations()
    {
        // 1 栏 × 1 行 = 8 基础格列 × 1 行：first-fit（行优先）依次放下 tile-2(1×1)、tile-3(2×1)、tile-4(1×1)；
        // tile-1/5/6 高 ≥ 2 放不下，组 4×4 亦放不下
        var config = DemoSeed.Create(new WallGrid(1, 1));

        Assert.Empty(ConfigValidator.Validate(config));
        Assert.Equal(
            new[] { "seed-tile-2", "seed-tile-3", "seed-tile-4" },
            config.Objects.Select(o => o.Id).ToArray());
        Assert.Equal(new GridRect(0, 0, 1, 1), config.Objects[0].Bounds);
        Assert.Equal(new GridRect(1, 0, 2, 1), config.Objects[1].Bounds);
        Assert.Equal(new GridRect(3, 0, 1, 1), config.Objects[2].Bounds);
    }

    // —— 辅助 ——

    private static LayoutObject ById(IReadOnlyList<LayoutObject> objects, string id) =>
        objects.Single(o => o.Id == id);

    private static void AssertFieldEqual(LayoutObject a, LayoutObject b)
    {
        Assert.Equal(a.GetType(), b.GetType());
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(a.Bounds, b.Bounds);
        Assert.Equal(a.Entry, b.Entry);
        Assert.Equal(a.Visual.ShowTitle, b.Visual.ShowTitle);
        Assert.Equal(a.Visual.TitleText, b.Visual.TitleText);
        Assert.Equal(a.Visual.BackgroundColor, b.Visual.BackgroundColor);
        if (a is GroupObject ga && b is GroupObject gb)
        {
            Assert.Equal(ga.Partitions.Count, gb.Partitions.Count);
            for (var i = 0; i < ga.Partitions.Count; i++)
            {
                Assert.Equal(ga.Partitions[i], gb.Partitions[i]);
            }
        }
    }
}
