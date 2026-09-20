using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Groups;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Entries;

/// <summary>
/// T-GSAVE（M5 设计 §8.3）：CommitGroup 成功路径（新建含入口/换目标/清链接/纯布局退化）；
/// FaultPoint 注入组路径 → 正式区 == 操作前（INV-E6 组侧）；Carousel/Images/Backdrop 透传保真；
/// 磁贴路径（Commit）对组仍拒绝——M4 共享代码语义不变。
/// </summary>
public sealed class GroupCommitTests : IDisposable
{
    private const string GroupId = "grp-0001";

    private readonly EntryCommitHarness _h = new();

    [Fact]
    public void Create_WithEntry_PlacesAtFirstFitAndCreatesSingleEntry()
    {
        var config = SeedTileOnly();
        var draft = FullGridDraft(4, 4) with { Entry = new EntryDraft.CreateForPath("C:\\tools\\app.exe") };

        var report = _h.Service.CommitGroup(config, new GroupCommitRequest(GroupId, "新建磁贴组", draft));

        // firstFit：(0,0,2,2) 被占 → (2,0,4,4)（同竖栏、行优先）
        var group = (GroupObject)report.NewConfig.Objects.Single(o => o.Id == GroupId);
        Assert.Equal(new GridRect(2, 0, 4, 4), group.Bounds);
        Assert.Equal("Objects/grp-0001/app.lnk", group.Entry!.RelativePath);
        Assert.NotNull(report.UndoMaterial); // 入口操作 → 撤销槽带材料（主墙 Ctrl+Z 可整体回滚）

        // 组唯一入口不变式（INV-E1 组侧）：二百块也只有一份入口文件
        var entryPath = _h.EntryPath(GroupId, "app.lnk");
        Assert.True(_h.Files.Exists(entryPath));
        Assert.Equal("C:\\tools\\app.exe", _h.Links.StateOf(entryPath).TargetPath);
        Assert.Single(_h.Files.FilePaths, p => p.Contains("\\Objects\\grp-0001\\", StringComparison.Ordinal));
        _h.AssertNoStagingResidue();

        var saved = (GroupObject)_h.LoadConfig().Objects.Single(o => o.Id == GroupId);
        Assert.Equal(new GridRect(2, 0, 4, 4), saved.Bounds);
        Assert.Equal(16, saved.Partitions.Count);
    }

    [Fact]
    public void Edit_SwapTarget_ReplacesEntryFileViaProtocol()
    {
        var config = SeedGroupWithEntry();
        var entryPath = _h.EntryPath(GroupId, "演示组.lnk");
        var draft = FullGridDraft(4, 4) with
        {
            TitleText = null,
            Entry = new EntryDraft.CreateForPath("C:\\tools\\other.exe"),
        };

        var report = _h.Service.CommitGroup(config, new GroupCommitRequest(GroupId, "编辑磁贴组", draft));

        Assert.NotNull(report.UndoMaterial);
        // 无标题文字 → 最终名 = 现入口主体名（同路径 replace 语义）
        Assert.Equal("Objects/grp-0001/演示组.lnk", report.NewConfig.Objects.OfType<GroupObject>().Single(o => o.Id == GroupId).Entry!.RelativePath);
        Assert.Equal("C:\\tools\\other.exe", _h.Links.StateOf(entryPath).TargetPath);
        _h.AssertNoStagingResidue();
    }

    [Fact]
    public void Edit_ClearLink_RemovesEntryFileAndNullsReference()
    {
        var config = SeedGroupWithEntry();
        var entryPath = _h.EntryPath(GroupId, "演示组.lnk");
        Assert.True(_h.Files.Exists(entryPath));

        var draft = FullGridDraft(4, 4) with { Entry = new EntryDraft.NoneDraft() };
        var report = _h.Service.CommitGroup(config, new GroupCommitRequest(GroupId, "编辑磁贴组", draft));

        var group = (GroupObject)report.NewConfig.Objects.Single(o => o.Id == GroupId);
        Assert.Null(group.Entry);
        Assert.False(_h.Files.Exists(entryPath)); // 入口移入 Recovery/…/removed/
        Assert.Contains(_h.Files.FilePaths, p => p.Contains("\\Recovery\\Entries\\", StringComparison.Ordinal) && p.Contains("\\removed\\", StringComparison.Ordinal));
        Assert.NotNull(report.UndoMaterial);
    }

