using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using TileWall.Core.Configuration;
using TileWall.Core.Grid;

namespace TileWall.Shell;

/// <summary>渲染层对象元素视图：根 Button + Transform + 横幅集合（磁贴 1 个；组每分区 1 个）。</summary>
public sealed class ObjectElementView
{
    public required LayoutObject Object { get; init; }

    public required Button Root { get; init; }

    public required TranslateTransform Transform { get; init; }

    public required IReadOnlyList<Grid> Banners { get; init; }

    public Brush BackgroundBrush { get; init; } = null!;

    /// <summary>当前 Canvas.Left/Top（不含 Transform 位移）；预览位移与终位化的基准。</summary>
    public DipPoint BaseOrigin { get; set; }
}

/// <summary>
/// 唯一的渲染出入口（M3 设计 §3）：读配置 → 摆控件，几何全部经 GridMetrics 换算，不自行推算。
/// AutomationId 契约（§4）：tile-&lt;Id&gt; / tile-&lt;Id&gt;-part-&lt;i&gt; / drag-ghost / drag-invalid。
/// 横幅规则（§3.4）：悬停/聚焦显示、底部 28 DIP 黑 50% 白字 12、空标题整体不创建、拖动中隐藏。
/// M8 §4.2：ClockObject 分支——两行文本常驻（时间大字 + 日期小字，单一默认样式），
/// 可选标题沿用悬停/聚焦横幅；文本经 <see cref="UpdateClockText"/> 由 ClockTickDriver 1 Hz 刷新（仅墙可见时）。
/// </summary>
public sealed class WallPresenter
{
    /// <summary>M8：时间/日期字号两档（常量集中，随 Q2 网格校准轮同批拍板——R-7）。</summary>
    internal const double ClockTimeFontSize = 32;

    internal const double ClockDateFontSize = 13;

    internal const double ClockLineSpacing = 4;

    /// <summary>小字号降档（min 边 < 96 DIP 的一格宽时用；R-7 占位值）。</summary>
    internal const double ClockTimeFontSizeSmall = 20;

    internal const double ClockDateFontSizeSmall = 11;

    /// <summary>无标题时钟的可访问名（恒定文本，避免 UIA 树每分钟抖动，§4.2）。</summary>
    public const string ClockAccessibleName = "时间日期";

