using System.Text.Json;
using TileWall.Core.Configuration;
using TileWall.Core.Grid;
using TileWall.Core.Imaging;
using Xunit;

namespace TileWall.Core.Tests.Configuration;

/// <summary>
/// T-XFORM-SER（M6 设计 §11.1；§9；XF-7/XF-8/XF-9）：
/// ImageTransformRecord round-trip；默认态剔除（保存链 Upsert）；旧 JSON（无 transforms 字段）默认值兼容。
/// </summary>
public sealed class ImageTransformSerializationTests
{
    private static readonly PixelSize Image = new(400, 300);
    private static readonly DipSize Canvas = new(408, 408);

    private static TileWallConfig Config(GroupImages images) => new()
    {
        Wall = new WallState(2, 9),
        Objects = new List<LayoutObject>
        {
            new GroupObject
            {
                Id = "g1",
                Bounds = new GridRect(8, 0, 4, 4),
                Partitions = [new GridRect(0, 0, 1, 1)],
                Images = images,
            },
        },
    };

    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        var source = new GroupImages
        {
            Kind = GroupImageSourceKind.Multiple,
            ImagePaths = [@"C:\pics\a.png", @"C:\pics\b.jpg"],
            Transforms =
            [
                new ImageTransformRecord { ImageId = @"C:\pics\b.jpg", Fit = FitMode.FitAll, Scale = 1.25, OffsetX = -12.5, OffsetY = 3 },
            ],
        };
        var bytes = ConfigJson.Serialize(Config(source));
        var loaded = ConfigJson.Deserialize(bytes);

        var images = Assert.IsType<GroupObject>(loaded.Objects.Single()).Images;
        Assert.Equal(source, images); // record 逐字段值相等（含 Transforms）
        var record = images.Transforms.Single();
        Assert.Equal(@"C:\pics\b.jpg", record.ImageId);
        Assert.Equal(FitMode.FitAll, record.Fit);
        Assert.Equal(1.25, record.Scale, 9);
        Assert.Equal(-12.5, record.OffsetX, 9);
        Assert.Equal(3, record.OffsetY, 9);
    }

    [Fact]
    public void LegacyJsonWithoutTransforms_DeserializesWithEmptyDefault()
    {
        // M5 时代字段形状：无 images/transforms —— 逐字段默认值兼容（schemaVersion 保持 1）
        const string legacy = """
            {
              "schemaVersion": 1,
              "wall": { "columns": 2, "rows": 9 },
              "settings": {},
              "objects": [
                {
                  "$kind": "group",
                  "id": "g1",
                  "bounds": { "column": 8, "row": 0, "width": 4, "height": 4 },
                  "partitions": [ { "column": 0, "row": 0, "width": 1, "height": 1 } ],
                  "visual": {},
                  "carousel": { "currentImageId": "C:\\pics\\a.png", "lastSwitchUtc": "2026-09-21T15:16:12Z" }
                }
              ]
            }
            """;
        var loaded = ConfigJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(legacy));

        var group = Assert.IsType<GroupObject>(loaded.Objects.Single());
        Assert.Equal("C:\\pics\\a.png", group.Carousel!.CurrentImageId);
        Assert.NotNull(group.Images);
        Assert.Equal(GroupImageSourceKind.None, group.Images.Kind);
        Assert.Empty(group.Images.Transforms); // 缺字段 → 默认空列表（XF-7：无条目 = 默认变换）
    }

    [Fact]
    public void Upsert_DropsDefaultState_KeepsOthers_And_MatchesCaseInsensitive()
    {
        var defaultTransform = SharedCanvasTransform.DefaultTransform(Image, Canvas, FitMode.CoverFill);
        var edited = SharedCanvasTransform.ZoomAt(
            defaultTransform, 1.5, new DipPoint(204, 204), SharedCanvasTransform.CoverFillScale(Image, Canvas));

        var existing = new List<ImageTransformRecord>
        {
            new() { ImageId = @"C:\pics\OTHER.png", Fit = FitMode.FitAll, Scale = 0.9, OffsetX = 1, OffsetY = 2 },
        };

        // 编辑态 → 覆盖同名（大小写不敏感匹配，保留原样大小写条目）
        var withEdit = ImageTransformSet.Upsert(existing, @"C:\pics\a.png", FitMode.CoverFill, edited, Image, Canvas);
        Assert.Single(withEdit, t => t.ImageId == @"C:\pics\a.png");
        Assert.Equal(existing.Single(t => t.ImageId == @"C:\pics\OTHER.png"), withEdit.Single(t => t.ImageId == @"C:\pics\OTHER.png"));

        // 回到默认态 → 条目剔除（XF-9），他图条目保留
        var backToDefault = ImageTransformSet.Upsert(withEdit, @"C:\pics\A.PNG", FitMode.CoverFill, defaultTransform, Image, Canvas);
        Assert.DoesNotContain(backToDefault, t => string.Equals(t.ImageId, @"C:\pics\a.png", StringComparison.OrdinalIgnoreCase));
        Assert.Single(backToDefault);

        // FitAll 档的「默认」是 FitAll 的居中初始：同值剔除、异值落盘
        var fitAllDefault = SharedCanvasTransform.DefaultTransform(Image, Canvas, FitMode.FitAll);
        var withFitAll = ImageTransformSet.Upsert([], @"C:\pics\a.png", FitMode.FitAll, fitAllDefault, Image, Canvas);
        Assert.Empty(withFitAll);
        var movedFitAll = ImageTransformSet.Upsert([], @"C:\pics\a.png", FitMode.FitAll, SharedCanvasTransform.Translate(fitAllDefault, 5, 0), Image, Canvas);
        Assert.Single(movedFitAll);
        Assert.Equal(FitMode.FitAll, movedFitAll[0].Fit);
    }

    [Fact]
    public void Find_MatchesCaseInsensitive_And_MissingYieldsNull()
    {
        var transforms = new List<ImageTransformRecord>
        {
            new() { ImageId = @"C:\Pics\A.png", Fit = FitMode.CoverFill, Scale = 2, OffsetX = 0, OffsetY = 0 },
        };
        var found = ImageTransformSet.Find(transforms, @"C:\pics\a.PNG");
        Assert.NotNull(found);
        Assert.Equal(2, found!.Value.Transform.Scale);
        Assert.Null(ImageTransformSet.Find(transforms, @"C:\pics\other.png"));
    }

    [Fact]
    public void Transforms_FullRoundTrip_ThroughConfigStore_WritesCamelCaseFields()
    {
        var source = new GroupImages
        {
            Kind = GroupImageSourceKind.Single,
            ImagePaths = [@"C:\pics\a.png"],
            Transforms = [new ImageTransformRecord { ImageId = @"C:\pics\a.png", Fit = FitMode.CoverFill, Scale = 1.5, OffsetX = -4, OffsetY = 8 }],
        };
        var json = System.Text.Encoding.UTF8.GetString(ConfigJson.Serialize(Config(source)));
        Assert.Contains("\"transforms\"", json);
        Assert.Contains("\"imageId\"", json);
        Assert.Contains("\"offsetX\"", json);
    }
}
