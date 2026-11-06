using Microsoft.UI.Xaml;
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
    {
        Orientation = Orientation.Horizontal; Spacing = 4;
        Children.Clear(); _buttons.Clear();
        var index = 0;
        foreach (var name in names)
        {
            var position = index++;
            var button = new ToggleButton();
            if (showLabels) ActionIcon.Set(button, name, glyphs[position]);
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