    [Fact]
    public void Edit_RenameWithKeepCurrent_RenamesEntryFileInPlace()
    {
        var config = SeedGroupWithEntry();
        var draft = FullGridDraft(4, 4) with { TitleText = "新组名", Entry = new EntryDraft.KeepCurrent() };

        var report = _h.Service.CommitGroup(config, new GroupCommitRequest(GroupId, "编辑磁贴组", draft));

        var group = (GroupObject)report.NewConfig.Objects.Single(o => o.Id == GroupId);
        Assert.Equal("Objects/grp-0001/新组名.lnk", group.Entry!.RelativePath);
        Assert.True(_h.Files.Exists(_h.EntryPath(GroupId, "新组名.lnk")));
        Assert.False(_h.Files.Exists(_h.EntryPath(GroupId, "演示组.lnk")));
        Assert.Null(group.Visual.TitleText); // 有入口 → 名称真值在托管文件名主体（单一真值）
    }

    [Fact]
    public void PureLayoutChange_DoesNotTouchEntryFiles_DegeneratePath()
    {
        var config = SeedGroupWithEntry();
        var before = _h.Files.SnapshotFiles();
        var entryKeys = before.Keys.Where(k => k.Contains("\\Objects\\", StringComparison.Ordinal)).ToList();

        // 只改布局：2×2 大块 + 其余 1×1；入口 KeepCurrent、无改名 → 退化「预检 + Save」
        var draft = new GroupEditDraft
        {
            Size = new GridSize(4, 4),
            Partitions = [new GridRect(0, 0, 2, 2), .. OtherSingles()],
            Entry = new EntryDraft.KeepCurrent(),
        };
        var report = _h.Service.CommitGroup(config, new GroupCommitRequest(GroupId, "编辑磁贴组", draft));

        Assert.Null(report.UndoMaterial); // 无入口操作 → 无撤销材料（§6.5 同款退化）
        foreach (var key in entryKeys)
        {
            Assert.True(_h.Files.SnapshotFiles().TryGetValue(key, out var after) && before[key].AsSpan().SequenceEqual(after),
                $"退化路径不得触碰入口文件字节：{key}"); // InMemoryFileStore 不记时间戳，字节级等价断言
        }

        var saved = (GroupObject)_h.LoadConfig().Objects.Single(o => o.Id == GroupId);
        Assert.Equal(new GridRect(0, 0, 2, 2), saved.Partitions[0]); // 布局已生效
        _h.AssertNoStagingResidue();
    }

    [Fact]
    public void CarouselImagesBackdrop_PassThroughWithFidelity()
    {
        var config = SeedTileOnly();
        var switchUtc = new DateTimeOffset(2026, 9, 21, 15, 16, 12, TimeSpan.Zero);
        var draft = FullGridDraft(4, 4) with
        {
            Backdrop = BackdropKind.SolidColor,
            BackdropColorHex = "#123456",
            Images = new GroupImages { Kind = GroupImageSourceKind.Multiple, ImagePaths = ["C:\\p\\1.png", "C:\\p\\2.png"] },
            Carousel = new CarouselState { CurrentImageId = "C:\\p\\1.png", LastSwitchUtc = switchUtc },
        };

        var report = _h.Service.CommitGroup(config, new GroupCommitRequest(GroupId, "新建磁贴组", draft));

        var group = (GroupObject)report.NewConfig.Objects.Single(o => o.Id == GroupId);
        Assert.Equal(BackdropKind.SolidColor, group.Visual.Backdrop);
        Assert.Equal("#123456", group.Visual.BackgroundColor);
        Assert.Equal(GroupImageSourceKind.Multiple, group.Images.Kind);
        Assert.Equal(["C:\\p\\1.png", "C:\\p\\2.png"], group.Images.ImagePaths);
        Assert.Equal(switchUtc, group.Carousel!.LastSwitchUtc);

        var saved = (GroupObject)_h.LoadConfig().Objects.Single(o => o.Id == GroupId);
        Assert.True(draft.Images.Equals(saved.Images)); // 落盘 round-trip 保真
        Assert.Equal(switchUtc, saved.Carousel!.LastSwitchUtc);
    }

