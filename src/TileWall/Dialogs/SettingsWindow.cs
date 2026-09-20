using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TileWall.Core.Settings;
using TileWall.Core.Shell;
using TileWall.Shell.Interop;
using VirtualKey = Windows.System.VirtualKey;

namespace TileWall.Dialogs;

/// <summary>设置窗运行上下文（MainWindow 注入；换绑「注册+保存双成功」编排在 MainWindow，窗只做绑定与呈现）。</summary>
public sealed record SettingsWindowContext(
    string? CurrentHotKeyText,              // 配置里的快捷键原文（null = 未配置）
    IHotKeyRegistration Registrar,          // 注册状态真值（状态行永远反映它，不缓存「成功」字样）
    Func<string, string?> ApplyHotKey,      // 配置串 → null = 已注册+已保存；否则错误文本（内部含回滚）
    ILoginStartup LoginStartup,             // 登录后启动（注册表唯一真值源）
    string VersionText);

/// <summary>
/// 设置窗（M7 设计 §8；P1 §3.4 线框）——三项：快捷键（当前组合 + 实时注册状态 + 录入）、
/// 登录后启动（HKCU Run 键）、关于（只读）。无「应用/保存」按钮：有效修改自动保存（§15.2）。
/// 录入态：点击录入按钮进入；Esc=取消（B11 子步骤优先），半成品不落盘（§3.4）；
/// 注册+保存双成功才替换，任一失败回滚旧组合、配置不动、就地报错（无虚假成功）。
/// 窗口形态复用属性窗模式：常规标题栏、不可调、owned window（恒在墙前）；经 _modal.Open 进单槽会话。
/// 退出参与面：自动保存故无草稿（HasUnsavedDraft=false）——退出流程直通关闭，不重复确认（§14.4）。
/// </summary>
public sealed class SettingsWindow : Window, IExitParticipant
{
    private readonly SettingsWindowContext _context;
    private readonly Grid _rootGrid;
    private readonly TextBlock _currentText;
    private readonly TextBlock _statusText;
    private readonly Button _captureButton;
    private readonly ToggleSwitch _runAtLogin;
    private readonly InfoBar _errorBar;

    private string? _currentHotKeyText;
    private bool _capturing;
    private bool _suppressToggle;

    public SettingsWindow(SettingsWindowContext context, IntPtr ownerHwnd)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _currentHotKeyText = context.CurrentHotKeyText;
        Title = "设置";

        _currentText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(_currentText, "settings-hotkey-current");

