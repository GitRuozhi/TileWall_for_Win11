namespace TileWall.Graphics;

/// <summary>Bounded separable Gaussian blur; input and output use premultiplied BGRA.</summary>
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
        var sigma = Math.Max(1, radius / 3d);
        var kernel = Enumerable.Range(-radius, radius * 2 + 1)
            .Select(offset => Math.Exp(-(offset * offset) / (2 * sigma * sigma))).ToArray();
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var start = Math.Max(0, x - radius);
                var end = Math.Min(source.Width - 1, x + radius);
                var offset = (y * source.Width + x) * 4;
                for (var channel = 0; channel < 4; channel++)
                {
                    var sum = 0d;
                    var weights = 0d;
                    for (var sample = start; sample <= end; sample++)
                    {
                        var weight = kernel[sample - x + radius];
                        sum += input[(y * source.Width + sample) * 4 + channel] * weight;
                        weights += weight;
                    }
                    horizontal[offset + channel] = (byte)Math.Clamp(Math.Round(sum / weights), 0, 255);
                }
            }
        }
        for (var y = 0; y < source.Height; y++)
        {
            var start = Math.Max(0, y - radius);
            var end = Math.Min(source.Height - 1, y + radius);
            for (var x = 0; x < source.Width; x++)
            {
                var offset = (y * source.Width + x) * 4;
                for (var channel = 0; channel < 4; channel++)
                {
                    var sum = 0d;
                    var weights = 0d;
                    for (var sample = start; sample <= end; sample++)
                    {
                        var weight = kernel[sample - y + radius];
                        sum += horizontal[(sample * source.Width + x) * 4 + channel] * weight;
                        weights += weight;
                    }
                    output[offset + channel] = (byte)Math.Clamp(Math.Round(sum / weights), 0, 255);
                }
            }
        }
        return source with { BgraPremultiplied = output };
    }
}
