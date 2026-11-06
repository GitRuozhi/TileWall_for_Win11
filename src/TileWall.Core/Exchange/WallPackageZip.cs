using System.Buffers.Binary;
using System.IO.Compression;

namespace TileWall.Core.Exchange;

internal static class WallPackageZip
{
    internal static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var parts = normalized.Split('/');
        if (normalized.Length is < 1 or > 1024 || parts.Any(p => p.Length == 0 || p is "." or ".." ||
                p.EndsWith('.') || p.EndsWith(' ') || p.Any(c => c < 32 || "<>:\"|?*".Contains(c))))
            throw new InvalidDataException("Unsafe package path.");
        return normalized;
    }

    // Inspect only bounded ZIP metadata before ZipArchive allocates its entry table.
    // No decompression implementation lives here; payloads use the .NET ZIP reader.
    internal static void Preflight(Stream stream, WallPackageLimits limits, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!stream.CanSeek || stream.Length < 22) throw new InvalidDataException("Invalid ZIP container.");
        // A legitimate stored/deflated container is bounded by the expansion budget,
        // deflate overhead and the maximum ZIP header/extra/comment fields. Reject huge
        // sparse prefixes before the whole-package confirmation hash reads them.
        var maximumContainerBytes = checked(limits.MaximumExpandedBytes + limits.MaximumExpandedBytes / 100 +
            limits.MaximumMetadataBytes);
        if (stream.Length > maximumContainerBytes) throw new InvalidDataException("ZIP physical size exceeds its metadata and payload budget.");
        var tail = new byte[(int)Math.Min(stream.Length, 65557)];
        stream.Position = stream.Length - tail.Length; stream.ReadExactly(tail);
        var end = -1;
        for (var i = tail.Length - 22; i >= 0; --i)
            if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length) { end = i; break; }
        if (end < 0 || U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0)
            throw new InvalidDataException("Unsupported multi-disk ZIP.");
        long count = U16(tail, end + 10), size = U32(tail, end + 12), offset = U32(tail, end + 16);
        if (U16(tail, end + 8) != count) throw new InvalidDataException("Multi-disk ZIP entry count.");
        var endOffset = stream.Length - tail.Length + end;
        if (count == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
        {
            if (endOffset < 20) throw new InvalidDataException("Missing ZIP64 locator.");
            stream.Position = endOffset - 20; var locator = new byte[20]; stream.ReadExactly(locator);
            if (U32(locator, 0) != 0x07064b50 || U32(locator, 4) != 0 || U32(locator, 16) != 1)
                throw new InvalidDataException("Invalid ZIP64 locator.");
            var zip64Offset = I64(locator, 8);
            if (zip64Offset < 0 || zip64Offset > endOffset - 56) throw new InvalidDataException("Invalid ZIP64 offset.");
            stream.Position = zip64Offset; var record = new byte[56]; stream.ReadExactly(record);
            if (U32(record, 0) != 0x06064b50 || U32(record, 16) != 0 || U32(record, 20) != 0 ||
                I64(record, 24) != I64(record, 32)) throw new InvalidDataException("Invalid ZIP64 directory.");
            count = I64(record, 32); size = I64(record, 40); offset = I64(record, 48);
        }
        if (count is < 1 || count > limits.MaximumEntries || size < 0 || size > limits.MaximumMetadataBytes || offset < 0 ||
            size > stream.Length || offset > stream.Length - size || offset + size > endOffset)
            throw new InvalidDataException("ZIP directory exceeds its budget.");
        stream.Position = offset;
        var header = new byte[46]; long expanded = 0, compressed = 0;
        for (long i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (stream.Position > offset + size - header.Length) throw new InvalidDataException("Truncated ZIP directory.");
            stream.ReadExactly(header);
            var nameLength = U16(header, 28); var extra = U16(header, 30); var comment = U16(header, 32);
            if (U32(header, 0) != 0x02014b50 || nameLength is < 1 or > 1024 || U16(header, 34) != 0 ||
                (U16(header, 8) & 1) != 0 || stream.Position + nameLength + extra + comment > offset + size)
                throw new InvalidDataException("Invalid or excessive ZIP entry metadata.");
            var length = U32(header, 24);
            compressed = checked(compressed + U32(header, 20));
            // Our individual payload limits are below 4 GiB; a ZIP64 per-file size is never needed.
            if (length > limits.MaximumFileBytes || (expanded += length) > limits.MaximumExpandedBytes)
                throw new InvalidDataException("ZIP expansion exceeds its budget.");
            stream.Seek(nameLength + extra + comment, SeekOrigin.Current);
        }
        if (stream.Position != offset + size) throw new InvalidDataException("Unexpected ZIP directory data.");
        // The whole physical container includes local extra fields, comments,
        // data descriptors and any unreferenced prefix. Bound their aggregate too.
        if (compressed > stream.Length || stream.Length - compressed > limits.MaximumMetadataBytes)
            throw new InvalidDataException("ZIP metadata exceeds its aggregate budget.");
        stream.Position = 0;
    }

    internal static async Task<byte[]> ReadAsync(ZipArchiveEntry entry, long maximum, CancellationToken token)
    {
        if (entry.Length < 0 || entry.Length > maximum || entry.Length > int.MaxValue)
            throw new InvalidDataException("Package file exceeds its budget.");
        using var input = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        var buffer = new byte[64 * 1024]; long actual = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            actual += read;
            if (actual > maximum || actual > entry.Length) throw new InvalidDataException("Actual ZIP expansion exceeds its declaration.");
            output.Write(buffer, 0, read);
        }
        if (actual != entry.Length) throw new InvalidDataException("Truncated ZIP payload.");
        return output.ToArray();
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static long I64(byte[] bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
}
