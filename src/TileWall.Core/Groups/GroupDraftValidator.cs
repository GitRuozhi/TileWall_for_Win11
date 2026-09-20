using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;

namespace TileWall.Core.Groups;

/// <summary>
/// 组草稿就地校验（M5 设计 §8.1）：只读不写，供属性窗与提交服务共用。
/// 校验次序固定：分区不变式（PARTITION_* 透传，落盘闸门同名码）→ 尺寸界限 → 放置。
/// 放置规则（设计 §9.1「以组左上角为参考，向右和向下增减区域」）：
/// 编辑态锚定 (current.Column, current.Row)（Contains → InSingleColumn → 与他对象无交，首错返回）；
/// 创建态 firstFit（GridPlacement.CanFitRect，行优先、只落单竖栏内起点）；无位 → GROUP_NO_FIT、零写入（A17）。
/// </summary>
public static partial class GroupDraftValidator
{
    /// <summary>组尺寸越界（复用落盘码同名）。</summary>
    public const string SizeOutOfRange = ConfigValidator.GroupSizeOutOfRange;

    /// <summary>编辑态锚定矩形越出墙。</summary>
    public const string PlaceOutOfWall = "GROUP_PLACE_OUT_OF_WALL";

    /// <summary>编辑态锚定矩形跨越竖栏。</summary>
    public const string PlaceCrossesColumn = "GROUP_PLACE_CROSSES_COLUMN";

    /// <summary>编辑态锚定矩形与其他对象相交。</summary>
    public const string PlaceConflict = "GROUP_PLACE_CONFLICT";

    /// <summary>创建态无位可放（含满墙；保存钮禁用、零写入，A17）。</summary>
    public const string NoFit = "GROUP_NO_FIT";

    public static DraftValidation Validate(
        GroupEditDraft draft,
        GroupObject? current,
        WallGrid wall,
        IReadOnlyList<GridRect> otherRects)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(wall);
        ArgumentNullException.ThrowIfNull(otherRects);

        var errors = new List<string>();

        // 0. 分区不变式先跑（任何校验路径；错误码与落盘闸门同名透传）
        var check = new PartitionLayout(draft.Size, draft.Partitions).Check();
        if (!check.IsValid)
        {
            errors.AddRange(check.Violations);
        }

        // 1. 尺寸界限（列 2–8、行 2–25）
        if (!GridPlacement.IsGroupSizeInRange(draft.Size))
        {
            errors.Add(SizeOutOfRange);
        }

        // 底色（Q6 纯色）：非空须为 #RRGGBB（复用磁贴色校验码）
        if (!string.IsNullOrEmpty(draft.BackdropColorHex)
            && !HexColorRegex().IsMatch(draft.BackdropColorHex))
        {
            errors.Add(DraftValidator.BackgroundColorInvalid);
        }

        // 2. 放置（分区/尺寸/颜色已错时不再给出误导性的放置码，但保留已报错误）
        GridRect? suggested = null;
        if (errors.Count == 0)
        {
            if (current is null)
            {
                var occupied = OccupancyMap.Build(wall, otherRects);
                if (GridPlacement.CanFitRect(wall, draft.Size, occupied, out var firstFit))
                {
                    suggested = firstFit;
                }
                else
                {
                    errors.Add(NoFit);
                }
            }
            else
            {
                var anchored = new GridRect(
                    current.Bounds.Column,
                    current.Bounds.Row,
                    draft.Size.Columns,
                    draft.Size.Rows);
                suggested = anchored;
                if (!wall.Contains(anchored))
                {
                    errors.Add(PlaceOutOfWall);
                }
                else if (!wall.InSingleColumn(anchored))
                {
                    errors.Add(PlaceCrossesColumn);
                }
                else if (otherRects.Any(other => other.Intersects(anchored)))
                {
                    errors.Add(PlaceConflict);
                }
            }
        }

        return new DraftValidation(errors, suggested);
    }

    [System.Text.RegularExpressions.GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial System.Text.RegularExpressions.Regex HexColorRegex();
}
