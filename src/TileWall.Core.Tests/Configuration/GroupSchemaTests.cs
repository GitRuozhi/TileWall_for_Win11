using TileWall.Core.Configuration;
using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Configuration;

/// <summary>
/// T-SER/T-VAL 增量（M5 设计 §12）：GroupImages/Backdrop round-trip、缺省字段旧 JSON 兼容
/// （旧格式字节 → 新模型默认值）、含新字段的组配置过 ConfigValidator 无新增告警。
/// </summary>
public sealed class GroupSchemaTests
{
    [Fact]
    public void RoundTrip_PreservesImagesAndBackdrop()
    {
        var config = Layouts.Config(Layouts.Wall2x9,
        [
            new GroupObject
            {
                Id = "g-img",
                Bounds = new GridRect(8, 0, 4, 4),
                Partitions = [new GridRect(0, 0, 4, 4)],
                Visual = new ObjectVisual
                {
                    ShowTitle = true,
                    TitleText = "图组",
                    Backdrop = BackdropKind.SolidColor,
                    BackgroundColor = "#203040",
                },
                Images = new GroupImages
                {
                    Kind = GroupImageSourceKind.Multiple,
                    ImagePaths = ["C:\\pics\\a.png", "C:\\pics\\b.jpg"],
                },
                Carousel = new CarouselState { CurrentImageId = "C:\\pics\\a.png", LastSwitchUtc = new DateTimeOffset(2026, 9, 21, 15, 16, 12, TimeSpan.Zero) },
            },
        ]);

        var restored = (GroupObject)ConfigJson.Deserialize(ConfigJson.Serialize(config)).Objects[0];

        Assert.Equal(BackdropKind.SolidColor, restored.Visual.Backdrop);
        Assert.Equal("#203040", restored.Visual.BackgroundColor);
        Assert.Equal(GroupImageSourceKind.Multiple, restored.Images.Kind);
        Assert.Equal(["C:\\pics\\a.png", "C:\\pics\\b.jpg"], restored.Images.ImagePaths);
        Assert.Null(restored.Images.FolderPath);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 15, 16, 12, TimeSpan.Zero), restored.Carousel!.LastSwitchUtc);
    }

    [Fact]
    public void RoundTrip_FolderSource_PreservesFolderPath()
    {
        var config = Layouts.Config(Layouts.Wall2x9,
        [
            new GroupObject
            {
                Id = "g-folder",
                Bounds = new GridRect(8, 0, 4, 4),
                Partitions = [new GridRect(0, 0, 4, 4)],
                Images = new GroupImages { Kind = GroupImageSourceKind.Folder, FolderPath = "C:\\pics\\wallpaper" },
            },
        ]);

        var restored = (GroupObject)ConfigJson.Deserialize(ConfigJson.Serialize(config)).Objects[0];

        Assert.Equal(GroupImageSourceKind.Folder, restored.Images.Kind);
        Assert.Equal("C:\\pics\\wallpaper", restored.Images.FolderPath);
        Assert.Empty(restored.Images.ImagePaths);
    }

    [Fact]
    public void OldConfigBytes_WithoutNewFields_DeserializeToDefaults()
    {
        // M4 时代字节形状：无 images / 无 backdrop 字段 → 全部落默认值（逐字段兼容）
        const string legacyJson = """
        {
          "schemaVersion": 1,
          "wall": { "columns": 2, "rows": 4 },
          "settings": { "hotKey": "Win+Oem3", "runAtLogin": false },
          "objects": [
            {
              "$kind": "group",
              "id": "g-legacy",
              "bounds": { "column": 8, "row": 0, "width": 4, "height": 4 },
              "partitions": [ { "column": 0, "row": 0, "width": 4, "height": 4 } ],
              "visual": { "showTitle": true, "titleText": "旧组" }
            }
          ]
        }
        """;

        var restored = (GroupObject)ConfigJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(legacyJson)).Objects[0];

        Assert.Equal(new GroupImages().Kind, restored.Images.Kind); // None
        Assert.Empty(restored.Images.ImagePaths);
        Assert.Null(restored.Images.FolderPath);
        Assert.Equal(BackdropKind.BlurFill, restored.Visual.Backdrop); // Q6 默认
    }

    [Fact]
    public void Deserialize_ThenReserialize_IsCanonical_StableBytes()
    {
        // 反序列化路径（运行时反射）改动后的确定性红线：字节与源生成序列化完全一致
        var config = Layouts.Config(Layouts.Wall2x9, Layouts.Mixed());
        var bytes = ConfigJson.Serialize(config);
        var again = ConfigJson.Serialize(ConfigJson.Deserialize(bytes));

        Assert.True(bytes.AsSpan().SequenceEqual(again), "反序列化→再序列化字节必须稳定");
    }

    [Fact]
    public void Deserialize_AbsentMembers_RestoreInitializedDefaults()
    {
        // §12 兼容性红线的机制面：JSON 缺省成员必须回到属性初始化默认值（而非 null）
        const string tileJson = """
        {
          "schemaVersion": 1,
          "wall": { "columns": 2, "rows": 4 },
          "objects": [
            { "$kind": "tile", "id": "t1", "bounds": { "column": 0, "row": 0, "width": 1, "height": 1 } }
          ]
        }
        """;

        var tile = (TileObject)ConfigJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(tileJson)).Objects[0];

        Assert.NotNull(tile.Visual);
        Assert.True(tile.Visual.ShowTitle);
        Assert.Null(tile.Entry);
    }

    [Fact]
    public void Validator_NoNewWarnings_ForGroupWithImagesAndBackdrop()
    {
        var config = Layouts.Config(Layouts.Wall2x9,
        [
            new GroupObject
            {
                Id = "g-full",
                Bounds = new GridRect(8, 0, 4, 4),
                Partitions = FullFourByFour(),
                Visual = new ObjectVisual { TitleText = "组", Backdrop = BackdropKind.Transparent },
                Images = new GroupImages { Kind = GroupImageSourceKind.Single, ImagePaths = ["C:\\pics\\solo.png"] },
            },
        ]);

        Assert.Empty(ConfigValidator.Validate(config));
    }

    [Fact]
    public void Validator_TitleTruthConflict_ReusedForGroups()
    {
        // 单一真值规则按对象类别通吃（§12：既有谓词无需改动）——组同样触发 TITLE_TRUTH_CONFLICT
        var config = Layouts.Config(Layouts.Wall2x9,
        [
            new GroupObject
            {
                Id = "g-bad",
                Bounds = new GridRect(8, 0, 4, 4),
                Partitions = FullFourByFour(),
                Visual = new ObjectVisual { TitleText = "不该有" },
                Entry = new EntryReference { RelativePath = "Objects/g-bad/入口.lnk" },
            },
        ]);

        var violations = ConfigValidator.Validate(config);
        Assert.Contains(violations, v => v.Code == ConfigValidator.TitleTruthConflict && v.ObjectId == "g-bad");
    }

    [Fact]
    public void Validator_GroupSizeOutOfRange_CodeReusedByGroupDraftValidator()
    {
        Assert.Equal("GROUP_SIZE_OUT_OF_RANGE", ConfigValidator.GroupSizeOutOfRange);
        Assert.Equal(ConfigValidator.GroupSizeOutOfRange, TileWall.Core.Groups.GroupDraftValidator.SizeOutOfRange);
    }

    private static GridRect[] FullFourByFour()
    {
        var partitions = new List<GridRect>(16);
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                partitions.Add(new GridRect(col, row, 1, 1));
            }
        }

        return [.. partitions];
    }
}
