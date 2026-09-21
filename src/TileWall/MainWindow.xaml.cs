using System.Globalization;
using System.Reflection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Carousel;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Groups;
using TileWall.Core.Import;
using TileWall.Core.Settings;
using TileWall.Core.Shell;
using TileWall.Dialogs;
using TileWall.Shell;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
#if DEBUG
using TileWall.Core.Seeding;
#endif

namespace TileWall;

/// <summary>
/// 磁贴墙窗口：M2 的外形（无边框、不可移动、左下锚定，拍板 Q7）+ M3 的接线
/// （渲染/手势/布局提交/撤销）+ M4 的接线（托管入口引擎、点击启动、属性窗模态、联合提交与撤销恢复）
/// + M7 的接线（Shell 命令路由、显隐状态机宿主、退出编排宿主、失焦收起与 Alt+F4 转接）
/// + M8 的接线（时间日期组件、开始菜单导入、显示适配状态机与提示/调整窗）。
/// 本类只做「事件 → 状态机/服务 → 渲染」的薄封装，不复制任何几何/腾位/校验/入口协议逻辑。
/// </summary>
public sealed partial class MainWindow : Window, IGestureHost, IPreviewTimer, IWallShowHost, IExitHost, IAdaptationHost
{
    private readonly ConfigStore _store;
    private readonly ConfigLoadResult _loadResult;
    private readonly bool _seedRequested;
    private readonly GridMetrics _metrics = GridMetrics.Default;
    private readonly WallPresenter _presenter;
    private readonly GestureMachine _machine;
    private readonly MenuFlyout _backgroundMenu;
    private readonly MenuFlyout _objectMenu;
    private readonly DispatcherQueueTimer _previewTimer;
    private readonly DispatcherQueueTimer _statusTimer;
    private readonly IFileStore _files;
    private readonly ILnkFileService _linkFiles;
    private readonly EntryCommitService _entryCommits;
    private readonly EntryLauncher _launcher = new();
    private readonly ModalSessionService _modal;
    private readonly IReadOnlyList<string> _recoveryDiagnostics;
    private readonly TileWall.Shell.Imaging.GroupVisualHost _visuals;
    private readonly TileWall.Shell.Imaging.ImageLoader _imageLoader = new();
    private readonly WallVisibility _visibility;
    private readonly ShellHostContext _shell;
    private readonly ShowHideAnimator _animator;
    private readonly WallShowMachine _showMachine;
    private readonly ExitCoordinator _exit;
    private readonly IKnownFolderPaths _knownFolders;
    private readonly IShortcutIconExtractor _iconExtractor;
    private readonly ClockTickDriver _clockDriver;
    private readonly AdaptationMachine _adaptationMachine;
    private readonly DispatcherQueueTimer _displayDebounceTimer; // 显示变化 500 ms 单发防抖（连发合并为一次评估）
    private double _lastRasterScale; // 栅格缩放变化检测基线（XamlRoot.Changed 不带旧值）
    private AdaptationPromptWindow? _adaptPromptWindow;
    private AdaptationAdjustWindow? _adaptAdjustWindow;
    private bool _exitInFlight;
    private bool _activatedOnce; // --background 首显判定（延迟 Activate，§2.2 第 8 步）

    /// <summary>M5：轮播决策状态机（§2.2 装配；无图不计时——M6 起由 CarouselCoordinator 驱动）。</summary>
    public CarouselScheduler Carousel { get; } = new(SystemClock.Instance);

    private CarouselCoordinator? _carouselCoordinator;
    private WallCarouselDriver? _carouselDriver;

    private LayoutCommitService? _commit;
    private WallGrid _wall = new(1, 1);
    private string _dataRoot = string.Empty;
    private bool _layoutReady;
    private bool _adaptationMode;
    private bool _pointerGestureActive;
    private DipPoint _lastPointerPos;
    private LayoutObject? _menuTarget;

    public MainWindow(
        ConfigStore store,
        ConfigLoadResult loadResult,
        bool seedRequested,
        IFileStore files,
        ILnkFileService linkFiles,
        ShellHostContext shell,
        bool startHidden,
        IKnownFolderPaths knownFolders,
        IShortcutIconExtractor iconExtractor,
        IReadOnlyList<string>? recoveryDiagnostics = null)
    {
        _store = store;
        _loadResult = loadResult;
        _seedRequested = seedRequested;
        _files = files;
        _linkFiles = linkFiles;
        _shell = shell;
        _knownFolders = knownFolders;
        _iconExtractor = iconExtractor;
        _recoveryDiagnostics = recoveryDiagnostics ?? [];

        InitializeComponent();
        Title = "TileWall";
        ConfigureBorderlessPresenter();

        _dataRoot = Path.GetDirectoryName(store.ConfigPath) ?? string.Empty;
        _entryCommits = new EntryCommitService(_files, store, _linkFiles, new EnvironmentDataDirectoryProvider(_dataRoot));
        _modal = new ModalSessionService(RootGrid);
        _animator = new ShowHideAnimator(RootGrid, GetRasterizationScale);
        _showMachine = new WallShowMachine(startHidden ? WallShowState.Hidden : WallShowState.Visible, this);
        // §6.1 完成回调接线：A6/A7 动画自然完成 → 状态机转移（缺此接线机器将永久卡在 Showing/Hiding，
        // SnapHide/AppWindow.Hide 永不执行——所有收起路径失效；打断路径经 Cancel 即 Stop，不触发 Completed）
        _animator.ShowCompleted += () => _showMachine.ShowAnimationCompleted();
        _animator.HideCompleted += () => _showMachine.HideAnimationCompleted();
        _exit = new ExitCoordinator(this);
        _modal.SessionOpened += () => _showMachine.InputGateClosed = true; // W3：模态期状态机防御行
        _modal.SessionClosed += () => _showMachine.InputGateClosed = false;
        _modal.SessionClosed += OnModalSessionClosedForAdaptation; // M8：调整窗提交成功/会话结束 → 适配重评估（deferred 补弹）
        _objectMenu = new MenuFlyout();
        _objectMenu.Opening += (_, _) => PopulateObjectMenu();
        _visuals = new TileWall.Shell.Imaging.GroupVisualHost(_metrics);
        _presenter = new WallPresenter(TilesCanvas, DragLayer, _metrics) { ObjectMenu = _objectMenu, GroupHost = _visuals };
        _visibility = new WallVisibility(this); // M6 §8.2：墙可见性状态源（M7 起显隐终态经状态机→AppWindow，事件链零改动）

        _previewTimer = DispatcherQueue.CreateTimer();
        _previewTimer.Interval = TimeSpan.FromMilliseconds(GestureThresholds.PreviewHoverDelayMs); // B4：250 ms
        _previewTimer.IsRepeating = false;

        _machine = new GestureMachine(_metrics, this, this);
        _previewTimer.Tick += (_, _) => _machine.PreviewTimerDue();

        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(5);
        _statusTimer.IsRepeating = false;
        _statusTimer.Tick += (_, _) => StatusHint.IsOpen = false;

        _backgroundMenu = WallMenuFactory.CreateBackgroundMenu(
            OnNewTileRequested, OnNewGroupRequested, OpenSettings, OpenImport, OpenClockCreator);
        _backgroundMenu.Opening += (_, _) => UpdateBackgroundMenuForAdaptation(); // §8：适配期两新增项置灰 + 原因
        RootGrid.ContextFlyout = _backgroundMenu;
        _presenter.ObjectContextRequested += (view, _) => _menuTarget = view.Object;
        _presenter.ObjectActivated += OnObjectInvokeActivated;

        // M8 §2.2：适配状态机 + 显示变化防抖 + 时钟驱动（C16，与轮播同一 WallVisibility 钩子）
        _adaptationMachine = new AdaptationMachine(this);
        _displayDebounceTimer = DispatcherQueue.CreateTimer();
        _displayDebounceTimer.Interval = TimeSpan.FromMilliseconds(500);
        _displayDebounceTimer.IsRepeating = false;
        _displayDebounceTimer.Tick += (_, _) => EvaluateAdaptationNow();
        _clockDriver = new ClockTickDriver(
            _presenter, _visibility, SystemClock.Instance, CultureInfo.CurrentCulture, DispatcherQueue);

        ((FrameworkElement)Content).Loaded += OnContentLoaded;
        HookPointerEvents();
        RootGrid.KeyDown += OnRootKeyDown;
        Activated += OnWindowDeactivated; // 失焦收起单机制（§6.4：墙外点击/切应用/开始菜单三场景合一）
        AppWindow.Closing += OnAppWindowClosingExit; // Alt+F4 → 统一退出流程（§9.1）
    }

    // ————————————————————————————— 启动装配（§2.2） —————————————————————————————

    private void OnContentLoaded(object sender, RoutedEventArgs e)
    {
        Bootstrap();
        HookDisplayChanges(); // M8 §2.2 第 5 步：WM_DISPLAYCHANGE/WM_SETTINGCHANGE + DpiChanged → 防抖评估
        _clockDriver.Start(); // M8 §2.2 第 6 步：时钟 1 Hz 驱动（仅墙可见时走表，C16）
    }

    /// <summary>M8 §6.2：显示变化触发源挂接（WndProc 分发 + 主窗栅格缩放变化——与 DisplayInformation.DpiChanged
    /// 同一栅格缩放信号，经 XamlRoot.Changed 自比对实现，XamlRoot 就绪后挂接）。不可得时静默——仍有兜底评估。</summary>
    private void HookDisplayChanges()
    {
        _shell.MessageHost.DisplayChanged += OnDisplayEnvironmentChanged;
        if ((Content as FrameworkElement)?.XamlRoot is { } xamlRoot)
        {
            _lastRasterScale = GetRasterizationScale();
            xamlRoot.Changed += (_, _) =>
            {
                var current = GetRasterizationScale();
                if (Math.Abs(current - _lastRasterScale) > 0.001)
                {
                    _lastRasterScale = current;
                    OnDisplayEnvironmentChanged(); // 每显示器缩放变化（§6.2 第三路触发源）
                }
            };
        }
    }

