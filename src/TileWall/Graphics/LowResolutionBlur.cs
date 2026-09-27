namespace TileWall.Graphics;

/// <summary>Small, bounded two-pass blur for a puzzle-wide underfill. Input and output are premultiplied BGRA.</summary>
public static class LowResolutionBlur
{
    public static DecodedImageFrame Apply(DecodedImageFrame source, int radius = 8)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (radius is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(radius));
        if (source.Width <= 0 || source.Height <= 0 ||
            (long)source.Width * source.Height > 512L * 512 ||
            source.BgraPremultiplied.Length != (long)source.Width * source.Height * 4)
            throw new ArgumentOutOfRangeException(nameof(source), "Blur input must be a valid frame of at most 512×512 pixels.");

        var input = source.BgraPremultiplied.Span;
        var horizontal = new byte[input.Length];
        var output = new byte[input.Length];
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var start = Math.Max(0, x - radius);
                var end = Math.Min(source.Width - 1, x + radius);
                var count = end - start + 1;
                var offset = (y * source.Width + x) * 4;
                for (var channel = 0; channel < 4; channel++)
                {
                    var sum = 0;
                    for (var sample = start; sample <= end; sample++)
                        sum += input[(y * source.Width + sample) * 4 + channel];
                    horizontal[offset + channel] = (byte)((sum + count / 2) / count);
                }
            }
        }
        for (var y = 0; y < source.Height; y++)
        {
            var start = Math.Max(0, y - radius);
            var end = Math.Min(source.Height - 1, y + radius);
            var count = end - start + 1;
            for (var x = 0; x < source.Width; x++)
            {
                var offset = (y * source.Width + x) * 4;
                for (var channel = 0; channel < 4; channel++)
                {
                    var sum = 0;
                    for (var sample = start; sample <= end; sample++)
                        sum += horizontal[(sample * source.Width + x) * 4 + channel];
                    output[offset + channel] = (byte)((sum + count / 2) / count);
                }
            }
        }
        return source with { BgraPremultiplied = output };
    }
}

public readonly record struct RenderGenerationToken(long Value, string MemberId, string CandidateId);

/// <summary>Cancellation saves work; this gate prevents a completed old request from reaching the wall.</summary>
public sealed class RenderGenerationGate
{
    private long _value;

    public RenderGenerationToken Begin(string memberId, string candidateId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        return new RenderGenerationToken(Interlocked.Increment(ref _value), memberId, candidateId);
    }

    public void Invalidate() => Interlocked.Increment(ref _value);

    public bool CanPresent(RenderGenerationToken token, string memberId, string candidateId, bool visible) =>
        visible && token.Value == Interlocked.Read(ref _value) &&
        string.Equals(token.MemberId, memberId, StringComparison.Ordinal) &&
        string.Equals(token.CandidateId, candidateId, StringComparison.Ordinal);
}
