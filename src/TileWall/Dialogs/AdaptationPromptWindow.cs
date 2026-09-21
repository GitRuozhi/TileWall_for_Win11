using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Shell;

namespace TileWall.Dialogs;

/// <summary>
/// 显示适配提示窗（M8 设计 §6.4，P1 §3.6 线框逐项）：标题「显示适配提示」；
/// 正文两组数字（需求 vs 容量）+ 恢复说明原文 + 两按钮「调整布局…」「稍后处理」。
/// 墙窗的 owned window（恒在墙前）；不占用模态单槽（自身无草稿、不实现 IExitParticipant——
/// 让出单槽使托盘设置在适配期仍可用）；分辨率恢复后由 AdaptationMachine 经宿主关闭。
/// AutomationId 契约（§8）：adapt-window / adapt-require / adapt-capacity / adapt-adjust / adapt-later。
/// </summary>
public sealed class AdaptationPromptWindow : Window
{
    private const int GwlHwndParent = -8;

    private readonly Grid _rootGrid;
    private readonly TextBlock _require;
    private readonly TextBlock _capacity;
    private readonly TextBlock _restoreNote;

    public AdaptationPromptWindow(AdaptationSnapshot snapshot, IntPtr ownerHwnd)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Title = "显示适配提示";

        _require = new TextBlock { Text = snapshot.RequirementText, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetAutomationId(_require, "adapt-require");

        _capacity = new TextBlock { Text = snapshot.CapacityText, FontSize = 16 };
        AutomationProperties.SetAutomationId(_capacity, "adapt-capacity");

        _restoreNote = new TextBlock
        {
            Text = "恢复原分辨率/缩放后将自动按原布局显示；不会自动删除、缩小或裁切任何内容。",
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(_restoreNote, "adapt-note");

        var adjustButton = new Button { Content = "调整布局…" };
        AutomationProperties.SetAutomationId(adjustButton, "adapt-adjust");
        adjustButton.Click += (_, _) => AdjustRequested?.Invoke();

        var laterButton = new Button { Content = "稍后处理" };
        AutomationProperties.SetAutomationId(laterButton, "adapt-later");
        laterButton.Click += (_, _) => Close(); // 关闭 → Closed 事件 → machine.PromptDismissed（MainWindow 收口）

        Content = _rootGrid = BuildLayout(snapshot, _require, _capacity, _restoreNote, adjustButton, laterButton);

        ConfigureWindowShape(ownerHwnd);
    }

    /// <summary>「调整布局…」点击（MainWindow 打开模态调整会话）。</summary>
    public event Action? AdjustRequested;

    /// <summary>数字更新（AdaptationNeeded 期间再评估；窗开着才刷）。</summary>
    public void UpdateSnapshot(AdaptationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _require.Text = snapshot.RequirementText;
        _capacity.Text = snapshot.CapacityText;
    }

    private static Grid BuildLayout(
        AdaptationSnapshot snapshot,
        TextBlock require,
        TextBlock capacity,
        TextBlock restoreNote,
        Button adjustButton,
        Button laterButton)
    {
        var grid = new Grid { Padding = new Thickness(20), RowSpacing = 12, MinWidth = 420 };
        for (var i = 0; i < 5; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var header = new TextBlock { Text = "显示适配提示", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetAutomationId(header, "adapt-window");
        AutomationProperties.SetName(header, "显示适配提示");
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var intro = new TextBlock
        {
            Text = snapshot.Fits ? string.Empty : "已保存的布局超出了当前屏幕工作区：",
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(intro, 1);
        grid.Children.Add(intro);

        var numbers = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };
        numbers.Children.Add(require);
        numbers.Children.Add(capacity);
        Grid.SetRow(numbers, 2);
        grid.Children.Add(numbers);

        Grid.SetRow(restoreNote, 3);
        grid.Children.Add(restoreNote);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(adjustButton);
        buttons.Children.Add(laterButton);
        Grid.SetRow(buttons, 4);
        grid.Children.Add(buttons);
        return grid;
    }

    private void ConfigureWindowShape(IntPtr ownerHwnd)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var workArea = DisplayArea.Primary.WorkArea;
        const int width = 480;
        const int height = 300;
        AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(
            workArea.X + ((workArea.Width - width) / 2),
            workArea.Y + ((workArea.Height - height) / 2),
            width,
            height));

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetOwnerWindow(hwnd, ownerHwnd); // owned window：恒在墙前
    }

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private static void SetOwnerWindow(IntPtr hwnd, IntPtr ownerHwnd)
    {
        if (hwnd == IntPtr.Zero || ownerHwnd == IntPtr.Zero)
        {
            return;
        }

        if (Environment.Is64BitProcess)
        {
            _ = SetWindowLongPtr64(hwnd, GwlHwndParent, ownerHwnd);
        }
        else
        {
            _ = SetWindowLong32(hwnd, GwlHwndParent, ownerHwnd.ToInt32());
        }
    }
}
