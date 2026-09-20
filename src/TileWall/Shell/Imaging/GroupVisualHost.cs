using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using TileWall.Core.Animation;
using TileWall.Core.Carousel;
using TileWall.Core.Configuration;
using TileWall.Core.Grid;
using TileWall.Core.Imaging;

namespace TileWall.Shell.Imaging;

/// <summary>一个分区的翻转单元视图（front/back 两面 + 横幅 overlay 位，M6 设计 §5.1 层结构）。</summary>
public sealed class FlipUnitView
{
    public required Canvas Root { get; init; }

    public required Grid FrontFace { get; init; }

    public required Grid BackFace { get; init; }

    public required PlaneProjection FrontPlane { get; init; }

    public required PlaneProjection BackPlane { get; init; }

    public required Canvas FrontSharpHolder { get; init; }

    public required Canvas FrontBlurHolder { get; init; }

    public required Canvas BackSharpHolder { get; init; }

    public required Canvas BackBlurHolder { get; init; }

    public required Image FrontSharp { get; init; }

    public required Image FrontBlur { get; init; }

    public required Image BackSharp { get; init; }

    public required Image BackBlur { get; init; }

    /// <summary>本分块的画布矩形 R_p（组内相对 DIP，§3.1）。</summary>
    public required DipRect Rect { get; init; }
}

/// <summary>组级视觉状态：一次 BuildContent 注册一份，ApplyImage/翻转在其上操作。</summary>
public sealed class GroupVisualState
{
    public required string GroupId { get; init; }

    public required GroupObject Group { get; init; }

    public required DipSize Canvas { get; init; }

    public required IReadOnlyList<FlipUnitView> Units { get; init; }

    public string? CurrentImageId { get; set; }

    /// <summary>当前图的位图资产（SettleToCurrent 跳终态重放 front 面所需）。</summary>
    public GroupImageAssets? CurrentAssets { get; set; }

    public Storyboard? ActiveFlip { get; set; }
}

/// <summary>
/// 组渲染宿主（M6 设计 §2.1 GroupVisualHost）：每分区一个翻转单元（front/back 两面 + Clip），
/// 贴图 = XAML Image + Clip 画布（§5.1 备胎；数据流同一 <see cref="PartitionClip"/>，由 Core
/// <see cref="SharedCanvasTransform.ClipFor"/> 驱动，UI 不自造第二套裁剪数学）。
/// 横幅不参与 3D 变换（FLIP-2）；动画只动渲染属性（Projection/Opacity），布局树与命中结构不变（FLIP-1/4）。
/// 翻转 = 组内全部单元由同一 Storyboard 同帧驱动（A5），角度逐关键帧取自 Core
/// <see cref="FlipTimeline.EasedProgress"/> 解析式（A3），面切换点固定在时间线 0.5（§7.1）。
/// </summary>
public sealed class GroupVisualHost
{
    /// <summary>翻转关键帧采样密度（每 1/20 时长一点，线性插值；点数与平滑度的占位折衷）。</summary>
    private const int FlipKeySamples = 20;

    private readonly GridMetrics _metrics;
    private readonly Dictionary<string, GroupVisualState> _states = new(StringComparer.Ordinal);

    public GroupVisualHost(GridMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _metrics = metrics;
    }

    public GroupVisualState? GetState(string groupId) =>
        _states.TryGetValue(groupId, out var state) ? state : null;

    /// <summary>组内容构建（WallPresenter.BuildGroupContent 委托入口）。横幅按分区序追加 banners。</summary>
    public Canvas BuildContent(
        GroupObject group,
        Brush backdrop,
        string? title,
        List<Grid> banners,
        Action<int>? partitionEntered,
        Action<int>? partitionExited)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(banners);

