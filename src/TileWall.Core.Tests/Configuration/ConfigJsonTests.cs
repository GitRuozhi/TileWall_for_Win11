using System.Text.Json;
using System.Text.RegularExpressions;
using TileWall.Core.Configuration;
using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Configuration;

/// <summary>T-SER：round-trip / $kind 多态 / 未知字段 / 非法判别式（Q11 技术栈、设计 §4.3）。</summary>
public class ConfigJsonTests
{
    [Fact]
    public void RoundTrip_L3MixedConfig_PreservesEveryField()
    {
        var config = Layouts.Config(Layouts.Wall2x9, Layouts.Mixed());

        var bytes = ConfigJson.Serialize(config);
        var restored = ConfigJson.Deserialize(bytes);

        Assert.Equal(config.SchemaVersion, restored.SchemaVersion);
        Assert.Equal(config.Wall, restored.Wall);
        Assert.Equal(config.Settings.HotKey, restored.Settings.HotKey);
        Assert.Equal(config.Settings.RunAtLogin, restored.Settings.RunAtLogin);
        Assert.Equal(config.Objects.Count, restored.Objects.Count);
        for (var i = 0; i < config.Objects.Count; i++)
        {
            LayoutAssert.DeepEqual(config.Objects[i], restored.Objects[i]);
        }
    }

    [Fact]
    public void Serialize_IsCanonical_SameInputSameBytes_AfterRoundTrip()
    {
        var config = Layouts.Config(Layouts.Wall2x9, Layouts.Mixed());
        var bytes = ConfigJson.Serialize(config);

        var again = ConfigJson.Serialize(ConfigJson.Deserialize(bytes));

        Assert.Equal(bytes, again); // 序列化确定性：写盘字节可复现
    }

    [Fact]
    public void SerializedShape_UsesCamelCase_AndKindDiscriminators()
    {
        var config = Layouts.Config(Layouts.Wall2x9, Layouts.Mixed());
        var json = System.Text.Encoding.UTF8.GetString(ConfigJson.Serialize(config));

        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"wall\"", json);
        Assert.Contains("\"$kind\": \"tile\"", json);
        Assert.Contains("\"$kind\": \"group\"", json);
        Assert.Contains("\"bounds\"", json);
        Assert.Contains("\"partitions\"", json);
    }

    [Fact]
    public void NullEntry_IsOmitted_AndRestoredAsNull()
    {
        var objects = new List<LayoutObject> { Layouts.Group("g", new GridRect(0, 0, 2, 2)) };
        var bytes = ConfigJson.Serialize(Layouts.Config(Layouts.Wall2x9, objects));
        var json = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("\"entry\"", json); // WhenWritingNull
        var restored = ConfigJson.Deserialize(bytes);
        Assert.Null(((GroupObject)restored.Objects[0]).Entry);
    }

    [Fact]
    public void UnknownField_IsSkippedWithoutError()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "futureTopLevel": 123,
              "wall": { "columns": 2, "rows": 9 },
              "settings": { "hotKey": "Win+Oem3", "runAtLogin": false },
              "objects": [
                { "$kind": "tile", "id": "a1", "futureObjectField": true,
                  "bounds": { "column": 0, "row": 0, "width": 1, "height": 1 },
                  "visual": { "showTitle": true, "titleText": "便签" } }
              ]
            }
            """;

        var config = ConfigJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.Equal("a1", config.Objects[0].Id);
        Assert.Equal("便签", config.Objects[0].Visual.TitleText);
    }

    [Fact]
    public void SchemaVersion2_StillDeserializes_ValidationIsUpstream()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "wall": { "columns": 2, "rows": 9 },
              "objects": []
            }
            """;

        var config = ConfigJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.Equal(2, config.SchemaVersion); // ConfigStore.Load 层判 FutureVersion（见 ConfigStoreTests）
    }

    [Fact]
    public void UnknownKindDiscriminator_Throws()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "wall": { "columns": 2, "rows": 9 },
              "objects": [ { "$kind": "widget", "id": "x", "bounds": { "column": 0, "row": 0, "width": 1, "height": 1 } } ]
            }
            """;

        var act = () => ConfigJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(json));

        var exception = Record.Exception(act);
        Assert.NotNull(exception); // 非法 $kind 必须异常 → 进入恢复链（设计 §4.3）
        Assert.True(exception is JsonException or NotSupportedException,
            $"期望 JsonException/NotSupportedException，实际 {exception.GetType().Name}");
    }

    [Fact]
    public void MalformedJson_ThrowsJsonException()
    {
        Assert.ThrowsAny<JsonException>(() =>
            ConfigJson.Deserialize(System.Text.Encoding.UTF8.GetBytes("{ \"wall\": ")));
    }

    [Fact]
    public void CarouselAndEntry_RoundTrip()
    {
        var objects = new List<LayoutObject>
        {
            new GroupObject
            {
                Id = "g1",
                Bounds = new GridRect(0, 0, 2, 2),
                Partitions = [new GridRect(0, 0, 2, 2)],
                Entry = new EntryReference { RelativePath = "Objects/g1/拼图.lnk" },
                Carousel = new CarouselState { CurrentImageId = "img7", LastSwitchUtc = new DateTimeOffset(2026, 9, 20, 8, 30, 0, TimeSpan.Zero) },
            },
        };
        var bytes = ConfigJson.Serialize(Layouts.Config(Layouts.Wall2x9, objects));

        var restored = (GroupObject)ConfigJson.Deserialize(bytes).Objects[0];

        Assert.Equal("Objects/g1/拼图.lnk", restored.Entry!.RelativePath);
        Assert.Equal("img7", restored.Carousel!.CurrentImageId);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 8, 30, 0, TimeSpan.Zero), restored.Carousel.LastSwitchUtc);
    }

    [Fact]
    public void StableId_NewId_Is32HexChars_AndUnique()
    {
        var first = StableId.NewId();
        var second = StableId.NewId();

        Assert.Matches(new Regex("^[0-9a-f]{32}$"), first); // GUID "N"：文件系统安全
        Assert.NotEqual(first, second);
    }
}
