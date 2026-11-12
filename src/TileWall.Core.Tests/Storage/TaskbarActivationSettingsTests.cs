using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Storage;

public sealed class TaskbarActivationSettingsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitActivationSurvivesSnapshotAndDoesNotChangeInputPreferences(bool enabled)
    {
        var source = WallDocument.Empty(4, 16) with { Settings = new()
            { ActivateTaskbar = enabled, ReplaceStartMenu = !enabled, RedirectTaskbarStartButton = enabled } };
        var snapshot = UserDataSnapshotJson.Prepare(source);
        var restored = UserDataSnapshotJson.Read(snapshot.Manifest, name => snapshot.Files[name],
            _ => throw new InvalidOperationException());
        Assert.Equal(enabled, restored.Settings.ActivateTaskbar);
        Assert.Equal(!enabled, restored.Settings.ReplaceStartMenu);
        Assert.Equal(enabled, restored.Settings.RedirectTaskbarStartButton);
        Assert.Equal(4, restored.Columns);
        Assert.Equal(16, restored.Rows);
    }

    [Fact]
    public void MissingActivationUsesTheEnabledProductDefault()
    {
        Assert.True(new WallSettings().ActivateTaskbar);
        var value = JsonNode.Parse(WallJson.Serialize(WallDocument.Empty(1, 4)))!;
        value["settings"]!.AsObject().Remove("activateTaskbar");
        var restored = WallJson.Deserialize(Encoding.UTF8.GetBytes(value.ToJsonString()));
        Assert.True(restored.Settings.ActivateTaskbar);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AHistoricalEnvelopeCannotSmuggleTheNewActivationField(bool enabled)
    {
        var value = JsonNode.Parse(WallJson.Serialize(WallDocument.Empty(1, 4)))!;
        value["schemaVersion"] = 5;
        value["settings"]!["activateTaskbar"] = enabled;
        Assert.Throws<JsonException>(() => WallJson.Deserialize(Encoding.UTF8.GetBytes(value.ToJsonString())));
    }
}