        var canvas = new Canvas
        {
            Width = _metrics.RectWidth(group.Bounds),
            Height = _metrics.RectHeight(group.Bounds),
        };
        var units = new List<FlipUnitView>(group.Partitions.Count);
        for (var i = 0; i < group.Partitions.Count; i++)
        {
            var p = group.Partitions[i];
            var rect = SharedCanvas.PartitionRect(p, _metrics);
            var unit = BuildUnit(group, rect, backdrop, title);
            AutomationProperties.SetAutomationId(unit.Root, $"tile-{group.Id}-part-{i}"); // §4 契约（FLIP-4）
            AutomationProperties.SetName(unit.Root, string.IsNullOrWhiteSpace(title) ? $"分块 {i}" : title);
            if (title is not null)
            {
                var banner = CreateBanner(title);
                unit.Root.Children.Add(banner); // 顶层 overlay：不参与 3D 旋转（§5.1/FLIP-2）
                banners.Add(banner);
                var index = i;
                unit.Root.PointerEntered += (_, _) => partitionEntered?.Invoke(index);
                unit.Root.PointerExited += (_, _) => partitionExited?.Invoke(index);
            }

            Canvas.SetLeft(unit.Root, rect.X);
            Canvas.SetTop(unit.Root, rect.Y);
            canvas.Children.Add(unit.Root);
            units.Add(unit);
        }

