namespace TileWall.Graphics;

/// <summary>Boolean native-icon admission. Never crops, repaints, scales, or ranks an image.</summary>
internal static class IconCompositionRules
{
    internal static bool Accept(byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > Array.MaxLength / 4 ||
            pixels.Length != (long)width * height * 4) return false;
        var visible = false;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            if (pixels[i] > a || pixels[i + 1] > a || pixels[i + 2] > a) return false;
            visible |= a != 0;
        }
        if (!visible) return false;
        // A source may have transparent corners around a flat plate. Sample the
        // perimeter rather than assuming the corner's RGB is its background.
        var samples = new List<int>();
        for (var step = 0; step <= 16; step++)
        {
            var x = (int)((long)(width - 1) * step / 16);
            var y = (int)((long)(height - 1) * step / 16);
            samples.Add(x); samples.Add((height - 1) * width + x);
            samples.Add(y * width); samples.Add(y * width + width - 1);
        }
        var seed = -1;
        var solidSamples = 0;
        var matchingSamples = 0;
        foreach (var index in samples)
        {
            if (pixels[index * 4 + 3] < 240) continue;
            solidSamples++;
            if (seed < 0) seed = index;
            if (Near(pixels, seed, index)) matchingSamples++;
        }
        var flatBorder = solidSamples > 0 && matchingSamples * 4 >= solidSamples * 3;
        var seen = new bool[checked(width * height)];
        var queue = new Queue<int>();
        foreach (var index in samples) Admit(index);
        // Every perimeter pixel can start the connected background, not just
        // the small sample used to identify its color.
        for (var x = 0; x < width; x++) { Admit(x); Admit((height - 1) * width + x); }
        for (var y = 0; y < height; y++) { Admit(y * width); Admit(y * width + width - 1); }
        while (queue.TryDequeue(out var index))
        {
            var x = index % width; var y = index / width;
            if (x > 0) Admit(index - 1); if (x + 1 < width) Admit(index + 1);
            if (y > 0) Admit(index - width); if (y + 1 < height) Admit(index + width);
        }
        var left = width; var top = height; var right = -1; var bottom = -1;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = y * width + x;
            if (seen[index] || pixels[index * 4 + 3] == 0) continue;
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        // A flat monochrome shape touching the edge has no separable border;
        // this is not evidence to remove its authored visible pixels.
        if (right < left) return true;
        var small = (long)(right - left + 1) * 5 <= (long)width * 3 &&
                    (long)(bottom - top + 1) * 5 <= (long)height * 3;
        var centered = Math.Abs((long)left + right + 1 - width) * 5 <= width * 2L &&
                       Math.Abs((long)top + bottom + 1 - height) * 5 <= height * 2L;
        return !(small && centered);

        void Admit(int index)
        {
            if (seen[index]) return;
            if (pixels[index * 4 + 3] != 0 && !(flatBorder && Near(pixels, seed, index))) return;
            seen[index] = true; queue.Enqueue(index);
        }
    }

    private static bool Near(ReadOnlySpan<byte> pixels, int first, int second)
    {
        var a = pixels[first * 4 + 3]; var b = pixels[second * 4 + 3];
        if (a < 240 || b < 240) return false;
        // Compare visible color, not premultiplied values or a named plate color.
        for (var channel = 0; channel < 3; channel++)
            if (Math.Abs(pixels[first * 4 + channel] * 255 / a - pixels[second * 4 + channel] * 255 / b) > 8) return false;
        return true;
    }
}
