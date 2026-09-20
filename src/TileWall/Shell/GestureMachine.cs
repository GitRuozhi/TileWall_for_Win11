using TileWall.Core.Configuration;
using TileWall.Core.Grid;

namespace TileWall.Shell;

/// <summary>手势状态（M3 设计 §5.2 四态）。</summary>
public enum GestureState
{
    Idle,
    Pressing,
    Dragging,
    DraggingWithPreview,
}

/// <summary>B4 悬停计时抽象：UI 层用 DispatcherQueueTimer 实现；测试可手动触发 <see cref="GestureMachine.PreviewTimerDue"/>。</summary>
public interface IPreviewTimer
{
    void Start();

    void Stop();
}

/// <summary>
/// 状态机的出口（只产出意图，不碰文件、不碰控件）。MainWindow 实现并接 WallPresenter/LayoutCommitService。
/// </summary>
public interface IGestureHost
{
    /// <summary>T2：进入 Dragging——建 ghost、原位降隐。</summary>
    void OnDragStarted(string objectId);

    /// <summary>T6 成功：渲染腾位预览（结果实例此后原样提交，INV-7）。</summary>
    void OnPreviewReady(RelocationResult result);

    /// <summary>T6 失败：显示「不可放置」标记（ghost 红描边）。</summary>
    void OnTargetInvalid(GridRect target);

    /// <summary>T5/T7：目标格变化——清除现有预览/标记、重启 B4 计时。</summary>
    void OnTargetChanged();

    /// <summary>T9 成功 / T10：提交（携带引擎结果的同一实例）。</summary>
    void OnCommitted(RelocationResult result);

    /// <summary>T9 失败：全量恢复之外的「无法放置」提示。</summary>
    void OnDropRejected();

    /// <summary>T8/T12/T9 失败的恢复动作：清预览、毁 ghost、原位复原。</summary>
    void OnGestureCancelled();

    /// <summary>T3：位移 ≤ B1 的抬起 = 点击。</summary>
    void OnObjectActivated(string objectId);
}

/// <summary>
/// 显式点击/拖动状态机（M3 设计 §5.2 转移表 T1–T12）：枚举 + 转移表即实现，不接受隐式布尔。
/// 纯 C# 类（Core 类型 + 注入计时器/出口），无 WinUI 依赖；指针事件薄封装在 MainWindow。
/// 同一状态机对组生效：从组任意分块按下 → draggedId = 组 Id，候选格为组尺寸矩形（A09）。
/// 拖动永不改变对象尺寸：候选格尺寸恒等于被拖对象 Bounds.Size（引擎对尺寸不符返回 TargetInvalid）。
/// </summary>
public sealed class GestureMachine
{
    private readonly GridMetrics _metrics;
    private readonly IPreviewTimer _timer;
    private readonly IGestureHost _host;

    private WallGrid _wall = new(1, 1);
    private string _objectId = string.Empty;
    private GridRect _objectBounds;
    private IReadOnlyList<LayoutObject> _originalObjects = [];
    private DipPoint _pressPoint;
    private double _dragStartThreshold;

    /// <summary>抓取偏移 = 按下点 − 对象原点（DIP）；ghost/候选格换算用。</summary>
    public DipPoint GrabOffset { get; private set; }

    public GestureState State { get; private set; } = GestureState.Idle;

    /// <summary>当前吸附候选格（拖动中有意义）。</summary>
    public GridRect? Target => _target;

    private GridRect? _target;
    private GridRect? _previewTarget;
    private RelocationResult? _previewResult;

