using TileWall.Core.Configuration;
using TileWall.Core.Entries;

namespace TileWall.Shell;

/// <summary>单槽撤销记录：配置 record 不可变 → 快照 = 引用（M3 设计 §7.1）。</summary>
public sealed record WallUndoSlot
{
    public required TileWallConfig Previous { get; init; }

    public required string ActionName { get; init; }

    /// <summary>M4 §8.3：指向 Recovery/Entries/&lt;commitId&gt;/ 的回滚材料；null = 纯布局级撤销。</summary>
    public EntryUndoMaterial? EntryMaterial { get; init; }
}

/// <summary>
/// 一切「改布局」的唯一通道（M3 设计 §5.5）：
/// Commit = 捕获单槽撤销 → ConfigStore.Save（校验失败零落盘上抛，ConfigStore.cs:130-166）→
/// 成功才替换当前配置。失败时当前配置不动，天然满足设计 §5.4「保存失败恢复上一有效布局」。
/// 撤销仅单槽、仅布局级、不跨会话、无重做（§7.1/§7.3）。
/// </summary>
public sealed class LayoutCommitService
{
    private readonly ConfigStore _store;

    public LayoutCommitService(ConfigStore store, TileWallConfig initialConfig)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(initialConfig);
        _store = store;
        Current = initialConfig;
    }

    /// <summary>当前生效配置（渲染与手势基线的唯一事实源）。</summary>
    public TileWallConfig Current { get; private set; }

    /// <summary>最近一次布局级提交的撤销槽；null = 槽空（「没有可撤销的操作」）。</summary>
    public WallUndoSlot? UndoSlot { get; private set; }

    /// <summary>
    /// M4：联合提交（EntryCommitService）成功后的接管——配置已由协议落盘（§5.5 步骤 6），
    /// 此处仅前移 Current 并建立带入口材料的撤销槽（§8.3）。
    /// </summary>
    public void Adopt(TileWallConfig newConfig, string actionName, EntryUndoMaterial? entryMaterial)
    {
        ArgumentNullException.ThrowIfNull(newConfig);
        ArgumentException.ThrowIfNullOrEmpty(actionName);
        UndoSlot = new WallUndoSlot { Previous = Current, ActionName = actionName, EntryMaterial = entryMaterial };
        Current = newConfig;
    }

    /// <summary>尝试提交：成功 true 且 Current 已替换；失败 false 且 Current 不动、槽清空。</summary>
    public bool Commit(TileWallConfig newConfig, string actionName, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(newConfig);
        ArgumentException.ThrowIfNullOrEmpty(actionName);

        UndoSlot = new WallUndoSlot { Previous = Current, ActionName = actionName };
        try
        {
            _store.Save(newConfig);
        }
        catch (Exception ex) // ConfigValidationException 与 IO 失败同路：磁盘零改动，当前配置保持原状
        {
            UndoSlot = null;
            failure = ex.Message;
            return false;
        }

        Current = newConfig;
        failure = null;
        return true;
    }

    /// <summary>槽空 → false（调用方提示「没有可撤销的操作」）；有 → 取出 Previous（不清槽，提交成功后由调用方清）。</summary>
    public bool TryPeekUndo(out WallUndoSlot slot)
    {
        if (UndoSlot is { } s)
        {
            slot = s;
            return true;
        }

        slot = null!;
        return false;
    }

    /// <summary>
    /// M5：非布局状态（轮播基准等）的静默持久化缝隙（M5 设计 §9.5）——Save 成功才前移 Current，
    /// 不触碰撤销槽（切图不是布局操作：不得覆盖用户待撤销项，也不得产生可 Ctrl+Z 的「操作」）。
    /// 失败 → false、Current 不动、槽不动（M2 既有 ConfigStore 原子语义：失败保旧）。
    /// </summary>
    public bool SaveWithoutUndo(TileWallConfig newConfig, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(newConfig);
        try
        {
            _store.Save(newConfig);
        }
        catch (Exception ex) // ConfigValidationException 与 IO 失败同路：磁盘零改动，当前配置保持原状
        {
            failure = ex.Message;
            return false;
        }

        Current = newConfig;
        failure = null;
        return true;
    }

    /// <summary>撤销成功后清空槽（撤销本身不再可重做，§7.3）。</summary>
    public void ClearUndoSlot() => UndoSlot = null;
}