    private void OnDisplayEnvironmentChanged()
    {
        _displayDebounceTimer.Stop(); // 500 ms 单发计时器合并连发（§6.2）
        _displayDebounceTimer.Start();
    }

    /// <summary>适配评估入口（四路触发 + 兜底统一汇入）：读取当前工作区 → 状态机 Evaluate。</summary>
    private void EvaluateAdaptationNow()
    {
        if (!_layoutReady && !_adaptationMode)
        {
            return; // 配置损坏/版本过高路径：无布局可评估（保留原文件不动，§17.2）
        }

        var scale = GetRasterizationScale();
        var workArea = DisplayArea.Primary.WorkArea;
        var snapshot = AdaptationEvaluator.Snapshot(
            _wall, workArea.Width / scale, workArea.Height / scale, _metrics);
        _adaptationMachine.Evaluate(snapshot);
        if (_adaptAdjustWindow is { } adjust)
        {
            adjust.UpdateWorkArea(workArea.Width / scale, workArea.Height / scale); // F-A2：草稿按新容量刷新
        }
    }

    private void OnModalSessionClosedForAdaptation()
    {
        if (_adaptationMode)
        {
            _displayDebounceTimer.Stop();
            EvaluateAdaptationNow(); // 调整窗确认/会话结束 → 立即重评估（Fit 恢复显示；NoFit 且无会话 → 补弹提示）
        }
    }

    private void Bootstrap()
    {
        var scale = GetRasterizationScale();
        var workArea = DisplayArea.Primary.WorkArea;
        var workWidthDip = workArea.Width / scale;
        var workHeightDip = workArea.Height / scale;

        var hotkeyLine = HotKeyStatusLine(); // C09：注册失败就地真实状态（墙内 InfoBar 呈现点之一）
        switch (_loadResult.Status)
        {
            case ConfigLoadStatus.Fresh:
            {
                var (columns, rows) = WallSizing.InitialForWorkArea(workWidthDip, workHeightDip, _metrics);
                if (columns < 1 || rows < 1)
                {
                    ShowStartupStatus("工作区容不下一栏八格，磁贴墙无法显示（设计 §4.5、A18）。", hotkeyLine);
                    return;
                }

                var config = TileWallConfig.CreateInitial(columns, rows);
                EnterReadyState(config, workArea, scale, workWidthDip, workHeightDip, saveFirst: true, hotkeyStatusLine: hotkeyLine);
                break;
            }
            case ConfigLoadStatus.Loaded:
            case ConfigLoadStatus.RecoveredFromBackup:
            {
                if (_loadResult.Status == ConfigLoadStatus.RecoveredFromBackup)
                {
                    ShowPersistentStatus(ComposeStartupDiagnostics(_loadResult.Diagnostics, hotkeyLine));
                }

                EnterReadyState(_loadResult.Config!, workArea, scale, workWidthDip, workHeightDip, saveFirst: false, hotkeyStatusLine: hotkeyLine);
                if (_loadResult.Status == ConfigLoadStatus.Loaded && _recoveryDiagnostics.Count > 0)
                {
                    ShowPersistentStatus(ComposeStartupDiagnostics([], hotkeyLine)); // 启动清扫报告（回滚/清理动作）
                }

                break;
            }
            default: // Corrupt / FutureVersion：保留原文件不动（§17.2）、本次不渲染对象、禁止 Save
            {
                var (columns, rows) = WallSizing.InitialForWorkArea(workWidthDip, workHeightDip, _metrics);
                if (columns >= 1 && rows >= 1)
                {
                    _wall = new WallGrid(columns, rows);
                    PlaceWindow(workArea, scale, clampToWorkArea: false);
                    DrawWallGridBase();
                }

                UpdateEmptyHint();
                ShowStartupStatus(string.Join(Environment.NewLine, _loadResult.Diagnostics), hotkeyLine);
                return;
            }
        }

        ShowStartupStatus(hotkeyLine); // 正常路径唯一可能的常驻提示：快捷键未注册（C09 真实状态）
    }

    /// <summary>Fresh / Loaded 共用：种子注入（#if DEBUG）→ 首启/种子保存 → 窗口定位 → 渲染。</summary>
    private void EnterReadyState(
        TileWallConfig config,
        RectInt32 workArea,
        double scale,
        double workWidthDip,
        double workHeightDip,
        bool saveFirst,
        string? hotkeyStatusLine = null)
    {
        var (seeded, configToUse) = ApplySeedIfRequested(config);
        if (saveFirst || seeded)
        {
            try
            {
                _store.Save(configToUse);
            }
            catch (Exception ex)
            {
                ShowTransientStatus($"保存失败：{ex.Message}"); // 不留半态：仍以内存配置渲染
            }
        }

        _commit = new LayoutCommitService(_store, configToUse);
        _wall = new WallGrid(configToUse.Wall.Columns, configToUse.Wall.Rows);
        _machine.UpdateWall(_wall);
        _machine.UpdateObjects(_commit.Current.Objects);

        // 显示适配（M8 §6.1：判定与运行时同一条规则；容差与 M3 既有分支逐字一致）
        // 超工作区 → 状态机 Fitted→AdaptationNeeded：不渲染对象、窗口钳到工作区、配置不动、弹提示窗（U-5）
        if (!AdaptationEvaluator.Fits(_wall, workWidthDip, workHeightDip, _metrics))
        {
            _adaptationMode = true;
            PlaceWindow(workArea, scale, clampToWorkArea: true);
            ShowStartupStatus(
                $"已保存布局需要 {_wall.Columns} 栏 × {_wall.Rows} 行，当前放不下；未渲染任何对象，配置未修改（设计 §17.1）。",
                hotkeyStatusLine);
            EvaluateAdaptationNow(); // → HideWallForAdaptation（墙可见则收起）+ ShowPrompt（需求 vs 容量）
            return;
        }

        _layoutReady = true;
        PlaceWindow(workArea, scale, clampToWorkArea: false);
        DrawWallGridBase();
        _presenter.Initialize(_wall);
        _presenter.RenderAll(_commit.Current);
        UpdateEmptyHint();
        StartCarousel(_commit); // M6 §2.2：轮播接线（引擎/协调器/驱动器装配 + 基线建立）
    }

    /// <summary>M6 轮播装配（§2.2）：Core 协调器（决策核）+ Shell 引擎（解码/持久化/上墙）+ 驱动器（哑闹钟）。</summary>
    private void StartCarousel(LayoutCommitService commit)
    {
        var engine = new WallCarouselEngine(commit, _imageLoader, _visuals, _metrics, GetRasterizationScale);
        var coordinator = new CarouselCoordinator(SystemClock.Instance, Carousel, _files, engine, engine)
        {
            LayoutReady = true, // 墙可见性/模态/拖动由驱动器与指针路径维护
        };
        _carouselCoordinator = coordinator;
        _carouselDriver = new WallCarouselDriver(coordinator, engine, _visibility, _modal, _presenter, DispatcherQueue);
        _carouselDriver.Start();
        _ = RunCarouselBaselineAsync(coordinator); // 首图/恢复显示（§8.2）
    }

    private async Task RunCarouselBaselineAsync(CarouselCoordinator coordinator)
    {
        try
        {
            await coordinator.OnConfigReadyAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // 基线建立失败不致命：组保持占位（主题兜底色），后续到期路径照常
        }
    }

    private (bool Seeded, TileWallConfig Config) ApplySeedIfRequested(TileWallConfig config)
    {
#if DEBUG
        if (_seedRequested && (_loadResult.Status == ConfigLoadStatus.Fresh || config.Objects.Count == 0))
        {
            var seeded = DemoSeed.Create(new WallGrid(config.Wall.Columns, config.Wall.Rows));
            return (true, seeded);
        }
#endif
        return (false, config);
    }

    // ————————————————————————————— 窗口外形与网格底（承 M2） —————————————————————————————

