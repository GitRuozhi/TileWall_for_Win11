using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Wall;
using TileWall.Graphics;
using Windows.Foundation;

namespace TileWall;

public sealed partial class MainWindow
{
    private readonly ScrollViewer _menuStart = new()
    {
        Width = WallViewportPolicy.MenuWidthDip, HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Visibility = Visibility.Collapsed,
    };
    private readonly Dictionary<string, Image> _menuIcons = new(StringComparer.Ordinal);

    private void InitializeMenuStart()
    {
        AutomationProperties.SetAutomationId(_menuStart, "menu-start-list");
        WallRoot.Children.Insert(1, _menuStart);
        _puzzles.FrameChanged += UpdateMenuIcon;
    }

    private void RenderMenuStart(WallDocument document)
    {
        _menuStart.Visibility = document.Settings.MenuStart ? Visibility.Visible : Visibility.Collapsed;
        if (!document.Settings.MenuStart) { _menuIcons.Clear(); _menuStart.Content = null; return; }
        var panel = new StackPanel { Spacing = 0 };
        _menuIcons.Clear();
        foreach (var group in document.Lanes.SelectMany(lane => lane.Groups))
        {
            var entries = group.Members.Where(member => member.Entry is not null).ToArray();
            if (entries.Length == 0) continue;
            if (!string.IsNullOrWhiteSpace(group.Title)) panel.Children.Add(new TextBlock
            {
                Text = group.Title, Height = 32, Padding = new Thickness(12, 8, 8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            foreach (var member in entries)
            {
                var content = new Grid();
                content.ColumnDefinitions.Add(new() { Width = new GridLength(44) });
                content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                var icon = new Image { Width = 24, Height = 24, Stretch = Stretch.Uniform,
                    Source = _puzzles.FrameFor(member.Id)?.Sharp };
                content.Children.Add(icon); _menuIcons.Add(member.Id, icon);
                var label = new TextBlock { Text = member.Visual.Title ?? MemberPresentationRules.ResolveTitle(member),
                    VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, 12, 0) };
                Grid.SetColumn(label, 1); content.Children.Add(label);
                var row = new Button { Content = content, Height = 36, Padding = new Thickness(0),
                    BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), ContextFlyout = Surface.CreateMemberContextMenu(member) };
                var id = member.Id;
                row.Click += (_, _) => LaunchMember(id);
                AutomationProperties.SetAutomationId(row, "menu-member-" + id);
                AutomationProperties.SetName(row, label.Text);
                ToolTipService.SetToolTip(row, label.Text);
                panel.Children.Add(row);
            }
        }
        _menuStart.Content = panel;
        PositionMenuStart(_scrollRegion);
    }

    private void UpdateMenuIcon(string id, PuzzleFrame? frame, double progress)
    {
        if (_menuIcons.TryGetValue(id, out var image)) image.Source = frame?.Sharp;
    }

    private void PositionMenuStart(Rect region)
    {
        if (!_hasDocument || !PresentedViewportSettings.MenuStart) return;
        var right = _sidebarAnchor is AnchorCorner.TopRight or AnchorCorner.BottomRight;
        var width = Math.Max(1, (AppWindow.Size.Width - _frameWidth) / Scale);
        _menuStart.Height = Math.Max(1, (AppWindow.Size.Height - _frameHeight) / Scale);
        _menuStart.Margin = new Thickness(right ? Math.Max(0, width - WallViewportPolicy.FixedWidth(true))
            : WallViewportPolicy.SidebarWidthDip, 0, 0, 0);
    }
}
