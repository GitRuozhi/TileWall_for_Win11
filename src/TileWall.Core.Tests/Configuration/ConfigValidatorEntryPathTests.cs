using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Configuration;

/// <summary>T-VAL（M4 设计 §10.2）：新增 ENTRY_PATH_MISMATCH 谓词负例/正例（schema 不变、其余规则不受影响）。</summary>
public class ConfigValidatorEntryPathTests
{
    private static TileWallConfig ConfigWithEntry(string objectId, string relativePath, GridRect? bounds = null)
    {
        var wall = new WallGrid(2, 9);
        return Layouts.Config(wall, [Layouts.Tile(objectId, bounds ?? new GridRect(0, 0, 1, 1), new EntryReference { RelativePath = relativePath })]);
    }

    [Fact]
    public void 路径指错对象目录_ENTRY_PATH_MISMATCH()
    {
        var config = ConfigWithEntry("obj-a", "Objects/obj-b/浏览器.lnk");
        var violation = Assert.Single(ConfigValidator.Validate(config).Where(v => v.Code == ConfigValidator.EntryPathMismatch));
        Assert.Equal("obj-a", violation.ObjectId);
    }

    [Fact]
    public void 扩展名非法_ENTRY_PATH_MISMATCH()
    {
        Assert.Contains(ConfigValidator.Validate(ConfigWithEntry("obj-a", "Objects/obj-a/notes.txt")),
            v => v.Code == ConfigValidator.EntryPathMismatch);
        Assert.Contains(ConfigValidator.Validate(ConfigWithEntry("obj-a", "Objects/obj-a/noext")),
            v => v.Code == ConfigValidator.EntryPathMismatch);
    }

    [Theory]
    [InlineData("Objects/obj-a/工作浏览器.lnk")]
    [InlineData("Objects/obj-a/页面.URL")] // 扩展名大小写不敏感
    public void 正确前缀与扩展名_不报ENTRY_PATH_MISMATCH(string relativePath)
    {
        Assert.DoesNotContain(ConfigValidator.Validate(ConfigWithEntry("obj-a", relativePath)),
            v => v.Code == ConfigValidator.EntryPathMismatch);
    }

    [Fact]
    public void 空目标对象_不触发ENTRY_PATH_MISMATCH()
    {
        var wall = new WallGrid(2, 9);
        var config = Layouts.Config(wall, [Layouts.Tile("obj-a", new GridRect(0, 0, 1, 1))]);
        Assert.DoesNotContain(ConfigValidator.Validate(config), v => v.Code == ConfigValidator.EntryPathMismatch);
    }
}