        _statusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 };
        AutomationProperties.SetAutomationId(_statusText, "settings-hotkey-status");

        _captureButton = new Button { Content = "点击此处录入新组合" };
        AutomationProperties.SetAutomationId(_captureButton, "settings-hotkey-capture");
        _captureButton.Click += (_, _) => BeginCapture();

        _runAtLogin = new ToggleSwitch { OnContent = "开", OffContent = "关", Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetAutomationId(_runAtLogin, "settings-runatlogin");
        _runAtLogin.Toggled += OnRunAtLoginToggled;

        _errorBar = new InfoBar { IsOpen = false, Severity = InfoBarSeverity.Warning, IsClosable = false };
        AutomationProperties.SetAutomationId(_errorBar, "settings-apply-error");

        Content = BuildLayout(out _rootGrid);

        RefreshHotKeyRows();
        RefreshLoginToggle();

        ConfigureWindowShape(ownerHwnd);
        _rootGrid.PreviewKeyDown += OnRootPreviewKeyDown; // 录入态捕获 + Esc 分级（先取消录入，再关窗）
        _context.Registrar.RegistrationChanged += RefreshHotKeyRows; // 状态行永远反映注册真值
        Closed += (_, _) => _context.Registrar.RegistrationChanged -= RefreshHotKeyRows;
    }

    // ————————————————————————————— 布局（P1 §3.4 线框逐项） —————————————————————————————

    private Grid BuildLayout(out Grid root)
    {
        var grid = new Grid { Padding = new Thickness(20), RowSpacing = 12 };
        for (var i = 0; i < 9; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        // §7.3 契约：窗根 AutomationId 挂在标题 TextBlock 上（带 peer，恒可被 UIA 断言）
        var header = new TextBlock { Text = "设置", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetAutomationId(header, "settings-root");
        AutomationProperties.SetName(header, "设置");
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var hotkeyHeader = new TextBlock { Text = "显示磁贴墙的快捷键", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        Grid.SetRow(hotkeyHeader, 1);
        grid.Children.Add(hotkeyHeader);

        var currentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        currentRow.Children.Add(new TextBlock { Text = "当前组合：", VerticalAlignment = VerticalAlignment.Center });
        currentRow.Children.Add(_currentText);
        Grid.SetRow(currentRow, 2);
        grid.Children.Add(currentRow);

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        statusRow.Children.Add(new TextBlock { Text = "状态：", VerticalAlignment = VerticalAlignment.Center });
        statusRow.Children.Add(_statusText);
        Grid.SetRow(statusRow, 3);
        grid.Children.Add(statusRow);

        var captureRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 0) };
        captureRow.Children.Add(_captureButton);
        Grid.SetRow(captureRow, 4);
        grid.Children.Add(captureRow);

        var loginRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        loginRow.Children.Add(new TextBlock { Text = "登录后启动", VerticalAlignment = VerticalAlignment.Center });
        loginRow.Children.Add(_runAtLogin);
        Grid.SetRow(loginRow, 5);
        grid.Children.Add(loginRow);

        Grid.SetRow(_errorBar, 6);
        grid.Children.Add(_errorBar);

        var separator = new Microsoft.UI.Xaml.Controls.Border
        {
            BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Gray),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Opacity = 0.3,
            Margin = new Thickness(0, 8, 0, 0),
        };
        Grid.SetRow(separator, 7);
        grid.Children.Add(separator);

        var aboutPanel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
        var aboutHeader = new TextBlock { Text = "关于", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetAutomationId(aboutHeader, "settings-about");
        aboutPanel.Children.Add(aboutHeader);
        var version = new TextBlock { Text = $"TileWall for Win11  版本 {_context.VersionText}", Opacity = 0.85 };
        AutomationProperties.SetAutomationId(version, "settings-about-version");
        aboutPanel.Children.Add(version);
        aboutPanel.Children.Add(new TextBlock { Text = "兼容：Windows 10 22H2 / Windows 11", Opacity = 0.85 });
        aboutPanel.Children.Add(new TextBlock { Text = "源码与许可：待定", Opacity = 0.85 }); // 不放置臆造链接（§8.4）
        Grid.SetRow(aboutPanel, 8);
        grid.Children.Add(aboutPanel);

        root = grid;
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
        const int width = 520;
        const int height = 480;
        AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(
            workArea.X + ((workArea.Width - width) / 2),
            workArea.Y + ((workArea.Height - height) / 2),
            width,
            height));

        WindowOwner.SetOwnerWindow(WinRT.Interop.WindowNative.GetWindowHandle(this), ownerHwnd); // owned：恒在墙前
    }

    // ————————————————————————————— 快捷键项（§8.2 交互细则） —————————————————————————————

    private void RefreshHotKeyRows()
    {
        // 当前组合（配置真值；非法/未配置如实呈现，不回退默认值覆盖用户配置）
        if (_currentHotKeyText is null)
        {
            _currentText.Text = "未配置（可在下方录入）";
        }
        else if (HotKeyGesture.TryParse(_currentHotKeyText, out var gesture, out _) && gesture is not null)
        {
            _currentText.Text = gesture.ToDisplayString();
        }
        else
        {
            _currentText.Text = $"{_currentHotKeyText}（无效）";
        }

        // 状态行 = 注册真值（不缓存「成功」字样）
        _statusText.Text = _capturing
            ? "录入中：请按下新组合（Esc 取消）"
            : _context.Registrar.IsRegistered
                ? "✓ 已注册"
                : $"未注册（{_context.Registrar.LastFailureReason ?? "尚未注册或组合无效"}）";
    }

    private void BeginCapture()
    {
        if (_capturing)
        {
            return;
        }

        _capturing = true;
        _captureButton.Content = "录入中…（按 Esc 取消）";
        RefreshHotKeyRows();
        Activate(); // 确保键盘焦点在设置窗内
    }

    private void EndCapture()
    {
        _capturing = false;
        _captureButton.Content = "点击此处录入新组合";
        RefreshHotKeyRows();
    }

    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && !_capturing)
        {
            Close(); // 非录入态 Esc = 关窗；关闭即 OnSessionClosed 解锁主墙（§8.2）
            return;
        }

        if (!_capturing)
        {
            return;
        }

        e.Handled = true; // 录入态吞掉一切按键（含 Tab/空格对窗内控件的副作用）
        if (e.Key == VirtualKey.Escape)
        {
            EndCapture(); // Esc=取消录入（B11 子步骤优先）；半成品不算已保存
            return;
        }

        var modifiers = ReadModifiers();
        var gesture = new HotKeyGesture(modifiers, unchecked((uint)e.Key));
        var reason = HotKeyGesture.Validate(gesture);
        if (reason is not null)
        {
            _statusText.Text = reason; // 无效组合就地提示、不进注册流程（主键为修饰键 / 仅 Shift 等）
            return;
        }

        var configText = gesture.ToConfigString();
        var error = _context.ApplyHotKey(configText); // 注册+保存双成功才替换（内部含回滚，§3.4）
        _currentHotKeyText = configText; // 展示与配置同步（失败时 MainWindow 已回滚注册，配置仍为旧值）
        EndCapture();
        if (error is not null)
        {
            ShowError(error); // 就地报错；状态行经 RegistrationChanged 已回到真实注册状态
        }
    }

    /// <summary>读当前修饰键状态（InputKeyboardSource；含左右 Win 键）。</summary>
    private static uint ReadModifiers()
    {
        uint modifiers = 0;
        if (IsDown(VirtualKey.Shift) || IsDown(VirtualKey.LeftShift) || IsDown(VirtualKey.RightShift))
        {
            modifiers |= HotKeyGesture.ModShift;
        }

        if (IsDown(VirtualKey.Control) || IsDown(VirtualKey.LeftControl) || IsDown(VirtualKey.RightControl))
        {
            modifiers |= HotKeyGesture.ModCtrl;
        }

        if (IsDown(VirtualKey.Menu) || IsDown(VirtualKey.LeftMenu) || IsDown(VirtualKey.RightMenu))
        {
            modifiers |= HotKeyGesture.ModAlt;
        }

        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
        {
            modifiers |= HotKeyGesture.ModWin;
        }

        return modifiers;
    }

    private static bool IsDown(VirtualKey key)
    {
        // CorePhysicalKeyState：None=0 / Down=1 / WasDown=2——按位判定，避免投影命名空间漂移
        var state = InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return ((int)state & 1) != 0; // Down
    }

    // ————————————————————————————— 登录后启动（§8.3：注册表唯一真值源） —————————————————————————————

    private void RefreshLoginToggle()
    {
        _suppressToggle = true;
        _runAtLogin.IsOn = _context.LoginStartup.IsEnabled;
        _suppressToggle = false;
    }

    private void OnRunAtLoginToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle)
        {
            return;
        }

        var enabled = _runAtLogin.IsOn;
        if (_context.LoginStartup.SetEnabled(enabled, out var error))
        {
            HideError();
            return;
        }

        // 写失败：UI 保持原态并就地报错（不把界面显示为已生效，§15.2）
        _suppressToggle = true;
        _runAtLogin.IsOn = !enabled;
        _suppressToggle = false;
        ShowError($"登录启动设置失败：{error}");
    }

    // ————————————————————————————— 就地报错 —————————————————————————————

    private void ShowError(string message)
    {
        _errorBar.Message = message;
        AutomationProperties.SetName(_errorBar, message);
        _errorBar.Visibility = Visibility.Visible;
        _errorBar.IsOpen = true;
    }

    private void HideError()
    {
        _errorBar.IsOpen = false;
        _errorBar.Visibility = Visibility.Collapsed;
    }

    // ————————————————————————————— IExitParticipant（无草稿：自动保存项不重复确认） —————————————————————————————

    bool IExitParticipant.HasUnsavedDraft => false;

    string? IExitParticipant.TrySaveNow() => null; // 全部设置即时落盘，无可保存草稿

    void IExitParticipant.DiscardDraft()
    {
        // 无草稿可弃
    }
}
