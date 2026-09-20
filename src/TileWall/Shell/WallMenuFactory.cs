using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Configuration;

namespace TileWall.Shell;

/// <summary>
/// 右键菜单工厂（M3 设计 §6）：
/// 背景五项 = 设计 §3.1 原文项序，M3 仅「新建磁贴」真实生效；对象四项 = §12.3，M3 仅「取消固定」可用。
/// 置灰项保留 AutomationId 与 Name（UIA 可断言 IsEnabled=False），原因写入 ToolTip 与 HelpText（§15.3）。
/// </summary>
public static class WallMenuFactory
{
    /// <summary>背景菜单（附着 RootGrid.ContextFlyout；空白命中：边距、栏间隙、格间隙）。</summary>
    public static MenuFlyout CreateBackgroundMenu(Action onNewTile)
    {
        ArgumentNullException.ThrowIfNull(onNewTile);
        var menu = new MenuFlyout();
        AutomationProperties.SetAutomationId(menu, "menu-blank");

        menu.Items.Add(Item("menu-blank-new-tile", "新建磁贴", enabled: true, reason: null, onNewTile));
        menu.Items.Add(Item(
            "menu-blank-new-group", "新建磁贴组", enabled: false,
            reason: "需要组属性窗口（后续里程碑）", action: null));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(
            "menu-blank-import", "从开始菜单导入", enabled: false,
            reason: "需要 .lnk 托管与导入窗（后续里程碑）", action: null));
        menu.Items.Add(Item(
            "menu-blank-datetime", "添加时间日期", enabled: false,
            reason: "时间日期组件（后续里程碑）", action: null));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(
            "menu-blank-settings", "设置", enabled: false,
            reason: "设置窗与托盘（后续里程碑）", action: null));
        return menu;
    }

    /// <summary>对象菜单（磁贴与组共用一份动态构建的 MenuFlyout；组从任意分块右键均为整组菜单，A12）。</summary>
    public static void PopulateObjectMenu(MenuFlyout menu, LayoutObject target, Action<string> onUnpin)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(onUnpin);
        menu.Items.Clear();
        if (AutomationProperties.GetAutomationId(menu) != "menu-object")
        {
            AutomationProperties.SetAutomationId(menu, "menu-object");
        }

        var isGroup = target is GroupObject;
        var unpinText = isGroup ? "取消固定磁贴组" : "从磁贴墙取消固定";
        var editText = isGroup ? "编辑磁贴组" : "编辑磁贴";

        menu.Items.Add(Item("menu-object-unpin", unpinText, enabled: true, reason: null, () => onUnpin(target.Id)));
        menu.Items.Add(Item("menu-object-edit", editText, enabled: false, reason: "属性窗（后续里程碑）", action: null));
        menu.Items.Add(Item(
            "menu-object-elevated", "以管理员身份启动", enabled: false,
            reason: "需要托管入口（后续里程碑）", action: null));
        menu.Items.Add(Item(
            "menu-object-location", "打开文件位置", enabled: false,
            reason: "定位数据目录内的托管入口；M3 无托管入口", action: null));
    }

    private static MenuFlyoutItem Item(string automationId, string text, bool enabled, string? reason, Action? action)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        AutomationProperties.SetAutomationId(item, automationId); // 置灰也保留 Id 与 Name（§4）
        AutomationProperties.SetName(item, text);
        if (reason is not null)
        {
            ToolTipService.SetToolTip(item, reason);
            AutomationProperties.SetHelpText(item, reason);
        }

        if (enabled && action is not null)
        {
            item.Click += (_, _) => action();
        }

        return item;
    }
}
