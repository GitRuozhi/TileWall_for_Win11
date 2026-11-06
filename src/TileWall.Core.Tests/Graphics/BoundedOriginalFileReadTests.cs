using TileWall.Graphics;
using Xunit;

namespace TileWall.Core.Tests.Graphics;

public sealed class BoundedOriginalFileReadTests
{
    [Fact]
    public async Task BoundarySizedStream_IsAcceptedAndReturnedExactly()
    {
        var payload = new byte[] { 7, 2, 9, 4 };
        await using var source = new MutableLengthStream(payload, payload.Length);

        var bytes = await BoundedOriginalFileRead.ReadAsync(source, payload.Length, CancellationToken.None);

        Assert.Equal(payload, bytes);
        Assert.Equal(payload.Length, source.BytesRead);
    }

    [Fact]
    public async Task InitialOversize_IsRejectedBeforeReading()
    {
        await using var source = new MutableLengthStream([1, 2, 3, 4, 5], initialLength: 5);

        await Assert.ThrowsAsync<IOException>(() => BoundedOriginalFileRead.ReadAsync(source, 4, CancellationToken.None));

        Assert.Equal(0, source.BytesRead);
    }

    [Fact]
    public async Task GrowthAfterInitialLength_ReadsAtMostBudgetPlusOneThenRejects()
    {
        const int maximumBytes = 17;
        await using var source = new MutableLengthStream(new byte[maximumBytes + 1], maximumBytes,
            growByOneOnFirstRead: true);

        await Assert.ThrowsAsync<IOException>(() => BoundedOriginalFileRead.ReadAsync(source, maximumBytes, CancellationToken.None));

        Assert.Equal(maximumBytes + 1, source.BytesRead);
    }

    [Fact]
    public async Task TruncationDuringRead_IsRejected()
    {
        await using var source = new MutableLengthStream([1, 2, 3, 4], initialLength: 4,
            truncateTo: 2);

        await Assert.ThrowsAsync<IOException>(() => BoundedOriginalFileRead.ReadAsync(source, 4, CancellationToken.None));

        Assert.Equal(2, source.BytesRead);
    }

    [Fact]
    public async Task CancellationBeforeRead_DoesNotConsumeBytes()
    {
        await using var source = new MutableLengthStream([1, 2, 3], initialLength: 3);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BoundedOriginalFileRead.ReadAsync(source, 3, cancellation.Token));

        Assert.Equal(0, source.BytesRead);
    }

    private sealed class MutableLengthStream(
        byte[] bytes,
        long initialLength,
        bool growByOneOnFirstRead = false,
        int? truncateTo = null) : Stream
    {
        private long _length = initialLength;
        private int _position;
        private bool _changed;

        internal long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_changed)
            {
                _changed = true;
                if (growByOneOnFirstRead) _length++;
                if (truncateTo is { } shorter) _length = shorter;
            }

            var count = (int)Math.Min(buffer.Length, Math.Max(0, _length - _position));
            if (count == 0) return ValueTask.FromResult(0);
            var available = Math.Min(count, Math.Max(0, bytes.Length - _position));
            if (available > 0) bytes.AsMemory(_position, available).CopyTo(buffer);
            if (count > available) buffer.Span[available..count].Clear();
            _position += count;
            BytesRead += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
