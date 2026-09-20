using System.Text.Json;
using TileWall.Core.Configuration;

namespace TileWall.Core.Entries;

/// <summary>清扫动作说明（UIA/状态提示与测试断言用；空列表 = 无待处理残留）。</summary>
public sealed record RecoveryReport(IReadOnlyList<string> Actions);

/// <summary>
/// 崩溃清扫与幂等回滚（M4 设计 §5.6）。启动序列在 store.Load() 之前 Sweep：
/// <list type="bullet">
/// <item>Staging/&lt;id&gt;/ 有 commit.json：配置「加载有效且全部入口存在」→ 判定提交已完成，仅清暂存与材料；
/// 否则按日志逆序幂等回滚（F-5/F-6 两个崩溃窗口的唯一分界）。</item>
/// <item>Staging/&lt;id&gt;/ 无 commit.json：步骤 2-3 之间崩溃的纯残留，仅删目录（日志先于一切入口操作落盘）。</item>
/// <item>Recovery/Entries/ 下过期材料：撤销不跨会话，启动时撤销槽为空 → 全部清理。</item>
/// </list>
/// <see cref="Rollback"/> 同时是提交步骤 5/6 失败路径的同步回滚例程——同一份重放代码、两处触发。
/// </summary>
public sealed class EntryRecovery
{
    private readonly IFileStore _files;
    private readonly ILnkFileService _linkFiles;
    private readonly string _rootPath;

