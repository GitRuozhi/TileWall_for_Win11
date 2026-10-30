using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using System.Runtime.CompilerServices;

namespace TileWall.Views;

/// <summary>One Win10-compatible vector font and size for application actions.</summary>
internal static class ActionIcon
{
    private sealed class DialogIcons { internal string Primary = Save, Secondary = Delete, Close = Cancel; }
    private static readonly ConditionalWeakTable<ContentDialog, DialogIcons> DecoratedDialogs = new();
    internal const string Save = "\uE74E", Cancel = "\uE711", Browse = "\uE8B7", Reset = "\uE777",
        Register = "\uE710", Link = "\uE71B", Settings = "\uE713", Import = "\uE8B5",
        Edit = "\uE70F", Undo = "\uE7A7", Redo = "\uE7A6", Add = "\uE710", Remove = "\uE738",
        Delete = "\uE74D", Previous = "\uE76B", Next = "\uE76C", Refresh = "\uE72C",
        OpenFile = "\uE8E5", Folder = "\uE8B7", Picture = "\uE8B9", Confirm = "\uE73E",
        PresetSmall = "\uE743", PresetMedium = "\uE744", PresetWide = "\uE745", PresetLarge = "\uE747",
        Fit = "\uE799", Fill = "\uE9A6", Original = "\uE91B", Custom = "\uE932",
        Photo2 = "\uEB9F", GallerySet = "\uF5ED", Checkbox = "\uE739", OpenFolderHorizontal = "\uED25", Share = "\uE72D",
        ImportAllMirrored = "\uEA53", PreviousItem = "\uE892", NextItem = "\uE893",
        BackgroundBlur = "\uED5B", Color = "\uE790", Draw = "\uEC87", Erase = "\uE75C", Clock = "\uE121";

    internal static FontIcon Glyph(string glyph, double size = 16) => new()
    {
        Glyph = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = size,
        IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center,
    };

