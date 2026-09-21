using System.Text.Json.Serialization;
using TileWall.Core.Grid;
using TileWall.Core.Imaging;

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
[JsonDerivedType(typeof(ClockObject), "clock")]
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
/// 分区（组内相对坐标矩形列表）为权威表示，「墙」为派生视图（§4.4 决策；M5 起引擎在 Core Grid）。
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

    /// <summary>M5 新增：图片来源三选一记录（设计 §7.2；M5 仅记录不做共享画布/解码，M6 接线）。</summary>
    public GroupImages Images { get; init; } = new();
}

/// <summary>
/// 时间日期组件（M8 设计 §3.1、设计 §15.1、拍板 Q9）：
/// 无入口、无点击动作（Entry 恒 null——不建入口文件、点击无动作）；
/// 主体两行常驻（时间大字 + 日期小字，ClockTextFormatter 单一默认样式），可选标题走 Visual.TitleText/ShowTitle。
/// 稳定标识与 Bounds 语义与磁贴一致；宽 ≤ 一栏八格（ConfigValidator 同 TileObject 检查）。
/// schemaVersion 保持 1（M5/M6 增量先例）；代价为前向不兼容：旧二进制读 "clock" 判别式走恢复链（ConfigJson 既有定义）。
/// </summary>
public sealed record ClockObject : LayoutObject
{
    /// <summary>恒 null：组件无目标（§15.1「不创建空 .lnk」）；非 null 为配置级错误（CLOCK_ENTRY_FORBIDDEN）。</summary>
    public override EntryReference? Entry { get; init; } = null;

    public override ObjectVisual Visual { get; init; } = new();
}

/// <summary>组图片来源（P1 §3.3 三选一；M5 §12 仅记录，候选枚举与加载属 M6）。</summary>
public enum GroupImageSourceKind
{
    /// <summary>未设置。</summary>
    None,

    /// <summary>单张图片。</summary>
    Single,

    /// <summary>多张图片（轮播候选）。</summary>
    Multiple,

    /// <summary>本机文件夹（M6 切换时枚举候选）。</summary>
    Folder,
}

/// <summary>
/// 逐图变换条目（M6 设计 §9；仅偏离默认态才落盘，XF-9）。
/// ImageId = 绝对路径字符串（与 CarouselState.CurrentImageId 同语义，§4.1）；匹配用
/// OrdinalIgnoreCase（Windows 路径不区分大小写，§9/§13 风险 8），存储保留用户原样大小写。
/// </summary>
public sealed record ImageTransformRecord
{
    public required string ImageId { get; init; }

    public FitMode Fit { get; init; } = FitMode.CoverFill;

    public required double Scale { get; init; }

    public required double OffsetX { get; init; }

    public required double OffsetY { get; init; }
}

/// <summary>图片来源记录：全部带默认值 → 旧配置反序列化逐字段兼容（schemaVersion 保持 1）。</summary>
public sealed record GroupImages
{
    public GroupImageSourceKind Kind { get; init; } = GroupImageSourceKind.None;

    /// <summary>Single/Multiple：绝对路径清单。</summary>
    public IReadOnlyList<string> ImagePaths { get; init; } = [];

    /// <summary>Folder 来源的文件夹路径。</summary>
    public string? FolderPath { get; init; }

    /// <summary>M6 新增：逐图变换（列表非字典——JSON 数组保序、schema 最简，§9）；无条目 = 默认居中填满（XF-7）。</summary>
    public IReadOnlyList<ImageTransformRecord> Transforms { get; init; } = [];

    public bool Equals(GroupImages? other) =>
        other is not null
        && Kind == other.Kind
        && ImagePaths.SequenceEqual(other.ImagePaths, StringComparer.Ordinal)
        && string.Equals(FolderPath, other.FolderPath, StringComparison.Ordinal)
        && Transforms.SequenceEqual(other.Transforms);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        foreach (var p in ImagePaths)
        {
            hash.Add(p, StringComparer.Ordinal);
        }

        hash.Add(FolderPath);
        foreach (var t in Transforms)
        {
            hash.Add(t);
        }

        return hash.ToHashCode();
    }
}

/// <summary>组背景形态（拍板 Q6：模糊补底（默认）/纯色/透明；M5 记录选择，纯色即时映射渲染，模糊真渲染属 M6）。</summary>
public enum BackdropKind
{
    BlurFill,
    SolidColor,
    Transparent,
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

    /// <summary>null=跟随系统；"#RRGGBB"。Backdrop=SolidColor 时承载纯色（拍板 Q6）。</summary>
    public string? BackgroundColor { get; init; }

    public string? BackgroundImagePath { get; init; }

    public string? ForegroundIconPath { get; init; }

    /// <summary>M5 新增：组背景形态（Q6 下拉；默认模糊补底，带默认值 → 旧配置兼容）。</summary>
    public BackdropKind Backdrop { get; init; } = BackdropKind.BlurFill;
}

/// <summary>轮播状态（设计 §11.2 唯一时间基准：只存当前图片与上次实际切换时间）。</summary>
public sealed record CarouselState
{
    public string? CurrentImageId { get; init; }

    public DateTimeOffset? LastSwitchUtc { get; init; }
}
