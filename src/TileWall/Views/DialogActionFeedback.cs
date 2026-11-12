using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace TileWall.Views;

/// <summary>Explains a disabled action without enabling it or executing its command.</summary>
internal static class DialogActionFeedback
{
    private sealed record Reason(Func<string?> Read);
    private static readonly DependencyProperty ReasonProperty = DependencyProperty.RegisterAttached(
        "DisabledReason", typeof(object), typeof(DialogActionFeedback), new PropertyMetadata(null));
    private sealed class Host(Action<string> show, Func<string?> fallback)
    {
        internal Action<string> Show = show;
        internal Func<string?> Fallback = fallback;
    }
    private static readonly ConditionalWeakTable<FrameworkElement, Host> Hosts = new();
    private static readonly ConditionalWeakTable<ComboBox, Host> DropDowns = new();
    private static readonly ConditionalWeakTable<PointerRoutedEventArgs, object> ExplainedPresses = new();

    internal static void SetReason(FrameworkElement control, Func<string?> reason)
        => control.SetValue(ReasonProperty, new Reason(reason));

    internal static void Attach(FrameworkElement root, Action<string> show, Func<string?> fallbackReason)
    {
        if (Hosts.TryGetValue(root, out var existing))
        {
            existing.Show = show; existing.Fallback = fallbackReason;
            return;
        }
        var host = new Host(show, fallbackReason);
        Hosts.Add(root, host);
        var pressed = new PointerEventHandler((_, e) =>
        {
            if (!root.IsLoaded || ExplainedPresses.TryGetValue(e, out _) ||
                !e.GetCurrentPoint(root).Properties.IsLeftButtonPressed) return;
            var hitRoot = root;
            if (root.XamlRoot is { } xamlRoot)
                foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot).Reverse())
                    if (popup.Child is FrameworkElement { IsLoaded: true, IsHitTestVisible: true } child)
                    {
                        var local = e.GetCurrentPoint(child).Position;
                        if (local.X >= 0 && local.Y >= 0 && local.X < child.ActualWidth && local.Y < child.ActualHeight)
                        { hitRoot = child; break; }
                    }
            var control = FindControlAt(hitRoot, hitRoot, e.GetCurrentPoint(hitRoot).Position);
            if (control is null) return;
            Control? disabled = null;
            for (DependencyObject? current = control; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is Control { IsEnabled: false } blocked) { disabled = blocked; break; }
                if (ReferenceEquals(current, hitRoot)) break;
            }
            if (disabled is null) return;
            var message = ReadReason(disabled, hitRoot) ?? host.Fallback();
            if (!string.IsNullOrWhiteSpace(message)) host.Show(message);
            ExplainedPresses.Add(e, new());
            e.Handled = true;
        });
        UIElement? inputRoot = null;
        void Connect()
        {
            UIElement next = root;
            for (DependencyObject? parent = VisualTreeHelper.GetParent(root); parent is not null;
                parent = VisualTreeHelper.GetParent(parent))
                if (parent is UIElement element) next = element;
            if (!ReferenceEquals(inputRoot, next))
            {
                inputRoot?.RemoveHandler(UIElement.PointerPressedEvent, pressed);
                inputRoot = next;
                // Disabled presses may be retargeted above the page content.
                inputRoot?.AddHandler(UIElement.PointerPressedEvent, pressed, true);
            }
            AttachDropDowns(root, host);
        }
        root.AddHandler(UIElement.PointerPressedEvent, pressed, true);
        root.Loaded += (_, _) => Connect();
        root.Unloaded += (_, _) =>
        {
            inputRoot?.RemoveHandler(UIElement.PointerPressedEvent, pressed);
            inputRoot = null;
        };
        if (root.IsLoaded) Connect();
    }

    private static Control? FindControlAt(FrameworkElement root, DependencyObject parent, Point point)
    {
        // Use arranged bounds even for disabled controls. Walking front to back
        // and clipping at each ancestor mirrors the dialog's existing drag hit test.
        for (var index = VisualTreeHelper.GetChildrenCount(parent) - 1; index >= 0; index--)
        {
            if (VisualTreeHelper.GetChild(parent, index) is not FrameworkElement child ||
                child.Visibility != Visibility.Visible || !child.IsHitTestVisible) continue;
            var local = root.TransformToVisual(child).TransformPoint(point);
            if (local.X < 0 || local.Y < 0 || local.X >= child.ActualWidth || local.Y >= child.ActualHeight) continue;
            if (FindControlAt(root, child, point) is { } nested) return nested;
            if (child is Control control) return control;
        }
        return null;
    }

    private static void AttachDropDowns(DependencyObject parent, Host host)
    {
        if (parent is ComboBox combo && !DropDowns.TryGetValue(combo, out _))
        {
            DropDowns.Add(combo, host);
            combo.DropDownOpened += (_, _) => combo.DispatcherQueue.TryEnqueue(() =>
            {
                if (combo.XamlRoot is not { } xamlRoot) return;
                // Drop-down content has its own input route. Keep the same page
                // feedback destination, including for disabled source choices.
                foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
                    if (popup.Child is FrameworkElement child)
                    {
                        Attach(child, host.Show, host.Fallback);
                    }
            });
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            AttachDropDowns(VisualTreeHelper.GetChild(parent, index), host);
    }

    private static string? ReadReason(Control control, FrameworkElement root)
    {
        for (DependencyObject? current = control; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current.GetValue(ReasonProperty) is Reason reason &&
                reason.Read() is { Length: > 0 } message) return message;
            if (ReferenceEquals(current, root)) break;
        }
        var help = AutomationProperties.GetHelpText(control);
        if (!string.IsNullOrWhiteSpace(help)) return help;
        return ToolTipService.GetToolTip(control) switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => text,
            ToolTip { Content: string text } when !string.IsNullOrWhiteSpace(text) => text,
            _ => null,
        };
    }
}
