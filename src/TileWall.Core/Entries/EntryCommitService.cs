using TileWall.Core.Configuration;
using TileWall.Core.Grid;
using TileWall.Core.Groups;

namespace TileWall.Core.Entries;

/// <summary>联合提交请求（M4 设计 §5.5）。新建时 <paramref name="ObjectId"/> 由调用方经 StableId.NewId() 生成。</summary>
public sealed record EntryCommitRequest(string ObjectId, string ActionName, TileDraft Draft);

/// <summary>组联合提交请求（M5 设计 §8.3）：与磁贴提交同协议，仅草稿与对象构造分支不同。</summary>
public sealed record GroupCommitRequest(string ObjectId, string ActionName, GroupEditDraft Draft);

/// <summary>撤销槽的入口材料（§8.3）：指向 Recovery/Entries/&lt;commitId&gt;/ 的日志与备份；撤销不跨会话。</summary>
public sealed record EntryUndoMaterial(string CommitId, IReadOnlyList<string> ObjectIds);

/// <summary>
/// 提交结果：新配置（调用方 Adopt 进 LayoutCommitService 前移 Current 并建撤销槽）+ 撤销材料
/// （无入口操作的退化提交为 null——「只改外观/文字不重写入口」，§6.5）。
/// </summary>
public sealed record EntryCommitReport(TileWallConfig NewConfig, LayoutObject CommittedObject, EntryUndoMaterial? UndoMaterial);

/// <summary>批量导入提交结果（M8 设计 §5.7）：N 对象 + N 托管副本一次协议生效；整批一个 commitId 的撤销材料（INV-I4）。</summary>
public sealed record EntryBatchCommitReport(
    TileWallConfig NewConfig,
    IReadOnlyList<LayoutObject> CommittedObjects,
    EntryUndoMaterial UndoMaterial);

/// <summary>提交协议内部的计划项：日志条目 + 把新入口文件生成进暂存区的动作（edit 类无暂存文件）。</summary>
internal sealed record PlannedOp(JournalOp Journal, Action<string>? StageIntoStaging);

/// <summary>
/// 联合提交编排（范围 D 核心；M4 设计 §5.5 协议 / §16.3「暂存、替换和恢复」）：
/// 预检 → Staging → 提交日志落盘 → 旧入口备份 Recovery → 入口生效（add/replace→rename→remove→edit 顺序）→
/// ConfigStore.Save（唯一权威提交点）→ 收尾。任一失败走 <see cref="EntryRecovery"/> 的同一份幂等 Rollback。
/// 布局级旧路径（拖动、空目标取消固定）不经此类（LayoutCommitService 不触碰入口文件，§2.2 不变式）。
/// </summary>
public sealed class EntryCommitService
{
    private const string OpAdd = "add";
    private const string OpReplace = "replace";
    private const string OpRename = "rename";
    private const string OpRemove = "remove";
    private const string OpEdit = "edit";

    private readonly IFileStore _files;
    private readonly ConfigStore _store;
    private readonly ILnkFileService _linkFiles;
    private readonly string _rootPath;
    private readonly EntryRecovery _recovery;

