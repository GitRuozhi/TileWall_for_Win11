using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Entries;

/// <summary>T-DRAFT（M4 设计 §10.2）：DraftValidator 尺寸边界/占用/名称/URL/目标可编辑性/firstFit 建议。</summary>
public class DraftValidatorTests
{
    private readonly WallGrid _wall = new(2, 9); // 16 基础格列 × 9 行
    private readonly FakeLinkFileService _links;
    private readonly string _root = Path.Combine("C:", "mem", "draft-" + Guid.NewGuid().ToString("N")[..8]);

    public DraftValidatorTests()
    {
        _links = new FakeLinkFileService(new InMemoryFileStore());
    }

    private DraftValidation Validate(TileDraft draft, LayoutObject? current = null, string? currentEntryFull = null, GridRect[]? others = null) =>
        DraftValidator.Validate(draft, current, currentEntryFull, _wall, others ?? [], _links);

    [Fact]
    public void 新建_空墙1x1_合法并给出firstFit建议()
    {
        var validation = Validate(new TileDraft { Size = new GridSize(1, 1) });
        Assert.True(validation.IsValid);
        Assert.Equal(new GridRect(0, 0, 1, 1), validation.SuggestedRect);
    }

    [Fact]
    public void 新建_宽度上限8_合法_宽度9被容量或跨栏拒()
    {
        Assert.True(Validate(new TileDraft { Size = new GridSize(8, 1) }).IsValid); // 单栏内贴边可行
        Assert.False(Validate(new TileDraft { Size = new GridSize(9, 1) }).IsValid); // 宽 > 8 一律非法
    }

    [Fact]
    public void 新建_满墙_SIZE_NO_FIT()
    {
        var full = Enumerable.Range(0, 16).SelectMany(col => Enumerable.Range(0, 9).Select(row => new GridRect(col, row, 1, 1)));
        var validation = Validate(new TileDraft { Size = new GridSize(1, 1) }, others: [.. full]);
        Assert.Equal([DraftValidator.SizeNoFit], validation.Errors);
        Assert.Null(validation.SuggestedRect);
    }

    [Fact]
    public void 新建_零或负尺寸_SIZE_INVALID()
    {
        Assert.Equal([DraftValidator.SizeInvalid], Validate(new TileDraft { Size = new GridSize(0, 1) }).Errors);
        Assert.Equal([DraftValidator.SizeInvalid], Validate(new TileDraft { Size = new GridSize(2, -1) }).Errors);
    }

    [Fact]
    public void 编辑_原点不动_与邻格重叠拒_无重叠合法()
    {
        var current = Layouts.Tile("t1", new GridRect(0, 0, 1, 1));
        var neighbor = new GridRect(1, 0, 1, 1);

        var grown = Validate(new TileDraft { Size = new GridSize(2, 1) }, current, others: [neighbor]); // 原点扩到 (0,0,2,1) 压邻居
        Assert.Equal([DraftValidator.SizeOverlap], grown.Errors);

        var ok = Validate(new TileDraft { Size = new GridSize(2, 2) }, current);
        Assert.True(ok.IsValid); // (0,0,2,2) 无他人占用 → 合法，原点仍 (0,0)
    }

    [Fact]
    public void 编辑_越出墙_SIZE_OUT_OF_WALL()
    {
        var current = Layouts.Tile("t1", new GridRect(0, 8, 2, 1)); // 底行
        var validation = Validate(new TileDraft { Size = new GridSize(2, 2) }, current); // 行 8+2=10 > 9
        Assert.Equal([DraftValidator.SizeOutOfWall], validation.Errors);
    }

    [Fact]
    public void 隐藏标题_不触发NAME_EMPTY_有字则过名称校验()
    {
        Assert.True(Validate(new TileDraft { TitleText = null }).IsValid);
        Assert.True(Validate(new TileDraft { TitleText = "   " }).IsValid); // 空白 = 隐藏标题（§6.3）
        Assert.Contains(EntryNames.NameReserved, Validate(new TileDraft { TitleText = "con" }).Errors);
    }

    [Fact]
    public void 网址草稿_过URL校验_ftp拒()
    {
        Assert.True(Validate(new TileDraft { Entry = new EntryDraft.CreateFromUrl("https://example.com/") }).IsValid);
        Assert.Equal([UrlShortcut.UrlSchemeUnsupported], Validate(new TileDraft { Entry = new EntryDraft.CreateFromUrl("ftp://x/") }).Errors);
    }

    [Fact]
    public void 改URL行_现入口非url_TARGET_NOT_EDITABLE()
    {
        var entryRel = EntryPaths.EntryRelativePath("t1", "工具.lnk");
        var entryFull = EntryPaths.Full(_root, entryRel);
        _links.Register(entryFull, "C:\\x\\tool.exe");

        var errors = Validate(new TileDraft { Entry = new EntryDraft.EditUrlLine("https://x.example/") }, Layouts.Tile("t1", new GridRect(0, 0, 1, 1), new EntryReference { RelativePath = entryRel }), entryFull).Errors;
        Assert.Equal([DraftValidator.TargetNotEditable], errors);
    }

    [Fact]
    public void 改目标_现入口为IDList链_TARGET_NOT_PATH_EDITABLE()
    {
        var entryRel = EntryPaths.EntryRelativePath("t1", "回收站.lnk");
        var entryFull = EntryPaths.Full(_root, entryRel);
        _links.Register(entryFull, target: null, hasIdList: true);

        var errors = Validate(new TileDraft { Entry = new EntryDraft.EditLnkTarget("C:\\x.exe") }, Layouts.Tile("t1", new GridRect(0, 0, 1, 1), new EntryReference { RelativePath = entryRel }), entryFull).Errors;
        Assert.Equal([DraftValidator.TargetNotPathEditable], errors);
    }

    [Fact]
    public void 改目标_无现入口_TARGET_NOT_EDITABLE_空目标_TARGET_EMPTY()
    {
        Assert.Equal([DraftValidator.TargetNotEditable], Validate(new TileDraft { Entry = new EntryDraft.EditLnkTarget("C:\\x.exe") }).Errors);
        Assert.Equal([DraftValidator.TargetEmpty], Validate(new TileDraft { Entry = new EntryDraft.CreateForPath(" ") }).Errors);
    }

    [Fact]
    public void 导入源_非lnk_url扩展名拒()
    {
        Assert.Equal([DraftValidator.EntrySourceUnsupported], Validate(new TileDraft { Entry = new EntryDraft.CopyFromFile("C:\\x\\notes.txt") }).Errors);
        Assert.True(Validate(new TileDraft { Entry = new EntryDraft.CopyFromFile("C:\\x\\page.url") }).IsValid); // 存在性不在草稿层（F-11 由 store 注入）
    }

    [Fact]
    public void 自定义颜色_非法格式拒_合法过()
    {
        Assert.Equal([DraftValidator.BackgroundColorInvalid], Validate(new TileDraft { BackgroundColorHex = "0078D4" }).Errors);
        Assert.True(Validate(new TileDraft { BackgroundColorHex = "#0078D4" }).IsValid);
    }
}
