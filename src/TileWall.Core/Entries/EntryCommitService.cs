using TileWall.Core.Configuration;
using TileWall.Core.Grid;

namespace TileWall.Core.Entries;

/// <summary>联合提交请求（M4 设计 §5.5）。新建时 <paramref name="ObjectId"/> 由调用方经 StableId.NewId() 生成。</summary>
public sealed record EntryCommitRequest(string ObjectId, string ActionName, TileDraft Draft);

/// <summary>撤销槽的入口材料（§8.3）：指向 Recovery/Entries/&lt;commitId&gt;/ 的日志与备份；撤销不跨会话。</summary>
public sealed record EntryUndoMaterial(string CommitId, IReadOnlyList<string> ObjectIds);

/// <summary>
/// 提交结果：新配置（调用方 Adopt 进 LayoutCommitService 前移 Current 并建撤销槽）+ 撤销材料
/// （无入口操作的退化提交为 null——「只改外观/文字不重写入口」，§6.5）。
/// </summary>
public sealed record EntryCommitReport(TileWallConfig NewConfig, LayoutObject CommittedObject, EntryUndoMaterial? UndoMaterial);

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

    /// <summary>撤销的文件还原半步与材料删除的透传（§8.3；实现在 <see cref="EntryRecovery"/>）。</summary>
    public bool RestoreMaterial(EntryUndoMaterial material) => _recovery.RestoreMaterial(material);

    public void DeleteMaterial(string commitId) => _recovery.DeleteMaterial(commitId);

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
            throw new NotSupportedException("磁贴组属性窗不在 M4 范围（M4 设计 §11 风险 9）；组仅支持取消固定与启动类操作。");
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
                    ? AppendRenameOp(planned, request.ObjectId, draft, currentEntryRelativePath)
                    : null;
                break;

            case EntryDraft.CopyFromFile copy:
                {
                    var finalRel = ResolveFinalRelativePath(request.ObjectId, draft, currentEntryRelativePath, copy.SourcePath, targetIsDirectory: false, finalKind);
                    finalEntryRelativePath = finalRel;
                    var stagedName = EntryPaths.FileNameOf(finalRel);
                    planned.Add(new PlannedOp(
                        new JournalOp(currentEntryRelativePath is null ? OpAdd : OpReplace, request.ObjectId, currentEntryRelativePath, finalRel, null, null),
                        stagingDirectory => _files.Copy(copy.SourcePath, Path.Combine(stagingDirectory, stagedName), overwrite: true)));
                    break;
                }

            case EntryDraft.CreateForPath createForPath:
                {
                    var finalRel = ResolveFinalRelativePath(request.ObjectId, draft, currentEntryRelativePath, createForPath.TargetPath, Directory.Exists(createForPath.TargetPath), EntryKind.Lnk);
                    finalEntryRelativePath = finalRel;
                    var workingDirectory = ResolveWorkingDirectory(createForPath.TargetPath);
                    var target = createForPath.TargetPath;
                    var stagedName = EntryPaths.FileNameOf(finalRel);
                    planned.Add(new PlannedOp(
                        new JournalOp(currentEntryRelativePath is null ? OpAdd : OpReplace, request.ObjectId, currentEntryRelativePath, finalRel, null, null),
                        stagingDirectory => _linkFiles.Create(Path.Combine(stagingDirectory, stagedName), target, arguments: string.Empty, workingDirectory)));
                    break;
                }

            case EntryDraft.CreateFromUrl createFromUrl:
                {
                    var finalRel = ResolveFinalRelativePath(request.ObjectId, draft, currentEntryRelativePath, createFromUrl.Url, targetIsDirectory: false, EntryKind.Url);
                    finalEntryRelativePath = finalRel;
                    var content = UrlShortcut.CreateContent(createFromUrl.Url);
                    var stagedName = EntryPaths.FileNameOf(finalRel);
                    planned.Add(new PlannedOp(
                        new JournalOp(currentEntryRelativePath is null ? OpAdd : OpReplace, request.ObjectId, currentEntryRelativePath, finalRel, null, null),
                        stagingDirectory => _files.WriteAllBytes(Path.Combine(stagingDirectory, stagedName), content)));
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
                    finalEntryRelativePath = AppendRenameOp(planned, request.ObjectId, draft, currentEntryRelativePath!);
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
                        stagingDirectory => _files.WriteAllBytes(Path.Combine(stagingDirectory, stagedName), newBytes)));
                    // 改名 × 改 URL 行并存：先以新字节覆盖旧名，再就地改名
                    finalEntryRelativePath = AppendRenameOp(planned, request.ObjectId, draft, currentEntryRelativePath!);
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
    /// 回放逆序恰好先撤销改名再撤销内容修改。
    /// </summary>
    private static string? AppendRenameOp(List<PlannedOp> planned, string objectId, TileDraft draft, string currentEntryRelativePath)
    {
        var finalRelativePath = currentEntryRelativePath;
        if (string.IsNullOrWhiteSpace(draft.TitleText))
        {
            return finalRelativePath; // 空白 = 隐藏标题，入口保留原合法名（§6.3）
        }

        var currentBase = EntryNames.BaseNameOf(currentEntryRelativePath);
        if (string.Equals(draft.TitleText, currentBase, StringComparison.Ordinal))
        {
            return finalRelativePath;
        }

        finalRelativePath = EntryPaths.EntryRelativePath(
            objectId, EntryNames.CombineName(draft.TitleText, EntryNames.KindOfRelativePath(currentEntryRelativePath)));
        planned.Add(new PlannedOp(
            new JournalOp(OpRename, objectId, currentEntryRelativePath, finalRelativePath, null, null), null));
        return finalRelativePath;
    }

    /// <summary>§5.5 步骤 0：替换/新建类直接以 finalName 落 Staging（一步到位，无「先复制旧名再改名」中间态）。</summary>
    private static string ResolveFinalRelativePath(
        string objectId,
        TileDraft draft,
        string? currentEntryRelativePath,
        string fallbackSource,
        bool targetIsDirectory,
        EntryKind kind)
    {
        var baseName = !string.IsNullOrWhiteSpace(draft.TitleText)
            ? draft.TitleText
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

    // ————————————————————————————— 协议（步骤 2–7） —————————————————————————————

    private void ExecuteProtocol(TileWallConfig newConfig, IReadOnlyList<PlannedOp> planned, string commitId)
    {
        var stagingDirectory = EntryPaths.StagingDir(_rootPath, commitId);
        var recoveryDirectory = EntryPaths.RecoveryEntriesDir(_rootPath, commitId);
        var stagingJournalPath = Path.Combine(stagingDirectory, CommitJournalFile.StagingFileName);
        try
        {
            // 步骤 2：Staging——新入口文件全部生成到暂存区；失败 → 清暂存，正式区零改动
            _files.CreateDirectory(_rootPath);
            _files.CreateDirectory(EntryPaths.StagingRoot(_rootPath));
            _files.CreateDirectory(stagingDirectory);
            foreach (var op in planned)
            {
                op.StageIntoStaging?.Invoke(stagingDirectory);
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

            var destination = op.Journal.Kind == OpRemove
                ? Path.Combine(recoveryDirectory, EntryPaths.RemovedDirName, EntryPaths.FileNameOf(op.Journal.From))
                : Path.Combine(recoveryDirectory, EntryPaths.FileNameOf(op.Journal.From));
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
                        Path.Combine(stagingDirectory, EntryPaths.FileNameOf(op.Journal.To!)),
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
                            _files.Move(oldPath, Path.Combine(recoveryDirectory, EntryPaths.FileNameOf(op.Journal.From)), overwrite: true);
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
                        _files.CreateDirectory(Path.Combine(recoveryDirectory, EntryPaths.RemovedDirName));
                        _files.Move(
                            removeSource,
                            Path.Combine(recoveryDirectory, EntryPaths.RemovedDirName, EntryPaths.FileNameOf(op.Journal.From!)),
                            overwrite: true);
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