    internal static StackPanel Label(string text, string glyph)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(Glyph(glyph));
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return content;
    }

    internal static void Set(ButtonBase button, string text, string glyph)
    {
        button.Content = Label(text, glyph);
        AutomationProperties.SetName(button, text);
        SetTip(button, text);
    }

    internal static void SetIcon(ButtonBase button, string text, string glyph)
    {
        button.Content = Glyph(glyph);
        button.MinWidth = 32; button.Padding = new Thickness(7);
        SetTip(button, text);
        AutomationProperties.SetName(button, text);
    }

    private sealed class Registration
    {
        internal readonly ToolTip Tip = new();
        internal DisabledTips? Root;
    }
    private static readonly ConditionalWeakTable<ButtonBase, Registration> RegisteredTips = new();
    private static readonly ConditionalWeakTable<UIElement, DisabledTips> RootTips = new();
    internal static void SetTip(ButtonBase button, string text)
    {
        if (!RegisteredTips.TryGetValue(button, out var registration))
        {
            registration = new Registration(); RegisteredTips.Add(button, registration);
            button.Loaded += (_, _) => AttachTip(button, registration);
            button.Unloaded += (_, _) =>
            {
                registration.Root?.Stop(); registration.Root = null;
                registration.Tip.IsOpen = false;
            };
        }
        // Keep one owner-assigned instance for both the normal tooltip service
        // and manual disabled hover. Updating a label must not replace that owner.
        registration.Tip.Content = text;
        if (!ReferenceEquals(ToolTipService.GetToolTip(button), registration.Tip))
        {
            registration.Tip.IsOpen = false;
            ToolTipService.SetToolTip(button, registration.Tip);
        }
        if (button.IsLoaded) AttachTip(button, registration);
    }
    private static void AttachTip(ButtonBase button, Registration registration)
    {
        if (button.XamlRoot is not { Content: UIElement root } xamlRoot) return;
        var manager = RootTips.GetValue(root, static element => new DisabledTips(element));
        if (!ReferenceEquals(registration.Root, manager))
        {
            registration.Root?.Stop(); registration.Tip.IsOpen = false;
            registration.Root = manager;
        }
        if (registration.Tip.XamlRoot != xamlRoot) registration.Tip.XamlRoot = xamlRoot;
        manager.Add(button);
    }
    // Disabled controls do not receive their own hover events. Resolve only after
    // the pointer rests, rather than walking controls on every pointer movement.
    private sealed class DisabledTips
    {
        private readonly UIElement _root;
        private readonly List<WeakReference<ButtonBase>> _buttons = [];
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
        private ToolTip? _activeTip;
        private Point _point;
        internal DisabledTips(UIElement root)
        {
            _root = root; _timer = root.DispatcherQueue.CreateTimer();
            _timer.IsRepeating = false; _timer.Interval = TimeSpan.FromMilliseconds(500);
            _timer.Tick += (_, _) => Show();
            root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
            { _point = e.GetCurrentPoint(root).Position; Stop(); _timer.Start(); }), true);
            root.PointerExited += (_, _) => Stop();
            root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => Stop()), true);
            if (root is FrameworkElement lifetime) lifetime.Unloaded += (_, _) => Stop();
        }
        internal void Add(ButtonBase button)
        {
            if (!_buttons.Any(reference => reference.TryGetTarget(out var existing) && ReferenceEquals(existing, button)))
                _buttons.Add(new WeakReference<ButtonBase>(button));
        }
        internal void Stop()
        {
            _timer.Stop();
            var active = _activeTip; _activeTip = null;
            if (active is not null) active.IsOpen = false;
        }
        private void Show()
        {
            if (_root.XamlRoot is not { } xamlRoot || _root is FrameworkElement { IsLoaded: false }) return;
            for (var index = _buttons.Count - 1; index >= 0; index--)
            {
                if (!_buttons[index].TryGetTarget(out var button)) { _buttons.RemoveAt(index); continue; }
                if (!button.IsLoaded || button.IsEnabled || button.Visibility != Visibility.Visible || button.XamlRoot != _root.XamlRoot || button.ActualWidth <= 0) continue;
                var position = button.TransformToVisual(_root).TransformPoint(new Point());
                if (!new Rect(position, new Size(button.ActualWidth, button.ActualHeight)).Contains(_point)) continue;
                if (!RegisteredTips.TryGetValue(button, out var registration) ||
                    !ReferenceEquals(ToolTipService.GetToolTip(button), registration.Tip)) continue;
                Stop();
                var tip = registration.Tip;
                if (tip.XamlRoot != xamlRoot) tip.XamlRoot = xamlRoot;
                tip.PlacementTarget = button; _activeTip = tip; tip.IsOpen = true; return;
            }
        }
    }

    internal static (string Primary, string Secondary, string Close) GetDialogIcons(ContentDialog dialog)
    {
        var icons = DecoratedDialogs.GetValue(dialog, static _ => new DialogIcons());
        return (icons.Primary, icons.Secondary, icons.Close);
    }

    internal static void DecorateDialog(ContentDialog dialog, string primary, string secondary, string close)
    {
        DecorateDialog(dialog);
        var icons = DecoratedDialogs.GetValue(dialog, static _ => new DialogIcons());
        icons.Primary = primary; icons.Secondary = secondary; icons.Close = close;
        DecorateButtons(dialog, dialog);
    }

    /// <summary>Keeps standard dialog command buttons, adding icons after the template exists.</summary>
    internal static void DecorateDialog(ContentDialog dialog)
    {
        if (!DecoratedDialogs.TryGetValue(dialog, out _))
        {
            DecoratedDialogs.Add(dialog, new DialogIcons());
            dialog.Opened += (_, _) => DecorateButtons(dialog, dialog);
        }
        DecorateButtons(dialog, dialog);
    }

    private static void DecorateButtons(DependencyObject parent, ContentDialog dialog)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ButtonBase button && VisualTreeHelper.GetParent(button) is FrameworkElement { Name: "CommandSpace" })
            {
                var text = button.Name switch
                {
                    "PrimaryButton" => dialog.PrimaryButtonText,
                    "SecondaryButton" => dialog.SecondaryButtonText,
                    "CloseButton" => dialog.CloseButtonText,
                    _ => null,
                };
                var icons = DecoratedDialogs.GetValue(dialog, static _ => new DialogIcons());
                var glyph = button.Name switch { "PrimaryButton" => icons.Primary, "SecondaryButton" => icons.Secondary, _ => icons.Close };
                if (!string.IsNullOrWhiteSpace(text)) Set(button, text, glyph);
            }
            DecorateButtons(child, dialog);
        }
    }

}
