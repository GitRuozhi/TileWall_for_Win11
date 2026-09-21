using System.Text.RegularExpressions;
using TileWall.Core.Configuration;
using TileWall.Core.Grid;

namespace TileWall.Core.Entries;

/// <summary>属性窗五字段的 Core 侧投影（M4 设计 §5.2；全部校验为纯函数，UI 只绑定显示，P1 §3.2）。</summary>
public sealed record TileDraft
{
    public GridSize Size { get; init; } = new(1, 1);     // 基础格整数矩形，宽 ≤ 8（WallGrid.TileMaxCells）

    /// <summary>null 或 "#RRGGBB"；null=跟随系统。</summary>
    public string? BackgroundColorHex { get; init; }

    public string? BackgroundImagePath { get; init; }

    /// <summary>null=目标图标/占位（M4 不做图标提取，§12 风险 4）。</summary>
    public string? ForegroundIconPath { get; init; }

    /// <summary>null 或空白 = 隐藏标题（设计 §6.3）。</summary>
    public string? TitleText { get; init; }

    /// <summary>null = 空目标（合法，设计 §6.2）。</summary>
    public EntryDraft? Entry { get; init; }
}

/// <summary>链接草稿判别联合（创建/编辑/清空/未动；M4 设计 §5.2）。</summary>
public abstract record EntryDraft
{
    /// <summary>清空链接（撤销绑定）。</summary>
    public sealed record NoneDraft : EntryDraft;

    /// <summary>链接未动（不重写入口文件）。</summary>
    public sealed record KeepCurrent : EntryDraft;

    /// <summary>选择现有 .lnk/.url → 完整副本（原字节不改写，INV-E4）。</summary>
    public sealed record CopyFromFile(string SourcePath) : EntryDraft;

    /// <summary>程序/文件/文件夹 → 新建 .lnk。</summary>
    public sealed record CreateForPath(string TargetPath) : EntryDraft;

    /// <summary>直接网址 → 新建 .url。</summary>
    public sealed record CreateFromUrl(string Url) : EntryDraft;

    /// <summary>编辑普通 .lnk 目标（[R12]；HasIdList 拒绝）。</summary>
    public sealed record EditLnkTarget(string NewTargetPath) : EntryDraft;

    /// <summary>编辑 .url 的 URL 行（其余行保留）。</summary>
    public sealed record EditUrlLine(string NewUrl) : EntryDraft;
}

/// <summary>草稿就地校验结果：固定错误码列表 + 容量建议位（firstFit，供表单就地显示，P1 §3.2）。</summary>
public sealed record DraftValidation(IReadOnlyList<string> Errors, GridRect? SuggestedRect)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>草稿校验失败（提交服务在零写入前上抛；表单侧直接用 <see cref="DraftValidation"/> 就地显示）。</summary>
public sealed class DraftValidationException : Exception
{
    public DraftValidationException(IReadOnlyList<string> errors)
        : base($"草稿校验失败，共 {errors.Count} 项：{string.Join("; ", errors)}")
        => Errors = errors;

    public IReadOnlyList<string> Errors { get; }
}

/// <summary>
/// 草稿就地校验（M4 设计 §5.2）：名称（隐藏标题不触发 NAME_EMPTY）、URL、目标可编辑性
/// （HasIdList → TARGET_NOT_PATH_EDITABLE）、尺寸（新建 = CanFitRect(firstFit)；
/// 编辑 = 原点不动的新矩形对 others 占用合法）。只读不写，供表单与提交服务共用。
/// </summary>
public static partial class DraftValidator
{
    public const string SizeInvalid = "SIZE_INVALID";
    public const string SizeTooWide = "SIZE_TOO_WIDE";
    public const string SizeOutOfWall = "SIZE_OUT_OF_WALL";
    public const string SizeCrossesColumn = "SIZE_CROSSES_COLUMN";
    public const string SizeOverlap = "SIZE_OVERLAP";
    public const string SizeNoFit = "SIZE_NO_FIT";
    public const string TargetNotEditable = "TARGET_NOT_EDITABLE";
    public const string TargetNotPathEditable = "TARGET_NOT_PATH_EDITABLE";
    public const string TargetEmpty = "TARGET_EMPTY";
    public const string EntrySourceUnsupported = "ENTRY_SOURCE_UNSUPPORTED";
    public const string BackgroundColorInvalid = "BACKGROUND_COLOR_INVALID";

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColorRegex();

    public static DraftValidation Validate(
        TileDraft draft,
        LayoutObject? current,
        string? currentEntryFullPath,
        WallGrid wall,
        IReadOnlyList<GridRect> otherRects,
        ILnkFileService linkFiles)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(wall);
        ArgumentNullException.ThrowIfNull(otherRects);
        ArgumentNullException.ThrowIfNull(linkFiles);

        var errors = new List<string>();

        ValidateSize(draft.Size, current, wall, otherRects, errors, out var suggestedRect);

