using System.Text;
using System.Text.Json.Nodes;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class WallSettingsTests
{
    [Fact]
    public void MissingStartReplacementPreferenceRemainsDisabled()
    {
        var json = JsonNode.Parse(WallJson.Serialize(WallDocument.Empty(1, 1)))!;
        json["settings"]!.AsObject().Remove("replaceStartMenu");
        Assert.False(WallJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())).Settings.ReplaceStartMenu);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartReplacementPreferenceSurvivesPersistence(bool enabled)
    {
        var document = WallDocument.Empty(1, 1) with { Settings = new() { ReplaceStartMenu = enabled } };
        var restored = WallJson.Deserialize(WallJson.Serialize(document));
        Assert.Equal(enabled, restored.Settings.ReplaceStartMenu);
        Assert.Empty(WallLayoutEngine.Validate(restored));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void WindowedViewportWidthsMustBeFiniteAndPositive(double width)
    {
        var source = WallDocument.Empty(1, 4);
        Assert.Contains("SETTINGS_WINDOWED_VIEWPORT_WIDTH", WallLayoutEngine.Validate(source with
            { Settings = new() { WindowedViewportWidthDip = width } }));
        Assert.Contains("SETTINGS_WINDOWED_VIEWPORT_WIDTH", WallLayoutEngine.Validate(source with
            { Settings = new() { ManualWindowedViewportWidthDip = width } }));
        Assert.Empty(WallLayoutEngine.Validate(source));
    }

    [Fact]
    public void MissingSettingsFieldsUseProductDefaults()
    {
        var defaults = new WallSettings();
        Assert.True(defaults.MenuStart);
        Assert.Equal(5, defaults.BackgroundTransparency);

        var json = JsonNode.Parse(WallJson.Serialize(WallDocument.Empty(1, 1)))!;
        foreach (var name in new[] { "themeMode", "backgroundMode", "backgroundTransparency", "backgroundImagePath", "languageMode", "menuStart" })
            json["settings"]!.AsObject().Remove(name);
        var settings = WallJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())).Settings;
        Assert.Equal(WallThemeMode.Dark, settings.ThemeMode);
        Assert.Equal(WallBackgroundMode.Acrylic, settings.BackgroundMode);
        Assert.Equal(5, settings.BackgroundTransparency);
        Assert.True(settings.MenuStart);
        Assert.Null(settings.BackgroundImagePath);
        Assert.Equal(WallLanguageMode.System, settings.LanguageMode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MenuStartPreferenceSurvivesPersistence(bool enabled)
    {
        var document = WallDocument.Empty(1, 1) with { Settings = new() { MenuStart = enabled } };
        var restored = WallJson.Deserialize(WallJson.Serialize(document));
        Assert.Equal(enabled, restored.Settings.MenuStart);
    }

    [Theory]
    [InlineData(WallThemeMode.System, WallBackgroundMode.Acrylic, WallLanguageMode.System, 0)]
    [InlineData(WallThemeMode.System, WallBackgroundMode.Acrylic, WallLanguageMode.System, 10)]
    [InlineData(WallThemeMode.Light, WallBackgroundMode.Mica, WallLanguageMode.Chinese, 15)]
    [InlineData(WallThemeMode.Dark, WallBackgroundMode.Custom, WallLanguageMode.English, 100)]
    public void AppearanceAndExternalImagePathSurvivePersistence(WallThemeMode theme, WallBackgroundMode background,
        WallLanguageMode language, int transparency)
    {
        var settings = new WallSettings { ThemeMode = theme, BackgroundMode = background, LanguageMode = language,
            BackgroundTransparency = transparency, BackgroundImagePath = @"C:\Pictures\wall.png" };
        var restored = WallJson.Deserialize(WallJson.Serialize(WallDocument.Empty(1, 1) with { Settings = settings }));
        Assert.Equal(settings, restored.Settings);
        Assert.Empty(WallLayoutEngine.Validate(restored));
    }

    [Theory]
    [InlineData("themeMode")]
    [InlineData("backgroundMode")]
    [InlineData("languageMode")]
    public void UnknownSettingsEnumsAreRejectedAfterPersistence(string field)
    {
        var json = JsonNode.Parse(WallJson.Serialize(WallDocument.Empty(1, 1)))!;
        json["settings"]![field] = 99;
        var restored = WallJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString()));
        Assert.Contains("SETTINGS_ENUM", WallLayoutEngine.Validate(restored));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void OutOfRangeTransparencyIsRejected(int transparency)
    {
        var document = WallDocument.Empty(1, 1) with { Settings = new WallSettings { BackgroundTransparency = transparency } };
        Assert.Contains("SETTINGS_TRANSPARENCY", WallLayoutEngine.Validate(document));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void CustomBackgroundRequiresPathWithoutDoingFileIo(string? path)
    {
        var document = WallDocument.Empty(1, 1) with { Settings = new WallSettings
            { BackgroundMode = WallBackgroundMode.Custom, BackgroundImagePath = path } };
        Assert.Contains("SETTINGS_BACKGROUND_PATH", WallLayoutEngine.Validate(document));
        Assert.Empty(WallLayoutEngine.Validate(document with { Settings = document.Settings with { BackgroundImagePath = @"Z:\unavailable\image.png" } }));
    }
}