    [Fact]
    public void FaultInjection_RollsBackConfigAndEntries_InvE6GroupSide()
    {
        var config = SeedGroupWithEntry();
        var before = _h.Files.SnapshotFiles();
        _h.Files.EntryFault = EntryFaultPoint.BeforeStageWrite; // 导入副本落 Staging 前失败
        var draft = FullGridDraft(4, 4) with { Entry = new EntryDraft.CopyFromFile("C:\\src\\工具.lnk") };

        Assert.ThrowsAny<IOException>(() => _h.Service.CommitGroup(config, new GroupCommitRequest(GroupId, "编辑磁贴组", draft)));

        _h.AssertFormalAreaUnchanged(before); // 配置与入口 == 操作前
    }

    [Fact]
    public void InvalidDraft_ThrowsDraftValidation_ZeroWrite()
    {
        var config = SeedGroupWithEntry();
        var before = _h.Files.SnapshotFiles();
        var draft = FullGridDraft(9, 4); // 尺寸越界

        var exception = Assert.Throws<DraftValidationException>(() =>
            _h.Service.CommitGroup(config, new GroupCommitRequest(GroupId, "编辑磁贴组", draft)));

        Assert.Contains(GroupDraftValidator.SizeOutOfRange, exception.Errors);
        _h.AssertFormalAreaUnchanged(before);
    }

    [Fact]
    public void PartitionInvalidDraft_ThrowsWithDiskGateCode_ZeroWrite()
    {
        var config = SeedGroupWithEntry();
        var before = _h.Files.SnapshotFiles();
        var draft = new GroupEditDraft
        {
            Size = new GridSize(4, 4),
            Partitions = [new GridRect(0, 0, 4, 4), new GridRect(0, 0, 4, 4)], // 完全重叠
        };

        var exception = Assert.Throws<DraftValidationException>(() =>
            _h.Service.CommitGroup(config, new GroupCommitRequest(GroupId, "编辑磁贴组", draft)));

        Assert.Contains("PARTITION_OVERLAP", exception.Errors);
        _h.AssertFormalAreaUnchanged(before);
    }

    [Fact]
    public void TileCommitPath_StillRejectsGroups_WithoutNotSupportedExceptionBranch()
    {
        var config = SeedGroupWithEntry();
        var draft = new TileDraft { Size = new GridSize(1, 1), Entry = new EntryDraft.KeepCurrent() };

        // M4 组 NotSupportedException 分支已删除；磁贴提交路径对组仍明确拒绝（组走 CommitGroup）
        Assert.Throws<InvalidOperationException>(() =>
            _h.Service.Commit(config, new EntryCommitRequest(GroupId, "编辑磁贴", draft)));
    }

    // ————————————————————————————— 夹具 —————————————————————————————

    private TileWallConfig SeedTileOnly()
    {
        var config = Layouts.Config(_h.Wall, [Layouts.Tile("tile-1", new GridRect(0, 0, 2, 2))]);
        _h.Store.Save(config);
        return config;
    }

    private TileWallConfig SeedGroupWithEntry()
    {
        var entryRelativePath = EntryPaths.EntryRelativePath(GroupId, EntryNames.CombineName("演示组", EntryKind.Lnk));
        _h.Links.Register(EntryPaths.Full(_h.Root, entryRelativePath), "C:\\tools\\demo.exe");
        var config = Layouts.Config(_h.Wall, [Layouts.Group(GroupId, new GridRect(8, 0, 4, 4)) with
        {
            Entry = new EntryReference { RelativePath = entryRelativePath },
        }]);
        _h.Store.Save(config);
        return config;
    }

    private static GroupEditDraft FullGridDraft(int columns, int rows)
    {
        var partitions = new List<GridRect>(columns * rows);
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < columns; col++)
            {
                partitions.Add(new GridRect(col, row, 1, 1));
            }
        }

        return new GroupEditDraft { Size = new GridSize(columns, rows), Partitions = partitions };
    }

    private static IEnumerable<GridRect> OtherSingles()
    {
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                if (col < 2 && row < 2)
                {
                    continue;
                }

                yield return new GridRect(col, row, 1, 1);
            }
        }
    }

    public void Dispose() => _h.Dispose();
}
