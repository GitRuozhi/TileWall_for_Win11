using Microsoft.UI.Xaml.Controls;
using TileWall.Resources;
using Windows.UI;

namespace TileWall.Views;

internal sealed class MemberBackgroundPicker
{
    internal IconSelector Mode { get; } = new(ActionIcon.Reset, ActionIcon.Color, ActionIcon.BackgroundBlur, ActionIcon.Checkbox);
    internal Button ColorButton { get; } = new();
    internal ColorPicker Color { get; } = new() { IsAlphaEnabled = false, IsMoreButtonVisible = false, Width = 240 };
    internal Flyout ColorFlyout { get; }
    internal StackPanel Row { get; }

    internal MemberBackgroundPicker()
    {
        ColorFlyout = new Flyout { Content = Color };
        ColorButton.Flyout = ColorFlyout;
        Row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Row.Children.Add(Mode);
        Row.Children.Add(ColorButton);
    }

    internal void RefreshStrings() =>
        Mode.SetItems(new[] { "Editor.Initial", "Tile.CustomColor", "Editor.Blur", "Editor.None" }.Select(ProductStrings.Get));

    internal static Color ParseColor(string? value) => WallSurface.ParseColor(value, Windows.UI.Color.FromArgb(255, 64, 64, 64));
    internal static string ColorText(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
