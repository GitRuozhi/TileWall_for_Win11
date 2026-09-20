using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Configuration;
using TileWall.Core.Grid;
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
/// （M3 设计 §2.2 装配序列）：ConfigStore 状态分支、WallPresenter 渲染、GestureMachine 手势、
/// LayoutCommitService 提交、右键菜单、取消固定与 Ctrl+Z。
/// 本类只做「事件 → 状态机/服务 → 渲染」的薄封装，不复制任何几何/腾位/校验逻辑。
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

    private LayoutCommitService? _commit;
    private WallGrid _wall = new(1, 1);
    private string _dataRoot = string.Empty;
    private bool _layoutReady;
    private bool _adaptationMode;
    private bool _pointerGestureActive;
    private DipPoint _lastPointerPos;
    private LayoutObject? _menuTarget;

    public MainWindow(ConfigStore store, ConfigLoadResult loadResult, bool seedRequested)
    {
        _store = store;
        _loadResult = loadResult;
        _seedRequested = seedRequested;

        InitializeComponent();
        Title = "TileWall";
        ConfigureBorderlessPresenter();

        _dataRoot = Path.GetDirectoryName(store.ConfigPath) ?? string.Empty;
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
                    ShowPersistentStatus(string.Join(Environment.NewLine, _loadResult.Diagnostics));
                }

                EnterReadyState(_loadResult.Config!, workArea, scale, workWidthDip, workHeightDip, saveFirst: false);
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

    /// <summary>T9 成功 / T10：预览=提交——引擎返回的同一 ResultObjects 实例进 Save（INV-7）。</summary>
    public void OnCommitted(RelocationResult result)
    {
        _presenter.EndDragVisuals();
        var newConfig = _commit!.Current with { Objects = result.ResultObjects!.ToArray() };
        if (_commit.Commit(newConfig, "拖动", out var failure))
        {
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

    /// <summary>T3 点击（§5.6）：M3 全部对象空目标 → 无动作；预留 Entry 文件存在才 ShellExecute 的 M4 出口。</summary>
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

    private void ActivateObject(string objectId)
    {
        var o = _commit?.Current.Objects.FirstOrDefault(x => x.Id == objectId);
        if (o?.Entry is null)
        {
            return; // 空目标：无动作、不收起墙（§12.1）
        }

        var fullPath = Path.Combine(_dataRoot, o.Entry.RelativePath);
        if (!File.Exists(fullPath))
        {
            ShowTransientStatus("入口文件缺失"); // §12.1：立即失败时保留墙并提示
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowTransientStatus($"启动失败：{ex.Message}");
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
            WallMenuFactory.PopulateObjectMenu(_objectMenu, _menuTarget, OnUnpinRequested);
        }
    }

    /// <summary>「新建磁贴」（§6.3 唯一真实创建入口）：CanFitRect first-fit → 空目标 1×1 TileObject → Commit。</summary>
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

        var occupied = OccupancyMap.Build(_wall, _commit.Current.Objects.Select(o => o.Bounds));
        if (!GridPlacement.CanFitRect(_wall, new GridSize(1, 1), occupied, out var firstFit))
        {
            ShowTransientStatus("墙面已满，无法新建磁贴"); // A06/A17：不提交、不部分创建
            return;
        }

        var tile = new TileObject
        {
            Id = StableId.NewId(),
            Bounds = firstFit,
            Entry = null, // 空目标 1×1（§6.3）
            Visual = new ObjectVisual { ShowTitle = true },
        };
        var newConfig = _commit.Current with { Objects = _commit.Current.Objects.Append(tile).ToArray() };
        if (CommitLayout(newConfig, "新建磁贴", _ => _presenter.AddObject(tile)))
        {
            ShowTransientStatus("已新建磁贴（空目标 1×1）");
        }
    }

    /// <summary>取消固定（§7.2）：只移除配置记录、不触碰文件（M3 无托管；§16.3 恢复入口属 M4 扩展点）。</summary>
    private void OnUnpinRequested(string objectId)
    {
        if (_commit is null)
        {
            return;
        }

        var target = _commit.Current.Objects.FirstOrDefault(o => o.Id == objectId);
        if (target is null)
        {
            return;
        }

        var newConfig = _commit.Current with { Objects = _commit.Current.Objects.Where(o => o.Id != objectId).ToArray() };
        var actionName = target is GroupObject ? "取消固定磁贴组" : "从磁贴墙取消固定";
        CommitLayout(newConfig, actionName, _ => _presenter.RemoveObject(objectId));
    }

    /// <summary>提交 + 渲染编排：增量回调（拖动/新建/取消固定）或全量 RenderAll（撤销）。</summary>
    private bool CommitLayout(TileWallConfig newConfig, string actionName, Action<TileWallConfig>? incrementalRender)
    {
        if (!_commit!.Commit(newConfig, actionName, out var failure))
        {
            ShowTransientStatus($"保存失败，布局未更改：{failure}");
            return false;
        }

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

    // ————————————————————————————— 撤销（§7.3） —————————————————————————————

    private void UndoLast()
    {
        if (!_layoutReady || _commit is null || _machine.State != GestureState.Idle)
        {
            return; // 仅 Idle 态响应（拖动中按 Esc 负责取消）
        }

        if (!_commit.TryPeekUndo(out var slot))
        {
            ShowTransientStatus("没有可撤销的操作"); // §15.3：不无声失败
            return;
        }

        var actionName = slot.ActionName;
        if (_commit.Commit(slot.Previous, "撤销", out var failure))
        {
            _commit.ClearUndoSlot(); // 撤销本身不再可重做（最小撤销范围）
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

    /// <summary>常驻诊断（恢复链/显示适配）：不清除。</summary>
    private void ShowPersistentStatus(string message)
    {
        _statusTimer.Stop();
        StatusHint.Message = message;
        AutomationProperties.SetName(StatusHint, message);
        StatusHint.IsClosable = false;
        StatusHint.IsOpen = true;
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