        // 名称：隐藏标题（空白）不触发 NAME_EMPTY（设计 §6.3「再次打开时可保持显示为空」）
        if (!string.IsNullOrWhiteSpace(draft.TitleText))
        {
            errors.AddRange(EntryNames.Validate(draft.TitleText));
        }

        if (!string.IsNullOrEmpty(draft.BackgroundColorHex)
            && !HexColorRegex().IsMatch(draft.BackgroundColorHex))
        {
            errors.Add(BackgroundColorInvalid);
        }

        ValidateEntryDraft(draft.Entry, currentEntryFullPath, linkFiles, errors);

        return new DraftValidation(errors, suggestedRect);
    }

    /// <summary>
    /// 尺寸两分支校验（新建 firstFit / 编辑原点锚定；M8 起也服务 ComponentDraftValidator——同一份代码）。
    /// </summary>
    internal static void ValidateSize(
        GridSize size,
        LayoutObject? current,
        WallGrid wall,
        IReadOnlyList<GridRect> otherRects,
        List<string> errors,
        out GridRect? suggestedRect)
    {
        suggestedRect = null;
        if (size.Columns < 1 || size.Rows < 1)
        {
            errors.Add(SizeInvalid);
            return;
        }

        if (current is null)
        {
            // 新建：容量预检（A06/A17 满墙不部分创建语义由 firstFit 延续，§8.1）
            var occupied = OccupancyMap.Build(wall, otherRects);
            if (GridPlacement.CanFitRect(wall, size, occupied, out var firstFit))
            {
                suggestedRect = firstFit;
            }
            else
            {
                errors.Add(SizeNoFit);
            }

            return;
        }

        // 编辑：原点不动的新矩形（§5.2）
        if (size.Columns > WallGrid.TileMaxCells)
        {
            errors.Add(SizeTooWide);
            return;
        }

        var resized = new GridRect(current.Bounds.Column, current.Bounds.Row, size.Columns, size.Rows);
        if (!wall.Contains(resized))
        {
            errors.Add(SizeOutOfWall);
            return;
        }

        if (!wall.InSingleColumn(resized))
        {
            errors.Add(SizeCrossesColumn);
            return;
        }

        foreach (var other in otherRects)
        {
            if (other.Intersects(resized))
            {
                errors.Add(SizeOverlap);
                return;
            }
        }
    }

    private static void ValidateEntryDraft(EntryDraft? entry, string? currentEntryFullPath, ILnkFileService linkFiles, List<string> errors)
    {
        switch (entry)
        {
            case null:
            case EntryDraft.NoneDraft:
            case EntryDraft.KeepCurrent:
                break;

            case EntryDraft.CopyFromFile copy:
                // 存在性不在此处检查：导入失败经 store.Copy 注入/抛出走协议回滚（F-11 的可注入性优先）
                if (string.IsNullOrWhiteSpace(copy.SourcePath))
                {
                    errors.Add(TargetEmpty);
                }
                else if (EntryNames.KindOfRelativePath(copy.SourcePath) == EntryKind.None)
                {
                    errors.Add(EntrySourceUnsupported); // 仅 .lnk/.url 可导入（完整副本不改写）
                }

                break;

            case EntryDraft.CreateForPath create:
                if (string.IsNullOrWhiteSpace(create.TargetPath))
                {
                    errors.Add(TargetEmpty);
                }

                break;

            case EntryDraft.CreateFromUrl url:
                errors.AddRange(UrlShortcut.ValidateUrl(url.Url));
                break;

            case EntryDraft.EditLnkTarget editLnk:
                ValidateLnkTargetEdit(editLnk.NewTargetPath, currentEntryFullPath, linkFiles, errors);
                break;

            case EntryDraft.EditUrlLine editUrl:
                if (currentEntryFullPath is null || EntryNames.KindOfRelativePath(currentEntryFullPath) != EntryKind.Url)
                {
                    errors.Add(TargetNotEditable); // 改 URL 行仅对现入口为 .url 的对象成立
                }
                else
                {
                    errors.AddRange(UrlShortcut.ValidateUrl(editUrl.NewUrl));
                }

                break;
        }
    }

    private static void ValidateLnkTargetEdit(string newTarget, string? currentEntryFullPath, ILnkFileService linkFiles, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(newTarget))
        {
            errors.Add(TargetEmpty);
            return;
        }

        if (currentEntryFullPath is null || EntryNames.KindOfRelativePath(currentEntryFullPath) != EntryKind.Lnk)
        {
            errors.Add(TargetNotEditable); // 无现入口或现入口非 .lnk → 只改目标不成立
            return;
        }

        try
        {
            var fields = linkFiles.Read(currentEntryFullPath);
            if (fields.HasIdList)
            {
                errors.Add(TargetNotPathEditable); // 特殊 Shell 入口：不伪造 EXE，只允许整体替换（§6.4 末段 [R5]）
            }
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            errors.Add(TargetNotEditable); // 现入口不可读（损坏 .lnk → COMException）→ 无法只改目标
        }
    }
}
