using TileWall.Core.Configuration;
using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Configuration;

/// <summary>T-VAL：每条结构不变式一条负例 + 合法配置零违规（设计 §5.3、§4.2 单一真值）。</summary>
public class ConfigValidatorTests
{
    private static List<ConfigViolation> Validate(WallGrid wall, IReadOnlyList<LayoutObject> objects) =>
        ConfigValidator.Validate(Layouts.Config(wall, objects)).ToList();

    private static ConfigViolation Single(IReadOnlyList<ConfigViolation> violations, string code)
    {
        var match = violations.Where(v => v.Code == code).ToList();
        var violation = Assert.Single(match);
        return violation;
    }

    private static void AssertContainsCode(IReadOnlyList<ConfigViolation> violations, string code) =>
        Assert.Contains(violations, v => v.Code == code);

    [Fact]
    public void ValidMixedLayout_HasZeroViolations()
    {
        Assert.Empty(Validate(Layouts.Wall2x9, Layouts.Mixed()));
    }

    [Fact]
    public void ValidExtremeLayout_HasZeroViolations()
    {
        Assert.Empty(Validate(Layouts.Wall2x25, Layouts.Extreme()));
    }

    [Fact]
    public void EmptyId_Flagged()
    {
        var objects = new List<LayoutObject> { Layouts.Tile("  ", new GridRect(0, 0, 1, 1)) };
        var violation = Single(Validate(Layouts.Wall2x9, objects), ConfigValidator.EmptyId);
        Assert.Null(violation.ObjectId);
    }

    [Fact]
    public void DuplicateId_Flagged_C20()
    {
        // C20：两个同名磁贴 → 稳定标识必须唯一；同名但不同 Id 合法
        var objects = new List<LayoutObject>
        {
            Layouts.Tile("dup", new GridRect(0, 0, 1, 1)),
            Layouts.Tile("dup", new GridRect(1, 0, 1, 1)),
        };
        AssertContainsCode(Validate(Layouts.Wall2x9, objects), ConfigValidator.DuplicateId);

        var sameNameDifferentId = new List<LayoutObject>
        {
            Layouts.Tile("id-1", new GridRect(0, 0, 1, 1), title: "浏览器"),
            Layouts.Tile("id-2", new GridRect(1, 0, 1, 1), title: "浏览器"),
        };
        Assert.Empty(Validate(Layouts.Wall2x9, sameNameDifferentId));
    }

    [Fact]
    public void ObjectOutOfWall_Flagged()
    {
        var objects = new List<LayoutObject> { Layouts.Tile("t", new GridRect(15, 8, 1, 2)) };
        AssertContainsCode(Validate(Layouts.Wall2x9, objects), ConfigValidator.ObjectOutOfWall);
    }

    [Fact]
    public void ObjectCrossesColumn_Flagged()
    {
        var objects = new List<LayoutObject> { Layouts.Tile("t", new GridRect(6, 0, 3, 1)) };
        AssertContainsCode(Validate(Layouts.Wall2x9, objects), ConfigValidator.ObjectCrossesColumn);
    }

    [Fact]
    public void TileTooWide_Flagged()
    {
        var objects = new List<LayoutObject> { Layouts.Tile("t", new GridRect(0, 0, 9, 1)) };
        AssertContainsCode(Validate(Layouts.Wall2x9, objects), ConfigValidator.TileTooWide);
    }

    [Fact]
    public void GroupSizeOutOfRange_Flagged()
    {
        var oneColumn = new GroupObject { Id = "g", Bounds = new GridRect(0, 0, 1, 4), Partitions = [new GridRect(0, 0, 1, 4)] };
        AssertContainsCode(Validate(Layouts.Wall2x9, [oneColumn]), ConfigValidator.GroupSizeOutOfRange);

        var tooTall = Layouts.Group("g2", new GridRect(0, 0, 4, 26));
        AssertContainsCode(
            Validate(new WallGrid(2, 30), [tooTall]),
            ConfigValidator.GroupSizeOutOfRange);
    }

    [Fact]
    public void ObjectsOverlap_Flagged()
    {
        var objects = new List<LayoutObject>
        {
            Layouts.Tile("a", new GridRect(0, 0, 2, 2)),
            Layouts.Tile("b", new GridRect(1, 1, 2, 2)),
        };
        AssertContainsCode(Validate(Layouts.Wall2x9, objects), ConfigValidator.ObjectsOverlap);
    }

