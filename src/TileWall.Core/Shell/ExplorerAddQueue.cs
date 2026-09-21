namespace TileWall.Core.Shell;

/// <summary>
/// Explorer 添加请求暂存队列（M9 设计 §4.5 第 2 步，§13.4/§17.2）：
/// 模态/适配会话打开期间到达的批次 FIFO 暂存，会话结束后由主应用侧逐批排空（排空时二次容量复检在调用方）；
/// 退出在飞（exitInFlight）时整体丢弃，不跨会话持久化。本类只存取不处理——「会话打开时入队不处理、
/// SessionClosed 后 FIFO 排空」的时序由调用方事件接线，队列保持纯逻辑可单测（M9 测试 T4）。
/// </summary>
public sealed class ExplorerAddQueue
{
    private readonly Queue<ExplorerAddMessage> _messages = new();

    /// <summary>暂存中的批次数。</summary>
    public int Count => _messages.Count;

    /// <summary>是否存在待处理批次。</summary>
    public bool HasPending => _messages.Count > 0;

    /// <summary>入队一批（原样保存，含空批次——空批次排空时呈现「无可添加对象」反馈）。</summary>
    public void Enqueue(ExplorerAddMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _messages.Enqueue(message);
    }

    /// <summary>FIFO 取出最早一批；空则 null。</summary>
    public ExplorerAddMessage? TryDequeue() => _messages.Count > 0 ? _messages.Dequeue() : null;

    /// <summary>整体丢弃（退出在飞；不持久化、不部分保留）。</summary>
    public void Discard() => _messages.Clear();
}