    public EntryCommitService(IFileStore files, ConfigStore store, ILnkFileService linkFiles, IDataDirectoryProvider directoryProvider)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(linkFiles);
        ArgumentNullException.ThrowIfNull(directoryProvider);
        _files = files;
        _store = store;
        _linkFiles = linkFiles;
        _rootPath = directoryProvider.GetDefault().RootPath;
        _recovery = new EntryRecovery(files, linkFiles, directoryProvider.GetDefault());
    }

    /// <summary>
    /// 一次提交 = 属性（大小/后景/前景/文字）+ 改名 + 目标/类型变更 + 新建插入的单次逻辑事务。
    /// 校验失败抛 <see cref="DraftValidationException"/>（零写入）；配置校验失败抛
    /// <see cref="ConfigValidationException"/>（零写入）；文件步骤失败回滚后上抛原异常（正式区 == 操作前）。
    /// </summary>
    public EntryCommitReport Commit(TileWallConfig current, EntryCommitRequest request)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.ObjectId);
        ArgumentException.ThrowIfNullOrEmpty(request.ActionName);

        var (newObject, planned) = PlanCommit(current, request);
        var isCreate = current.Objects.All(o => o.Id != request.ObjectId);
        var newConfig = isCreate
            ? current with { Objects = [.. current.Objects, newObject] }
            : current with { Objects = ReplaceById(current.Objects, newObject) };

        // 步骤 1（预检，纯读零写入）：新配置整体过结构校验
        var violations = ConfigValidator.Validate(newConfig);
        if (violations.Count > 0)
        {
            throw new ConfigValidationException(violations);
        }

        if (planned.Count == 0)
        {
            // 退化路径：纯属性/布局提交——不产生任何入口文件操作（§6.5）
            _store.Save(newConfig);
            return new EntryCommitReport(newConfig, newObject, null);
        }

        var commitId = StableId.NewId();
        ExecuteProtocol(newConfig, planned, commitId);
        return new EntryCommitReport(newConfig, newObject, new EntryUndoMaterial(commitId, [request.ObjectId]));
    }

    /// <summary>
    /// 组联合提交（M5 设计 §8.3）：复用磁贴提交的全部协议机制（staging→日志→备份→入口生效→
    /// ConfigStore.Save→收尾→失败回滚），仅对象构造与校验分支不同。
    /// 校验失败抛 <see cref="DraftValidationException"/> / <see cref="ConfigValidationException"/>（零写入）；
    /// 纯布局/属性改动（Entry 为 KeepCurrent 且无改名）退化为「预检 + Save」，不触碰入口文件（§6.5 同款退化）。
    /// 组唯一入口不变式与失败矩阵（F-1…F-6/F-8/F-11…F-14）由同一协议代码路径保证。
    /// </summary>
    public EntryCommitReport CommitGroup(TileWallConfig current, GroupCommitRequest request)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.ObjectId);
        ArgumentException.ThrowIfNullOrEmpty(request.ActionName);

        var (newObject, planned) = PlanGroupCommit(current, request);
        var isCreate = current.Objects.All(o => o.Id != request.ObjectId);
        var newConfig = isCreate
            ? current with { Objects = [.. current.Objects, newObject] }
            : current with { Objects = ReplaceById(current.Objects, newObject) };

        // 预检（纯读零写入）：新配置整体过结构校验（分区落盘闸门在此复跑）
        var violations = ConfigValidator.Validate(newConfig);
        if (violations.Count > 0)
        {
            throw new ConfigValidationException(violations);
        }

        if (planned.Count == 0)
        {
            // 退化路径：纯属性/布局提交——不产生任何入口文件操作（§6.5 同款）
            _store.Save(newConfig);
            return new EntryCommitReport(newConfig, newObject, null);
        }

        var commitId = StableId.NewId();
        ExecuteProtocol(newConfig, planned, commitId);
        return new EntryCommitReport(newConfig, newObject, new EntryUndoMaterial(commitId, [request.ObjectId]));
    }

    /// <summary>撤销的文件还原半步与材料删除的透传（§8.3；实现在 <see cref="EntryRecovery"/>）。</summary>
    public bool RestoreMaterial(EntryUndoMaterial material) => _recovery.RestoreMaterial(material);    public void DeleteMaterial(string commitId) => _recovery.DeleteMaterial(commitId);

    /// <summary>
    /// 批量导入提交（M8 设计 §5.6/§5.7，C26 核心）：N 候选一次协议执行——
    /// 计划段（纯读零写入，INV-I1）：逐候选走 <see cref="PlanCommit"/> 创建分支，第 i 个候选的
    /// otherRects = 现有对象 ∪ 前 i−1 个已排位候选 → 顺序 first-fit（确定性 → 预检结论 == 提交结果，A15）；
    /// 任一候选草稿校验失败 → <see cref="DraftValidationException"/>（在任何 IO 之前，零写入）。
    /// 合并全部 PlannedOp（导入场景全为 add）→ 一次 ExecuteProtocol：一次 Staging(N 文件)/一份提交日志/
    /// 一次 Recovery 备份/一次入口生效/一次 ConfigStore.Save——失败走 <see cref="EntryRecovery.Rollback"/>
    /// （INV-I2：正式区 == 操作前、配置未保存、暂存已清）；崩溃由配置指纹 Sweep 收敛（INV-I3）。
    /// 成功 → 单 commitId 整批撤销材料，Ctrl+Z 一次整体撤销整批（INV-I4）。
    /// </summary>
    public EntryBatchCommitReport CommitBatch(TileWallConfig current, IReadOnlyList<EntryCommitRequest> requests, string actionName)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentException.ThrowIfNullOrEmpty(actionName);
        if (requests.Count == 0)
        {
            throw new ArgumentException("批量导入至少需要一个候选。", nameof(requests));
        }

        // 计划段：对累积配置逐候选规划（同函数同序的重放式排位；零写入）
        var workingConfig = current;
        var planned = new List<PlannedOp>(requests.Count);
        var newObjects = new List<LayoutObject>(requests.Count);
        foreach (var request in requests)
        {
            ArgumentException.ThrowIfNullOrEmpty(request.ObjectId);
            if (workingConfig.Objects.Any(o => o.Id == request.ObjectId))
            {
                throw new InvalidOperationException($"批量导入的对象 Id 重复或已存在：{request.ObjectId}。");
            }

            var (newObject, plannedForRequest) = PlanCommit(workingConfig, request);
            planned.AddRange(plannedForRequest);
            newObjects.Add(newObject);
            workingConfig = workingConfig with { Objects = [.. workingConfig.Objects, newObject] }; // 后续候选的排位基线
        }

        // 步骤 1（预检，纯读零写入）：整批新配置整体过结构校验
        var violations = ConfigValidator.Validate(workingConfig);
        if (violations.Count > 0)
        {
            throw new ConfigValidationException(violations);
        }

        var commitId = StableId.NewId();
        ExecuteProtocol(workingConfig, planned, commitId);
        return new EntryBatchCommitReport(
            workingConfig,
            newObjects,
            new EntryUndoMaterial(commitId, [.. requests.Select(r => r.ObjectId)]));
    }

    /// <summary>
    /// 适配确认提交（M8 设计 §6.6）：N 个入口移除 + 最终配置（草稿墙收敛、保留对象 Bounds 已缩放）单事务。
    /// 计划段（纯读零写入）：对每个移除对象生成 OpRemove（有入口者；纯对象仅从 finalConfig 消失）；
    /// 保留对象入口文件零触碰（只改 Bounds——防御性校验：现存对象的 Entry 相对路径在 finalConfig 中必须不变）。
    /// ConfigValidator.Validate(finalConfig) 通过后一次 ExecuteProtocol（日志含 N 条 remove + 新配置指纹）；
    /// 回滚/崩溃语义与 INV-I2/I3 同源；撤销材料 = EntryUndoMaterial(commitId, 全部移除 Id)，Ctrl+Z 一次整体回滚。
    /// 无入口移除（纯布局收敛）退化为「预检 + Save」，撤销材料 null（§6.5 同款退化，撤销走布局级单槽）。
    /// </summary>
    public EntryCommitReport CommitAdaptation(
        TileWallConfig current,
        IReadOnlyList<string> removeObjectIds,
        TileWallConfig finalConfig,
        string actionName)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(removeObjectIds);
        ArgumentNullException.ThrowIfNull(finalConfig);
        ArgumentException.ThrowIfNullOrEmpty(actionName);

        var planned = new List<PlannedOp>(removeObjectIds.Count);
        var removedObjects = new List<LayoutObject>(removeObjectIds.Count);
        foreach (var objectId in removeObjectIds)
        {
            ArgumentException.ThrowIfNullOrEmpty(objectId);
            var target = current.Objects.FirstOrDefault(o => o.Id == objectId)
                ?? throw new InvalidOperationException($"适配移除失败：对象 {objectId} 不存在。");
            if (finalConfig.Objects.Any(o => o.Id == objectId))
            {
                throw new InvalidOperationException($"适配提交不成立：对象 {objectId} 在移除列表中但仍存在于最终配置。");
            }

            removedObjects.Add(target);
            if (target.Entry is { } entry)
            {
                planned.Add(new PlannedOp(new JournalOp(OpRemove, objectId, entry.RelativePath, null, null, null), null));
            }
        }

        // 防御性校验：保留对象的入口引用不得被适配提交改写（§6.6「保留对象的入口文件零触碰，只改 Bounds」）
        foreach (var retained in finalConfig.Objects)
        {
            var original = current.Objects.FirstOrDefault(o => o.Id == retained.Id);
            if (original is null)
            {
                throw new InvalidOperationException($"适配提交不成立：最终配置含当前配置不存在的对象 {retained.Id}。");
            }

            var originalPath = original.Entry?.RelativePath;
            var retainedPath = retained.Entry?.RelativePath;
            if (!string.Equals(originalPath, retainedPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"适配提交不成立：对象 {retained.Id} 的托管入口被改变（只允许改 Bounds/外观）。");
            }
        }

        var violations = ConfigValidator.Validate(finalConfig);
        if (violations.Count > 0)
        {
            throw new ConfigValidationException(violations);
        }

        if (planned.Count == 0)
        {
            // 退化路径：无入口移除（纯布局收敛）——零入口文件操作（§6.5 同款）
            _store.Save(finalConfig);
            var representative = removedObjects.FirstOrDefault()
                ?? finalConfig.Objects.FirstOrDefault()
                ?? throw new InvalidOperationException("适配提交不成立：移除列表与最终配置均为空。");
            return new EntryCommitReport(finalConfig, representative, null);
        }

        var commitId = StableId.NewId();
        ExecuteProtocol(finalConfig, planned, commitId);
        return new EntryCommitReport(
            finalConfig,
            removedObjects[0],
            new EntryUndoMaterial(commitId, [.. removeObjectIds]));
    }
    /// <summary>取消固定有入口对象（§8.2）：入口 Move → Recovery/…/removed/ + 配置移除对象一次 Save。</summary>
    public EntryCommitReport RemoveEntry(TileWallConfig current, string objectId, string actionName)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentException.ThrowIfNullOrEmpty(actionName);

        var target = current.Objects.FirstOrDefault(o => o.Id == objectId)
            ?? throw new InvalidOperationException($"取消固定失败：对象 {objectId} 不存在。");
        var entryRelativePath = target.Entry?.RelativePath
            ?? throw new InvalidOperationException("该对象没有托管入口，请走纯配置路径（LayoutCommitService）。");

        var planned = new List<PlannedOp>
        {
            new(new JournalOp(OpRemove, objectId, entryRelativePath, null, null, null), null),
        };
        var newConfig = current with { Objects = [.. current.Objects.Where(o => o.Id != objectId)] };
        var violations = ConfigValidator.Validate(newConfig);
        if (violations.Count > 0)
        {
            throw new ConfigValidationException(violations);
        }

        var commitId = StableId.NewId();
        ExecuteProtocol(newConfig, planned, commitId);
        return new EntryCommitReport(newConfig, target, new EntryUndoMaterial(commitId, [objectId]));
    }

    // ————————————————————————————— 计划（步骤 0/1，纯读零写入） —————————————————————————————

    private (LayoutObject NewObject, IReadOnlyList<PlannedOp> Planned) PlanCommit(TileWallConfig current, EntryCommitRequest request)
    {
        var draft = request.Draft;
        var currentObject = current.Objects.FirstOrDefault(o => o.Id == request.ObjectId);
        var isCreate = currentObject is null;
        if (currentObject is GroupObject)
        {
            throw new InvalidOperationException("磁贴组属性提交请走 CommitGroup（M5 设计 §8.3）；Commit 仅受理独立磁贴。");
        }

        var wall = new WallGrid(current.Wall.Columns, current.Wall.Rows);
        var otherRects = current.Objects.Where(o => o.Id != request.ObjectId).Select(o => o.Bounds).ToArray();
        var currentEntryRelativePath = currentObject?.Entry?.RelativePath;
        var currentEntryFullPath = currentEntryRelativePath is null ? null : EntryPaths.Full(_rootPath, currentEntryRelativePath);

        var validation = DraftValidator.Validate(draft, currentObject, currentEntryFullPath, wall, otherRects, _linkFiles);
        if (!validation.IsValid)
        {
            throw new DraftValidationException(validation.Errors); // F-7/F-9/F-10：就地错误码、零写入
        }

        // 尺寸与边界（校验已过：新建用 firstFit；编辑为原点不动的新矩形）
        var bounds = isCreate
            ? validation.SuggestedRect!.Value
            : new GridRect(
                currentObject!.Bounds.Column,
                currentObject.Bounds.Row,
                draft.Size.Columns,
                draft.Size.Rows);

        // 最终名解析（§5.5 步骤 0 的替换类前提）：文字非空 → 该文字；否则现有入口名 → 推导名
        var finalKind = FinalEntryKind(draft, currentEntryRelativePath);
        var finalEntryRelativePath = currentEntryRelativePath;
        var planned = new List<PlannedOp>(1);

        switch (draft.Entry)
        {
            case null:
            case EntryDraft.NoneDraft:
                // 空目标 / 清空链接
                if (currentEntryRelativePath is not null)
                {
                    planned.Add(new PlannedOp(new JournalOp(OpRemove, request.ObjectId, currentEntryRelativePath, null, null, null), null));
                    finalEntryRelativePath = null;
                }

                break;

            case EntryDraft.KeepCurrent:
                finalEntryRelativePath = currentEntryRelativePath is not null
                    ? AppendRenameOp(planned, request.ObjectId, draft.TitleText, currentEntryRelativePath)
                    : null;
                break;

            case EntryDraft.CopyFromFile copy:
                {
                    var finalRel = ResolveFinalRelativePath(request.ObjectId, draft.TitleText, currentEntryRelativePath, copy.SourcePath, targetIsDirectory: false, finalKind);
                    finalEntryRelativePath = finalRel;
                    var stagedName = EntryPaths.FileNameOf(finalRel);
                    planned.Add(new PlannedOp(
                        new JournalOp(currentEntryRelativePath is null ? OpAdd : OpReplace, request.ObjectId, currentEntryRelativePath, finalRel, null, null),
                        stagingDirectory => _files.Copy(copy.SourcePath, StagedFile(stagingDirectory, request.ObjectId, stagedName), overwrite: true)));
                    break;
                }

            case EntryDraft.CreateForPath createForPath:
                {
                    var finalRel = ResolveFinalRelativePath(request.ObjectId, draft.TitleText, currentEntryRelativePath, createForPath.TargetPath, Directory.Exists(createForPath.TargetPath), EntryKind.Lnk);
                    finalEntryRelativePath = finalRel;
                    var workingDirectory = ResolveWorkingDirectory(createForPath.TargetPath);
                    var target = createForPath.TargetPath;
                    var stagedName = EntryPaths.FileNameOf(finalRel);
                    planned.Add(new PlannedOp(
                        new JournalOp(currentEntryRelativePath is null ? OpAdd : OpReplace, request.ObjectId, currentEntryRelativePath, finalRel, null, null),
                        stagingDirectory => _linkFiles.Create(StagedFile(stagingDirectory, request.ObjectId, stagedName), target, arguments: string.Empty, workingDirectory)));
                    break;
                }

            case EntryDraft.CreateFromUrl createFromUrl:
                {
                    var finalRel = ResolveFinalRelativePath(request.ObjectId, draft.TitleText, currentEntryRelativePath, createFromUrl.Url, targetIsDirectory: false, EntryKind.Url);
                    finalEntryRelativePath = finalRel;
                    var content = UrlShortcut.CreateContent(createFromUrl.Url);
                    var stagedName = EntryPaths.FileNameOf(finalRel);
                    planned.Add(new PlannedOp(
                        new JournalOp(currentEntryRelativePath is null ? OpAdd : OpReplace, request.ObjectId, currentEntryRelativePath, finalRel, null, null),
                        stagingDirectory => _files.WriteAllBytes(StagedFile(stagingDirectory, request.ObjectId, stagedName), content)));
                    break;
                }

            case EntryDraft.EditLnkTarget editLnkTarget:
                {
                    // 现入口为普通路径 .lnk（校验已拒 HasIdList/非 .lnk）；正式路径就地 Load→SetPath→Save（探针 C）
                    var oldTarget = _linkFiles.Read(currentEntryFullPath!).TargetPath;
                    planned.Add(new PlannedOp(
                        new JournalOp(OpEdit, request.ObjectId, null, currentEntryRelativePath, oldTarget, editLnkTarget.NewTargetPath),
                        null));
                    // 改名 × 改目标并存（§6.3「修改非空文字在最终保存时执行文件重命名」）：先在旧名上改目标，再就地改名
                    finalEntryRelativePath = AppendRenameOp(planned, request.ObjectId, draft.TitleText, currentEntryRelativePath!);
                    break;
                }

            case EntryDraft.EditUrlLine editUrlLine:
                {
                    // 现入口必须是 .url（DraftValidator 已拒其余形态）；同一提交内随协议走 staging，不就地改正式文件（§5.3）
                    var bytes = _files.ReadAllBytes(currentEntryFullPath!);
                    var newBytes = UrlShortcut.RewriteUrlLine(bytes, editUrlLine.NewUrl);
                    var stagedName = EntryPaths.FileNameOf(currentEntryRelativePath!);
                    planned.Add(new PlannedOp(
                        new JournalOp(OpReplace, request.ObjectId, currentEntryRelativePath, currentEntryRelativePath, null, null),
                        stagingDirectory => _files.WriteAllBytes(StagedFile(stagingDirectory, request.ObjectId, stagedName), newBytes)));
                    // 改名 × 改 URL 行并存：先以新字节覆盖旧名，再就地改名
                    finalEntryRelativePath = AppendRenameOp(planned, request.ObjectId, draft.TitleText, currentEntryRelativePath!);
                    break;
                }
        }

        // 最终名合法性兜底（推导名不过校验 → 就地报错，不静默替换，§5.1）
        if (finalEntryRelativePath is not null)
        {
            var finalBase = EntryNames.BaseNameOf(finalEntryRelativePath);
            var nameErrors = EntryNames.Validate(finalBase);
            if (nameErrors.Count > 0)
            {
                throw new DraftValidationException(nameErrors);
            }
        }

        var newObject = BuildObject(request.ObjectId, bounds, draft, finalEntryRelativePath, currentObject);
        return (newObject, planned);
    }

    private static TileObject BuildObject(
        string objectId,
        GridRect bounds,
        TileDraft draft,
        string? finalEntryRelativePath,
        LayoutObject? currentObject)
    {
        var titleEmpty = string.IsNullOrWhiteSpace(draft.TitleText);
        var visual = finalEntryRelativePath is not null
            ? new ObjectVisual // 有入口：名称真值在托管文件名主体，TitleText 必为 null（§16.2 单一真值）
            {
                ShowTitle = !titleEmpty,
                BackgroundColor = draft.BackgroundColorHex,
                BackgroundImagePath = draft.BackgroundImagePath,
                ForegroundIconPath = draft.ForegroundIconPath,
                TitleText = null,
            }
            : new ObjectVisual // 无入口：直写 TitleText（§7.1 文字草稿）
            {
                ShowTitle = true,
                BackgroundColor = draft.BackgroundColorHex,
                BackgroundImagePath = draft.BackgroundImagePath,
                ForegroundIconPath = draft.ForegroundIconPath,
                TitleText = titleEmpty ? null : draft.TitleText,
            };

        return new TileObject
        {
            Id = objectId,
            Bounds = bounds,
            Entry = finalEntryRelativePath is null ? null : new EntryReference { RelativePath = finalEntryRelativePath },
            Visual = visual,
        };
    }

    private static EntryKind FinalEntryKind(TileDraft draft, string? currentEntryRelativePath) => draft.Entry switch
    {
        null => EntryKind.None,
        EntryDraft.NoneDraft => EntryKind.None,
        EntryDraft.KeepCurrent => currentEntryRelativePath is null
            ? EntryKind.None
            : EntryNames.KindOfRelativePath(currentEntryRelativePath),
        EntryDraft.CopyFromFile copy => EntryNames.KindOfRelativePath(copy.SourcePath),
        EntryDraft.CreateForPath => EntryKind.Lnk,
        EntryDraft.CreateFromUrl => EntryKind.Url,
        EntryDraft.EditLnkTarget => EntryKind.Lnk,
        EntryDraft.EditUrlLine => EntryKind.Url,
        _ => EntryKind.None,
    };

    /// <summary>
    /// 改名 op 生成（§6.3「修改非空文字在最终保存时执行文件重命名」）：
    /// 文字非空且 ≠ 当前主体名 → 追加 rename op 并返回新相对路径；否则原路径原样返回。
    /// 与 EditLnkTarget/EditUrlLine 并存时：生效顺序 edit/replace 先于 rename（先改内容后改名），
    /// 回放逆序恰好先撤销改名再撤销内容修改。M5 起以 titleText 为参（磁贴与组共用）。
    /// </summary>
    private static string? AppendRenameOp(List<PlannedOp> planned, string objectId, string? titleText, string currentEntryRelativePath)
    {
        var finalRelativePath = currentEntryRelativePath;
        if (string.IsNullOrWhiteSpace(titleText))
        {
            return finalRelativePath; // 空白 = 隐藏标题，入口保留原合法名（§6.3）
        }

        var currentBase = EntryNames.BaseNameOf(currentEntryRelativePath);
        if (string.Equals(titleText, currentBase, StringComparison.Ordinal))
        {
            return finalRelativePath;
        }

        finalRelativePath = EntryPaths.EntryRelativePath(
            objectId, EntryNames.CombineName(titleText, EntryNames.KindOfRelativePath(currentEntryRelativePath)));
        planned.Add(new PlannedOp(
            new JournalOp(OpRename, objectId, currentEntryRelativePath, finalRelativePath, null, null), null));
        return finalRelativePath;
    }

    /// <summary>§5.5 步骤 0：替换/新建类直接以 finalName 落 Staging（一步到位，无「先复制旧名再改名」中间态）。</summary>
    private static string ResolveFinalRelativePath(
        string objectId,
        string? titleText,
        string? currentEntryRelativePath,
        string fallbackSource,
        bool targetIsDirectory,
        EntryKind kind)
    {
        var baseName = !string.IsNullOrWhiteSpace(titleText)
            ? titleText
            : currentEntryRelativePath is not null
                ? EntryNames.BaseNameOf(currentEntryRelativePath)
                : EntryNames.SuggestBaseName(fallbackSource, targetIsDirectory);
        return EntryPaths.EntryRelativePath(objectId, EntryNames.CombineName(baseName, kind));
    }

    /// <summary>§5.3：新建 .lnk 的工作目录 = 目标目录（文件夹本体）或目标父目录。</summary>
    private static string ResolveWorkingDirectory(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return string.Empty;
        }

        var trimmed = targetPath.TrimEnd('\\', '/');
        if (Directory.Exists(trimmed))
        {
            return trimmed;
        }

        return Path.GetDirectoryName(trimmed) ?? string.Empty;
    }

    private static IReadOnlyList<LayoutObject> ReplaceById(IReadOnlyList<LayoutObject> objects, LayoutObject replacement) =>
        [.. objects.Select(o => o.Id == replacement.Id ? replacement : o)];

    // ————————————————————————————— 组路径（M5 设计 §8.3；新增私有分支，不改磁贴步骤序列） —————————————————————————————

    /// <summary>组提交计划（步骤 0/1，纯读零写入）：GroupDraftValidator → 锚定/firstFit → 入口计划 → 组对象构造。</summary>
    private (LayoutObject NewObject, IReadOnlyList<PlannedOp> Planned) PlanGroupCommit(TileWallConfig current, GroupCommitRequest request)
    {
        var draft = request.Draft;
        var currentObject = current.Objects.FirstOrDefault(o => o.Id == request.ObjectId);
        if (currentObject is not null and not GroupObject)
        {
            throw new InvalidOperationException($"对象 {request.ObjectId} 已存在且不是磁贴组，组提交请求不成立。");
        }

        var currentGroup = currentObject as GroupObject;
        var wall = new WallGrid(current.Wall.Columns, current.Wall.Rows);
        var otherRects = current.Objects.Where(o => o.Id != request.ObjectId).Select(o => o.Bounds).ToArray();
        var currentEntryRelativePath = currentGroup?.Entry?.RelativePath;
        var currentEntryFullPath = currentEntryRelativePath is null ? null : EntryPaths.Full(_rootPath, currentEntryRelativePath);

        // 步骤 1 预检：组草稿校验（错误 → DraftValidationException，零写入；F-7/F-9/F-10 同语义）
        var validation = GroupDraftValidator.Validate(draft, currentGroup, wall, otherRects);
        if (!validation.IsValid)
        {
            throw new DraftValidationException(validation.Errors);
        }

        // 尺寸与边界（校验已过：新建用 firstFit；编辑为锚定原点的新矩形，§9.1）
        var bounds = currentGroup is null
            ? validation.SuggestedRect!.Value
            : new GridRect(
                currentGroup.Bounds.Column,
                currentGroup.Bounds.Row,
                draft.Size.Columns,
                draft.Size.Rows);

        var planned = new List<PlannedOp>(1);
        var finalEntryRelativePath = PlanGroupEntries(request.ObjectId, draft, currentEntryRelativePath, currentEntryFullPath, planned);

        // 最终名合法性兜底（推导名不过校验 → 就地报错，不静默替换，§5.1）
        if (finalEntryRelativePath is not null)
        {
            var nameErrors = EntryNames.Validate(EntryNames.BaseNameOf(finalEntryRelativePath));
            if (nameErrors.Count > 0)
            {
                throw new DraftValidationException(nameErrors);
            }
        }

        var newObject = BuildGroupObject(request.ObjectId, bounds, draft, finalEntryRelativePath);
        return (newObject, planned);
    }

    /// <summary>组入口草稿计划（与磁贴 PlanCommit 同一套机制：remove→rename→staging 替换；顺序与回滚语义逐字一致）。</summary>
    private string? PlanGroupEntries(
        string objectId,
        GroupEditDraft draft,
        string? currentEntryRelativePath,
        string? currentEntryFullPath,
        List<PlannedOp> planned)
    {
        switch (draft.Entry)
        {
            case null:
            case EntryDraft.NoneDraft:
                // 空目标 / 清空链接
                if (currentEntryRelativePath is not null)
                {
                    planned.Add(new PlannedOp(new JournalOp(OpRemove, objectId, currentEntryRelativePath, null, null, null), null));
                    return null;
                }

                return null;

            case EntryDraft.KeepCurrent:
                return currentEntryRelativePath is not null
                    ? AppendRenameOp(planned, objectId, draft.TitleText, currentEntryRelativePath)
                    : null;

            case EntryDraft.CopyFromFile copy:
                {
                    var finalKind = EntryNames.KindOfRelativePath(copy.SourcePath);
                    var finalRel = ResolveFinalRelativePath(objectId, draft.TitleText, currentEntryRelativePath, copy.SourcePath, targetIsDirectory: false, finalKind);
                    var stagedName = EntryPaths.FileNameOf(finalRel);
                    planned.Add(new PlannedOp(
                        new JournalOp(currentEntryRelativePath is null ? OpAdd : OpReplace, objectId, currentEntryRelativePath, finalRel, null, null),
                        stagingDirectory => _files.Copy(copy.SourcePath, StagedFile(stagingDirectory, objectId, stagedName), overwrite: true)));
                    return finalRel;
                }

            case EntryDraft.CreateForPath createForPath:
                {
                    var finalRel = ResolveFinalRelativePath(objectId, draft.TitleText, currentEntryRelativePath, createForPath.TargetPath, Directory.Exists(createForPath.TargetPath), EntryKind.Lnk);
                    var workingDirectory = ResolveWorkingDirectory(createForPath.TargetPath);
                    var stagedName = EntryPaths.FileNameOf(finalRel);
                    planned.Add(new PlannedOp(
                        new JournalOp(currentEntryRelativePath is null ? OpAdd : OpReplace, objectId, currentEntryRelativePath, finalRel, null, null),
                        stagingDirectory => _linkFiles.Create(StagedFile(stagingDirectory, objectId, stagedName), createForPath.TargetPath, arguments: string.Empty, workingDirectory)));
                    return finalRel;
                }

            case EntryDraft.CreateFromUrl createFromUrl:
                {
                    var finalRel = ResolveFinalRelativePath(objectId, draft.TitleText, currentEntryRelativePath, createFromUrl.Url, targetIsDirectory: false, EntryKind.Url);
                    var content = UrlShortcut.CreateContent(createFromUrl.Url);
                    var stagedName = EntryPaths.FileNameOf(finalRel);
                    planned.Add(new PlannedOp(
                        new JournalOp(currentEntryRelativePath is null ? OpAdd : OpReplace, objectId, currentEntryRelativePath, finalRel, null, null),
                        stagingDirectory => _files.WriteAllBytes(StagedFile(stagingDirectory, objectId, stagedName), content)));
                    return finalRel;
                }

            case EntryDraft.EditLnkTarget editLnkTarget:
                {
                    // 现入口为普通路径 .lnk（校验已拒 HasIdList/非 .lnk）；正式路径就地 Load→SetTarget→Save（探针 C）
                    var oldTarget = _linkFiles.Read(currentEntryFullPath!).TargetPath;
                    planned.Add(new PlannedOp(
                        new JournalOp(OpEdit, objectId, null, currentEntryRelativePath, oldTarget, editLnkTarget.NewTargetPath),
                        null));
                    // 改名 × 改目标并存：先在旧名上改目标，再就地改名（生效顺序 edit 先于 rename）
                    return AppendRenameOp(planned, objectId, draft.TitleText, currentEntryRelativePath!);
                }

            case EntryDraft.EditUrlLine editUrlLine:
                {
                    // 现入口必须是 .url（校验已拒其余形态）；同一提交内随协议走 staging，不就地改正式文件（§5.3）
                    var bytes = _files.ReadAllBytes(currentEntryFullPath!);
                    var newBytes = UrlShortcut.RewriteUrlLine(bytes, editUrlLine.NewUrl);
                    var stagedName = EntryPaths.FileNameOf(currentEntryRelativePath!);
                    planned.Add(new PlannedOp(
                        new JournalOp(OpReplace, objectId, currentEntryRelativePath, currentEntryRelativePath, null, null),
                        stagingDirectory => _files.WriteAllBytes(StagedFile(stagingDirectory, objectId, stagedName), newBytes)));
                    // 改名 × 改 URL 行并存：先以新字节覆盖旧名，再就地改名
                    return AppendRenameOp(planned, objectId, draft.TitleText, currentEntryRelativePath!);
                }

            default:
                return currentEntryRelativePath;
        }
    }

    /// <summary>组对象构造（§8.3 步骤 1）：单一真值规则与磁贴同构（有入口 → TitleText=null）；纯色 Q6 即时映射 BackgroundColor。</summary>
    private static GroupObject BuildGroupObject(
        string objectId,
        GridRect bounds,
        GroupEditDraft draft,
        string? finalEntryRelativePath)
    {
        var titleEmpty = string.IsNullOrWhiteSpace(draft.TitleText);
        var visual = new ObjectVisual
        {
            ShowTitle = finalEntryRelativePath is not null ? !titleEmpty : true,
            TitleText = finalEntryRelativePath is null && !titleEmpty ? draft.TitleText : null,
            BackgroundColor = draft.Backdrop == BackdropKind.SolidColor ? draft.BackdropColorHex : null,
            Backdrop = draft.Backdrop,
        };

        return new GroupObject
        {
            Id = objectId,
            Bounds = bounds,
            Partitions = [.. draft.Partitions],
            Visual = visual,
            Entry = finalEntryRelativePath is null ? null : new EntryReference { RelativePath = finalEntryRelativePath },
            Images = draft.Images,
            Carousel = draft.Carousel,
        };
    }

    // ————————————————————————————— 协议（步骤 2–7） —————————————————————————————

    /// <summary>
    /// 暂存文件路径：Staging/&lt;commitId&gt;/&lt;objectId&gt;/&lt;名&gt;——按对象隔离（M8 §5.6 INV-I6）。
    /// 批量提交（CommitBatch）中同名候选各自 Objects/&lt;id&gt;/ 生效，扁平暂存会互相覆盖 → 以 objectId 分目录；
    /// objectId 为空（理论不可达）退化为扁平名，M4 单提交语义不变。
    /// </summary>
    private static string StagedFile(string stagingDirectory, string objectId, string fileName) =>
        Path.Combine(stagingDirectory, objectId ?? string.Empty, fileName);

    private void ExecuteProtocol(TileWallConfig newConfig, IReadOnlyList<PlannedOp> planned, string commitId)
    {
        var stagingDirectory = EntryPaths.StagingDir(_rootPath, commitId);
        var recoveryDirectory = EntryPaths.RecoveryEntriesDir(_rootPath, commitId);
        var stagingJournalPath = Path.Combine(stagingDirectory, CommitJournalFile.StagingFileName);
        try
        {
            // 步骤 2：Staging——新入口文件全部生成到暂存区；失败 → 清暂存，正式区零改动。
            // M8：暂存按对象分目录（Staging/<commitId>/<objectId>/<名>），批量提交同名候选互不覆盖
            _files.CreateDirectory(_rootPath);
            _files.CreateDirectory(EntryPaths.StagingRoot(_rootPath));
            _files.CreateDirectory(stagingDirectory);
            foreach (var op in planned)
            {
                if (op.StageIntoStaging is null)
                {
                    continue;
                }

                _files.CreateDirectory(Path.Combine(stagingDirectory, op.Journal.ObjectId));
                op.StageIntoStaging(stagingDirectory);
            }

            // 步骤 3：提交日志落盘（先于一切正式区操作——无日志即必然未动过正式区）。
            // 记录新配置指纹：Sweep 据此区分「配置已生效（已完成）」与「配置未动（需回滚）」，
            // 覆盖 add/同路径 replace 等引用存在性无法判定的崩溃窗口（§5.6、评审⑥）
            var journal = new CommitJournal(commitId, [.. planned.Select(p => p.Journal)], CommitJournalFile.Fingerprint(newConfig));
            CommitJournalFile.Write(_files, stagingJournalPath, journal);

            // 步骤 4：备份将被覆盖/改名/删除的现有入口（Copy：正式区零改动、可重入）
            _files.CreateDirectory(Path.Combine(_rootPath, EntryPaths.RecoveryDirName));
            _files.CreateDirectory(EntryPaths.RecoveryEntriesRoot(_rootPath));
            _files.CreateDirectory(recoveryDirectory);
            BackupOldEntries(planned, recoveryDirectory);

            // 步骤 5：入口生效（顺序 add → replace → edit → rename → remove）
            ApplyEntries(planned, stagingDirectory, recoveryDirectory);

            // 步骤 6：配置生效（唯一权威提交点；此后崩溃由 Sweep 一致性检测识别为「已完成」）
            _store.Save(newConfig);
        }
        catch
        {
            // 失败路径与崩溃清扫共用同一份幂等回滚例程（§5.6「同一份代码、两处调用」）
            // 仅覆盖步骤 2–6：配置尚未生效，回滚入口即回到操作前一致态
            _recovery.Rollback(commitId);
            throw;
        }

        // 步骤 7：收尾（配置已生效——此后任何失败不得回滚入口，残留由下次启动 Sweep 清理，F-6 窗口）
        try
        {
            _files.Move(stagingJournalPath, Path.Combine(recoveryDirectory, CommitJournalFile.UndoFileName), overwrite: true);
        }
        catch (IOException)
        {
            // 日志未移交：保留暂存区原样（含日志），Sweep 按「已完成提交」清理；材料缺日志 → 撤销安全中止（F-12）
            return;
        }

        _files.DeleteDirectory(stagingDirectory); // 尽力而为：残余目录不影响一致性（Sweep 兜底）
    }

    /// <summary>回滚材料内备份路径：&lt;commitId&gt;/&lt;objectId&gt;/[removed/]&lt;名&gt;（M8 按对象隔离；与 EntryRecovery 回放查找同构）。</summary>
    private static string BackupPathOf(string recoveryDirectory, JournalOp op, bool isRemove) =>
        isRemove
            ? Path.Combine(recoveryDirectory, op.ObjectId, EntryPaths.RemovedDirName, EntryPaths.FileNameOf(op.From!))
            : Path.Combine(recoveryDirectory, op.ObjectId, EntryPaths.FileNameOf(op.From!));

    private void BackupOldEntries(IReadOnlyList<PlannedOp> planned, string recoveryDirectory)
    {
        var backedUp = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in planned)
        {
            if (op.Journal.From is null)
            {
                continue; // add/edit 无旧文件可备份（edit 由日志 OldTarget 语义回滚）
            }

            var source = EntryPaths.Full(_rootPath, op.Journal.From);
            if (!_files.Exists(source))
            {
                continue; // 旧入口本就缺失：视为已清理，生效阶段同样跳过
            }

            // M8：备份按对象分目录（Recovery/Entries/<commitId>/<objectId>/…）——批量提交中
            // 同名条目互不覆盖（CommitBatch 多 add / CommitAdaptation 多 remove 的撤销材料各自成立）
            var destination = BackupPathOf(recoveryDirectory, op.Journal, isRemove: op.Journal.Kind == OpRemove);
            if (!backedUp.Add(destination))
            {
                continue;
            }

            _files.CreateDirectory(Path.GetDirectoryName(destination)!);
            _files.Copy(source, destination, overwrite: true);
        }
    }

    private void ApplyEntries(IReadOnlyList<PlannedOp> planned, string stagingDirectory, string recoveryDirectory)
    {
        foreach (var op in OrderForApply(planned))
        {
            switch (op.Journal.Kind)
            {
                case OpAdd:
                case OpReplace:
                    var destination = EntryPaths.Full(_rootPath, op.Journal.To!);
                    _files.CreateDirectory(Path.GetDirectoryName(destination)!); // 真实文件系统：对象目录可能尚不存在（新建首入口）
                    _files.Move(
                        StagedFile(stagingDirectory, op.Journal.ObjectId, EntryPaths.FileNameOf(op.Journal.To!)),
                        destination,
                        overwrite: true);
                    if (op.Journal.Kind == OpReplace
                        && op.Journal.From is not null
                        && !string.Equals(op.Journal.From, op.Journal.To, StringComparison.Ordinal))
                    {
                        // 类型切换：旧入口移入 Recovery（备份副本已在步骤 4 就位，Move 覆盖为同内容）
                        var oldPath = EntryPaths.Full(_rootPath, op.Journal.From);
                        if (_files.Exists(oldPath))
                        {
                            _files.Move(oldPath, BackupPathOf(recoveryDirectory, op.Journal, isRemove: false), overwrite: true);
                        }
                    }

                    break;

                case OpRename:
                    _files.Move(
                        EntryPaths.Full(_rootPath, op.Journal.From!),
                        EntryPaths.Full(_rootPath, op.Journal.To!),
                        overwrite: false); // 扩展名不变的就地改名；目标名必不存在（INV-E1）
                    break;

                case OpRemove:
                    var removeSource = EntryPaths.Full(_rootPath, op.Journal.From!);
                    if (_files.Exists(removeSource))
                    {
                        _files.CreateDirectory(Path.GetDirectoryName(BackupPathOf(recoveryDirectory, op.Journal, isRemove: true))!);
                        _files.Move(removeSource, BackupPathOf(recoveryDirectory, op.Journal, isRemove: true), overwrite: true);
                    }

                    break;

                case OpEdit:
                    _linkFiles.EditTargetOnly(EntryPaths.Full(_rootPath, op.Journal.To!), op.Journal.NewTarget!);
                    break;
            }
        }
    }

    private static IEnumerable<PlannedOp> OrderForApply(IReadOnlyList<PlannedOp> planned) =>
        [.. planned
            .Select((op, index) => (Op: op, Index: index))
            .OrderBy(pair => RankOf(pair.Op.Journal.Kind))
            .ThenBy(pair => pair.Index)
            .Select(pair => pair.Op)];

    private static int RankOf(string kind) => kind switch
    {
        OpAdd => 0,
        OpReplace => 1,
        OpEdit => 2,    // edit 先于 rename：改名×改目标并存时先在旧名上改内容再改名（回放逆序成立）
        OpRename => 3,
        OpRemove => 4,
        _ => 5,
    };
}