        _states[group.Id] = new GroupVisualState
        {
            GroupId = group.Id,
            Group = group,
            Canvas = SharedCanvas.CanvasSize(new GridSize(group.Bounds.Width, group.Bounds.Height), _metrics),
            Units = units,
        };
        return canvas;
    }

    private FlipUnitView BuildUnit(GroupObject group, DipRect rect, Brush backdrop, string? title)
    {
        _ = group;
        var width = rect.Width;
        var height = rect.Height;

        // 每个 holder 独享一份 Clip 几何（WinRT DependencyObject 实例不可多处共享——共享实例会启动崩溃）
        var frontSharpHolder = BuildHolder(width, height, NewClip(width, height));
        var frontBlurHolder = BuildHolder(width, height, NewClip(width, height));
        var backSharpHolder = BuildHolder(width, height, NewClip(width, height));
        var backBlurHolder = BuildHolder(width, height, NewClip(width, height));

        var frontFace = BuildFace(width, height, backdrop, frontBlurHolder, frontSharpHolder);
        var frontPlane = new PlaneProjection { RotationY = 0 };
        frontFace.Projection = frontPlane;

        var backFace = BuildFace(width, height, backdrop, backBlurHolder, backSharpHolder);
        var backPlane = new PlaneProjection { RotationY = -90 }; // 备胎 B：面切换时刻恰为侧对观众
        backFace.Projection = backPlane;
        backFace.Opacity = 0; // 待机隐藏（Opacity=0 不改变布局矩形与命中结构，FLIP-1/4）

        var root = new Canvas { Width = width, Height = height, IsHitTestVisible = true };
        root.Children.Add(backFace);
        root.Children.Add(frontFace);

        return new FlipUnitView
        {
            Root = root,
            FrontFace = frontFace,
            BackFace = backFace,
            FrontPlane = frontPlane,
            BackPlane = backPlane,
            FrontSharpHolder = frontSharpHolder,
            FrontBlurHolder = frontBlurHolder,
            BackSharpHolder = backSharpHolder,
            BackBlurHolder = backBlurHolder,
            FrontSharp = CreateImage(frontSharpHolder),
            FrontBlur = CreateImage(frontBlurHolder),
            BackSharp = CreateImage(backSharpHolder),
            BackBlur = CreateImage(backBlurHolder),
            Rect = rect,
        };
    }

    private static Grid BuildFace(double width, double height, Brush backdrop, Canvas blurHolder, Canvas sharpHolder)
    {
        // 层序（§5.1）：主题兜底 Border（Q6 三态映射的落点）→ 模糊后景 → 清晰前景
        var face = new Grid { Width = width, Height = height };
        face.Children.Add(new Border
        {
            Width = width,
            Height = height,
            Background = backdrop,
            IsHitTestVisible = true, // 悬停命中面（悬停哪个分块、横幅落在哪个分块，M5 §7.3）
        });
        face.Children.Add(blurHolder);
        face.Children.Add(sharpHolder);
        return face;
    }

    private static RectangleGeometry NewClip(double width, double height) => new()
    {
        Rect = new Windows.Foundation.Rect(0, 0, width, height),
    };

    private static Canvas BuildHolder(double width, double height, RectangleGeometry clip) => new()
    {
        Width = width,
        Height = height,
        Clip = clip,
        IsHitTestVisible = false, // 命中面只留兜底 Border，避免多层面重复计数
    };

    private static Image CreateImage(Canvas holder)
    {
        var image = new Image { Visibility = Visibility.Collapsed };
        holder.Children.Add(image);
        return image;
    }

    private static Grid CreateBanner(string title)
    {
        var banner = new Grid
        {
            Height = 28, // 底部 28 DIP（P1 §1.4）
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = 0.5 },
            Visibility = Visibility.Collapsed,
        };
        banner.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
        });
        return banner;
    }

    // ————————————————————————————— 贴图与翻转 —————————————————————————————

    /// <summary>
    /// 全组跳终态（M6 设计 §7.6 第 2 行「动画中收起」）：停止所有活动翻转 Storyboard，
    /// front 面 = 新图（重放 CurrentAssets），投影/透明度经 Stop 回本地值——重开不从半张翻转继续。
    /// </summary>
    public void SettleAll()
    {
        foreach (var state in _states.Values)
        {
            if (state.ActiveFlip is null)
            {
                continue;
            }

            state.ActiveFlip.Stop(); // 不触发 Completed：由本方法负责落定
            state.ActiveFlip = null;
            if (state.CurrentImageId is { } imageId && state.CurrentAssets is { } assets)
            {
                ApplyImage(state.GroupId, imageId, assets);
            }
        }
    }

    /// <summary>该组当前应显示的变换（有条目用条目，无条目 = CoverFill 居中，XF-7）。</summary>
    public static ImageTransform TransformOf(GroupVisualState state, PixelSize pixels)
    {
        var canvas = state.Canvas;
        var found = ImageTransformSet.Find(state.Group.Images.Transforms, state.CurrentImageId ?? string.Empty);
        return found?.Transform ?? SharedCanvasTransform.DefaultTransform(pixels, canvas, found?.Fit ?? FitMode.CoverFill);
    }

    /// <summary>直显（首图/恢复/落定后）：front ← 资产，不动 back。</summary>
    public void ApplyImage(string groupId, string imageId, GroupImageAssets assets)
    {
        if (!_states.TryGetValue(groupId, out var state))
        {
            return;
        }

        state.ActiveFlip?.Stop();
        state.ActiveFlip = null;
        state.CurrentImageId = imageId;
        state.CurrentAssets = assets;
        var transform = TransformOf(state, assets.SourcePixels);
        foreach (var unit in state.Units)
        {
            PlaceFace(unit.FrontSharp, unit.FrontSharpHolder, transform, assets.SourcePixels, unit.Rect, assets.SharpSource);
            PlaceBlur(unit.FrontBlur, assets, unit.Rect, state.Canvas);
        }
    }

    /// <summary>
    /// 同步翻转（T4，§7.2）：back ← 新图（早已解码就绪，§7.2 T1），一个 Storyboard 同帧启动全部单元；
    /// 完成后落定 front ← 新图、back 复位（§7.2 T6；§7.6 中途打断由 Stop 跳终态，不从半张翻转继续）。
    /// </summary>
    public void StartFlip(string groupId, string imageId, GroupImageAssets assets)
    {
        if (!_states.TryGetValue(groupId, out var state))
        {
            return;
        }

        state.ActiveFlip?.Stop();
        state.CurrentImageId = imageId;
        state.CurrentAssets = assets;
        var transform = TransformOf(state, assets.SourcePixels);
        foreach (var unit in state.Units)
        {
            PlaceFace(unit.BackSharp, unit.BackSharpHolder, transform, assets.SourcePixels, unit.Rect, assets.SharpSource);
            PlaceBlur(unit.BackBlur, assets, unit.Rect, state.Canvas);
        }

        var storyboard = BuildFlipStoryboard(state);
        state.ActiveFlip = storyboard;
        storyboard.Completed += (_, _) =>
        {
            storyboard.Stop();
            if (ReferenceEquals(state.ActiveFlip, storyboard))
            {
                state.ActiveFlip = null;
            }

            ApplyImage(groupId, imageId, assets); // T6 落定：front ← 新图、投影/透明度回本地值
        };
        storyboard.Begin(); // T4：一个 ScopedBatch 语义 = 一个 Storyboard 同帧启动全部块
    }

    private Storyboard BuildFlipStoryboard(GroupVisualState state)
    {
        var storyboard = new Storyboard();
        var duration = FlipTimeline.DurationMs;
        var swap = duration / 2;

        foreach (var unit in state.Units)
        {
            // 前面：0→180°（A1），逐采样点取 Core 解析式（A3）
            var frontRotation = new DoubleAnimationUsingKeyFrames { Duration = new Duration(TimeSpan.FromMilliseconds(duration)) };
            // 背面：前半程保持 −90°（侧对），后半程 −90→0 随同一时间线展开
            var backRotation = new DoubleAnimationUsingKeyFrames { Duration = new Duration(TimeSpan.FromMilliseconds(duration)) };
            // 面切换（离散关键帧，恰在 0.5）：front 1→0、back 0→1（§7.1 BackFaceAt 按时间线判定）
            var frontOpacity = new DoubleAnimationUsingKeyFrames { Duration = new Duration(TimeSpan.FromMilliseconds(duration)) };
            var backOpacity = new DoubleAnimationUsingKeyFrames { Duration = new Duration(TimeSpan.FromMilliseconds(duration)) };

            frontOpacity.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 1 });
            frontOpacity.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(swap)), Value = 0 });
            backOpacity.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 0 });
            backOpacity.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(swap)), Value = 1 });

            for (var i = 0; i <= FlipKeySamples; i++)
            {
                var milliseconds = duration * i / FlipKeySamples;
                var keyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds));
                var eased = FlipTimeline.EasedProgress(TimeSpan.FromMilliseconds(milliseconds));
                frontRotation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = keyTime, Value = FlipTimeline.MaxAngleDeg * eased });
                var backValue = -90 + (Math.Max(0, eased - 0.5) * 2 * 90);
                backRotation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = keyTime, Value = backValue });
            }

            AddAnimation(storyboard, frontRotation, unit.FrontPlane, "RotationY");
            AddAnimation(storyboard, backRotation, unit.BackPlane, "RotationY");
            AddAnimation(storyboard, frontOpacity, unit.FrontFace, "Opacity");
            AddAnimation(storyboard, backOpacity, unit.BackFace, "Opacity");
        }

        storyboard.Duration = new Duration(TimeSpan.FromMilliseconds(duration));
        return storyboard;
    }

    private static void AddAnimation(Storyboard storyboard, Timeline animation, DependencyObject target, string property)
    {
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private static void PlaceFace(Image image, Canvas holder, ImageTransform t, PixelSize pixels, DipRect rect, ImageSource source)
    {
        _ = holder;
        var clip = SharedCanvasTransform.ClipFor(t, pixels, rect);
        if (clip is null || source is null)
        {
            image.Visibility = Visibility.Collapsed; // 整块露后景/底色（XF-4 第三态）
            return;
        }

        image.Source = source;
        image.Width = pixels.Width * t.Scale;
        image.Height = pixels.Height * t.Scale;
        Canvas.SetLeft(image, t.OffsetX - rect.X);
        Canvas.SetTop(image, t.OffsetY - rect.Y);
        image.Visibility = Visibility.Visible;
    }

    /// <summary>模糊后景：变换固定 = DefaultTransform(CoverFill)，整组一张、不随前景缩小（§5.2 步骤 5/§10.4）。</summary>
    private static void PlaceBlur(Image image, GroupImageAssets assets, DipRect rect, DipSize canvas)
    {
        if (assets.BlurSource is null)
        {
            image.Visibility = Visibility.Collapsed;
            return;
        }

        var t = SharedCanvasTransform.DefaultTransform(assets.BlurPixels, canvas, FitMode.CoverFill);
        image.Source = assets.BlurSource;
        image.Width = assets.BlurPixels.Width * t.Scale;
        image.Height = assets.BlurPixels.Height * t.Scale;
        Canvas.SetLeft(image, t.OffsetX - rect.X);
        Canvas.SetTop(image, t.OffsetY - rect.Y);
        image.Visibility = Visibility.Visible; // holder 的 Clip 把溢出限幅在分块内
    }
}
