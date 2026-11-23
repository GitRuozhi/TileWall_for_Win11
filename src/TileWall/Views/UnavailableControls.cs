using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TileWall.Views;

/// <summary>Projects existing availability into visibility for pages that opt in.</summary>
internal static class UnavailableControls
{
    internal static void Project(Control control, bool showUnavailable) =>
        control.Visibility = showUnavailable || control.IsEnabled ? Visibility.Visible : Visibility.Collapsed;

    internal static void WatchAvailability(Control control, Action changed)
    {
        control.RegisterPropertyChangedCallback(Control.IsEnabledProperty, (_, _) => changed());
        control.IsEnabledChanged += (_, _) => changed();
        control.Loaded += (_, _) => changed();
    }

    internal static void WatchRow(FrameworkElement row, FrameworkElement value)
    {
        var actions = ActionsIn(value).ToArray();
        if (actions.Length == 0) return;

        void UpdateRow()
        {
            var visible = actions.Any(action => action switch
            {
                Control control => control.IsEnabled,
                IconSelector selector => selector.HasEnabledItem,
                _ => false,
            });
            row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var action in actions)
        {
            if (action is Control control) WatchAvailability(control, UpdateRow);
            else if (action is IconSelector selector) selector.ObserveAvailability(UpdateRow);
        }
        UpdateRow();
    }

    private static IEnumerable<FrameworkElement> ActionsIn(FrameworkElement element) => element switch
    {
        Control or IconSelector => [element],
        Panel panel => panel.Children.OfType<FrameworkElement>().SelectMany(ActionsIn),
        Border { Child: FrameworkElement child } => ActionsIn(child),
        _ => [],
    };
}
