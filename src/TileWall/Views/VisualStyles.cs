using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace TileWall.Views;

/// <summary>Fixed page surfaces, independent of the wall's tile corner preference.</summary>
internal static class VisualStyles
{
    internal static Border Card(UIElement content)
    {
        // Keep ThemeResource expressions live when an open dialog changes theme.
        var card = (Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                Padding="12" CornerRadius="0" Background="{ThemeResource TileWallSurfaceBrush}" />
            """);
        card.Child = content;
        return card;
    }

    internal static Border InputFrame() => (Border)XamlReader.Load("""
        <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
            CornerRadius="0" BorderThickness="1"
            Background="{ThemeResource TileWallSurfaceBrush}"
            BorderBrush="{ThemeResource TileWallStrokeBrush}" />
        """);
}
