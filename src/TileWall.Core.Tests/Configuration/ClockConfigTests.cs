using System.Text.Json;
using TileWall.Core.Configuration;
using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Configuration;

/// <summary>
/// T-CLOCK-CFG（M8 设计 §3.1）：ClockObject JSON 往返（$kind:"clock"）；
/// ConfigValidator：时钟宽 &gt; 8 报 TILE_TOO_WIDE、越墙、重叠；Entry != null 的时钟非法（防御性）；
/// TITLE_TRUTH_CONFLICT / ENTRY_PATH_MISMATCH 因 Entry == null 天然不触发（可选标题合法）。
/// </summary>
public sealed class ClockConfigTests
{
    private static ClockObject Clock(string id, GridRect bounds, string? title = null, EntryReference? entry = null) => new()
    {
        Id = id,
        Bounds = bounds,
        Entry = entry,
        Visual = new ObjectVisual { ShowTitle = title is not null, TitleText = title },
    };

    [Fact]
    public void JsonRoundTrip_PreservesClockKindBoundsAndTitle()
    {
        var config = Layouts.Config(new WallGrid(2, 9), [Clock("clock-1", new GridRect(0, 0, 2, 2), "时钟")]);
        var bytes = ConfigJson.Serialize(config);
        var restored = ConfigJson.Deserialize(bytes);

        var clock = Assert.IsType<ClockObject>(Assert.Single(restored.Objects)); // $kind:"clock" 判别式还原
        Assert.Equal("clock-1", clock.Id);
        Assert.Equal(new GridRect(0, 0, 2, 2), clock.Bounds);
        Assert.Null(clock.Entry); // 组件无目标（不建入口文件）
        Assert.Equal("时钟", clock.Visual.TitleText);

        var json = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"$kind\": \"clock\"", json, StringComparison.Ordinal); // 判别式按 camelCase 落盘
    }

    [Fact]
    public void JsonRoundTrip_DefaultVisual_RestoresDefaults()
    {
        // 不触碰 Visual → ObjectVisual 默认值（ShowTitle=true）经序列化/反射还原（§12 兼容语义）
        var config = Layouts.Config(new WallGrid(1, 4), [new ClockObject { Id = "clock-2", Bounds = new GridRect(0, 0, 1, 1) }]);
        var restored = ConfigJson.Deserialize(ConfigJson.Serialize(config));

        var clock = Assert.IsType<ClockObject>(Assert.Single(restored.Objects));
        Assert.Null(clock.Visual.TitleText);
        Assert.True(clock.Visual.ShowTitle);
        Assert.Equal(BackdropKind.BlurFill, clock.Visual.Backdrop);
    }

    [Fact]
    public void Validator_ClockWiderThanOneColumn_ReportsTileTooWide()
    {
        var config = Layouts.Config(new WallGrid(2, 9), [Clock("clock-1", new GridRect(0, 0, 9, 1))]);
        var violations = ConfigValidator.Validate(config);
        Assert.Contains(violations, v => v.Code == ConfigValidator.TileTooWide && v.ObjectId == "clock-1"); // 宽超八格检查扩到时钟
    }

    [Fact]
    public void Validator_ClockOutOfWall_ReportsObjectOutOfWall()
    {
        var config = Layouts.Config(new WallGrid(1, 4), [Clock("clock-1", new GridRect(0, 3, 1, 2))]);
        var violations = ConfigValidator.Validate(config);
        Assert.Contains(violations, v => v.Code == ConfigValidator.ObjectOutOfWall && v.ObjectId == "clock-1");
    }

    [Fact]
    public void Validator_ClockOverlappingTile_ReportsObjectsOverlap()
    {
        var config = Layouts.Config(new WallGrid(1, 4),
        [
            Clock("clock-1", new GridRect(0, 0, 2, 2)),
            Layouts.Tile("t-1", new GridRect(1, 1, 1, 1)),
        ]);
        var violations = ConfigValidator.Validate(config);
        Assert.Contains(violations, v => v.Code == ConfigValidator.ObjectsOverlap);
    }

    [Fact]
    public void Validator_ClockWithEntry_IsForbiddenDefensively()
    {
        var config = Layouts.Config(new WallGrid(1, 4),
        [
            Clock("clock-1", new GridRect(0, 0, 1, 1), entry: new EntryReference { RelativePath = "Objects/clock-1/x.lnk" }),
        ]);
        var violations = ConfigValidator.Validate(config);
        Assert.Contains(violations, v => v.Code == ConfigValidator.ClockEntryForbidden && v.ObjectId == "clock-1");
    }

    [Fact]
    public void Validator_ClockWithTitleButNoEntry_PassesAllTruthChecks()
    {
        var config = Layouts.Config(new WallGrid(2, 9), [Clock("clock-1", new GridRect(0, 0, 2, 2), "时间日期")]);
        Assert.Empty(ConfigValidator.Validate(config)); // Entry==null → TITLE_TRUTH_CONFLICT/ENTRY_PATH_MISMATCH 天然不触发
    }

    [Fact]
    public void Validator_ClockWithEntryAndTitle_ReportsBothForbiddenAndTruthConflict()
    {
        var config = Layouts.Config(new WallGrid(1, 4),
        [
            Clock("clock-1", new GridRect(0, 0, 1, 1), "标题", new EntryReference { RelativePath = "Objects/clock-1/标题.lnk" }),
        ]);
        var violations = ConfigValidator.Validate(config);
        Assert.Contains(violations, v => v.Code == ConfigValidator.ClockEntryForbidden);
        Assert.Contains(violations, v => v.Code == ConfigValidator.TitleTruthConflict); // 既有单一真值规则并行生效
    }

    [Fact]
    public void SchemaVersion_StaysAtOne()
    {
        Assert.Equal(1, TileWallConfig.CurrentSchemaVersion); // M8 增量不升版（M5/M6 先例）
    }

    [Fact]
    public void OldBinaryForwardCompat_UnknownKindThrows_AndStoreKeepsFile()
    {
        // 前向不兼容语义（R-1）：未知判别式 → 异常 → 恢复链（此处验证「含 clock 的配置对本组装/反序列化无损」对偶面：
        // 旧二进制行为由 ConfigJson 既有定义与 ConfigStore 恢复链保证，M4 测试已覆盖）
        const string futureJson = """{"schemaVersion":1,"wall":{"columns":1,"rows":1},"objects":[{"$kind":"future","id":"x","bounds":{"column":0,"row":0,"width":1,"height":1}}],"settings":{}}""";
        Assert.ThrowsAny<JsonException>(() => ConfigJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(futureJson)));
    }
}
