using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Windows.Foundation;

namespace TileWall.Views;

/// <summary>Fixed page surfaces, independent of the wall's tile corner preference.</summary>
internal static class VisualStyles
{
    private const double DefaultPreviewPageWidth = 930;
    private const double DefaultPreviewFormWidth = 460;

    internal static Grid ResizablePreviewPage(FrameworkElement previewPane, StackPanel cards, Size budget,
        double preferredHeight, Button primary, Button cancel) =>
        PreviewPage(previewPane, cards, budget, DefaultPreviewPageWidth, DefaultPreviewFormWidth,
            preferredHeight, (primary, cancel));

    internal static Grid PreviewPage(FrameworkElement previewPane, StackPanel cards, Size budget,
        double preferredWidth, double preferredRightWidth, double? preferredHeight = null,
        (Button Primary, Button Cancel)? footer = null)
    {
        const double gap = 24;
        var stretchRight = preferredHeight.HasValue;
        var width = Math.Min(preferredWidth, budget.Width);
        var columnSpace = Math.Max(2, width - gap);
        var minRight = Math.Min(preferredRightWidth, Math.Min(340, columnSpace * .75));
        var minLeft = Math.Min(220, columnSpace - minRight);
        var right = Math.Min(preferredRightWidth, columnSpace - minLeft);
        var page = new Grid
        {
            Width = width, MinWidth = Math.Min(680, width), ColumnSpacing = gap,
            Height = preferredHeight is { } height ? Math.Min(height, budget.Height) : double.NaN,
            MaxHeight = budget.Height,
        };
        page.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(stretchRight ? Math.Max(1, columnSpace - right) : 1, GridUnitType.Star),
            MinWidth = minLeft,
        });
        page.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = stretchRight ? new GridLength(Math.Max(1, right), GridUnitType.Star) : new GridLength(right),
            MinWidth = stretchRight ? minRight : 0,
        });
        var controls = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
        FrameworkElement rightPane = controls;
        if (footer is { } buttons)
        {
            var actions = ActionCard(buttons.Primary, buttons.Cancel, stacked: right < 370);
            var form = new Grid { RowSpacing = cards.Spacing };
            form.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            form.RowDefinitions.Add(new() { Height = GridLength.Auto });
            form.Children.Add(controls);
            Grid.SetRow(actions, 1);
            form.Children.Add(actions);
            rightPane = form;
        }
        page.Children.Add(previewPane);
        Grid.SetColumn(rightPane, 1);
        page.Children.Add(rightPane);
        return page;
    }

    internal static Grid FormRow(TextBlock label, FrameworkElement value, double labelWidth, double gap)
    {
        var row = new Grid { ColumnSpacing = gap, RowSpacing = gap };
        if (labelWidth > 0)
        {
            row.ColumnDefinitions.Add(new() { Width = new GridLength(labelWidth) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(value, 1);
        }
        else
        {
            row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            Grid.SetRow(value, 1);
        }
        row.Children.Add(label);
        row.Children.Add(value);
        AutomationProperties.SetLabeledBy(value, label);
        return row;
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
