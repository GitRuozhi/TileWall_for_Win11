using System.Text.Json.Nodes;
using TileWall.Core.Settings;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Settings;

public sealed class WallLanguagesTests
{
    [Theory]
    [InlineData("zh-TW", "zh-TW")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("zh-MO", "zh-TW")]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-SG", "zh-CN")]
    [InlineData("zh", "zh-CN")]
    [InlineData("ja", "ja-JP")]
    [InlineData("ko", "ko-KR")]
    [InlineData("de-AT", "de-DE")]
    [InlineData("fr-CA", "fr-FR")]
    [InlineData("es-MX", "es-ES")]
    [InlineData("pt-BR", "en-US")]
    [InlineData("", "en-US")]
    public void SystemLanguageUsesSupportedFamilies(string system, string expected) =>
        Assert.Equal(expected, WallLanguages.ResolveTag(WallLanguageMode.System, system));

    [Theory]
    [InlineData("fr")]
    [InlineData("zh-HK")]
    [InlineData("de-AT")]
    [InlineData("system")]
    [InlineData(" en-US")]
    public void CommandLanguageRejectsUnlistedTags(string tag) => Assert.False(WallLanguages.TryParseTag(tag, out _));

    [Fact]
    public void EightLanguagesRetainIdentityInFormatSixAndKeepWallEnvelopeUnchanged()
    {
        Assert.Equal(0, (int)WallLanguageMode.System);
        Assert.Equal(1, (int)WallLanguageMode.Chinese);
        Assert.Equal(2, (int)WallLanguageMode.English);
        Assert.Equal(8, WallLanguages.Supported.Count);
        foreach (var language in WallLanguages.Supported)
        {
            var tag = WallLanguages.ResolveTag(language);
            Assert.True(WallLanguages.TryParseTag(tag, out var parsed));
            Assert.Equal(language, parsed);
            var snapshot = UserDataSnapshotJson.Prepare(WallDocument.Empty(1, 4) with
                { Settings = new() { LanguageMode = language } });
            Assert.Equal(6, snapshot.Manifest.SchemaVersion);
            Assert.Equal(6, (int)JsonNode.Parse(snapshot.Files["settings.json"])!["schemaVersion"]!);
            Assert.Equal(1, (int)JsonNode.Parse(snapshot.Files["wall.json"])!["schemaVersion"]!);
            Assert.Equal(1, (int)JsonNode.Parse(snapshot.Files["recycle-bin.json"])!["schemaVersion"]!);
            var restored = UserDataSnapshotJson.Read(snapshot.Manifest, name => snapshot.Files[name],
                _ => throw new InvalidOperationException());
            Assert.Equal(language, restored.Settings.LanguageMode);
            Assert.Equal(6, restored.SchemaVersion);
        }
    }
}
