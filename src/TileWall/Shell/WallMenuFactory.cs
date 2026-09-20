using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Configuration;

namespace TileWall.Shell;

/// <summary>
/// 右键菜单工厂（M3 设计 §6、M4 §6.2、M5 §11.2/§11.3）：
/// 背景五项 = 设计 §3.1 原文项序，「新建磁贴」「新建磁贴组」M5 起均打开属性窗；
/// 对象四项启用矩阵（§6.2）：unpin 恒可用；edit 恒可用（M5 起组由 MainWindow 分派到组属性窗）；
/// elevated/location 由注入的入口状态函数判定（有入口且文件存在 → 可用，否则置灰 + HelpText 原因）。
/// 置灰项保留 AutomationId 与 Name（UIA 可断言 IsEnabled=False），原因写入 ToolTip 与 HelpText（§15.3）。
/// </summary>
public static class WallMenuFactory
{
    /// <summary>对象菜单四个命令出口（M4 §8.2；MainWindow 注入，工厂不持有服务）。</summary>
    public sealed record ObjectMenuActions(
        Action<string> Unpin,
        Action<string> Edit,
        Action<string> LaunchElevated,
        Action<string> RevealLocation);

    /// <summary>背景菜单（附着 RootGrid.ContextFlyout；空白命中：边距、栏间隙、格间隙）。
    /// M5 起「新建磁贴组」生效（§11.2：第二参回调 → 组属性窗创建模式）。</summary>
    public static MenuFlyout CreateBackgroundMenu(Action onNewTile, Action onNewGroup)
    {
        ArgumentNullException.ThrowIfNull(onNewTile);
        ArgumentNullException.ThrowIfNull(onNewGroup);
        var menu = new MenuFlyout();
        AutomationProperties.SetAutomationId(menu, "menu-blank");

        menu.Items.Add(Item("menu-blank-new-tile", "新建磁贴", enabled: true, reason: null, onNewTile));
        menu.Items.Add(Item("menu-blank-new-group", "新建磁贴组", enabled: true, reason: null, onNewGroup));
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

    /// <summary>
    /// 对象菜单（磁贴与组共用一份动态构建的 MenuFlyout；组从任意分块右键均为整组菜单，A12）。
    /// <paramref name="entryDisabledReason"/> 返回 null = 入口可用；否则为置灰原因（「无托管入口」/「入口文件缺失」）。
    /// </summary>
    public static void PopulateObjectMenu(
        MenuFlyout menu,
        LayoutObject target,
        ObjectMenuActions actions,
        Func<LayoutObject, string?> entryDisabledReason)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(entryDisabledReason);
        menu.Items.Clear();
        if (AutomationProperties.GetAutomationId(menu) != "menu-object")
        {
            AutomationProperties.SetAutomationId(menu, "menu-object");
        }

        var isGroup = target is GroupObject;
        var unpinText = isGroup ? "取消固定磁贴组" : "从磁贴墙取消固定";
        var editText = isGroup ? "编辑磁贴组" : "编辑磁贴";
        var entryReason = entryDisabledReason(target); // §6.2：无入口或入口文件已丢失时置灰并提示

        menu.Items.Add(Item("menu-object-unpin", unpinText, enabled: true, reason: null, () => actions.Unpin(target.Id)));
        // M5：组的「编辑磁贴组」启用（§11.3；MainWindow 按 ObjectKind 分派到组属性窗）
        menu.Items.Add(Item("menu-object-edit", editText, enabled: true, reason: null, () => actions.Edit(target.Id)));
        menu.Items.Add(Item(
            "menu-object-elevated", "以管理员身份启动",
            enabled: entryReason is null,
            reason: entryReason,
            action: entryReason is null ? () => actions.LaunchElevated(target.Id) : null));
        menu.Items.Add(Item(
            "menu-object-location", "打开文件位置",
            enabled: entryReason is null,
            reason: entryReason,
            action: entryReason is null ? () => actions.RevealLocation(target.Id) : null));
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
