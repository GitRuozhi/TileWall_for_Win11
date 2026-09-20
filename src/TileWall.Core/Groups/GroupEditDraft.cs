using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;

namespace TileWall.Core.Groups;

/// <summary>
/// 组属性窗草稿（M5 设计 §8.1）：主页与布局子页共用的两级草稿承载；全 record 不可变，取消 = 直接丢弃。
/// Entry 复用 M4 草稿判别联合；Carousel 透传（编辑属性不重置轮播基准）。
/// </summary>
public sealed record GroupEditDraft
{
    public required GridSize Size { get; init; }                       // 列 2–8、行 2–25

    /// <summary>已确认的布局子草稿（组内相对坐标；来自 PartitionEditSession.Current）。</summary>
    public required IReadOnlyList<GridRect> Partitions { get; init; }

    public GroupImages Images { get; init; } = new();                  // 图片来源三选一（M5 仅记录，§12）

    public BackdropKind Backdrop { get; init; } = BackdropKind.BlurFill;   // 拍板 Q6 下拉

    /// <summary>Backdrop=SolidColor 时的底色（"#RRGGBB"）；模糊补底/透明不携带。</summary>
    public string? BackdropColorHex { get; init; }

    /// <summary>组级文字；有入口时真值在入口名（单一真值），非空 = 改名（M4 §5.5 同规则）。</summary>
    public string? TitleText { get; init; }

    /// <summary>null = 空目标（合法）；复用 M4 草稿联合（KeepCurrent/NoneDraft/CopyFromFile/…）。</summary>
    public EntryDraft? Entry { get; init; }

    /// <summary>轮播基准透传（编辑属性不重置，§7.3）。</summary>
    public CarouselState? Carousel { get; init; }

    public bool Equals(GroupEditDraft? other) =>
        other is not null
        && Size.Equals(other.Size)
        && Partitions.SequenceEqual(other.Partitions)
        && EqualityComparer<GroupImages>.Default.Equals(Images, other.Images)
        && Backdrop == other.Backdrop
        && string.Equals(BackdropColorHex, other.BackdropColorHex, StringComparison.Ordinal)
        && string.Equals(TitleText, other.TitleText, StringComparison.Ordinal)
        && EqualityComparer<EntryDraft?>.Default.Equals(Entry, other.Entry)
        && EqualityComparer<CarouselState?>.Default.Equals(Carousel, other.Carousel);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Size);
        foreach (var p in Partitions)
        {
            hash.Add(p);
        }

        hash.Add(Images);
        hash.Add(Backdrop);
        hash.Add(BackdropColorHex);
        hash.Add(TitleText);
        hash.Add(Entry);
        hash.Add(Carousel);
        return hash.ToHashCode();
    }
}