    [Fact]
    public void PartitionOutOfBounds_Flagged()
    {
        var group = new GroupObject
        {
            Id = "g",
            Bounds = new GridRect(0, 0, 4, 4),
            Partitions = Layouts.Group("g", new GridRect(0, 0, 4, 4)).Partitions
                .Append(new GridRect(3, 3, 2, 2))
                .ToList(),
        };
        AssertContainsCode(Validate(Layouts.Wall2x9, [group]), ConfigValidator.PartitionOutOfBounds);
    }

    [Fact]
    public void PartitionOverlap_Flagged()
    {
        var group = new GroupObject
        {
            Id = "g",
            Bounds = new GridRect(0, 0, 2, 2),
            Partitions =
            [
                new GridRect(0, 0, 2, 2),
                new GridRect(0, 0, 2, 2), // 完整重复 → 两两相交
            ],
        };
        AssertContainsCode(Validate(Layouts.Wall2x9, [group]), ConfigValidator.PartitionOverlap);
    }

    [Fact]
    public void PartitionHole_Flagged_ForInteriorGap()
    {
        // 4×4 十六块 1×1，抽掉内部块 (1,1) → 内部空洞
        var group = Layouts.Group("g", new GridRect(0, 0, 4, 4));
        var withHole = new GroupObject
        {
            Id = "g",
            Bounds = group.Bounds,
            Partitions = group.Partitions.Where(p => p.Column != 1 || p.Row != 1).ToList(),
        };
        var violations = Validate(Layouts.Wall2x9, [withHole]);
        AssertContainsCode(violations, ConfigValidator.PartitionHole);
        AssertContainsCode(violations, ConfigValidator.PartitionCover); // 覆盖不足同时成立
    }

    [Fact]
    public void PartitionCover_Flagged_ForMissingCorner_WithoutHole()
    {
        // 抽掉角块 (0,0)：覆盖不足，但缺格不在行/列两侧覆盖之间 → 无空洞（语义互斥的断言点）
        var group = Layouts.Group("g", new GridRect(0, 0, 4, 4));
        var missingCorner = new GroupObject
        {
            Id = "g",
            Bounds = group.Bounds,
            Partitions = group.Partitions.Where(p => p.Column != 0 || p.Row != 0).ToList(),
        };
        var violations = Validate(Layouts.Wall2x9, [missingCorner]);
        AssertContainsCode(violations, ConfigValidator.PartitionCover);
        Assert.DoesNotContain(violations, v => v.Code == ConfigValidator.PartitionHole);
    }

    [Fact]
    public void TitleTruthConflict_Flagged_EntryObjectMustNotCarryTitleText()
    {
        // §16.2：有入口对象 TitleText 必须为 null（名称真值在托管文件名主体）
        var entryWithTitle = Layouts.Tile("t", new GridRect(0, 0, 1, 1),
            entry: new EntryReference { RelativePath = "Objects/t/工作浏览器.lnk" },
            title: "工作浏览器");
        AssertContainsCode(Validate(Layouts.Wall2x9, [entryWithTitle]), ConfigValidator.TitleTruthConflict);

        // 正例：无入口对象标题存配置；有入口对象 TitleText=null 均合法
        var noEntryWithTitle = Layouts.Tile("t2", new GridRect(1, 0, 1, 1), title: "便签");
        var entryWithoutTitle = Layouts.Tile("t3", new GridRect(2, 0, 1, 1),
            entry: new EntryReference { RelativePath = "Objects/t3/工作浏览器.lnk" });
        Assert.Empty(Validate(Layouts.Wall2x9, [noEntryWithTitle, entryWithoutTitle]));
    }

    [Fact]
    public void SchemaVersionMismatch_Flagged()
    {
        var config = new TileWallConfig
        {
            SchemaVersion = 99,
            Wall = new WallState(2, 9),
            Objects = [],
        };
        AssertContainsCode(ConfigValidator.Validate(config), ConfigValidator.SchemaVersionUnsupported);
    }

    [Fact]
    public void WallInvalid_Flagged_AndStopsGeometryChecks()
    {
        var config = new TileWallConfig
        {
            Wall = new WallState(0, 5),
            Objects = [Layouts.Tile("t", new GridRect(0, 0, 1, 1))],
        };
        var violations = ConfigValidator.Validate(config);
        AssertContainsCode(violations, ConfigValidator.WallInvalid);
        Assert.Single(violations); // 无墙即返回，不产生几何误报
    }
}
