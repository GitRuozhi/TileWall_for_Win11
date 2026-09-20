using System.Text.Json.Serialization;
using TileWall.Core.Grid;

namespace TileWall.Core.Configuration;

/// <summary>
/// 配置根（JSON 单文件；设计 §16.1「配置文件：墙布局、对象元数据、设置、配置版本」）。
/// 像素常量不入配置（拍板 Q2）：配置存栏数/行数（结构事实），不存 DIP（视觉事实）。
/// </summary>
public sealed record TileWallConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>墙：栏数×行数（设计 §16.2「墙面位置、尺寸、竖栏」）。</summary>
    public required WallState Wall { get; init; }

    /// <summary>仅快捷键 + 登录启动（设计 §16.2、§15.2）。</summary>
    public AppSettings Settings { get; init; } = new();

    public required IReadOnlyList<LayoutObject> Objects { get; init; }

    /// <summary>首启空墙工厂（对象列表为空）；墙尺寸由 UI 经 WallSizing.InitialForWorkArea 得出（§8.3）。</summary>
    public static TileWallConfig CreateInitial(int columns, int rows) =>
        new() { Wall = new WallState(columns, rows), Objects = [] };
}

/// <summary>墙状态（栏数 × 行数）。</summary>
public sealed record WallState(int Columns, int Rows);

/// <summary>全局可变设置；HotKey 为不透明字符串，M3 才定义语义与校验（默认值仅占位：设计 附录 A Win＋`）。</summary>
public sealed record AppSettings
{
    public string? HotKey { get; init; } = "Win+Oem3";

    public bool RunAtLogin { get; init; }
}

/// <summary>
/// 布局对象基类：稳定标识 + 墙内基础格矩形（设计 §2.1「稳定对象标识」、§2.4）。
/// 以 $kind 判别式多态分 tile 与 group 两种。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind",
                 UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(TileObject), "tile")]
[JsonDerivedType(typeof(GroupObject), "group")]
public abstract record LayoutObject
{
    /// <summary>稳定标识：不随标题/文件名/位置变化（设计 §2.1）；配置内唯一（C20）。</summary>
    public required string Id { get; init; }

    /// <summary>墙内基础格矩形，不跨竖栏（设计 §2.4）。</summary>
    public required GridRect Bounds { get; init; }

    /// <summary>托管入口；null = 无目标，合法（设计 §6.2「空目标合法」）。</summary>
    public abstract EntryReference? Entry { get; init; }

    public abstract ObjectVisual Visual { get; init; }
}

/// <summary>独立磁贴（设计 §2.2：位置、尺寸 + 前景/后景/文字/链接；默认 1×1）。</summary>
public sealed record TileObject : LayoutObject
{
    public override ObjectVisual Visual { get; init; } = new();

    public override EntryReference? Entry { get; init; }
}

/// <summary>
/// 磁贴组（设计 §2.3：整体位置、总行列、矩形分区；默认 4×4 十六块 1×1，§7.1）。
/// 分区（组内相对坐标矩形列表）为权威表示，「墙」为派生视图（§4.4 决策；画墙/拆墙留 P3）。
/// </summary>
public sealed record GroupObject : LayoutObject
{
    /// <summary>组内分区，坐标相对 Bounds 左上角；互不重叠、无空洞、完整覆盖 Bounds（设计 §2.4）。</summary>
    public required IReadOnlyList<GridRect> Partitions { get; init; }

    public override ObjectVisual Visual { get; init; } = new();

    /// <summary>整组唯一入口，默认空（设计 §2.5、附录 A）。</summary>
    public override EntryReference? Entry { get; init; }

    /// <summary>§16.2：当前图片 + 上次实际切换时间；M2 仅占位字段。</summary>
    public CarouselState? Carousel { get; init; }
}

/// <summary>
/// 托管入口引用：形如 "Objects/&lt;id&gt;/工作浏览器.lnk"，相对数据根目录；
/// M2 不创建/不解析文件，仅承载字段（设计 §16.2「入口相对路径」）。
/// </summary>
public sealed record EntryReference
{
    public required string RelativePath { get; init; }
}

/// <summary>对象外观（组级统一；组内分块只定义显示区域，无局部属性——设计 §2.3）。</summary>
public sealed record ObjectVisual
{
    public bool ShowTitle { get; init; } = true;

    /// <summary>仅无入口对象承载标题真值；有入口对象必须为 null（§16.2 名称真值在托管文件名主体）。</summary>
    public string? TitleText { get; init; }

    /// <summary>null=跟随系统；"#RRGGBB"。</summary>
    public string? BackgroundColor { get; init; }

    public string? BackgroundImagePath { get; init; }

    public string? ForegroundIconPath { get; init; }
}

/// <summary>轮播状态（设计 §11.2 唯一时间基准：只存当前图片与上次实际切换时间）。</summary>
public sealed record CarouselState
{
    public string? CurrentImageId { get; init; }

    public DateTimeOffset? LastSwitchUtc { get; init; }
}