    private readonly Canvas _tilesCanvas;
    private readonly Canvas _dragLayer;
    private readonly GridMetrics _metrics;
    private readonly Dictionary<string, ObjectElementView> _views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Microsoft.UI.Xaml.Controls.TextBlock Time, Microsoft.UI.Xaml.Controls.TextBlock Date)> _clockTexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Time, string Date)> _clockLastText = new(StringComparer.Ordinal);

    private WallGrid _wall = new(1, 1);
    private Border? _ghost;
    private FontIcon? _invalidMark;
    private DipPoint _ghostOffset;
    private Storyboard? _previewStoryboard;
    private bool _dragActive;

    public WallPresenter(Canvas tilesCanvas, Canvas dragLayer, GridMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(tilesCanvas);
        ArgumentNullException.ThrowIfNull(dragLayer);
        ArgumentNullException.ThrowIfNull(metrics);
        _tilesCanvas = tilesCanvas;
        _dragLayer = dragLayer;
        _metrics = metrics;
    }

    public WallGrid Wall => _wall;

    /// <summary>对象右键菜单（磁贴与组共用一份动态构建的 MenuFlyout）；设为 null 则元素不挂 ContextFlyout。</summary>
    public MenuFlyout? ObjectMenu { get; set; }

    /// <summary>M6：组渲染宿主（共享画布 + 翻转单元）；null 时组回退 M5 骨架（装配前安全）。</summary>
    public TileWall.Shell.Imaging.GroupVisualHost? GroupHost { get; set; }

    /// <summary>M6：全量重渲染完成（驱动器据此把已解码的当前图重放到重建后的视觉树上）。</summary>
    public event Action? RenderedAll;

    /// <summary>对象元素收到 ContextRequested（右键 / Shift+F10）——MainWindow 借此记录菜单目标。</summary>
    public event Action<ObjectElementView, ContextRequestedEventArgs>? ObjectContextRequested;

    /// <summary>
    /// 对象根 Button 的 Click（UIA Invoke / 键盘 Enter·空格）——「点击启动」语义的同一条出口（§3.1 要点 1、§5.6）。
    /// 指针手势路径不经此事件：MainWindow 在指针按下期间抑制 Click，由 GestureMachine 按 B1 阈值判定（防拖后误启动）。
    /// </summary>
    public event Action<string>? ObjectActivated;

    public ObjectElementView? GetView(string objectId) =>
        _views.TryGetValue(objectId, out var view) ? view : null;

    public void Initialize(WallGrid wall)
    {
        ArgumentNullException.ThrowIfNull(wall);
        _wall = wall;
    }

    /// <summary>全量重渲染（§3.3）：清空 TilesCanvas 重建——用于启动、撤销恢复。</summary>
    public void RenderAll(TileWallConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        StopPreviewStoryboard();
        _tilesCanvas.Children.Clear();
        _views.Clear();
        _clockTexts.Clear();
        _clockLastText.Clear();
        foreach (var o in config.Objects)
        {
            BuildAndAttach(o);
        }

        RenderedAll?.Invoke(); // M6：重建后驱动器重放当前图（视觉树已换新，位图资产缓存复用）
    }

    /// <summary>增量：新建磁贴后追加单个元素（§3.3）。</summary>
    public void AddObject(LayoutObject o)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (!_views.ContainsKey(o.Id))
        {
            BuildAndAttach(o);
        }
    }

    /// <summary>增量：取消固定后移除单个元素（§3.3）。</summary>
    public void RemoveObject(string objectId)
    {
        if (_views.TryGetValue(objectId, out var view))
        {
            _tilesCanvas.Children.Remove(view.Root);
            _views.Remove(objectId);
            _clockTexts.Remove(objectId);
            _clockLastText.Remove(objectId);
        }
    }

    /// <summary>
    /// M8：时钟文本刷新（ClockTickDriver 调用，仅墙可见时）。与上次相同则不动 UI
    /// （秒级信号、分钟级变化，避免无效重绘——§4.1 C16 实现要点 2）。对象不存在时静默（渲染树刚重建的竞态）。
    /// </summary>
    public void UpdateClockText(string objectId, string timeLine, string dateLine)
    {
        if (_clockTexts.TryGetValue(objectId, out var texts))
        {
            if (_clockLastText.TryGetValue(objectId, out var last) && last.Time == timeLine && last.Date == dateLine)
            {
                return;
            }

            texts.Time.Text = timeLine;
            texts.Date.Text = dateLine;
            _clockLastText[objectId] = (timeLine, dateLine);
        }
    }

    /// <summary>M8：当前渲染树中的时钟对象 Id（ClockTickDriver 的刷新目标集）。</summary>
    public IReadOnlyList<string> ClockIds => [.. _clockTexts.Keys];

    /// <summary>M8：时钟对象立即重算两行文本（Shown 即时求值一次的复用入口）。</summary>
    public IReadOnlyList<string> RefreshClocks(Func<DateTimeOffset> utcNow, System.Globalization.CultureInfo culture)
    {
        foreach (var id in _clockTexts.Keys)
        {
            var (timeLine, dateLine) = TileWall.Core.Components.ClockTextFormatter.Format(utcNow(), culture);
            UpdateClockText(id, timeLine, dateLine);
        }

        return ClockIds;
    }

    /// <summary>提交终位化（§3.3）：Canvas.Left/Top 固定为引擎结果、Transform 归零。</summary>
    public void FinalizeAll(IReadOnlyList<LayoutObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        StopPreviewStoryboard();
        foreach (var o in objects)
        {
            if (!_views.TryGetValue(o.Id, out var view))
            {
                continue;
            }

            var origin = _metrics.OriginOf(o.Bounds);
            Canvas.SetLeft(view.Root, origin.X);
            Canvas.SetTop(view.Root, origin.Y);
            view.BaseOrigin = origin;
            view.Transform.X = 0;
            view.Transform.Y = 0;
        }
    }

    /// <summary>T6/T10 预览渲染（§5.4）：对 ResultObjects 逐对象做 200 ms EaseOut 位移动画（Moves ⊆ ResultObjects 的位移全集）。</summary>
    public void ApplyPreview(RelocationResult result, bool animate)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ResultObjects is null)
        {
            return;
        }

        StopPreviewStoryboard();
        var anyMoved = false;
        Storyboard? storyboard = animate ? new Storyboard() : null;
        var duration = new Duration(TimeSpan.FromMilliseconds(GestureThresholds.PreviewMoveDurationMs));

        foreach (var o in result.ResultObjects)
        {
            if (!_views.TryGetValue(o.Id, out var view))
            {
                continue;
            }

            var targetOrigin = _metrics.OriginOf(o.Bounds);
            var dx = targetOrigin.X - view.BaseOrigin.X;
            var dy = targetOrigin.Y - view.BaseOrigin.Y;
            if (dx == 0 && dy == 0)
            {
                continue;
            }

            anyMoved = true;
            if (storyboard is null)
            {
                view.Transform.X = dx;
                view.Transform.Y = dy;
            }
            else
            {
                foreach (var (axis, to) in new[] { ("X", dx), ("Y", dy) })
                {
                    var animation = new DoubleAnimation
                    {
                        To = to,
                        Duration = duration,
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, // B5
                        EnableDependentAnimation = true,
                    };
                    Storyboard.SetTarget(animation, view.Transform);
                    Storyboard.SetTargetProperty(animation, axis);
                    storyboard.Children.Add(animation);
                }
            }
        }

        if (storyboard is not null)
        {
            if (anyMoved)
            {
                _previewStoryboard = storyboard;
                storyboard.Begin();
            }
        }
    }

    /// <summary>取消预览：Transform 归零回原位（Esc/非法/目标格变化）。</summary>
    public void ClearPreview()
    {
        StopPreviewStoryboard();
        foreach (var view in _views.Values)
        {
            view.Transform.X = 0;
            view.Transform.Y = 0;
        }
    }

    // —— 拖动视觉（ghost / 原位降隐 / 不可放置标记） ——

    public void BeginDrag(ObjectElementView view, DipPoint pointerPos, DipPoint grabOffset)
    {
        _dragActive = true;
        HideAllBanners();
        view.Root.Opacity = GestureThresholds.OriginDimOpacity;
        _ghostOffset = grabOffset;

        _ghost = new Border
        {
            Width = view.Root.Width,
            Height = view.Root.Height,
            Background = view.BackgroundBrush,
            Opacity = GestureThresholds.GhostOpacity, // B6：70% 跟随
            IsHitTestVisible = false,
        };
        AutomationProperties.SetAutomationId(_ghost, "drag-ghost");

        _invalidMark = new FontIcon
        {
            Glyph = "\uE711",
            FontSize = 16,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        AutomationProperties.SetAutomationId(_invalidMark, "drag-invalid");

        _dragLayer.Children.Add(_ghost);
        _dragLayer.Children.Add(_invalidMark);
        SetGhostPosition(pointerPos);
    }

    /// <summary>B6：ghost 以「指针 − 抓取偏移」1:1 跟随。</summary>
    public void SetGhostPosition(DipPoint pointerPos)
    {
        if (_ghost is not null)
        {
            Canvas.SetLeft(_ghost, pointerPos.X - _ghostOffset.X);
            Canvas.SetTop(_ghost, pointerPos.Y - _ghostOffset.Y);
            if (_invalidMark is { } mark)
            {
                Canvas.SetLeft(mark, (pointerPos.X - _ghostOffset.X) + (_ghost.Width / 2) - 8);
                Canvas.SetTop(mark, pointerPos.Y - _ghostOffset.Y - 8);
            }
        }
    }

    /// <summary>T6 失败：ghost 红描边 + [drag-invalid] 标记。</summary>
    public void ShowInvalidMark(bool invalid)
    {
        if (_ghost is not null)
        {
            _ghost.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Red);
            _ghost.BorderThickness = invalid ? new Thickness(2) : new Thickness(0);
        }

        if (_invalidMark is not null)
        {
            _invalidMark.Visibility = invalid ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>提交路径收尾：毁 ghost、原位复原（终位化由 FinalizeAll 另行完成）。</summary>
    public void EndDragVisuals()
    {
        _dragActive = false;
        if (_ghost is not null)
        {
            _dragLayer.Children.Remove(_ghost);
            _ghost = null;
        }

        if (_invalidMark is not null)
        {
            _dragLayer.Children.Remove(_invalidMark);
            _invalidMark = null;
        }

        foreach (var view in _views.Values)
        {
            view.Root.Opacity = 1;
        }
    }

    /// <summary>T8/T12/T9 失败：全量恢复——清预览 + 毁 ghost + 原位复原。</summary>
    public void AbortGesture()
    {
        EndDragVisuals();
        ClearPreview();
    }

    // —— 横幅（§3.4：拖动中隐藏；空标题的横幅从未创建） ——

    public void ShowBanner(ObjectElementView view, int bannerIndex)
    {
        if (!_dragActive && bannerIndex >= 0 && bannerIndex < view.Banners.Count)
        {
            view.Banners[bannerIndex].Visibility = Visibility.Visible;
        }
    }

    public void HideBanners(ObjectElementView view)
    {
        foreach (var banner in view.Banners)
        {
            banner.Visibility = Visibility.Collapsed;
        }
    }

    public void HideAllBanners()
    {
        foreach (var view in _views.Values)
        {
            HideBanners(view);
        }
    }

    // —— 元素构建（§3.1 控件树 / §3.2 渲染映射表） ——

    private void BuildAndAttach(LayoutObject o)
    {
        var title = DisplayTitleOf(o);
        // M5 §11.1：组底色按 Q6 Backdrop 映射（SolidColor→配置色；BlurFill→主题兜底（真模糊 M6）；
        // Transparent→透明画刷）；磁贴维持既有配置色路径。
        var brush = o is GroupObject groupObject ? GroupBackdropBrush(groupObject) : BackgroundBrushFor(o.Visual.BackgroundColor);
        var width = _metrics.RectWidth(o.Bounds);
        var height = _metrics.RectHeight(o.Bounds);
        var transform = new TranslateTransform();

        var root = new Button
        {
            Width = width,
            Height = height,
            Style = (Style)Application.Current.Resources["TileButtonStyle"],
            RenderTransform = transform,
            IsTabStop = true, // P1 B10：对象可 Tab/方向键聚焦，Enter/空格 → Invoke 走点击出口
        };
        AutomationProperties.SetAutomationId(root, $"tile-{o.Id}"); // §4：断言跨会话稳定
        // M8 §4.2：时钟可访问名 = 标题；无标题 → 恒定「时间日期」（不随时间变化，避免 UIA 树每分钟抖动）
        if (o is ClockObject)
        {
            AutomationProperties.SetName(root, string.IsNullOrWhiteSpace(title) ? ClockAccessibleName : title);
        }
        else if (!string.IsNullOrWhiteSpace(title))
        {
            AutomationProperties.SetName(root, title); // 完整文本可访问（长标题省略在横幅内）
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            ToolTipService.SetToolTip(root, title);
        }

        var banners = new List<Grid>();
        FrameworkElement content;
        if (o is ClockObject clock)
        {
            content = BuildClockContent(clock, brush, title, banners);
        }
        else if (o is GroupObject group)
        {
            content = BuildGroupContent(group, brush, title, banners);
        }
        else
        {
            content = BuildTileContent((TileObject)o, brush, title, banners);
        }

        root.Content = content;
        if (ObjectMenu is not null)
        {
            root.ContextFlyout = ObjectMenu; // §6.2：右键具体对象只弹对象菜单
        }

        // M5 §11.3：ButtonBase 会吞掉右键 pointer 事件（右键 press 被标记 handled）→ ContextFlyout 的
        // 自动显示与 RightTapped 在对象按钮上永远不触发（探测证实）。因此 ContextRequested（键盘
        // Shift+F10 / 触控长按仍会触发）显式 ShowAt，指针右键由 MainWindow 在右键释放处补发。
        root.ContextRequested += (_, args) =>
        {
            args.Handled = true;
            if (_views.TryGetValue(o.Id, out var sender))
            {
                ObjectContextRequested?.Invoke(sender, args);
            }

            ObjectMenu?.ShowAt(root);
        };
        root.Click += (_, _) => ObjectActivated?.Invoke(o.Id); // UIA Invoke / Enter·空格 → 点击出口（§5.6）
        root.GotFocus += (_, _) =>
        {
            if (banners.Count > 0)
            {
                ShowBanner(GetViewOrThrow(o.Id), 0); // 键盘聚焦显示横幅（组取首分区）
            }
        };
        root.LostFocus += (_, _) => HideBanners(GetViewOrThrow(o.Id));

        var origin = _metrics.OriginOf(o.Bounds);
        Canvas.SetLeft(root, origin.X);
        Canvas.SetTop(root, origin.Y);
        _tilesCanvas.Children.Add(root);
        var view = new ObjectElementView
        {
            Object = o,
            Root = root,
            Transform = transform,
            Banners = banners,
            BackgroundBrush = brush,
            BaseOrigin = origin,
        };
        _views[o.Id] = view;
        root.Tag = view; // 指针命中反查（MainWindow.FindView 沿可视树找 Tag）
    }

    private FrameworkElement BuildTileContent(TileObject o, Brush brush, string? title, List<Grid> banners)
    {
        var chrome = new Grid
        {
            Width = _metrics.RectWidth(o.Bounds),
            Height = _metrics.RectHeight(o.Bounds),
            Background = brush,
        };

        // 前景占位：40×40 DIP 居中（P1 §1.2-7）；M3 无图标文件 → FontIcon 占位字形
        FrameworkElement foreground = o.Visual.ForegroundIconPath is { } iconPath && File.Exists(iconPath)
            ? new Image
            {
                Width = 40,
                Height = 40,
                Source = new BitmapImage(new Uri(iconPath)),
            }
            : new FontIcon { Glyph = "\uE7C3", FontSize = 20 };
        var holder = new Border
        {
            Width = 40,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = foreground,
        };
        chrome.Children.Add(holder);

        if (title is not null)
        {
            var banner = CreateBanner(title);
            chrome.Children.Add(banner);
            banners.Add(banner);
            chrome.PointerEntered += (_, _) => ShowBanner(GetViewOrThrow(o.Id), 0);
            chrome.PointerExited += (_, _) => HideBanners(GetViewOrThrow(o.Id));
        }

        return chrome;
    }

    /// <summary>
    /// M8 §4.2：时间日期组件内容——两行文本常驻（时间大字 + 日期小字，居中堆叠），可选标题走横幅
    /// （仅标题遵循悬停/聚焦规则，主体不隐藏）。字号两档：min 边 ≥ 96 DIP 用大档，否则降档（R-7 占位值）。
    /// 初始文本即构建时刻的当前值；此后由 ClockTickDriver 刷新（墙可见时）。
    /// </summary>
    private FrameworkElement BuildClockContent(ClockObject clock, Brush brush, string? title, List<Grid> banners)
    {
        var chrome = new Grid
        {
            Width = _metrics.RectWidth(clock.Bounds),
            Height = _metrics.RectHeight(clock.Bounds),
            Background = brush,
        };

        var minWidth = _metrics.RectWidth(clock.Bounds);
        var minHeight = _metrics.RectHeight(clock.Bounds);
        var largeTier = Math.Min(minWidth, minHeight) >= GridMetrics.DefaultCellCore;
        var timeBlock = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            FontSize = largeTier ? ClockTimeFontSize : ClockTimeFontSizeSmall,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiLight,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var dateBlock = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            FontSize = largeTier ? ClockDateFontSize : ClockDateFontSizeSmall,
            Opacity = 0.8,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = ClockLineSpacing,
        };
        stack.Children.Add(timeBlock);
        stack.Children.Add(dateBlock);
        chrome.Children.Add(stack);

        var (timeLine, dateLine) = TileWall.Core.Components.ClockTextFormatter.Format(DateTimeOffset.UtcNow, System.Globalization.CultureInfo.CurrentCulture);
        timeBlock.Text = timeLine;
        dateBlock.Text = dateLine;
        _clockTexts[clock.Id] = (timeBlock, dateBlock);
        _clockLastText[clock.Id] = (timeLine, dateLine);

        if (title is not null)
        {
            var banner = CreateBanner(title);
            chrome.Children.Add(banner);
            banners.Add(banner);
            chrome.PointerEntered += (_, _) => ShowBanner(GetViewOrThrow(clock.Id), 0);
            chrome.PointerExited += (_, _) => HideBanners(GetViewOrThrow(clock.Id));
        }

        return chrome;
    }

    private FrameworkElement BuildGroupContent(GroupObject group, Brush brush, string? title, List<Grid> banners)
    {
        // 组 = 单个焦点单元（P1 B10）：整组拖动/右键/点击命中体；分区纯视觉、不聚焦。
        // M6：共享画布渲染升级为「每分区一个翻转单元」（§5.1），委托 GroupVisualHost 构建
        // （几何全部经 GridMetrics/SharedCanvas 派生，定位数学 p.Column·Pitch 不变 §3.1）。
        if (GroupHost is { } host)
        {
            return host.BuildContent(
                group,
                brush,
                title,
                banners,
                partitionEntered: index => ShowBanner(GetViewOrThrow(group.Id), index),
                partitionExited: _ => HideBanners(GetViewOrThrow(group.Id)));
        }

        // 无宿主装配（理论不可达：MainWindow 恒装配）时维持 M5 分区骨架
        var canvas = new Canvas
        {
            Width = _metrics.RectWidth(group.Bounds),
            Height = _metrics.RectHeight(group.Bounds),
        };

        for (var i = 0; i < group.Partitions.Count; i++)
        {
            var p = group.Partitions[i];
            var partition = new Border
            {
                Width = _metrics.WidthOf(p.Width),
                Height = _metrics.HeightOf(p.Height),
                Background = brush,
                IsHitTestVisible = true, // 支撑「悬停哪个分块、横幅落在哪个分块」（§7.3）
            };
            AutomationProperties.SetAutomationId(partition, $"tile-{group.Id}-part-{i}"); // §4：仅几何定位用
            // UIA peer 只有在设置了 Name 等标识属性时才会为非控件元素创建（Border 无内建 peer）；
            // 无标题组也必须可被 UIA 几何定位（§4 契约对无标题组同样成立）
            AutomationProperties.SetName(partition, string.IsNullOrWhiteSpace(title) ? $"分块 {i}" : title);

            if (title is not null)
            {
                var banner = CreateBanner(title);
                partition.Child = banner;
                banners.Add(banner);
                var index = i;
                partition.PointerEntered += (_, _) => ShowBanner(GetViewOrThrow(group.Id), index);
                partition.PointerExited += (_, _) => HideBanners(GetViewOrThrow(group.Id));
            }

            // 组内相对坐标（同栏内步距恒 Pitch：OriginOf(p+Bounds) − OriginOf(Bounds) 的解析形式）
            Canvas.SetLeft(partition, p.Column * _metrics.Pitch);
            Canvas.SetTop(partition, p.Row * _metrics.Pitch);
            canvas.Children.Add(partition);
        }

        return canvas;
    }

    private static Grid CreateBanner(string title)
    {
        var banner = new Grid
        {
            Height = 28, // 底部 28 DIP（P1 §1.4）
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = 0.5 }, // 黑底 50%
            Visibility = Visibility.Collapsed, // 默认隐藏：悬停/聚焦才显示
        };
        banner.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12, // 白字 12 DIP
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            TextTrimming = TextTrimming.CharacterEllipsis, // 长标题省略；完整文本在 AutomationProperties.Name
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
        });
        return banner;
    }

    /// <summary>组背景映射（M5 §11.1 / 拍板 Q6）：SolidColor → 既有 BackgroundBrushFor 路径（即时映射）；
    /// Transparent → 透明画刷；BlurFill → M5 记录选择、渲染退化为主题兜底色（真模糊补底 = M6）。</summary>
    private Brush GroupBackdropBrush(GroupObject group) => group.Visual.Backdrop switch
    {
        BackdropKind.SolidColor => BackgroundBrushFor(group.Visual.BackgroundColor),
        BackdropKind.Transparent => new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        _ => BackgroundBrushFor(null),
    };

    /// <summary>标题真值（§16.2 单一真值）：有入口=托管文件名主体；无入口=TitleText；ShowTitle=false → 无横幅。</summary>
    private static string? DisplayTitleOf(LayoutObject o)
    {
        if (!o.Visual.ShowTitle)
        {
            return null;
        }

        if (o.Entry is not null)
        {
            return Path.GetFileNameWithoutExtension(o.Entry.RelativePath);
        }

        return string.IsNullOrWhiteSpace(o.Visual.TitleText) ? null : o.Visual.TitleText;
    }

    /// <summary>后景色：配置色优先；null → 主题实色兜底（深/浅两档，§10.1；色值随 Q2 校准轮定）。</summary>
    private Brush BackgroundBrushFor(string? configured)
    {
        if (configured is not null)
        {
            try
            {
                return new SolidColorBrush(ParseHexColor(configured));
            }
            catch (FormatException)
            {
                // 非法色串按主题兜底渲染（校验器不管颜色；显示不崩溃优先）
            }
        }

        var themeKey = _tilesCanvas.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        if (Application.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var dictObj)
            && dictObj is ResourceDictionary dict
            && dict.TryGetValue("TileBackgroundFallbackBrush", out var brushObj)
            && brushObj is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    private static Windows.UI.Color ParseHexColor(string text)
    {
        var s = text.TrimStart('#');
        if (s.Length != 6 || !byte.TryParse(s[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !byte.TryParse(s[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !byte.TryParse(s[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            throw new FormatException($"非法颜色值：{text}");
        }

        return Windows.UI.Color.FromArgb(0xFF, r, g, b);
    }

    private ObjectElementView GetViewOrThrow(string objectId) =>
        _views.TryGetValue(objectId, out var view)
            ? view
            : throw new InvalidOperationException($"渲染层缺少对象元素：{objectId}");

    private void StopPreviewStoryboard()
    {
        if (_previewStoryboard is not null)
        {
            _previewStoryboard.Stop();
            _previewStoryboard = null;
        }
    }
}