    public EntryRecovery(IFileStore files, ILnkFileService linkFiles, IDataDirectory directories)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(linkFiles);
        ArgumentNullException.ThrowIfNull(directories);
        _files = files;
        _linkFiles = linkFiles;
        _rootPath = directories.RootPath;
    }

    public RecoveryReport Sweep()
    {
        var actions = new List<string>();

        foreach (var stagingDirectory in _files.ListDirectories(EntryPaths.StagingRoot(_rootPath)))
        {
            var commitId = Path.GetFileName(stagingDirectory);
            var stagingJournalPath = Path.Combine(stagingDirectory, CommitJournalFile.StagingFileName);
            if (!_files.Exists(stagingJournalPath))
            {
                _files.DeleteDirectory(stagingDirectory);
                actions.Add($"已清理无日志暂存区 {commitId}（步骤 2-3 之间中断，正式区未动）。");
                continue;
            }

            if (!CommitJournalFile.TryRead(_files, stagingJournalPath, out var journal) || journal is null)
            {
                _files.DeleteDirectory(stagingDirectory);
                actions.Add($"暂存区 {commitId} 的提交日志不可读，仅清理残留（未回滚——无日志依据不动正式区）。");
                continue;
            }

            // 完成判定：日志记有新配置指纹 → 当前 config.json 指纹与之相等 = 步骤 6 已生效（F-6），
            // 不等 = 配置未动、需回滚（F-5，含 add 新入口孤儿与 From==To replace 的半提交场景）。
            // 无指纹（手写/旧日志）时回退到「配置有效且全部入口存在」判定。
            var completed = journal.ConfigFingerprint is not null
                ? string.Equals(CurrentConfigFingerprint(), journal.ConfigFingerprint, StringComparison.Ordinal)
                : IsConfigConsistentWithEntries();
            if (completed)
            {
                _files.DeleteDirectory(stagingDirectory);
                _files.DeleteDirectory(EntryPaths.RecoveryEntriesDir(_rootPath, commitId));
                actions.Add($"提交 {commitId} 已完成配置生效（配置指纹与日志一致），仅清理暂存与回滚材料。");
            }
            else
            {
                ReplayReverse(journal!, EntryPaths.RecoveryEntriesDir(_rootPath, commitId));
                _files.DeleteDirectory(stagingDirectory);
                _files.DeleteDirectory(EntryPaths.RecoveryEntriesDir(_rootPath, commitId));
                actions.Add($"提交 {commitId} 未完成，已按提交日志幂等回滚（设计 §16.3 崩溃恢复）。");
            }
        }

        foreach (var materialDirectory in _files.ListDirectories(EntryPaths.RecoveryEntriesRoot(_rootPath)))
        {
            _files.DeleteDirectory(materialDirectory);
            actions.Add($"已清理过期撤销材料 {Path.GetFileName(materialDirectory)}（撤销不跨会话，§7.1）。");
        }

        return new RecoveryReport(actions);
    }

    /// <summary>按提交日志幂等回滚（逐条逆序；重复执行结果一致）。结束后暂存区与回滚材料一并删除。</summary>
    public void Rollback(string commitId)
    {
        var stagingDirectory = EntryPaths.StagingDir(_rootPath, commitId);
        var materialDirectory = EntryPaths.RecoveryEntriesDir(_rootPath, commitId);
        if (CommitJournalFile.TryRead(_files, Path.Combine(stagingDirectory, CommitJournalFile.StagingFileName), out var journal)
            && journal is not null)
        {
            ReplayReverse(journal, materialDirectory);
        }

        _files.DeleteDirectory(stagingDirectory);
        _files.DeleteDirectory(materialDirectory);
    }

    /// <summary>
    /// 撤销的文件还原半步（§8.3）：按材料日志逆序把入口文件恢复到提交前状态；
    /// 只动文件不动配置——配置回滚由调用方经 LayoutCommitService 提交 slot.Previous 完成。
    /// 返回 false = 材料缺失/不可读/还原中断，调用方必须中止撤销（宁可不撤销，不留混合态）。
    /// </summary>
    public bool RestoreMaterial(EntryUndoMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        var materialDirectory = EntryPaths.RecoveryEntriesDir(_rootPath, material.CommitId);
        if (!CommitJournalFile.TryRead(_files, Path.Combine(materialDirectory, CommitJournalFile.UndoFileName), out var journal)
            || journal is null)
        {
            return false;
        }

        try
        {
            ReplayReverse(journal, materialDirectory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return false; // F-12：还原失败 → 中止撤销、保留现状（已还原部分不追回，材料保留供重试）
        }
    }

    /// <summary>删除一份撤销材料（新提交覆盖单槽时调用；目录不存在时静默）。</summary>
    public void DeleteMaterial(string commitId) =>
        _files.DeleteDirectory(EntryPaths.RecoveryEntriesDir(_rootPath, commitId));

    /// <summary>
    /// 日志逆序重放（Rollback / RestoreMaterial / Sweep 共用的同一份代码）：
    /// add → 删新文件；replace → 从备份还原旧文件、删新路径；rename → 新名移回旧名；
    /// remove → 从 removed/ 还原；edit → 恢复旧目标。逐条做存在性检查，重复执行结果一致。
    /// </summary>
    private void ReplayReverse(CommitJournal journal, string materialDirectory)
    {
        for (var i = journal.Ops.Count - 1; i >= 0; i--)
        {
            var op = journal.Ops[i];
            switch (op.Kind)
            {
                case "add":
                    DeleteIfExists(EntryPaths.Full(_rootPath, op.To!));
                    break;

                case "replace":
                    if (op.From is not null)
                    {
                        var backupPath = Path.Combine(materialDirectory, EntryPaths.FileNameOf(op.From));
                        if (_files.Exists(backupPath))
                        {
                            _files.Copy(backupPath, EntryPaths.Full(_rootPath, op.From), overwrite: true);
                        }
                    }

                    if (!string.Equals(op.From, op.To, StringComparison.Ordinal))
                    {
                        DeleteIfExists(EntryPaths.Full(_rootPath, op.To!));
                    }

                    break;

                case "rename":
                    var renamedPath = EntryPaths.Full(_rootPath, op.To!);
                    var originalPath = EntryPaths.Full(_rootPath, op.From!);
                    if (_files.Exists(renamedPath) && !_files.Exists(originalPath))
                    {
                        _files.Move(renamedPath, originalPath, overwrite: false);
                    }

                    break;

                case "remove":
                    var removedCopy = Path.Combine(materialDirectory, EntryPaths.RemovedDirName, EntryPaths.FileNameOf(op.From!));
                    if (_files.Exists(removedCopy))
                    {
                        _files.Copy(removedCopy, EntryPaths.Full(_rootPath, op.From!), overwrite: true);
                    }

                    break;

                case "edit":
                    var editedPath = EntryPaths.Full(_rootPath, op.To!);
                    if (_files.Exists(editedPath))
                    {
                        _linkFiles.EditTargetOnly(editedPath, op.OldTarget!); // 语义恢复：目标回到旧值（探针 C 语义）
                    }

                    break;
            }
        }
    }

    /// <summary>当前 config.json 的指纹；缺失 → null（与任何日志指纹不等 → 判未完成回滚）。</summary>
    private string? CurrentConfigFingerprint()
    {
        var configPath = Path.Combine(_rootPath, ConfigStore.ConfigFileName);
        if (!_files.Exists(configPath))
        {
            return null;
        }

        return CommitJournalFile.FingerprintOfBytes(_files.ReadAllBytes(configPath));
    }

    /// <summary>「配置加载有效且全部入口存在」（§5.6 一致性检测；config 缺失/损坏/校验失败一律判不一致 → 回滚）。</summary>
    private bool IsConfigConsistentWithEntries()
    {
        var configPath = Path.Combine(_rootPath, ConfigStore.ConfigFileName);
        if (!_files.Exists(configPath))
        {
            return false;
        }

        try
        {
            var config = ConfigJson.Deserialize(_files.ReadAllBytes(configPath));
            if (ConfigValidator.Validate(config).Count > 0)
            {
                return false;
            }

            return config.Objects.All(o => o.Entry is null
                || _files.Exists(EntryPaths.Full(_rootPath, o.Entry.RelativePath)));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    private void DeleteIfExists(string path)
    {
        if (_files.Exists(path))
        {
            _files.Delete(path);
        }
    }
}
