using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TileWall.Core.Grid;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Resources;
using MenuAction = TileWall.Core.Settings.WallContextMenuActions;

namespace TileWall.Views;

internal sealed record WallMenuItemDefinition(MenuAction Id, string ResourceKey, string Glyph)
{
    internal IReadOnlyList<WallMenuItemDefinition> Children { get; init; } = [];
    internal GridSize? Size { get; init; }
}

internal sealed record WallMenuCommand(Action Execute, bool IsEnabled = true, string? HelpResourceKey = null, bool LogClick = false);

internal static class WallContextMenu
{
    internal static IReadOnlyList<WallMenuItemDefinition> WallActions { get; } =
    [
        new(MenuAction.NewTile, "Menu.New", ActionIcon.Add),
        new(MenuAction.AddComponent, "Menu.AddComponent", ActionIcon.Add)
        {
            Children = [new(MenuAction.NewClock, "Wall.TimeAndDate", ActionIcon.Clock)],
        },
        new(MenuAction.Import, "Wall.Import", ActionIcon.Share),
        new(MenuAction.Settings, "Wall.Settings", ActionIcon.Settings),
    ];

    internal static IReadOnlyList<WallMenuItemDefinition> MemberActions { get; } =
    [
        new(MenuAction.NextImage, "Wall.NextImage", ActionIcon.Next),
        new(MenuAction.Admin, "Wall.Admin", "\uE7EF"),
        new(MenuAction.Location, "Wall.Location", ActionIcon.Browse),
        new(MenuAction.Size, "Wall.Size", ActionIcon.PresetMedium)
        {
            Children =
            [
                SizeItem(MenuAction.SizeSmall, TileSizePreset.Small, ActionIcon.PresetSmall),
                SizeItem(MenuAction.SizeMedium, TileSizePreset.Medium, ActionIcon.PresetMedium),
                SizeItem(MenuAction.SizeWide, TileSizePreset.Wide, ActionIcon.PresetWide),
                SizeItem(MenuAction.SizeLarge, TileSizePreset.Large, ActionIcon.PresetLarge),
            ],
        },
        new(MenuAction.Edit, "Wall.Edit", ActionIcon.Edit),
        new(MenuAction.Remove, "Wall.Remove", ActionIcon.Delete),
    ];

    internal static void AddItems(IList<MenuFlyoutItemBase> items, IReadOnlyList<WallMenuItemDefinition> definitions,
        MenuAction hidden, bool showUnavailable, Func<WallMenuItemDefinition, WallMenuCommand?> resolve)
    {
        if ((hidden & MenuAction.MenuHidden) != 0) return;
        foreach (var definition in definitions)
        {
            if ((hidden & definition.Id) != 0) continue;
            if (definition.Children.Count > 0)
            {
                var submenu = new MenuFlyoutSubItem();
                // Preferences manage top-level entries. Older child bits do not filter the submenu.
                AddItems(submenu.Items, definition.Children, MenuAction.None, showUnavailable, resolve);
                if (submenu.Items.Count == 0) continue;
                var label = ProductStrings.Get(definition.ResourceKey);
                submenu.Text = label;
                submenu.Icon = ActionIcon.Glyph(definition.Glyph);
                SetDescription(submenu, definition.Id.ToString(), label, label);
                items.Add(submenu);
            }
            else if (resolve(definition) is { } command && (command.IsEnabled || showUnavailable))
            {
                var label = ProductStrings.Get(definition.ResourceKey);
                var automationId = definition.Id.ToString();
                var tooltip = label;
                if (definition.Size is { } size)
                {
                    automationId = $"Size-{size.Columns}x{size.Rows}";
                    tooltip = $"{label} ({size.Columns}×{size.Rows})";
                }
                var item = new MenuFlyoutItem { Text = label, Icon = ActionIcon.Glyph(definition.Glyph), IsEnabled = command.IsEnabled };
                if (command.HelpResourceKey is { } helpKey)
                {
                    tooltip = ProductStrings.Get(helpKey);
                    AutomationProperties.SetHelpText(item, tooltip);
                }
                SetDescription(item, automationId, label, tooltip);
                item.Click += (_, _) =>
                {
                    if (command.LogClick) AppLog.Info("WallContextMenu", $"Click action={label}");
                    command.Execute();
                };
                items.Add(item);
            }
        }
    }

    private static WallMenuItemDefinition SizeItem(MenuAction id, TileSizePreset preset, string glyph)
    {
        var bounds = MemberSizeRules.TileBounds(preset);
        return new(id, $"Editor.Preset{preset}", glyph) { Size = new(bounds.Width, bounds.Height) };
    }

    private static void SetDescription(MenuFlyoutItemBase item, string id, string label, string tooltip)
    {
        AutomationProperties.SetAutomationId(item, $"wall-menu-{id}");
        AutomationProperties.SetName(item, label);
        ToolTipService.SetToolTip(item, tooltip);
    }
}
