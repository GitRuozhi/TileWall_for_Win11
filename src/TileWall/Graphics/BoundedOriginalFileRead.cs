namespace TileWall.Graphics;

/// <summary>Reads one external image from a single read-only handle with a strict byte budget.</summary>
internal static class BoundedOriginalFileRead
{
    internal static FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    internal static async Task<byte[]> ReadAsync(Stream source, int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (!source.CanRead || !source.CanSeek || source.Position != 0)
            throw new IOException("Image source must be a readable stream positioned at its beginning.");

        var initialLength = source.Length;
        if (initialLength is < 1 || initialLength > maximumBytes)
            throw new IOException("Missing or oversized image.");

        // Read the reported bytes directly into the retained array, then probe one extra
        // byte. This distinguishes a source that grew without keeping a second 32 MiB
        // buffer alive. Total bytes read can never exceed maximumBytes + 1.
        var buffer = new byte[checked((int)initialLength)];
        var count = 0;
        while (count < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer.AsMemory(count, buffer.Length - count), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }

        if (count != initialLength)
            throw new IOException("Image changed or exceeded its byte budget while being prepared.");

        cancellationToken.ThrowIfCancellationRequested();
        var overflowProbe = new byte[1];
        if (await source.ReadAsync(overflowProbe.AsMemory(), cancellationToken).ConfigureAwait(false) != 0 ||
            source.Length != initialLength)
            throw new IOException("Image changed or exceeded its byte budget while being prepared.");

        return buffer;
    }
}
