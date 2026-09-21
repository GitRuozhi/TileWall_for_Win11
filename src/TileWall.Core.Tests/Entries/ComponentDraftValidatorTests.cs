using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Entries;

/// <summary>
/// T-CLOCK-DRAFT（M8 设计 §3.2/§4）：ComponentDraftValidator 的 firstFit / 锚定缩放越墙 / 跨栏 /
/// 重叠 / SIZE_NO_FIT；标题非法名就地报错（组件标题即 TitleText 真值）。
/// </summary>
public sealed class ComponentDraftValidatorTests
{
    private static readonly WallGrid Wall = new(2, 9); // 16 列 × 9 行

    private static DraftValidation Validate(ClockDraft draft, LayoutObject? current, GridRect[]? others = null) =>
        ComponentDraftValidator.Validate(draft, current, Wall, others ?? []);

    [Fact]
    public void Create_Default2x2_GetsFirstFitAtOrigin()
    {
        var validation = Validate(new ClockDraft(), current: null);
        Assert.True(validation.IsValid);
        Assert.Equal(new GridRect(0, 0, 2, 2), validation.SuggestedRect); // 新建 = firstFit 建议位
    }

    [Fact]
    public void Create_FirstFit_SkipsOccupiedRegion()
    {
        var others = new[] { new GridRect(0, 0, 16, 1) }; // 首行占满
        var validation = Validate(new ClockDraft { Size = new GridSize(2, 2) }, current: null, others);
        Assert.True(validation.IsValid);
        Assert.Equal(new GridRect(0, 1, 2, 2), validation.SuggestedRect); // 行优先扫描落到次行
    }

    [Fact]
    public void Create_FullWall_ReportsSizeNoFit()
    {
        var others = Enumerable.Range(0, 9).Select(row => new GridRect(0, row, 16, 1)).ToArray();
        var validation = Validate(new ClockDraft { Size = new GridSize(2, 2) }, current: null, others);
        Assert.False(validation.IsValid);
        Assert.Contains(DraftValidator.SizeNoFit, validation.Errors); // 满墙不部分创建（A06/A17 同语义）
    }

    [Fact]
    public void Edit_AnchoredResize_KeepsOriginAndValidates()
    {
        var current = new ClockObject
        {
            Id = "clock-1",
            Bounds = new GridRect(8, 0, 2, 2),
            Visual = new ObjectVisual { TitleText = "时钟" },
        };
        var others = new[] { new GridRect(8, 2, 2, 1) }; // 紧贴下方
        var validation = Validate(new ClockDraft { Size = new GridSize(2, 1), TitleText = "时钟" }, current, others);
        Assert.True(validation.IsValid); // 缩小为 2×1：原点不动、不与下方对象相交
    }

    [Fact]
    public void Edit_WiderThanOneColumn_ReportsSizeTooWide()
    {
        var current = new ClockObject { Id = "clock-1", Bounds = new GridRect(0, 0, 2, 2) };
        var validation = Validate(new ClockDraft { Size = new GridSize(9, 1) }, current);
        Assert.Contains(DraftValidator.SizeTooWide, validation.Errors); // 宽超一栏八格
    }

    [Fact]
    public void Edit_AnchoredGrowOutOfWall_ReportsSizeOutOfWall()
    {
        var current = new ClockObject { Id = "clock-1", Bounds = new GridRect(0, 8, 2, 1) }; // 末行
        var validation = Validate(new ClockDraft { Size = new GridSize(2, 2) }, current);
        Assert.Contains(DraftValidator.SizeOutOfWall, validation.Errors); // 锚定原点放大 → 越墙
    }

    [Fact]
    public void Edit_OverlapWithOther_ReportsSizeOverlap()
    {
        var current = new ClockObject { Id = "clock-1", Bounds = new GridRect(0, 0, 2, 2) };
        var others = new[] { new GridRect(2, 0, 2, 1) };
        var validation = Validate(new ClockDraft { Size = new GridSize(4, 1) }, current, others);
        Assert.Contains(DraftValidator.SizeOverlap, validation.Errors);
    }

    [Fact]
    public void Create_SizeCrossingColumnBoundary_ReportsSizeNoFitFromFirstFit()
    {
        // 新建分支的 firstFit 只落单栏内起点：9 格宽的矩形在 16 列墙上无处可放 → SIZE_NO_FIT
        var validation = Validate(new ClockDraft { Size = new GridSize(9, 1) }, current: null);
        Assert.False(validation.IsValid);
        Assert.Contains(DraftValidator.SizeNoFit, validation.Errors);
    }

    [Fact]
    public void Title_ReservedName_ReportsInPlace()
    {
        var validation = Validate(new ClockDraft { TitleText = "CON" }, current: null);
        Assert.Contains(EntryNames.NameReserved, validation.Errors); // 非法名就地报错，不静默清洗
    }

    [Fact]
    public void Title_InvalidChars_ReportsInPlace()
    {
        var validation = Validate(new ClockDraft { TitleText = "a<b" }, current: null);
        Assert.Contains(EntryNames.NameInvalidChars, validation.Errors);
    }

    [Fact]
    public void Title_Empty_IsLegal_HidesBanner()
    {
        var validation = Validate(new ClockDraft { TitleText = string.Empty }, current: null);
        Assert.True(validation.IsValid); // null/空白 = 隐藏标题（§6.3 同语义，不触发 NAME_EMPTY）
    }
}
