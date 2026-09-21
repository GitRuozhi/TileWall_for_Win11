using TileWall.Core.Shell;
using Xunit;

namespace TileWall.Core.Tests.Shell;

/// <summary>
/// M9 测试 T4（设计 §6.1）：暂存队列——会话打开时入队不处理（队列只存取）；SessionClosed 后
/// FIFO 排空；排空复检由调用方执行（本套件验证 FIFO 次序）；exitInFlight → Discard 整体丢弃。
/// </summary>
public sealed class ExplorerAddQueueTests
{
    [Fact]
    public void Enqueue_WhileSessionOpen_StagesWithoutProcessing()
    {
        var queue = new ExplorerAddQueue();

        queue.Enqueue(new ExplorerAddMessage(["C:\\a.txt"], false));
        queue.Enqueue(new ExplorerAddMessage(["C:\\b.txt", "C:\\c.txt"], false));

        // 入队即暂存：不触发任何处理回调（队列无事件/回调面），仅计数可见
        Assert.True(queue.HasPending);
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void Drain_AfterSessionClosed_YieldsBatchesFifo()
    {
        var queue = new ExplorerAddQueue();
        var first = new ExplorerAddMessage(["C:\\first"], false);
        var second = new ExplorerAddMessage(["C:\\second-1", "C:\\second-2"], true);
        queue.Enqueue(first);
        queue.Enqueue(second);

        Assert.Equal(first, queue.TryDequeue()); // FIFO：先到先处理
        Assert.Equal(second, queue.TryDequeue()); // 截断标记随批次保留
        Assert.Null(queue.TryDequeue());
        Assert.False(queue.HasPending);
    }

    [Fact]
    public void Discard_OnExitInFlight_DropsAllBatches()
    {
        var queue = new ExplorerAddQueue();
        queue.Enqueue(new ExplorerAddMessage(["C:\\a.txt"], false));
        queue.Enqueue(new ExplorerAddMessage([], false));

        queue.Discard(); // exitInFlight：整体丢弃、不部分保留

        Assert.Equal(0, queue.Count);
        Assert.False(queue.HasPending);
        Assert.Null(queue.TryDequeue());
    }

    [Fact]
    public void Enqueue_NullMessage_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ExplorerAddQueue().Enqueue(null!));
    }
}