    /// <summary>无边框无标题栏、不可移动、不可最大化/最小化（拍板 Q7）。</summary>
    private void ConfigureBorderlessPresenter()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
    }

    private double GetRasterizationScale()
    {
        var scale = (Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;
        return scale > 0 && double.IsFinite(scale) ? scale : 1.0;
    }

    /// <summary>左下锚定（不覆盖任务栏）；clampToWorkArea = 显示适配降级（窗口取工作区上限）。</summary>
    private void PlaceWindow(RectInt32 workArea, double scale, bool clampToWorkArea)
    {
        if (clampToWorkArea)
        {
            AppWindow.MoveAndResize(new RectInt32(workArea.X, workArea.Y, workArea.Width, workArea.Height));
            return;
        }

        var wallWidthPx = (int)Math.Round(_metrics.WallWidth(_wall.Columns) * scale);
        var wallHeightPx = (int)Math.Round(_metrics.WallHeight(_wall.Rows) * scale);
        var left = workArea.X;
        var top = workArea.Y + workArea.Height - wallHeightPx;
        AppWindow.MoveAndResize(new RectInt32(left, top, wallWidthPx, wallHeightPx));
    }

    /// <summary>竖栏网格底占位（M2 既有逻辑，尺寸源改为 _wall）。</summary>
    private void DrawWallGridBase()
    {
        WallCanvas.Children.Clear();
        WallCanvas.Width = _metrics.WallWidth(_wall.Columns);
        WallCanvas.Height = _metrics.WallHeight(_wall.Rows);
        EmptyHint.Margin = new Thickness(0, WallCanvas.Height * 0.28, 0, 0);

        var columnHeight = WallCanvas.Height - (2 * _metrics.Margin);
        var columnFill = (Brush)RootGrid.Resources["ColumnFillBrush"];
        var columnStroke = (Brush)RootGrid.Resources["ColumnStrokeBrush"];
        var gridLine = (Brush)RootGrid.Resources["GridLineBrush"];

        for (var n = 0; n < _wall.Columns; n++)
        {
            var x = _metrics.CellX(n * GridMetrics.CellsPerColumn);
            var column = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = _metrics.ColumnWidth,
                Height = columnHeight,
                Fill = columnFill,
                Stroke = columnStroke,
                StrokeThickness = 1,
            };
            Canvas.SetLeft(column, x);
            Canvas.SetTop(column, _metrics.Margin);
            WallCanvas.Children.Add(column);

            for (var row = 1; row < _wall.Rows; row++)
            {
                var horizontal = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = _metrics.ColumnWidth,
                    Height = 1,
                    Fill = gridLine,
                };
                Canvas.SetLeft(horizontal, x);
                Canvas.SetTop(horizontal, _metrics.CellY(row) - (_metrics.Gap / 2));
                WallCanvas.Children.Add(horizontal);
            }

            for (var cell = 1; cell < GridMetrics.CellsPerColumn; cell++)
            {
                var vertical = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = 1,
                    Height = columnHeight,
                    Fill = gridLine,
                };
                Canvas.SetLeft(vertical, x + (cell * _metrics.Pitch) - (_metrics.Gap / 2));
                Canvas.SetTop(vertical, _metrics.Margin);
                WallCanvas.Children.Add(vertical);
            }
        }
    }

    // ————————————————————————————— 指针手势薄封装（§5.2） —————————————————————————————

    private void HookPointerEvents()
    {
        TilesCanvas.AddHandler(UIElement.PointerPressedEvent, (PointerEventHandler)OnTilesPointerPressed, handledEventsToo: true);
        TilesCanvas.AddHandler(UIElement.PointerMovedEvent, (PointerEventHandler)OnTilesPointerMoved, handledEventsToo: true);
        TilesCanvas.AddHandler(UIElement.PointerReleasedEvent, (PointerEventHandler)OnTilesPointerReleased, handledEventsToo: true);
        TilesCanvas.AddHandler(UIElement.PointerCanceledEvent, (PointerEventHandler)OnTilesPointerLost, handledEventsToo: true);
        TilesCanvas.AddHandler(UIElement.PointerCaptureLostEvent, (PointerEventHandler)OnTilesPointerLost, handledEventsToo: true);
    }

    private void OnTilesPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(TilesCanvas);
        _lastPointerPos = new DipPoint(point.Position.X, point.Position.Y);
        if (!_layoutReady)
        {
            return;
        }

        var view = FindView(e.OriginalSource as DependencyObject);
        if (view is null)
        {
            return; // 背景命中：无对象手势
        }

        if (point.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            _machine.RightButton(); // T4：终止 Pressing，右键交由 ContextFlyout 弹对象菜单
            return;
        }

        var threshold = e.Pointer.PointerDeviceType == PointerDeviceType.Touch
            ? GestureThresholds.DragStartTouchDip // Q8：触摸尽力
            : GestureThresholds.DragStartMouseDip;
        _pointerGestureActive = true; // 本次手势派生的 Button.Click 将被抑制（激活判定归状态机 T3）
        _machine.Press(view.Object.Id, view.Object.Bounds, _commit!.Current.Objects, _lastPointerPos, threshold);
        if (_carouselCoordinator is { } coordinator)
        {
            coordinator.GestureActive = true; // M6 §7.5：指针手势期间暂缓切图（Pressing/Dragging 全程）
        }

        view.Root.CapturePointer(e.Pointer); // T1：捕获指针
        e.Handled = true;
    }

    private void OnTilesPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(TilesCanvas).Position;
        _lastPointerPos = new DipPoint(position.X, position.Y);
        if (_machine.State is GestureState.Dragging or GestureState.DraggingWithPreview)
        {
            _presenter.SetGhostPosition(_lastPointerPos); // B6：1:1 跟随
        }

        _machine.Move(_lastPointerPos);
    }

    private void OnTilesPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(TilesCanvas);
        var position = point.Position;
        if (FindView(e.OriginalSource as DependencyObject) is { } view)
        {
            view.Root.ReleasePointerCapture(e.Pointer);
        }

        _machine.Release(new DipPoint(position.X, position.Y)); // T3 点击判定在此（≤B1），Click 已被抑制
        _pointerGestureActive = false;
        if (_carouselCoordinator is { } coordinator)
        {
            coordinator.GestureActive = false; // M6 §7.5：手势结束 → 下一 tick/显式检查恢复
        }

        // M5 §11.3：对象 Button 吞掉右键 pointer 事件 → ContextFlyout 自动显示失效；右键释放处显式
        // 弹出对象菜单（A12：组从任意分块右键均为整组菜单）。背景右键仍走 RootGrid.ContextFlyout。
        if (point.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased
            && FindView(e.OriginalSource as DependencyObject) is { } menuView
            && !_modal.IsActive)
        {
            _menuTarget = menuView.Object;
            _objectMenu.ShowAt(menuView.Root, e.GetCurrentPoint(menuView.Root).Position);
            return;
        }

        e.Handled = true;
    }

    private void OnTilesPointerLost(object sender, PointerRoutedEventArgs e)
    {
        _pointerGestureActive = false;
        if (_carouselCoordinator is { } coordinator)
        {
            coordinator.GestureActive = false;
        }

        _machine.PointerLost();
    }

    private ObjectElementView? FindView(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { Tag: ObjectElementView view })
            {
                return view;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    // ————————————————————————————— IGestureHost（状态机出口） —————————————————————————————

    public void OnDragStarted(string objectId)
    {
        if (_presenter.GetView(objectId) is { } view)
        {
            _presenter.BeginDrag(view, _lastPointerPos, _machine.GrabOffset); // T2：ghost + 原位降隐
        }
    }

    public void OnPreviewReady(RelocationResult result)
    {
        _presenter.ShowInvalidMark(false);
        _presenter.ApplyPreview(result, animate: true); // T6 成功：200 ms EaseOut 位移动画
    }

    public void OnTargetInvalid(GridRect target) => _presenter.ShowInvalidMark(true);

    public void OnTargetChanged()
    {
        _presenter.ShowInvalidMark(false);
        _presenter.ClearPreview(); // T5/T7：预览失效即回原位，B4 重新计时（计时器由状态机重启）
    }

    /// <summary>T9 成功 / T10：预览=提交——引擎返回的同一 ResultObjects 实例进 Save（INV-7）。拖动为纯布局提交，不触碰入口文件。</summary>
    public void OnCommitted(RelocationResult result)
    {
        _presenter.EndDragVisuals();
        var previousMaterial = _commit!.UndoSlot?.EntryMaterial;
        var newConfig = _commit.Current with { Objects = result.ResultObjects!.ToArray() };
        if (_commit.Commit(newConfig, "拖动", out var failure))
        {
            DiscardPreviousEntryMaterial(previousMaterial);
            _presenter.FinalizeAll(_commit.Current.Objects);
            _machine.UpdateObjects(_commit.Current.Objects);
        }
        else
        {
            _presenter.ClearPreview();
            ShowTransientStatus($"保存失败，布局未更改：{failure}");
        }
    }

    public void OnDropRejected() => ShowTransientStatus("无法放置在此处");

    public void OnGestureCancelled() => _presenter.AbortGesture(); // T8/T12：全量恢复，配置与磁盘零改动

    /// <summary>T3 点击（§5.6）：M4 起 → EntryLauncher（ShellExecute/预检/收墙判定，§6.1）。</summary>
    public void OnObjectActivated(string objectId) => ActivateObject(objectId);

    /// <summary>
    /// 对象根 Button 的 Click（UIA Invoke / 键盘 Enter·空格）走同一点击出口（§3.1 要点 1、§5.6）。
    /// 指针手势期间产生的 Click 在此抑制：激活判定归 GestureMachine（T3 的 ≤B1 阈值，A08 拖后不启动）。
    /// </summary>
    private void OnObjectInvokeActivated(string objectId)
    {
        if (_pointerGestureActive || _machine.State != GestureState.Idle)
        {
            return; // 该 Click 由指针手势派生（Button 在 PointerReleased 处理中先行触发 Click）
        }

        ActivateObject(objectId);
    }

    /// <summary>
    /// 点击启动（M4 设计 §6.1）：空目标无动作不收墙；入口缺失/目标缺失/启动失败保留墙提示；
    /// ShellExecute 成功 → AppWindow.Hide() 收墙（M4 边界：托盘不在范围，本会话无唤回手段，§11 风险 2）。
    /// </summary>
    private void ActivateObject(string objectId)
    {
        if (_modal.IsActive)
        {
            return; // 模态会话期主墙零命令（§7.2 出口守卫）
        }

        var o = _commit?.Current.Objects.FirstOrDefault(x => x.Id == objectId);
        if (o?.Entry is null)
        {
            return; // 空目标：无动作、不收起墙（§12.1）
        }

        var fullPath = Path.Combine(_dataRoot, o.Entry.RelativePath);
        var result = _launcher.Launch(fullPath, _linkFiles);
        switch (result.Outcome)
        {
            case LaunchOutcome.Launched:
                HideAfterLaunch(); // C01：系统接受启动请求后收起墙（M7 起经状态机走 A7 统一路径）
                break;
            case LaunchOutcome.EntryMissing:
                ShowTransientStatus("入口文件缺失"); // §12.1：立即失败时保留墙并提示
                break;
            case LaunchOutcome.TargetMissing:
                ShowTransientStatus("启动目标缺失"); // 预检拒绝，保留墙
                break;
            case LaunchOutcome.ElevatedDeclined:
                ShowTransientStatus("已取消管理员启动"); // [R4]：不重试不改配置
                break;
            default:
                ShowTransientStatus($"启动失败：{result.FailureReason ?? "系统拒绝启动请求"}");
                break;
        }
    }

    // ————————————————————————————— 键盘：Esc 与 Ctrl+Z —————————————————————————————

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape
            && _machine.State is GestureState.Dragging or GestureState.DraggingWithPreview or GestureState.Pressing)
        {
            _machine.Escape(); // T8：全量恢复（B11：菜单打开时 MenuFlyout 先消费 Esc）
            e.Handled = true;
            return;
        }

        // B11 下一层级：Idle 可见态 Esc 收起（模态期墙收不到 Esc——焦点在会话窗）
        if (e.Key == VirtualKey.Escape
            && !_modal.IsActive
            && _showMachine.State == WallShowState.Visible)
        {
            _showMachine.Request(WallShowTrigger.Hide);
            e.Handled = true;
        }
    }

    private void OnUndoAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        UndoLast();
    }

    // ————————————————————————————— 菜单动作（§6） —————————————————————————————

    private void PopulateObjectMenu()
    {
        if (_menuTarget is not null)
        {
            WallMenuFactory.PopulateObjectMenu(
                _objectMenu,
                _menuTarget,
                new WallMenuFactory.ObjectMenuActions(OnUnpinRequested, OnEditRequested, OnElevatedRequested, OnLocationRequested),
                EntryDisabledReason);
        }
    }

    /// <summary>§6.2 启用矩阵的运行时判定：null = 入口可用；否则为置灰原因。</summary>
    private string? EntryDisabledReason(LayoutObject target)
    {
        if (target.Entry is null)
        {
            return "无托管入口";
        }

        return File.Exists(Path.Combine(_dataRoot, target.Entry.RelativePath)) ? null : "入口文件缺失";
    }

    private void OnEditRequested(string objectId)
    {
        if (_commit is null)
        {
            return;
        }

        var target = _commit.Current.Objects.FirstOrDefault(o => o.Id == objectId);
        if (target is GroupObject group)
        {
            OpenGroupPropertyWindow(group); // M5 §11.3：组的「编辑磁贴组」→ 组属性窗编辑模式
            return;
        }

        if (target is ClockObject clock)
        {
            OpenClockWindow(clock); // M8 §4.3：时钟的「编辑时间日期」→ 组件属性窗编辑模式
            return;
        }

        OpenPropertyWindow(target);
    }

    private void OnElevatedRequested(string objectId)
    {
        var entryPath = EntryFullPathOf(objectId);
        if (entryPath is null)
        {
            return;
        }

        var result = _launcher.LaunchElevated(entryPath);
        switch (result.Outcome)
        {
            case LaunchOutcome.Launched:
                HideAfterLaunch(); // runas 成功同样收墙（§6.1；M7 起经状态机）
                break;
            case LaunchOutcome.EntryMissing:
                ShowTransientStatus("入口文件缺失");
                break;
            case LaunchOutcome.ElevatedDeclined:
                ShowTransientStatus("已取消管理员启动"); // C42：取消后不重试不改配置
                break;
            default:
                ShowTransientStatus($"启动失败：{result.FailureReason ?? "系统拒绝提权启动请求"}");
                break;
        }
    }

    private void OnLocationRequested(string objectId)
    {
        var entryPath = EntryFullPathOf(objectId);
        if (entryPath is null)
        {
            return;
        }

        _launcher.RevealInExplorer(entryPath); // C29：explorer /select 定位托管文件
    }

    private string? EntryFullPathOf(string objectId)
    {
        var o = _commit?.Current.Objects.FirstOrDefault(x => x.Id == objectId);
        return o?.Entry is null ? null : Path.Combine(_dataRoot, o.Entry.RelativePath);
    }

    /// <summary>
    /// 「新建磁贴」（§8.1 升级）：不再直接建空磁贴，打开属性窗（创建模式，遮罩阻塞主墙）；
    /// 容量预检（firstFit）移入 DraftValidator（满墙 → 就地提示、窗口不关、零写入）。
    /// </summary>
    private void OnNewTileRequested()
    {
        if (!_layoutReady || _commit is null)
        {
            ShowTransientStatus("配置不可用（损坏或版本过高），已禁用布局修改（设计 §17.2）。");
            return;
        }

        if (_adaptationMode)
        {
            ShowTransientStatus("已保存布局超出当前工作区，请先调整布局（设计 §17.1 不裁切不删）。");
            return;
        }

        OpenPropertyWindow(null);
    }

    /// <summary>打开属性窗（创建/编辑共用；模态会话阻塞主墙，§7.2）。</summary>
    private void OpenPropertyWindow(LayoutObject? target)
    {
        if (_modal.IsActive || !_layoutReady || _commit is null)
        {
            return; // §3.3「重复请求只聚焦」由 ModalSessionService 处理；此处拦截新会话创建
        }

        if (_adaptationMode)
        {
            ShowTransientStatus("已保存布局超出当前工作区，请先调整布局（设计 §17.1 不裁切不删）。");
            return;
        }

        var otherRects = _commit.Current.Objects
            .Where(o => target is null || o.Id != target.Id)
            .Select(o => o.Bounds)
            .ToArray();
        string? currentEntryFullPath = null;
        string? currentEntryDisplay = null;
        if (target?.Entry is { } entry)
        {
            currentEntryFullPath = Path.Combine(_dataRoot, entry.RelativePath);
            currentEntryDisplay = DescribeEntry(entry.RelativePath);
        }

        var context = new PropertyWindowContext(
            _wall,
            otherRects,
            target,
            currentEntryFullPath,
            currentEntryDisplay,
            _linkFiles,
            draft => SaveDraft(target, draft));
        _modal.Open(new TilePropertyWindow(context, WinRT.Interop.WindowNative.GetWindowHandle(this)));
    }

    /// <summary>属性窗保存回调：联合提交（§5.5）；异常转错误文本就地显示（窗口不关、磁盘零残留）。</summary>
    private string? SaveDraft(LayoutObject? target, TileDraft draft)
    {
        if (_commit is null)
        {
            return "配置不可用（损坏或版本过高），已禁用修改（设计 §17.2）。";
        }

        try
        {
            var isCreate = target is null;
            var request = new EntryCommitRequest(
                target?.Id ?? StableId.NewId(),
                isCreate ? "新建磁贴" : "编辑磁贴",
                draft);
            var report = _entryCommits.Commit(_commit.Current, request);
            AdoptEntryCommit(report, request.ActionName);
            ShowTransientStatus(isCreate ? "已新建磁贴" : "已保存磁贴属性");
            return null;
        }
        catch (Exception ex) when (ex is DraftValidationException or ConfigValidationException or IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return ex.Message;
        }
    }

    /// <summary>联合提交成功后：覆盖单槽时清旧材料（§8.3）、Current 前移 + 建带材料撤销槽、全量渲染。</summary>
    private void AdoptEntryCommit(EntryCommitReport report, string actionName)
    {
        var previousMaterial = _commit!.UndoSlot?.EntryMaterial;
        if (previousMaterial is not null && report.UndoMaterial?.CommitId != previousMaterial.CommitId)
        {
            _entryCommits.DeleteMaterial(previousMaterial.CommitId);
        }

        _commit.Adopt(report.NewConfig, actionName, report.UndoMaterial);
        _presenter.RenderAll(_commit.Current);
        _machine.UpdateObjects(_commit.Current.Objects);
        UpdateEmptyHint();
    }

    // ————————————————————————————— 组属性窗（M5 §11.2/§11.3/§8.3） —————————————————————————————

    /// <summary>「新建磁贴组」右键生效（B02）：守卫链与新建磁贴同构；创建模式默认 4×4 十六块 1×1、链接空。</summary>
    private void OnNewGroupRequested()
    {
        if (!_layoutReady || _commit is null)
        {
            ShowTransientStatus("配置不可用（损坏或版本过高），已禁用布局修改（设计 §17.2）。");
            return;
        }

        if (_adaptationMode)
        {
            ShowTransientStatus("已保存布局超出当前工作区，请先调整布局（设计 §17.1 不裁切不删）。");
            return;
        }

        OpenGroupPropertyWindow(null);
    }

    /// <summary>打开组属性窗（创建/编辑共用；模态会话阻塞主墙；满墙无位由 GroupDraftValidator 就地 GROUP_NO_FIT、零写入，A17）。</summary>
    private void OpenGroupPropertyWindow(GroupObject? target)
    {
        if (_modal.IsActive || !_layoutReady || _commit is null)
        {
            return; // §3.3「重复请求只聚焦」由 ModalSessionService 处理；此处拦截新会话创建
        }

        if (_adaptationMode)
        {
            ShowTransientStatus("已保存布局超出当前工作区，请先调整布局（设计 §17.1 不裁切不删）。");
            return;
        }

        var otherRects = _commit.Current.Objects
            .Where(o => target is null || o.Id != target.Id)
            .Select(o => o.Bounds)
            .ToArray();
        string? currentEntryFullPath = null;
        string? currentEntryDisplay = null;
        if (target?.Entry is { } entry)
        {
            currentEntryFullPath = Path.Combine(_dataRoot, entry.RelativePath);
            currentEntryDisplay = DescribeEntry(entry.RelativePath);
        }

        var context = new GroupPropertyWindowContext(
            _wall,
            otherRects,
            target,
            currentEntryFullPath,
            currentEntryDisplay,
            _linkFiles,
            _files,
            draft => SaveGroupDraft(target, draft));
        _modal.Open(new GroupPropertyWindow(context, WinRT.Interop.WindowNative.GetWindowHandle(this)));
    }

    /// <summary>组属性窗保存回调：CommitGroup 联合提交（§8.3）；异常转错误文本就地显示（窗口不关、磁盘零残留）。</summary>
    private string? SaveGroupDraft(GroupObject? target, GroupEditDraft draft)
    {
        if (_commit is null)
        {
            return "配置不可用（损坏或版本过高），已禁用修改（设计 §17.2）。";
        }

        try
        {
            var isCreate = target is null;
            var request = new GroupCommitRequest(
                target?.Id ?? StableId.NewId(),
                isCreate ? "新建磁贴组" : "编辑磁贴组",
                draft);
            var report = _entryCommits.CommitGroup(_commit.Current, request);
            AdoptEntryCommit(report, request.ActionName); // 主墙 Ctrl+Z 可整体回滚这次组保存（§8.4）
            ShowTransientStatus(isCreate ? "已新建磁贴组" : "已保存磁贴组属性");
            return null;
        }
        catch (Exception ex) when (ex is DraftValidationException or ConfigValidationException or IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return ex.Message;
        }
    }

    // ————————————————————————————— M8：时间日期组件（§4） —————————————————————————————

    /// <summary>「添加时间日期」右键生效（§4.3）：守卫链与 OpenPropertyWindow 同构——适配期不允许新增对象（会加剧不容）。</summary>
    private void OpenClockCreator()
    {
        if (!_layoutReady || _commit is null)
        {
            ShowTransientStatus("配置不可用（损坏或版本过高），已禁用布局修改（设计 §17.2）。");
            return;
        }

        if (_adaptationMode)
        {
            ShowTransientStatus("已保存布局超出当前工作区，请先调整布局（设计 §17.1 不裁切不删）。");
            return;
        }

        OpenClockWindow(null);
    }

    /// <summary>组件属性窗（创建/编辑共用；模态会话阻塞主墙）。</summary>
    private void OpenClockWindow(ClockObject? target)
    {
        if (_modal.IsActive || !_layoutReady || _commit is null)
        {
            return; // §3.3「重复请求只聚焦」由 ModalSessionService 处理；此处拦截新会话创建
        }

        if (_adaptationMode)
        {
            ShowTransientStatus("已保存布局超出当前工作区，请先调整布局（设计 §17.1 不裁切不删）。");
            return;
        }

        var otherRects = _commit.Current.Objects
            .Where(o => target is null || o.Id != target.Id)
            .Select(o => o.Bounds)
            .ToArray();
        var context = new ClockPropertyWindowContext(
            _wall,
            otherRects,
            target,
            (bounds, draft) => SaveClock(target, bounds, draft));
        _modal.Open(new ClockPropertyWindow(context, WinRT.Interop.WindowNative.GetWindowHandle(this)));
    }

    /// <summary>
    /// 组件保存回调（§3.3 提交路径）：纯配置路径——ClockObject 追加/替换进 Current → LayoutCommitService.Commit
    /// （校验失败零落盘）。零入口文件操作，天然满足「不创建空 .lnk」。
    /// </summary>
    private string? SaveClock(ClockObject? target, GridRect bounds, ClockDraft draft)
    {
        if (_commit is null)
        {
            return "配置不可用（损坏或版本过高），已禁用修改（设计 §17.2）。";
        }

        var isCreate = target is null;
        var clock = new ClockObject
        {
            Id = target?.Id ?? StableId.NewId(),
            Bounds = bounds,
            Entry = null,
            Visual = new ObjectVisual
            {
                ShowTitle = !string.IsNullOrWhiteSpace(draft.TitleText),
                TitleText = string.IsNullOrWhiteSpace(draft.TitleText) ? null : draft.TitleText,
            },
        };
        var actionName = isCreate ? "添加时间日期" : "编辑时间日期";
        var newConfig = isCreate
            ? _commit.Current with { Objects = [.. _commit.Current.Objects, clock] }
            : _commit.Current with { Objects = [.. _commit.Current.Objects.Select(o => o.Id == clock.Id ? clock : o)] };

        if (!CommitLayout(newConfig, actionName, incrementalRender: null))
        {
            return "保存失败：尺寸无法放置或校验未通过（布局未更改）。"; // CommitLayout 已就地显示具体原因
        }

        ShowTransientStatus(isCreate ? "已添加时间日期" : "已保存时间日期属性");
        return null;
    }

    // ————————————————————————————— M8：开始菜单导入（§5） —————————————————————————————

    /// <summary>「从开始菜单导入」右键生效（§5.3）：模态单槽会话；来源查询全失败 → 如实提示不弹空窗。</summary>
    private void OpenImport()
    {
        if (_modal.IsActive || !_layoutReady || _commit is null)
        {
            return;
        }

        if (_adaptationMode)
        {
            ShowTransientStatus("已保存布局超出当前工作区，请先调整布局（设计 §17.1 不裁切不删）。");
            return;
        }

        var roots = new List<(string Path, SourceKind Source)>();
        if (_knownFolders.UserPrograms is { } userPrograms)
        {
            roots.Add((userPrograms, SourceKind.User));
        }

        if (_knownFolders.CommonPrograms is { } commonPrograms)
        {
            roots.Add((commonPrograms, SourceKind.Common));
        }

        if (roots.Count == 0)
        {
            ShowTransientStatus("无法定位开始菜单程序目录（已知文件夹查询失败，[R6] 不硬编码回退）。");
            return;
        }

        var context = new ImportWindowContext(
            roots,
            _wall,
            [.. _commit.Current.Objects.Select(o => o.Bounds)],
            new IconCache(_files, _dataRoot),
            _iconExtractor,
            ImportSelected);
        _modal.Open(new ImportWindow(context, WinRT.Interop.WindowNative.GetWindowHandle(this)));
    }

    /// <summary>导入确认（§7.1 时序）：N 候选一次 CommitBatch 协议执行；异常转错误文本就地显示（窗不关、勾选保留）。</summary>
    private string? ImportSelected(IReadOnlyList<StartMenuCandidate> selected)
    {
        if (_commit is null)
        {
            return "配置不可用（损坏或版本过高），已禁用修改（设计 §17.2）。";
        }

        try
        {
            var requests = selected.Select(c => new EntryCommitRequest(
                StableId.NewId(),
                "开始菜单导入",
                new TileDraft
                {
                    Size = new GridSize(1, 1), // 导入磁贴逐个 1×1 入墙（§5.5）
                    Entry = new EntryDraft.CopyFromFile(c.FullPath),
                })).ToList();
            var report = _entryCommits.CommitBatch(_commit.Current, requests, "开始菜单导入");
            AdoptEntryBatch(report);
            ShowTransientStatus($"已导入 {report.CommittedObjects.Count} 个磁贴");
            return null;
        }
        catch (Exception ex) when (ex is DraftValidationException or ConfigValidationException or IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return ex.Message;
        }
    }

    /// <summary>批量提交采纳（§5.7）：覆盖单槽时清旧材料、Current 前移 + 整批撤销材料、一次 RenderAll 全量重渲染。</summary>
    private void AdoptEntryBatch(EntryBatchCommitReport report)
    {
        var previousMaterial = _commit!.UndoSlot?.EntryMaterial;
        if (previousMaterial is not null && report.UndoMaterial.CommitId != previousMaterial.CommitId)
        {
            _entryCommits.DeleteMaterial(previousMaterial.CommitId);
        }

        _commit.Adopt(report.NewConfig, "开始菜单导入", report.UndoMaterial);
        _presenter.RenderAll(_commit.Current);
        _machine.UpdateObjects(_commit.Current.Objects);
        UpdateEmptyHint();
    }

    // ————————————————————————————— M8：显示适配接线（§6） —————————————————————————————

    /// <summary>「调整布局…」（提示窗按钮 → 模态单槽会话）：草稿编辑零提交；确认经 CommitAdaptation 单事务。</summary>
    private void OpenAdaptationAdjustWindow()
    {
        if (_exitInFlight || _commit is null || !_adaptationMode)
        {
            return;
        }

        if (_modal.IsActive)
        {
            FocusTopSession(); // 调整窗已开 → 聚焦（模态单槽语义）
            return;
        }

        var scale = GetRasterizationScale();
        var workArea = DisplayArea.Primary.WorkArea;
        var context = new AdaptationAdjustWindowContext(
            _commit.Current,
            _wall,
            _metrics,
            workArea.Width / scale,
            workArea.Height / scale,
            CommitAdaptationDraft);
        var adjust = new AdaptationAdjustWindow(context, WinRT.Interop.WindowNative.GetWindowHandle(this));
        _adaptAdjustWindow = adjust;
        adjust.Closed += (_, _) => _adaptAdjustWindow = null;
        _modal.Open(adjust);
    }

    /// <summary>
    /// 调整确认（§6.6/§7.2）：CommitAdaptation 单事务（N remove + 最终配置）→ 成功即经状态机
    /// <see cref="AdaptationMachine.AdjustmentCommitted"/> 退出适配态——RestoreWallAfterAdaptation
    /// 以新配置同步墙基线、重渲染并 Request(Show)（「确认 → 退出适配态 → 墙按新布局显示」）。
    /// 此后调整窗关闭的 SessionClosed 钩子再评估为幂等（Fitted + Fit 无宿主动作）。
    /// </summary>
    private string? CommitAdaptationDraft(IReadOnlyList<string> removedObjectIds, TileWallConfig finalConfig)
    {
        if (_commit is null)
        {
            return "配置不可用（损坏或版本过高），已禁用修改（设计 §17.2）。";
        }

        try
        {
            var report = _entryCommits.CommitAdaptation(_commit.Current, removedObjectIds, finalConfig, "调整布局以适配显示");
            var previousMaterial = _commit.UndoSlot?.EntryMaterial;
            if (previousMaterial is not null && report.UndoMaterial?.CommitId != previousMaterial.CommitId)
            {
                _entryCommits.DeleteMaterial(previousMaterial.CommitId);
            }

            _commit.Adopt(report.NewConfig, "调整布局以适配显示", report.UndoMaterial);
            // 修复（评审①）：确认成功必须经状态机退出适配态——AdjustmentCommitted → DismissPrompt +
            // RestoreWallAfterAdaptation（内部以 _commit.Current 重建 _wall 并整墙重渲染）。
            // 缺此调用时评估仍读旧 _wall（缩小前的墙）恒 NoFit，适配态永不退出。
            _adaptationMachine.AdjustmentCommitted();
            ShowTransientStatus("已按新布局适配显示");
            return null;
        }
        catch (Exception ex) when (ex is ConfigValidationException or IOException or InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return ex.Message; // F-A1：提交失败全回滚（原布局配置不动），错误就地显示
        }
    }

    /// <summary>§6.3：适配期 Show 类输入改弹/聚焦提示窗（墙本体不渲染被裁切内容）；先兜底评估——已恢复则直接显示。</summary>
    private void ShowOrFocusDuringAdaptation()
    {
        EvaluateAdaptationNow(); // 每次墙将显示前强制评估（兜底，R-2）；Fit 时 Restore 内已 Request(Show)
        if (!_adaptationMode)
        {
            return;
        }

        if (_modal.IsActive)
        {
            FocusTopSession(); // 调整窗开着 → 聚焦
            return;
        }

        if (_adaptPromptWindow is { } prompt)
        {
            prompt.Activate();
            return;
        }

        // 曾「稍后处理」：热键/托盘 → 重开提示窗（机器仍在 AdaptationNeeded，快照为最近一次 NoFit）
        if (_adaptationMachine.LastNoFitSnapshot is { } snapshot)
        {
            ((IAdaptationHost)this).ShowPrompt(snapshot);
        }
    }

    /// <summary>§8：适配期背景菜单两新增项置灰 + 原因（菜单结构不动，HelpText 供 UIA 断言）。</summary>
    private void UpdateBackgroundMenuForAdaptation()
    {
        foreach (var item in _backgroundMenu.Items.OfType<MenuFlyoutItem>())
        {
            var id = AutomationProperties.GetAutomationId(item);
            if (id is not "menu-blank-import" and not "menu-blank-datetime")
            {
                continue;
            }

            if (_adaptationMode)
            {
                const string reason = "显示适配期不允许新增对象（当前布局放不下，请先调整布局）";
                item.IsEnabled = false;
                ToolTipService.SetToolTip(item, reason);
                AutomationProperties.SetHelpText(item, reason);
            }
            else if (!item.IsEnabled)
            {
                item.IsEnabled = true;
                ToolTipService.SetToolTip(item, null);
                AutomationProperties.SetHelpText(item, null);
            }
        }
    }

    // ————————————————————————————— M8：IAdaptationHost（适配状态机出口） —————————————————————————————

    void IAdaptationHost.ShowPrompt(AdaptationSnapshot snapshot)
    {
        if (_adaptPromptWindow is { } existing)
        {
            existing.UpdateSnapshot(snapshot);
            existing.Activate();
            return;
        }

        var prompt = new AdaptationPromptWindow(snapshot, WinRT.Interop.WindowNative.GetWindowHandle(this));
        prompt.AdjustRequested += OpenAdaptationAdjustWindow;
        prompt.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_adaptPromptWindow, prompt))
            {
                return;
            }

            _adaptPromptWindow = null;
            _adaptationMachine.PromptDismissed(); // 「稍后处理」/手动关闭：墙保持隐藏，Show 类输入重开提示窗
        };
        _adaptPromptWindow = prompt;
        prompt.Activate();
    }

    void IAdaptationHost.DismissPrompt()
    {
        var prompt = _adaptPromptWindow;
        _adaptPromptWindow = null;
        prompt?.Close();
    }

    void IAdaptationHost.RefreshPrompt(AdaptationSnapshot snapshot) => _adaptPromptWindow?.UpdateSnapshot(snapshot);

    bool IAdaptationHost.HasActiveSession => _modal.IsActive;

    bool IAdaptationHost.IsPromptVisible => _adaptPromptWindow is not null;

    void IAdaptationHost.HideWallForAdaptation()
    {
        CancelGestureIfAny(); // T12：在飞手势全量恢复（§6.3 host.OnEnterAdaptation）
        _adaptationMode = true;
        _layoutReady = false; // 全部墙命令出口既有守卫生效
        if (_showMachine.State is not (WallShowState.Hidden or WallShowState.Hiding))
        {
            _showMachine.Request(WallShowTrigger.Hide); // 经状态机走 A7 统一路径 → ClockTickDriver/轮播停表
        }
    }

    void IAdaptationHost.RestoreWallAfterAdaptation()
    {
        _adaptationMode = false;
        _layoutReady = true;
        if (_commit is not null)
        {
            _wall = new WallGrid(_commit.Current.Wall.Columns, _commit.Current.Wall.Rows);
            _machine.UpdateWall(_wall);
            _machine.UpdateObjects(_commit.Current.Objects);
            _presenter.Initialize(_wall);
        }

        PlaceWindow(DisplayArea.Primary.WorkArea, GetRasterizationScale(), clampToWorkArea: false);
        DrawWallGridBase();
        if (_commit is not null)
        {
            _presenter.RenderAll(_commit.Current); // §6.5：按（原/新）布局重渲染，无任何自动修改（A14）
            if (_carouselDriver is null)
            {
                StartCarousel(_commit); // 启动即适配、运行中恢复的补装配（轮播驱动此前未建）
            }
        }

        UpdateEmptyHint();
        _showMachine.Request(WallShowTrigger.Show); // 恢复显示必经状态机（R-6，M7 W3 门控语义不变）
    }

    /// <summary>撤销后重评估：恢复的旧配置若超出当前工作区 → 重新进入适配态（不渲染被裁切墙）。</summary>
    private void EvaluateAfterUndo()
    {
        if (_layoutReady)
        {
            EvaluateAdaptationNow();
        }
    }


    /// <summary>入口显示文本（§7.1）：.lnk → 归一化目标 + 参数；IDList → 特殊项说明；.url → URL 行。</summary>
    private string DescribeEntry(string relativePath)
    {
        var fullPath = Path.Combine(_dataRoot, relativePath);
        try
        {
            switch (EntryNames.KindOfRelativePath(relativePath))
            {
                case EntryKind.Lnk:
                    var fields = _linkFiles.Read(fullPath);
                    if (fields.HasIdList)
                    {
                        return "（特殊 Shell 入口：目标不可编辑，仅可整体替换）";
                    }

                    var target = ShellLinkFileService.NormalizePath(fields.TargetPath ?? string.Empty);
                    return string.IsNullOrEmpty(fields.Arguments) ? target : $"{target} {fields.Arguments}";
                case EntryKind.Url:
                    var url = UrlShortcut.ReadUrlLine(_files.ReadAllBytes(fullPath));
                    return url ?? "（未找到 URL 行）";
            }
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // 读取失败（含损坏 .lnk 的 COMException）降级为文件名显示
        }

        return Path.GetFileName(relativePath);
    }

    /// <summary>取消固定（§7.2/§8.2）：有入口对象改走联合提交的 remove 路径（入口入 Recovery + 配置移除一次 Save）；空目标保持 M3 纯配置路径。</summary>
    private void OnUnpinRequested(string objectId)
    {
        if (_modal.IsActive || _commit is null)
        {
            return;
        }

        var target = _commit.Current.Objects.FirstOrDefault(o => o.Id == objectId);
        if (target is null)
        {
            return;
        }

        var actionName = target is GroupObject ? "取消固定磁贴组" : "从磁贴墙取消固定";
        if (target.Entry is not null)
        {
            try
            {
                var report = _entryCommits.RemoveEntry(_commit.Current, objectId, actionName);
                AdoptEntryCommit(report, actionName);
                ShowTransientStatus($"已取消固定（入口副本短暂保留，§12.4）");
            }
            catch (Exception ex) when (ex is ConfigValidationException or IOException or InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                ShowTransientStatus($"保存失败，布局未更改：{ex.Message}");
            }

            return;
        }

        var newConfig = _commit.Current with { Objects = _commit.Current.Objects.Where(o => o.Id != objectId).ToArray() };
        CommitLayout(newConfig, actionName, _ => _presenter.RemoveObject(objectId));
    }

    /// <summary>提交 + 渲染编排：增量回调（拖动/新建/取消固定）或全量 RenderAll（撤销）。纯布局提交不触碰入口文件。</summary>
    private bool CommitLayout(TileWallConfig newConfig, string actionName, Action<TileWallConfig>? incrementalRender)
    {
        var previousMaterial = _commit!.UndoSlot?.EntryMaterial;
        if (!_commit.Commit(newConfig, actionName, out var failure))
        {
            ShowTransientStatus($"保存失败，布局未更改：{failure}");
            return false;
        }

        DiscardPreviousEntryMaterial(previousMaterial);
        if (incrementalRender is null)
        {
            _presenter.RenderAll(_commit.Current);
        }
        else
        {
            incrementalRender(_commit.Current);
        }

        _machine.UpdateObjects(_commit.Current.Objects);
        UpdateEmptyHint();
        return true;
    }

    /// <summary>§8.3：新提交覆盖单槽时删除上一份 Recovery/Entries 材料（启动清扫兜底，§5.6）。</summary>
    private void DiscardPreviousEntryMaterial(EntryUndoMaterial? previousMaterial)
    {
        var currentMaterial = _commit!.UndoSlot?.EntryMaterial;
        if (previousMaterial is not null && currentMaterial?.CommitId != previousMaterial.CommitId)
        {
            _entryCommits.DeleteMaterial(previousMaterial.CommitId);
        }
    }

    // ————————————————————————————— 撤销（§7.3） —————————————————————————————

    /// <summary>Ctrl+Z（§7.3/§8.3）：纯布局槽走现行配置撤销；带入口材料的槽先恢复入口文件再回滚配置。</summary>
    private void UndoLast()
    {
        if (!_layoutReady || _commit is null || _machine.State != GestureState.Idle || _modal.IsActive)
        {
            return; // 仅 Idle 态响应（拖动中按 Esc 负责取消）；模态期主墙零命令
        }

        if (!_commit.TryPeekUndo(out var slot))
        {
            ShowTransientStatus("没有可撤销的操作"); // §15.3：不无声失败
            return;
        }

        var material = slot.EntryMaterial;
        if (material is not null && !_entryCommits.RestoreMaterial(material))
        {
            ShowTransientStatus("撤销中止：入口文件还原失败，已保留现状（宁可不撤销，不留混合态）");
            return; // F-12：配置不回滚、材料保留供重试
        }

        var actionName = slot.ActionName;
        if (_commit.Commit(slot.Previous, "撤销", out var failure))
        {
            _commit.ClearUndoSlot(); // 撤销本身不再可重做（最小撤销范围）
            if (material is not null)
            {
                _entryCommits.DeleteMaterial(material.CommitId); // 文件与配置都已还原 → 材料移交完成
            }

            // 修复（评审②）：墙维度可能随适配提交的撤销而回退（如 Ctrl+Z 撤销「调整布局」）——
            // 必须先以恢复后的配置同步墙基线并重摆窗/重画网格底，再渲染对象；
            // 否则大墙配置按缩小后的旧 _wall 渲染即被裁切（违反 §17.1），且下方重评估会读旧墙失真。
            _wall = new WallGrid(_commit.Current.Wall.Columns, _commit.Current.Wall.Rows);
            _machine.UpdateWall(_wall);
            _machine.UpdateObjects(_commit.Current.Objects);
            _presenter.Initialize(_wall);
            PlaceWindow(DisplayArea.Primary.WorkArea, GetRasterizationScale(), clampToWorkArea: false);
            DrawWallGridBase();
            _presenter.RenderAll(_commit.Current);
            UpdateEmptyHint();
            ShowTransientStatus($"已撤销：{actionName}");
            EvaluateAfterUndo(); // 以同步后的 _wall 重评估：恢复的大墙放不下 → 重新进入适配态（§17.1）
        }
        else
        {
            ShowTransientStatus($"保存失败，布局未更改：{failure}");
        }
    }

    // ————————————————————————————— 状态提示与空墙提示 —————————————————————————————

    /// <summary>一次性提示（容量拒绝/撤销/失败原因）：非模态、约 5 秒自动消失。</summary>
    private void ShowTransientStatus(string message)
    {
        _statusTimer.Stop();
        StatusHint.Message = message;
        AutomationProperties.SetName(StatusHint, message);
        StatusHint.IsClosable = true;
        StatusHint.IsOpen = true;
        _statusTimer.Start();
    }

    /// <summary>常驻诊断（恢复链/显示适配/启动清扫）：不清除。</summary>
    private void ShowPersistentStatus(string message)
    {
        _statusTimer.Stop();
        StatusHint.Message = message;
        AutomationProperties.SetName(StatusHint, message);
        StatusHint.IsClosable = false;
        StatusHint.IsOpen = true;
    }

    /// <summary>M7：多段常驻启动提示合并（避免后段覆盖前段——InfoBar 单条消息）。</summary>
    private void ShowStartupStatus(params string?[] parts)
    {
        var lines = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (lines.Length > 0)
        {
            ShowPersistentStatus(string.Join(Environment.NewLine, lines));
        }
    }

    /// <summary>M7（§5.3 C09）：快捷键注册状态的墙内 InfoBar 文案；null = 已注册（无提示）。</summary>
    private string? HotKeyStatusLine()
    {
        if (_shell.HotKeys.IsRegistered)
        {
            return null;
        }

        var configured = _shell.ConfiguredHotKey;
        if (configured is null)
        {
            return "未配置显示磁贴墙的快捷键；可在「设置」中录入（托盘图标右键 → 设置）。";
        }

        if (HotKeyGesture.TryParse(configured, out var gesture, out _) && gesture is not null)
        {
            return $"快捷键 {gesture.ToDisplayString()} 注册失败：{_shell.HotKeys.LastFailureReason ?? "组合可能被占用"}；"
                + "可在「设置」中更换（托盘图标右键 → 设置）。";
        }

        return $"快捷键配置无效（{configured}），未注册；可在「设置」中录入（托盘图标右键 → 设置）。";
    }

    /// <summary>加载诊断 + M4 启动清扫报告合并（App 在 Load() 之前执行 EntryRecovery.Sweep，§5.6）。</summary>
    private string ComposeStartupDiagnostics(IReadOnlyList<string> loadDiagnostics, string? hotkeyLine = null)
    {
        var lines = new List<string>(loadDiagnostics);
        if (_recoveryDiagnostics.Count > 0)
        {
            lines.Add("启动清扫（EntryRecovery）：");
            lines.AddRange(_recoveryDiagnostics);
        }

        if (!string.IsNullOrWhiteSpace(hotkeyLine))
        {
            lines.Add(hotkeyLine);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void UpdateEmptyHint()
    {
        var empty = _commit is null || _commit.Current.Objects.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed; // §4：对象数 > 0 → wall-empty-hint 消失
    }

    // ————————————————————————————— M7：Shell 命令路由（§6.2 输入路由表） —————————————————————————————

    /// <summary>呼出热键（WM_HOTKEY → 唯一汇聚点）。模态期只聚焦会话不切显隐（§14.4）；适配期改弹提示窗（§6.3）。</summary>
    public void ToggleWall()
    {
        if (_exitInFlight)
        {
            return;
        }

        if (_adaptationMode)
        {
            ShowOrFocusDuringAdaptation(); // §6.3：Show 类输入改弹/聚焦适配提示窗，墙本体不渲染被裁切内容
            return;
        }

        if (_modal.IsActive)
        {
            FocusTopSession(); // 录入态由窗内 Esc 处理；热键只负责聚焦（§6.2 录入中行）
            return;
        }

        CancelGestureIfAny(); // 拖动中：先取消拖动（T12 零改动）再切换（设计决策注记 1）
        _showMachine.Request(WallShowTrigger.Toggle);
    }

    /// <summary>托盘左双击 / 二次启动汇入（§3.3 同一条命令）：显示或聚焦现有模态，绝不收起；适配期改弹提示窗。</summary>
    public void ShowOrFocus()
    {
        if (_exitInFlight)
        {
            return;
        }

        if (_adaptationMode)
        {
            ShowOrFocusDuringAdaptation(); // §6.3：与热键同路（重开/聚焦提示窗）
            return;
        }

        if (_modal.IsActive)
        {
            FocusTopSession(); // §14.2：主墙仍不可交互，只聚焦会话
            return;
        }

        CancelGestureIfAny(); // 防御（指针被墙捕获时该输入实际不可达，§6.2 表注）
        _showMachine.Request(WallShowTrigger.Show);
    }

    /// <summary>
    /// 打开设置（§8.5 两入口汇同一路径）：托盘菜单 / 墙面右键 menu-blank-settings。
    /// 墙隐藏时走「预置模态」序列——Attach（ModalActive 先于墙 Shown 生效）→ 显示墙为背景 → ActivateTop。
    /// </summary>
    public void OpenSettings()
    {
        if (_exitInFlight)
        {
            return;
        }

        if (_modal.IsActive)
        {
            FocusTopSession(); // 设置窗已开→聚焦；属性窗会话在→聚焦属性窗（先聚焦当前会话）
            return;
        }

        var settings = CreateSettingsWindow();
        if (_showMachine.State is WallShowState.Hidden or WallShowState.Hiding)
        {
            CancelGestureIfAny();
            _modal.Attach(settings); // ModalActive=true → 显式检查被 CanSwitchNow=false 短路（§3.4）
            _showMachine.Request(WallShowTrigger.Show); // 恢复背景（顺带把 Hiding 打断落 Visible）
            _modal.ActivateTop(); // owned 关系保证恒在墙之上
        }
        else
        {
            _modal.Open(settings);
        }
    }

    /// <summary>退出入口（托盘菜单 / 墙窗 Alt+F4）；重入防御。</summary>
    public void RequestExit()
    {
        if (_exitInFlight)
        {
            return;
        }

        _exitInFlight = true;
        _ = RequestExitCoreAsync();
    }

    private async Task RequestExitCoreAsync()
    {
        try
        {
            var participant = _modal.CurrentSessionWindow as IExitParticipant;
            if (participant is not null)
            {
                if (_exit.ShouldConfirmDraft(participant))
                {
                    var decision = await ShowExitDecisionDialogAsync(); // 草稿三分支（§9.1）
                    if (!_exit.ApplyDecision(participant, decision))
                    {
                        return; // 取消/保存失败：原窗口与阻塞状态均不变（§3.5 / §14.4）
                    }
                }
                else
                {
                    _exit.CloseDraftlessSession(); // 设置窗：已自动保存项不重复确认，直接关闭会话
                }
            }

            _exit.RunTeardown(); // 固定释放序 → Application.Exit（进程结束）
        }
        finally
        {
            _exitInFlight = false;
        }
    }

    private async Task<ExitDecision> ShowExitDecisionDialogAsync()
    {
        var sessionWindow = _modal.CurrentSessionWindow!;
        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)sessionWindow.Content).XamlRoot,
            Title = "退出 TileWall",
            Content = "有未保存的草稿。退出前如何处理？",
            PrimaryButtonText = "保存后退出",
            SecondaryButtonText = "放弃更改并退出",
            CloseButtonText = "取消退出",
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "exit-draft-dialog");
        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => ExitDecision.SaveAndExit,
            ContentDialogResult.Secondary => ExitDecision.DiscardAndExit,
            _ => ExitDecision.CancelExit,
        };
    }

    private void FocusTopSession() => _modal.CurrentSessionWindow?.Activate();

    /// <summary>收起类输入的统一前置：拖动进行中先 T12 全量恢复（配置零改动），返回是否有手势被取消。</summary>
    private bool CancelGestureIfAny()
    {
        if (_machine.State == GestureState.Idle)
        {
            return false;
        }

        _machine.PointerLost();
        _pointerGestureActive = false;
        if (_carouselCoordinator is { } coordinator)
        {
            coordinator.GestureActive = false;
        }

        return true;
    }

    /// <summary>点击启动收墙（两处 LaunchOutcome.Launched 出口）：M7 起经状态机走 A7 统一收起路径（§6.2 注 3）。</summary>
    private void HideAfterLaunch() => _showMachine.Request(WallShowTrigger.Hide);

    // ————————————————————————————— M7：失焦收起与 Alt+F4 转接 —————————————————————————————

    /// <summary>
    /// 失焦收起单机制（§6.4）：墙外点击 / 切应用 / 系统开始菜单三场景统一为 Window.Deactivated。
    /// 「不干扰新前景」= 收起路径只 AppWindow.Hide()，绝不 Activate/SetForegroundWindow 墙窗。
    /// </summary>
    private void OnWindowDeactivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            return;
        }

        if (_exitInFlight || _modal.IsActive)
        {
            return; // §14.4：模态保留——失焦不关窗不藏墙，不向其他应用抢焦点
        }

        if (_showMachine.State != WallShowState.Visible)
        {
            return; // Hidden 忽略重复 Hide（与点击启动收起幂等收敛，§6.2 注 3）
        }

        CancelGestureIfAny(); // 先取消拖动（T12 语义）再收起
        _showMachine.Request(WallShowTrigger.Hide);
    }

    /// <summary>Alt+F4 → 统一退出流程（§9.1 / §14 风险 8）；取消退出则窗保持。程序化 Close 不触发本事件，无递归再入。</summary>
    private void OnAppWindowClosingExit(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        RequestExit();
    }

    // ————————————————————————————— M7：IWallShowHost（显隐状态机出口） —————————————————————————————

    void IWallShowHost.BeginShowAnimation() => _animator.BeginShow(); // A6：200ms 淡入上移（物理已可见）

    void IWallShowHost.BeginHideAnimation() => _animator.BeginHide(); // A7：150ms 淡出（物理仍可见）

    void IWallShowHost.CancelAnimations() => _animator.Cancel(); // A8 打断

    void IWallShowHost.SnapShow()
    {
        _animator.ResetVisual();
        if (_activatedOnce)
        {
            AppWindow.Show(); // 物理终态唯一出口（WallVisibility.Shown → 轮播恢复，M6 钩子零改动）
            return;
        }

        _activatedOnce = true;
        Activate(); // --background 首显：延迟 Activate 补跑 Loaded→Bootstrap（§2.2 第 8 步；§14 风险 4）
    }

    /// <summary>手动启动的首显标记（App 调 Activate 后调用）：此后隐藏→唤回走 AppWindow.Show 直达——
    /// Activate 不能可靠撤销 AppWindow.Hide 的隐藏（失焦收起后托盘/热键/二次启动唤回的正确路径）。</summary>
    public void MarkInitialActivation() => _activatedOnce = true;

    void IWallShowHost.SnapHide()
    {
        _animator.ResetVisual();
        AppWindow.Hide(); // 渲染停止点唯一（W2）；驱动器停表 + SettleFlips（M6 既有）
    }

    void IWallShowHost.FocusWall() => Activate(); // Visible 态托盘双击：聚焦不收起

    // ————————————————————————————— M7：IExitHost（退出序列宿主动作，§10 固定释放序） —————————————————————————————

    IExitParticipant? IExitHost.TopParticipant => _modal.CurrentSessionWindow as IExitParticipant;

    void IExitHost.CloseTopSession() => _modal.CurrentSessionWindow?.Close();

    void IExitHost.CancelTransientState()
    {
        CancelGestureIfAny(); // Fallout 兜底：在飞手势全量恢复
        _backgroundMenu.Hide();
        _objectMenu.Hide();
        var prompt = _adaptPromptWindow; // 适配提示窗不占模态槽，退出序列就地收口
        _adaptPromptWindow = null;
        prompt?.Close();
    }

    void IExitHost.SnapHideWall()
    {
        _animator.ResetVisual();
        AppWindow.Hide(); // 无动画直达（退出序列的 Hidden 化）
    }

    void IExitHost.UnregisterHotKey() => _shell.HotKeys.Unregister(); // R5：先于托盘/宿主窗销毁（C11）

    void IExitHost.RemoveTrayIcon() => _shell.Tray.Dispose(); // R1+R2：NIM_DELETE + DestroyIcon

    void IExitHost.DestroyShellHostWindow() => _shell.MessageHost.Dispose(); // R4

    void IExitHost.ReleaseSingleInstanceMutex() => _shell.SingleInstance.Release(); // R7：显式双保险

    void IExitHost.ExitApplication() => Application.Current.Exit();

    // ————————————————————————————— M7：设置窗装配与换绑（§3.4 / §5.4 / §8） —————————————————————————————

    private SettingsWindow CreateSettingsWindow() => new(
        new SettingsWindowContext(
            CurrentHotKeyText: _commit?.Current.Settings.HotKey ?? _loadResult.Config?.Settings.HotKey ?? new AppSettings().HotKey,
            Registrar: _shell.HotKeys,
            ApplyHotKey: ApplyHotKeyBinding,
            LoginStartup: _shell.LoginStartup,
            VersionText: AppVersionText()),
        WinRT.Interop.WindowNative.GetWindowHandle(this));

    /// <summary>
    /// 换绑（§5.4 不变量）：注册+保存双成功才替换；任一失败回滚到旧有效值——
    /// 注册失败 → TryRegister(旧)；保存失败 → 回滚注册（配置不动，下次启动仍尝试旧组合）；
    /// 回滚也失败 → 如实呈现「未注册」（C09：托盘双击与设置入口无条件可用）。保存走 SaveWithoutUndo 不产生撤销槽。
    /// </summary>
    private string? ApplyHotKeyBinding(string configText)
    {
        if (_commit is null)
        {
            return "配置不可用（损坏或版本过高），已禁用修改（设计 §17.2）。";
        }

        if (!HotKeyGesture.TryParse(configText, out var newGesture, out var parseFailure) || newGesture is null)
        {
            return parseFailure ?? "无效的快捷键组合";
        }

        var oldGesture = HotKeyGesture.TryParse(_commit.Current.Settings.HotKey, out var parsedOld, out _)
            ? parsedOld
            : null;
        if (!_shell.HotKeys.TryRegister(newGesture))
        {
            RestoreOldBinding(oldGesture);
            return _shell.HotKeys.LastFailureReason ?? "注册失败";
        }

        var newConfig = _commit.Current with { Settings = _commit.Current.Settings with { HotKey = configText } };
        if (!_commit.SaveWithoutUndo(newConfig, out var saveFailure))
        {
            RestoreOldBinding(oldGesture); // 设置变更不是布局操作：不产生撤销槽（§5.4）
            return $"已回滚到原组合：保存失败（{saveFailure}）";
        }

        return null;
    }

    private void RestoreOldBinding(HotKeyGesture? oldGesture)
    {
        if (oldGesture is not null && HotKeyGesture.IsValid(oldGesture))
        {
            _ = _shell.HotKeys.TryRegister(oldGesture); // 回滚旧组合（旧也失败 → 未注册，如实呈现）
        }
        else
        {
            _shell.HotKeys.Unregister(); // 配置本无有效组合 → 回到未注册态
        }
    }

    private static string AppVersionText()
    {
        var assembly = typeof(MainWindow).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString(3) ?? "0.0.0"
            : informational;
    }

    // ————————————————————————————— IPreviewTimer（B4 计时） —————————————————————————————

    void IPreviewTimer.Start()
    {
        _previewTimer.Stop(); // 重启：目标格变化即重置 250 ms 窗口
        _previewTimer.Start();
    }

    void IPreviewTimer.Stop() => _previewTimer.Stop();
}
