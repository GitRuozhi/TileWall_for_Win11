using System.Runtime.InteropServices;
using TileWall.Core.Shell;

namespace TileWall.Shell.Interop;

/// <summary>
/// IExplorerCommand 的 CCW 实现（M9 设计 §4.2）：com:ExeServer 激活后 Explorer 经它查询菜单状态与执行 Invoke。
/// 红线约束 [R2]：GetTitle/GetIcon/GetToolTip/GetState 全部恒常量、零 IO、零扫描、零 XAML——
/// 菜单构建线程上绝不允许磁盘探测（多选 N 项的存在性检查是明令禁止的重活）；
/// 不可添加对象在 Invoke 时过滤并给出结果反馈，菜单项恒可用。
/// </summary>
internal sealed class TileWallExplorerCommand : IExplorerCommand
{
    // —— IInspectable 三槽：占位返回 E_NOTIMPL（槽位本身必须存在，否则 vtable 错位崩 Explorer） ——

    public int GetIids(out uint iidCount, IntPtr iids)
    {
        iidCount = 0;
        return ExplorerCommandInterop.ENotimpl;
    }

    public int GetRuntimeClassName(out IntPtr className)
    {
        className = IntPtr.Zero;
        return ExplorerCommandInterop.ENotimpl;
    }

    public int GetTrustLevel(out int trustLevel)
    {
        trustLevel = 0; // BaseTrust
        return ExplorerCommandInterop.ENotimpl;
    }

    // —— IExplorerCommand 八法 ——

    public int GetCanonicalName(out Guid guidCommandName)
    {
        guidCommandName = ExplorerCommandIds.CanonicalName;
        return 0;
    }

    public int GetFlags(out uint pFlags)
    {
        pFlags = ExplorerCommandInterop.EcfDefault; // ECF_DEFAULT：无 OpenInNewWindow 等修饰
        return 0;
    }

    public int GetTitle(IShellItemArray psiItemArray, out string ppszName)
    {
        ppszName = ExplorerCommandIds.MenuTitle;
        return 0;
    }

    public int GetIcon(IShellItemArray psiItemArray, out string ppszIcon)
    {
        ppszIcon = ExplorerCommandIds.MenuIcon;
        return 0;
    }

    public int GetToolTip(IShellItemArray psiItemArray, out string ppszInfotip)
    {
        ppszInfotip = string.Empty;
        return 0;
    }

    public int GetState(IShellItemArray psiItemArray, bool fOkToBeSlow, out uint pCmdState)
    {
        pCmdState = ExplorerCommandInterop.EcsEnabled; // 恒可用：不做存在性/可用性检查（零 IO）
        return 0;
    }

    public int Invoke(IShellItemArray psiItemArray, IBindCtx pbc)
    {
        // Invoke 只允许三类系统调用（§4.2 约束 4）：IShellItemArray 计数/取项、IShellItem 取
        // FILESYSPATH、末尾的一次路由投递。非文件系统项（取不到路径/裸盘符根）就地丢弃。
        var paths = ExplorerCommandSelection.FilesystemPathsOf(psiItemArray);
        ExplorerAddRouter.Route(paths);
        return 0; // S_OK：路由尽力而为，不让 Explorer 弹通用错误 UI
    }
}

/// <summary>Invoke 的选中项提取（§4.2 约束 4/5）：多选经 IShellItemArray 天然承载，一次 Invoke 一次投递。</summary>
internal static class ExplorerCommandSelection
{
    public static IReadOnlyList<string> FilesystemPathsOf(IShellItemArray? psiItemArray)
    {
        if (psiItemArray is null)
        {
            return [];
        }

        try
        {
            if (psiItemArray.GetCount(out var count) != 0)
            {
                return [];
            }

            var paths = new List<string>((int)Math.Min(count, 4096));
            for (var i = 0u; i < count; i++)
            {
                if (psiItemArray.GetItemAt(i, out var item) != 0 || item is null)
                {
                    continue;
                }

                if (item.GetDisplayName(ExplorerCommandInterop.SigdnFilesysPath, out var display) != 0
                    || string.IsNullOrEmpty(display))
                {
                    continue; // 虚拟对象（此电脑/回收站/控制面板）：无文件系统路径，就地丢弃
                }

                paths.Add(display);
            }

            return ExplorerAddPathClassifier.PrepareForRouting(paths); // 剔除裸盘符根（纯逻辑，零额外 IO）
        }
        catch (Exception ex) when (ex is InvalidCastException or COMException or ArgumentException)
        {
            // 选中集不可用（Explorer 提前释放等极端时序）：按空集处理，绝不让异常穿透进 Explorer
            return [];
        }
    }
}
