namespace TileWall.Graphics;

/// <summary>Bounded separable Gaussian blur; input and output use premultiplied BGRA.</summary>
public static class LowResolutionBlur
{
    /// <summary>A desktop-only Gaussian with its sigma already converted from DIPs to capture pixels.</summary>
    internal static DecodedImageFrame ApplySigma(DecodedImageFrame source, double sigmaX, double sigmaY)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Width <= 0 || source.Height <= 0 || (long)source.Width * source.Height > 512L * 512 ||
            source.BgraPremultiplied.Length != (long)source.Width * source.Height * 4)
            throw new ArgumentOutOfRangeException(nameof(source));
        if (!double.IsFinite(sigmaX) || !double.IsFinite(sigmaY) || sigmaX is <= 0 or > 128 || sigmaY is <= 0 or > 128)
            throw new ArgumentOutOfRangeException(nameof(sigmaX));
        var horizontal = new byte[source.BgraPremultiplied.Length];
        var output = new byte[horizontal.Length];
        ApplyAxis(source.BgraPremultiplied.Span, horizontal, source.Width, source.Height, sigmaX, vertical: false);
        ApplyAxis(horizontal, output, source.Width, source.Height, sigmaY, vertical: true);
        return source with { BgraPremultiplied = output };
    }

    private static void ApplyAxis(ReadOnlySpan<byte> input, Span<byte> output, int width, int height,
        double sigma, bool vertical)
    {
        var radius = (int)Math.Ceiling(3 * sigma);
        var kernel = new double[radius * 2 + 1];
        var total = 0d;
        for (var step = -radius; step <= radius; step++)
            total += kernel[step + radius] = Math.Exp(-(step * (double)step) / (2 * sigma * sigma));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double blue = 0, green = 0, red = 0, alpha = 0;
                for (var step = -radius; step <= radius; step++)
                {
                    var sampleX = vertical ? x : Math.Clamp(x + step, 0, width - 1);
                    var sampleY = vertical ? Math.Clamp(y + step, 0, height - 1) : y;
                    var offset = (sampleY * width + sampleX) * 4;
                    var weight = kernel[step + radius];
                    blue += input[offset] * weight;
                    green += input[offset + 1] * weight;
                    red += input[offset + 2] * weight;
                    alpha += input[offset + 3] * weight;
                }
                var destination = (y * width + x) * 4;
                output[destination] = (byte)Math.Clamp(Math.Round(blue / total), 0, 255);
                output[destination + 1] = (byte)Math.Clamp(Math.Round(green / total), 0, 255);
                output[destination + 2] = (byte)Math.Clamp(Math.Round(red / total), 0, 255);
                output[destination + 3] = (byte)Math.Clamp(Math.Round(alpha / total), 0, 255);
            }
        }
    }

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
