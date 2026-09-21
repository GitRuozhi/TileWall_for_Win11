using TileWall.Core.Configuration;
using TileWall.Core.Grid;

namespace TileWall.Core.Entries;

/// <summary>
/// 时间日期组件属性草稿（M8 设计 §3.2）：仅两项——大小与可选标题。
/// 创建默认 2×2（一行大字一行小字的可读最小形）；null/空白标题 = 隐藏标题（横幅不创建，§6.3 同语义）。
/// </summary>
public sealed record ClockDraft
{
    public GridSize Size { get; init; } = new(2, 2);

    public string? TitleText { get; init; }
}

/// <summary>
/// 组件草稿就地校验（M8 设计 §3.2）：尺寸复用 <see cref="DraftValidator.ValidateSize"/> 的两分支语义
/// （新建 firstFit、编辑原点锚定 + 不重叠），错误码沿用 SIZE_INVALID / SIZE_TOO_WIDE / SIZE_OUT_OF_WALL /
/// SIZE_CROSSES_COLUMN / SIZE_OVERLAP / SIZE_NO_FIT；标题走 EntryNames.Validate（组件标题即 TitleText 真值，
/// 非法名就地报错不静默清洗）。只读不写，供 ClockPropertyWindow 与主窗提交前共用。
/// </summary>
public static class ComponentDraftValidator
{
    public static DraftValidation Validate(
        ClockDraft draft,
        LayoutObject? current,
        WallGrid wall,
        IReadOnlyList<GridRect> otherRects)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(wall);
        ArgumentNullException.ThrowIfNull(otherRects);

        var errors = new List<string>();

        DraftValidator.ValidateSize(draft.Size, current, wall, otherRects, errors, out var suggestedRect);

        if (!string.IsNullOrWhiteSpace(draft.TitleText))
        {
            errors.AddRange(EntryNames.Validate(draft.TitleText)); // 组件标题即真值：非法名就地报错
        }

        return new DraftValidation(errors, suggestedRect);
    }
}
