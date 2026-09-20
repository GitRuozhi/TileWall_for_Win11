using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
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
/// （渲染/手势/布局提交/撤销）+ M4 的接线（托管入口引擎、点击启动、属性窗模态、联合提交与撤销恢复）。
/// 本类只做「事件 → 状态机/服务 → 渲染」的薄封装，不复制任何几何/腾位/校验/入口协议逻辑。
/// </summary>
public sealed partial class MainWindow : Window, IGestureHost, IPreviewTimer
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
        IReadOnlyList<string>? recoveryDiagnostics = null)
    {
        _store = store;
        _loadResult = loadResult;
        _seedRequested = seedRequested;
        _files = files;
        _linkFiles = linkFiles;
        _recoveryDiagnostics = recoveryDiagnostics ?? [];

        InitializeComponent();
        Title = "TileWall";
        ConfigureBorderlessPresenter();

        _dataRoot = Path.GetDirectoryName(store.ConfigPath) ?? string.Empty;
        _entryCommits = new EntryCommitService(_files, store, _linkFiles, new EnvironmentDataDirectoryProvider(_dataRoot));
        _modal = new ModalSessionService(RootGrid);
        _objectMenu = new MenuFlyout();
        _objectMenu.Opening += (_, _) => PopulateObjectMenu();
        _presenter = new WallPresenter(TilesCanvas, DragLayer, _metrics) { ObjectMenu = _objectMenu };

        _previewTimer = DispatcherQueue.CreateTimer();
        _previewTimer.Interval = TimeSpan.FromMilliseconds(GestureThresholds.PreviewHoverDelayMs); // B4：250 ms
        _previewTimer.IsRepeating = false;

        _machine = new GestureMachine(_metrics, this, this);
        _previewTimer.Tick += (_, _) => _machine.PreviewTimerDue();

        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(5);
        _statusTimer.IsRepeating = false;
        _statusTimer.Tick += (_, _) => StatusHint.IsOpen = false;

        _backgroundMenu = WallMenuFactory.CreateBackgroundMenu(OnNewTileRequested);
        RootGrid.ContextFlyout = _backgroundMenu;
        _presenter.ObjectContextRequested += (view, _) => _menuTarget = view.Object;
        _presenter.ObjectActivated += OnObjectInvokeActivated;

        ((FrameworkElement)Content).Loaded += OnContentLoaded;
        HookPointerEvents();
        RootGrid.KeyDown += OnRootKeyDown;
    }

    // ————————————————————————————— 启动装配（§2.2） —————————————————————————————

    private void OnContentLoaded(object sender, RoutedEventArgs e) => Bootstrap();

    private void Bootstrap()
    {
        var scale = GetRasterizationScale();
        var workArea = DisplayArea.Primary.WorkArea;
        var workWidthDip = workArea.Width / scale;
        var workHeightDip = workArea.Height / scale;

        switch (_loadResult.Status)
        {
            case ConfigLoadStatus.Fresh:
            {
                var (columns, rows) = WallSizing.InitialForWorkArea(workWidthDip, workHeightDip, _metrics);
                if (columns < 1 || rows < 1)
                {
                    ShowPersistentStatus("工作区容不下一栏八格，磁贴墙无法显示（设计 §4.5、A18）。");
                    return;
                }

                var config = TileWallConfig.CreateInitial(columns, rows);
                EnterReadyState(config, workArea, scale, workWidthDip, workHeightDip, saveFirst: true);
                break;
            }
            case ConfigLoadStatus.Loaded:
            case ConfigLoadStatus.RecoveredFromBackup:
            {
                if (_loadResult.Status == ConfigLoadStatus.RecoveredFromBackup)
                {
                    ShowPersistentStatus(ComposeStartupDiagnostics(_loadResult.Diagnostics));
                }

                EnterReadyState(_loadResult.Config!, workArea, scale, workWidthDip, workHeightDip, saveFirst: false);
                if (_loadResult.Status == ConfigLoadStatus.Loaded && _recoveryDiagnostics.Count > 0)
                {
                    ShowPersistentStatus(ComposeStartupDiagnostics([])); // 启动清扫报告（回滚/清理动作）
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
                ShowPersistentStatus(string.Join(Environment.NewLine, _loadResult.Diagnostics));
                return;
            }
        }
    }

    /// <summary>Fresh / Loaded 共用：种子注入（#if DEBUG）→ 首启/种子保存 → 窗口定位 → 渲染。</summary>
    private void EnterReadyState(
        TileWallConfig config,
        RectInt32 workArea,
        double scale,
        double workWidthDip,
        double workHeightDip,
        bool saveFirst)
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

        // 显示适配最小行为（M3 设计 §2.2）：墙超工作区 → 不渲染对象、窗口取工作区上限、配置不动
        if (_metrics.WallWidth(_wall.Columns) > workWidthDip + 0.5
            || _metrics.WallHeight(_wall.Rows) > workHeightDip + 0.5)
        {
            _adaptationMode = true;
            PlaceWindow(workArea, scale, clampToWorkArea: true);
            ShowPersistentStatus(
                $"已保存布局需要 {_wall.Columns} 栏 × {_wall.Rows} 行，当前放不下；未渲染任何对象，配置未修改（设计 §17.1）。");
            return;
        }

        _layoutReady = true;
        PlaceWindow(workArea, scale, clampToWorkArea: false);
        DrawWallGridBase();
        _presenter.Initialize(_wall);
        _presenter.RenderAll(_commit.Current);
        UpdateEmptyHint();
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
        var position = e.GetCurrentPoint(TilesCanvas).Position;
        if (FindView(e.OriginalSource as DependencyObject) is { } view)
        {
            view.Root.ReleasePointerCapture(e.Pointer);
        }

        _machine.Release(new DipPoint(position.X, position.Y)); // T3 点击判定在此（≤B1），Click 已被抑制
        _pointerGestureActive = false;
        e.Handled = true;
    }

    private void OnTilesPointerLost(object sender, PointerRoutedEventArgs e)
    {
        _pointerGestureActive = false;
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
                AppWindow.Hide(); // C01：系统接受启动请求后收起墙
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

        OpenPropertyWindow(_commit.Current.Objects.FirstOrDefault(o => o.Id == objectId));
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
                AppWindow.Hide(); // runas 成功同样收墙（§6.1）
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

            _presenter.RenderAll(_commit.Current);
            _machine.UpdateObjects(_commit.Current.Objects);
            UpdateEmptyHint();
            ShowTransientStatus($"已撤销：{actionName}");
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

    /// <summary>加载诊断 + M4 启动清扫报告合并（App 在 Load() 之前执行 EntryRecovery.Sweep，§5.6）。</summary>
    private string ComposeStartupDiagnostics(IReadOnlyList<string> loadDiagnostics)
    {
        var lines = new List<string>(loadDiagnostics);
        if (_recoveryDiagnostics.Count > 0)
        {
            lines.Add("启动清扫（EntryRecovery）：");
            lines.AddRange(_recoveryDiagnostics);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void UpdateEmptyHint()
    {
        var empty = _commit is null || _commit.Current.Objects.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed; // §4：对象数 > 0 → wall-empty-hint 消失
    }

    // ————————————————————————————— IPreviewTimer（B4 计时） —————————————————————————————

    void IPreviewTimer.Start()
    {
        _previewTimer.Stop(); // 重启：目标格变化即重置 250 ms 窗口
        _previewTimer.Start();
    }

    void IPreviewTimer.Stop() => _previewTimer.Stop();
}
