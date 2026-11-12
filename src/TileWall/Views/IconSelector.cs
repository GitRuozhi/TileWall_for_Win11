using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace TileWall.Views;

/// <summary>A compact mutually exclusive group with accessible native font icons.</summary>
internal sealed partial class IconSelector(params string[] glyphs) : StackPanel
{
    private readonly List<ToggleButton> _buttons = [];
    private int _selectedIndex = -1;
    internal event EventHandler? SelectionChanged;
    internal int SelectedIndex
    {
        get => _selectedIndex;
        set { _selectedIndex = value; for (var i = 0; i < _buttons.Count; i++) _buttons[i].IsChecked = i == value; }
    }
    internal void SetEnabled(bool enabled)
    {
        foreach (var button in _buttons) button.IsEnabled = enabled;
    }
    internal void SetItems(IEnumerable<string> names, bool showLabels = false, bool notifyOnReselect = true)
        => SetItemsCore(names, showLabels, notifyOnReselect, -1, null);

    internal void SetItems(IEnumerable<string> names, bool showLabels, bool notifyOnReselect,
        int iconFactoryIndex, Func<FrameworkElement> iconFactory)
        => SetItemsCore(names, showLabels, notifyOnReselect, iconFactoryIndex, iconFactory);

    private void SetItemsCore(IEnumerable<string> names, bool showLabels, bool notifyOnReselect,
        int iconFactoryIndex, Func<FrameworkElement>? iconFactory)
    {
        Orientation = Orientation.Horizontal; Spacing = 4;
        Children.Clear(); _buttons.Clear();
        var items = names.ToArray();
        if (iconFactoryIndex < -1 || iconFactoryIndex >= items.Length || iconFactoryIndex == -1 && iconFactory is not null ||
            iconFactoryIndex >= 0 && iconFactory is null)
            throw new ArgumentException("A custom icon factory must target exactly one existing item.", nameof(iconFactory));
        var index = 0;
        foreach (var name in items)
        {
            var position = index++;
            var button = new ToggleButton();
            if (position == iconFactoryIndex)
            {
                var content = iconFactory!();
                if (showLabels)
                {
                    var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    label.Children.Add(content);
                    label.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
                    button.Content = label;
                }
                else
                {
                    button.Content = content;
                    button.MinWidth = 32; button.Padding = new Thickness(7);
                }
                ActionIcon.SetTip(button, name);
                AutomationProperties.SetName(button, name);
            }
            else if (showLabels) ActionIcon.Set(button, name, glyphs[position]);
            else ActionIcon.SetIcon(button, name, glyphs[position]);
            button.Click += (_, _) =>
            {
                var previous = SelectedIndex;
                SelectedIndex = position;
                if (notifyOnReselect || previous != position) SelectionChanged?.Invoke(this, EventArgs.Empty);
            };
            _buttons.Add(button); Children.Add(button);
        }
        SelectedIndex = _selectedIndex;
    }
}
