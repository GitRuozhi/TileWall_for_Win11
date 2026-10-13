using System.Text;
using System.Text.Json.Nodes;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class WallVisualStyleTests
{
    [Theory]
    [InlineData(WallVisualStyle.Win10)]
    [InlineData(WallVisualStyle.Win11)]
    public void VisualStyleRoundTripsThroughWallJson(WallVisualStyle style)
    {
        var document = WallDocument.Empty(1, 1) with
        {
            Settings = new WallSettings { VisualStyle = style },
        };

        var restored = WallJson.Deserialize(WallJson.Serialize(document));

        Assert.Equal(style, restored.Settings.VisualStyle);
        Assert.Empty(WallLayoutEngine.Validate(restored));
    }

    [Fact]
    public void MissingVisualStyleDefaultsToWin10()
    {
        var json = JsonNode.Parse(WallJson.Serialize(WallDocument.Empty(1, 1)))!;
        json["settings"]!.AsObject().Remove("visualStyle");

        var restored = WallJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString()));

        Assert.Equal(WallVisualStyle.Win10, new WallSettings().VisualStyle);
        Assert.Equal(WallVisualStyle.Win10, restored.Settings.VisualStyle);
    }

    [Fact]
    public void InvalidVisualStyleIsRejectedAfterDeserialization()
    {
        var document = WallDocument.Empty(1, 1) with
        {
            Settings = new WallSettings { VisualStyle = (WallVisualStyle)99 },
        };

        var restored = WallJson.Deserialize(WallJson.Serialize(document));

        Assert.Contains("SETTINGS_ENUM", WallLayoutEngine.Validate(restored));
    }
}
