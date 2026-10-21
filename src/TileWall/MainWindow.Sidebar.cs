using Microsoft.UI.Xaml;
using TileWall.Core.Wall;
using TileWall.Views;

namespace TileWall;

public sealed partial class MainWindow
{
    private WindowsSidebar? _windowsSidebar;

    partial void CreateWindowsSidebar()
    {
        _windowsSidebar = new WindowsSidebar(Status, OnContextMenuVisibilityChanged);
        WindowsSidebarHost.Children.Add(_windowsSidebar);
        WallRoot.SizeChanged += (_, _) => UpdateWindowsSidebar();
        UpdateWindowsSidebar();
    }

    partial void UpdateWindowsSidebar()
    {
        if (_windowsSidebar is null) return;
        // The fields describe the currently displayed preview, including resize and mode
        // transitions. Reading committed Settings here would jump back during a gesture.
        var right = _sidebarAnchor is AnchorCorner.TopRight or AnchorCorner.BottomRight;
        WindowsSidebarHost.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        if (_scrollRegion.Width > 0)
        {
            WindowsSidebarHost.Margin = new Thickness(0);
            WindowsSidebarHost.Height = Math.Max(1, (AppWindow.Size.Height - _frameHeight) / Scale);
            WindowsSidebarHost.VerticalAlignment = VerticalAlignment.Top;
        }
        _windowsSidebar.MaxWidth = Math.Max(48, WallRoot.ActualWidth);
        _windowsSidebar.Configure(_sidebarAnchor, _sidebarFullScreen);
        PositionMenuStart(_scrollRegion);
    }
}
