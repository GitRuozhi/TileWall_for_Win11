using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using TileWall.Core.Wall;
using TileWall.Resources;
using Windows.Foundation;
using Windows.Graphics;

namespace TileWall;

public sealed partial class MainWindow
{
    private WorkAreaRect _screenArea;
    private WallViewportBudget CurrentViewportBudget => ViewportBudget(Settings.FullScreen);
    private WallViewportBudget ViewportBudget(bool fullScreen) => WallViewportPolicy.Create(
        Math.Max(1, _screenArea.Right - _screenArea.Left - _frameWidth) / Scale,
        Math.Max(1, _screenArea.Bottom - _screenArea.Top - _frameHeight) / Scale, fullScreen, _metrics);

    private void ReadPrimaryScreen() => _screenArea = new WorkAreaRect
        { Left = 0, Top = 0, Right = GetSystemMetrics(0), Bottom = GetSystemMetrics(1) };

    private static string ViewportFailure(WallLayoutResult result)
    {
        if (result.Detail?.StartsWith("MEMBER_TOO_TALL:", StringComparison.Ordinal) == true)
        {
            var parts = result.Detail.Split(':');
            if (parts.Length == 3)
                return string.Format(ProductStrings.Get("Wall.MemberTooTall"), parts[1], parts[2]);
        }
        const string prefix = "GROUP_TOO_TALL:";
        return result.Detail?.StartsWith(prefix, StringComparison.Ordinal) == true
            ? string.Format(ProductStrings.Get("Wall.GroupTooTall"), result.Detail[prefix.Length..])
            : ProductStrings.Get("Wall.ContentTooLarge");
    }

    private WallLayoutResult ProposeMode(WallDocument source, WallSettings requested,
        int? columns = null, int? rows = null, AnchorCorner? anchor = null)
    {
        var budget = ViewportBudget(requested.FullScreen);
        if (requested.FullScreen)
        {
            var settings = source.Settings.FullScreen ? requested : requested with
            {
                WindowedColumns = source.Columns, WindowedRows = source.Rows,
                WindowedAnchorCorner = source.Settings.AnchorCorner,
            };
            return WallViewportPolicy.TryResize(source, source.Columns, budget.MaxRows, settings, budget, _metrics);
        }
        var targetColumns = columns ?? (source.Settings.FullScreen ? source.Settings.WindowedColumns : source.Columns) ?? source.Columns;
        var targetRows = rows ?? (source.Settings.FullScreen ? source.Settings.WindowedRows : source.Rows) ?? source.Rows;
        var targetAnchor = anchor ?? (source.Settings.FullScreen ? source.Settings.WindowedAnchorCorner : requested.AnchorCorner) ?? requested.AnchorCorner;
        targetColumns = Math.Min(targetColumns, budget.MaxColumns);
        targetRows = Math.Min(targetRows, budget.MaxRows);
        return WallViewportPolicy.TryResize(source, targetColumns, targetRows,
            requested with { AnchorCorner = targetAnchor, WindowedColumns = targetColumns,
                WindowedRows = targetRows, WindowedAnchorCorner = targetAnchor }, budget, _metrics);
    }

    // The Surface and all hit/drop calculations use content coordinates. Only window overlays
    // consume viewport coordinates, so scrolling never changes stored member or pointer offsets.
    private Point ContentToViewport(Point point) => new(point.X - WallScroll.HorizontalOffset + WallScroll.Margin.Left, point.Y + WallScroll.Margin.Top);

    private Rect ScrollRegionFor(bool fullScreen, double width, double height, AnchorCorner? anchor = null)
    {
        var right = (anchor ?? Settings.AnchorCorner) is AnchorCorner.TopRight or AnchorCorner.BottomRight;
        double x, y, visibleWidth, visibleHeight;
        if (fullScreen)
        {
            (visibleWidth, visibleHeight) = WallViewportPolicy.FullScreenVisibleSize(width, height, _metrics);
            x = (width - visibleWidth) / 2; y = (height - visibleHeight) / 2;
        }
        else
        {
            visibleWidth = Math.Max(1, width - WallViewportPolicy.SidebarWidthDip);
            visibleHeight = Math.Max(1, height);
            x = right ? 0 : WallViewportPolicy.SidebarWidthDip; y = 0;
        }
        return new Rect(x, y, visibleWidth, visibleHeight);
    }

    private void SetScrollRegion(bool fullScreen, Rect region, bool updateSurface = true)
    {
        if (_scrollMode != fullScreen)
        {
            _scrollMode = fullScreen;
            WallScroll.HorizontalScrollMode = fullScreen ? ScrollMode.Enabled : ScrollMode.Disabled;
            WallScroll.HorizontalScrollBarVisibility = fullScreen ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            WallScroll.VerticalScrollMode = ScrollMode.Disabled;
            WallScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            if (!fullScreen && WallScroll.HorizontalOffset != 0) WallScroll.ChangeView(0, 0, null, disableAnimation: true);
        }
        if (_scrollRegion != region)
        {
            _scrollRegion = region;
            WallScroll.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left;
            WallScroll.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Top;
            WallScroll.Width = region.Width; WallScroll.Height = region.Height;
            WallScroll.Margin = new Microsoft.UI.Xaml.Thickness(region.X, region.Y, 0, 0);
        }
        // The document owns its complete row budget. A larger clip must not create phantom rows.
        if (updateSurface) Surface.SetViewportMinimum(fullScreen ? region.Width : 0, 0);
    }

    private bool? _scrollMode;
    private Rect _scrollRegion;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
