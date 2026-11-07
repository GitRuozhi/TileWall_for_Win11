using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Windows.Foundation;

namespace TileWall.Views;

/// <summary>Fixed page surfaces, independent of the wall's tile corner preference.</summary>
internal static class VisualStyles
{
    internal static Grid PreviewPage(FrameworkElement previewPane, StackPanel cards, Size budget,
        double preferredWidth, double preferredRightWidth, double? preferredLeftWidth = null)
    {
        const double gap = 24;
        var width = Math.Min(preferredWidth, budget.Width);
        var columnSpace = Math.Max(2, width - gap);
        var minRight = Math.Min(preferredRightWidth, Math.Min(340, columnSpace * .75));
        var minLeft = Math.Min(220, columnSpace - minRight);
        var right = Math.Min(preferredRightWidth, columnSpace - minLeft);
        if (preferredLeftWidth is { } left)
            right = Math.Clamp(columnSpace - left, minRight, columnSpace - minLeft);
        var page = new Grid { Width = width, MinWidth = Math.Min(680, width), MaxHeight = budget.Height, ColumnSpacing = gap };
        page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = minLeft });
        page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(right) });
        var controls = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
        page.Children.Add(previewPane);
        Grid.SetColumn(controls, 1);
        page.Children.Add(controls);
        return page;
    }

    internal static Border ActionCard(Button primary, Button cancel, bool stacked = false)
    {
        var actions = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        if (stacked)
        {
            actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(cancel, 1);
        }
        else
        {
            actions.ColumnDefinitions.Add(new ColumnDefinition());
            actions.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(cancel, 1);
        }
        primary.Style = (Style)Application.Current.Resources["TileWallDialogPrimaryButtonStyle"];
        primary.HorizontalAlignment = cancel.HorizontalAlignment = HorizontalAlignment.Stretch;
        actions.Children.Add(primary);
        actions.Children.Add(cancel);
        return Card(actions);
    }

    internal static Border Card(UIElement content, bool useTileBackground = true)
    {
        var card = (Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                Padding="12" CornerRadius="0" Background="{ThemeResource TileWallSurfaceBrush}" />
            """);
        card.Child = content;
        if (useTileBackground) card.Loaded += (_, _) =>
        {
            if (OwnedDialogWindow.PreviewCardBackground(card.XamlRoot) is { } brush) card.Background = brush;
        };
        return card;
    }

    internal static Border InputFrame(bool transparent = false)
    {
        var frame = (Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                CornerRadius="0" BorderThickness="1"
                Background="{ThemeResource TileWallSurfaceBrush}"
                BorderBrush="{ThemeResource TileWallStrokeBrush}" />
            """);
        if (transparent) frame.Background = null;
        return frame;
    }
}
