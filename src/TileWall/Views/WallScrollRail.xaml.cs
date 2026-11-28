using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TileWall.Core.Wall;
using TileWall.Resources;
using Windows.Foundation;

namespace TileWall.Views;

public sealed partial class WallScrollRail : UserControl
{
    private bool _syncing;

    public WallScrollRail() => InitializeComponent();

    internal void Connect(ScrollViewer viewer, WallMetrics metrics, Action? onUserScroll = null)
    {
        RefreshStrings();
        viewer.RegisterPropertyChangedCallback(ScrollViewer.ScrollableWidthProperty, (_, _) => Sync());
        viewer.RegisterPropertyChangedCallback(ScrollViewer.ViewportWidthProperty, (_, _) => Sync());
        viewer.ViewChanged += (_, _) => Sync();
        Rail.ValueChanged += (_, args) =>
        {
            if (_syncing) return;
            onUserScroll?.Invoke();
            viewer.ChangeView(Math.Clamp(args.NewValue, 0, viewer.ScrollableWidth), null, null, disableAnimation: true);
        };
        Sync();

        void Sync()
        {
            _syncing = true;
            try
            {
                var width = viewer.ScrollableWidth;
                Rail.Maximum = width;
                Rail.ViewportSize = viewer.ViewportWidth;
                Rail.SmallChange = metrics.Pitch;
                Rail.LargeChange = Math.Max(metrics.Pitch, viewer.ViewportWidth - metrics.ColumnGap);
                Rail.Value = Math.Clamp(viewer.HorizontalOffset, 0, width);
                UpdateVisibility(viewer);
            }
            finally { _syncing = false; }
        }
    }

    internal void RefreshStrings() => AutomationProperties.SetName(Rail, ProductStrings.Get("Settings.ExpandColumns"));

    internal void Place(ScrollViewer viewer, Rect region, bool allowScroll)
    {
        var mode = allowScroll ? ScrollMode.Enabled : ScrollMode.Disabled;
        if (viewer.HorizontalScrollMode != mode)
        {
            viewer.HorizontalScrollMode = mode;
            viewer.HorizontalScrollBarVisibility = allowScroll ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
            if (!allowScroll) viewer.ChangeView(0, null, null, disableAnimation: true);
        }
        Width = region.Width;
        Margin = new Thickness(region.X, region.Y + Math.Max(0, region.Height - Height), 0, 0);
        UpdateVisibility(viewer);
    }

    private void UpdateVisibility(ScrollViewer viewer) =>
        Visibility = viewer.HorizontalScrollMode == ScrollMode.Enabled && viewer.ScrollableWidth > .01
            ? Visibility.Visible : Visibility.Collapsed;
}
