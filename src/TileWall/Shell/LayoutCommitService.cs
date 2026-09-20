using TileWall.Core.Configuration;

namespace TileWall.Shell;

/// <summary>单槽撤销记录：配置 record 不可变 → 快照 = 引用（M3 设计 §7.1）。</summary>
public sealed record WallUndoSlot
{
    public required TileWallConfig Previous { get; init; }

    public required string ActionName { get; init; }
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

    /// <summary>撤销成功后清空槽（撤销本身不再可重做，§7.3）。</summary>
    public void ClearUndoSlot() => UndoSlot = null;
}