    public GestureMachine(GridMetrics metrics, IPreviewTimer timer, IGestureHost host)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timer);
        ArgumentNullException.ThrowIfNull(host);
        _metrics = metrics;
        _timer = timer;
        _host = host;
    }

    /// <summary>配置变化后同步（墙尺寸）；拖动进行中不换墙。</summary>
    public void UpdateWall(WallGrid wall)
    {
        ArgumentNullException.ThrowIfNull(wall);
        if (State == GestureState.Idle)
        {
            _wall = wall;
        }
    }

    /// <summary>布局级提交成功后同步「原布局」基线。</summary>
    public void UpdateObjects(IReadOnlyList<LayoutObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (State == GestureState.Idle)
        {
            _originalObjects = objects;
        }
    }

    /// <summary>T1：Idle → Pressing。thresholds：鼠标 8 / 触摸 12（Q8）。</summary>
    public void Press(
        string objectId,
        GridRect objectBounds,
        IReadOnlyList<LayoutObject> currentObjects,
        DipPoint pointerPos,
        double dragStartThreshold)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentNullException.ThrowIfNull(currentObjects);
        if (State != GestureState.Idle)
        {
            return; // 拖动中的第二次按下（多点触控）不受理
        }

        _objectId = objectId;
        _objectBounds = objectBounds;
        _originalObjects = currentObjects;
        _pressPoint = pointerPos;
        _dragStartThreshold = dragStartThreshold > 0 ? dragStartThreshold : GestureThresholds.DragStartMouseDip;
        var origin = _metrics.OriginOf(objectBounds);
        GrabOffset = new DipPoint(pointerPos.X - origin.X, pointerPos.Y - origin.Y);
        _target = null;
        _previewTarget = null;
        _previewResult = null;
        State = GestureState.Pressing;
    }

    /// <summary>T2 / T5 / T7：位移启动拖动；拖动中目标格变化则清预览并重启 B4 计时。</summary>
    public void Move(DipPoint pointerPos)
    {
        switch (State)
        {
            case GestureState.Pressing:
                if (Distance(pointerPos, _pressPoint) > _dragStartThreshold)
                {
                    State = GestureState.Dragging;
                    _target = ComputeTarget(pointerPos);
                    _host.OnDragStarted(_objectId);
                    _timer.Start();
                }

                break;
            case GestureState.Dragging:
            {
                var candidate = ComputeTarget(pointerPos);
                if (_target is { } current && current != candidate)
                {
                    _target = candidate;
                    _previewTarget = null;
                    _previewResult = null;
                    _host.OnTargetChanged();
                    _timer.Start();
                }

                break;
            }
            case GestureState.DraggingWithPreview:
            {
                var candidate = ComputeTarget(pointerPos);
                if (_previewTarget is { } previewed && previewed != candidate)
                {
                    _target = candidate;
                    _previewTarget = null;
                    _previewResult = null;
                    State = GestureState.Dragging;
                    _host.OnTargetChanged();
                    _timer.Start();
                }

                break;
            }
        }
    }

    /// <summary>T3 / T9 / T10 / T11：抬起。</summary>
    public void Release(DipPoint pointerPos)
    {
        switch (State)
        {
            case GestureState.Pressing:
                State = GestureState.Idle;
                if (Distance(pointerPos, _pressPoint) <= _dragStartThreshold)
                {
                    _host.OnObjectActivated(_objectId); // 点击语义（§5.6）；M3 空目标无动作
                }

                ResetGesture();
                break;
            case GestureState.Dragging:
                FinalizeByEngine(); // T9：对最终候选格同步再跑一次 Relocate
                break;
            case GestureState.DraggingWithPreview:
                if (_previewTarget is { } previewed && _target is { } current && previewed == current)
                {
                    // T10：预览目标 == 松手目标 → 直接提交预览中的 ResultObjects 实例（不重跑引擎，INV-7）
                    var result = _previewResult!;
                    State = GestureState.Idle;
                    _timer.Stop();
                    ResetGesture();
                    _host.OnCommitted(result);
                }
                else
                {
                    FinalizeByEngine(); // T11
                }

                break;
        }
    }

    /// <summary>T8：Esc 全量恢复；B11 下菜单打开时 MenuFlyout 先消费 Esc，此处只处理拖动中的 Esc。</summary>
    public void Escape() => CancelGesture();

    /// <summary>T12：系统取消（指针丢失/Canceled）。</summary>
    public void PointerLost() => CancelGesture();

    /// <summary>T4：Pressing 中右键 → 终止手势（右键交由 ContextFlyout 弹对象菜单）；拖动态不受理。</summary>
    public void RightButton()
    {
        if (State == GestureState.Pressing)
        {
            State = GestureState.Idle;
            ResetGesture();
        }
    }

    /// <summary>B4 到时（目标格未变）：对原布局跑引擎；成功 → 预览态，失败 → 「不可放置」标记。</summary>
    public void PreviewTimerDue()
    {
        if (State != GestureState.Dragging || _target is not { } target)
        {
            return;
        }

        var result = RelocationEngine.Relocate(_originalObjects, _wall, _objectId, target);
        if (result.Success)
        {
            State = GestureState.DraggingWithPreview;
            _previewTarget = target;
            _previewResult = result;
            _host.OnPreviewReady(result);
        }
        else
        {
            _host.OnTargetInvalid(target);
        }
    }

    /// <summary>T9/T11：抬起时预览不可用 → 对最终候选格同步重跑引擎（成功提交 / 失败恢复+提示）。</summary>
    private void FinalizeByEngine()
    {
        State = GestureState.Idle;
        _timer.Stop();
        var target = _target;
        ResetGesture();
        if (target is not { } t)
        {
            _host.OnGestureCancelled();
            _host.OnDropRejected();
            return;
        }

        var result = RelocationEngine.Relocate(_originalObjects, _wall, _objectId, t);
        if (result.Success)
        {
            _host.OnCommitted(result);
        }
        else
        {
            _host.OnGestureCancelled();
            _host.OnDropRejected();
        }
    }

    private void CancelGesture()
    {
        switch (State)
        {
            case GestureState.Pressing:
                State = GestureState.Idle; // 尚无 ghost/预览，无需视觉恢复
                ResetGesture();
                break;
            case GestureState.Dragging:
            case GestureState.DraggingWithPreview:
                State = GestureState.Idle;
                _timer.Stop();
                ResetGesture();
                _host.OnGestureCancelled(); // 全量恢复原布局；配置与磁盘零改动
                break;
        }
    }

    private void ResetGesture()
    {
        _objectId = string.Empty;
        _target = null;
        _previewTarget = null;
        _previewResult = null;
    }

    /// <summary>
    /// 吸附规则（§5.3）：desiredTopLeft = 指针 − 抓取偏移；round 取最近格后 clamp 到墙内。
    /// clamp 后仍非法（跨栏/越界占用冲突）不自行修正，交引擎判 TargetInvalid。
    /// </summary>
    private GridRect ComputeTarget(DipPoint pointerPos)
    {
        var desiredX = pointerPos.X - GrabOffset.X;
        var desiredY = pointerPos.Y - GrabOffset.Y;
        var col = (int)Math.Round((desiredX - _metrics.Margin) / _metrics.Pitch);
        var row = (int)Math.Round((desiredY - _metrics.Margin) / _metrics.Pitch);
        col = Math.Clamp(col, 0, Math.Max(0, _wall.CellColumns - _objectBounds.Width));
        row = Math.Clamp(row, 0, Math.Max(0, _wall.Rows - _objectBounds.Height));
        return new GridRect(col, row, _objectBounds.Width, _objectBounds.Height);
    }

    private static double Distance(DipPoint a, DipPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
