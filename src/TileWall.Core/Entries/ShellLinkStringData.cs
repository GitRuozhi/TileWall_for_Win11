using System.Buffers.Binary;
using System.Text;

namespace TileWall.Core.Entries;

/// <summary>Reads only persisted Unicode WORKING_DIR/ICON_LOCATION, which Shell's path getters may truncate internally.</summary>
internal static class ShellLinkStringData
{
    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");
    private static readonly UnicodeEncoding Unicode = new(false, false, true);

    internal static (string WorkingDirectory, string IconPath)? ReadPaths(Stream source)
    {
        source.Position = 0;
        Span<byte> header = stackalloc byte[76];
        ReadExactly(source, header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 76 || new Guid(header[4..20]) != ShellLinkClassId)
            throw InvalidStringData();
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        var isUnicode = (flags & 0x80) != 0;

        Span<byte> size = stackalloc byte[4];
        if ((flags & 1) != 0)
        {
            ReadExactly(source, size[..2]);
            Skip(source, BinaryPrimitives.ReadUInt16LittleEndian(size));
        }
        if ((flags & 2) != 0)
        {
            ReadExactly(source, size);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(size);
            if (length < 4) throw InvalidStringData();
            Skip(source, length - 4);
        }
        // MS-SHLLINK 2.4: NAME, RELATIVE_PATH, WORKING_DIR, ARGUMENTS, ICON_LOCATION.
        var workingDirectory = string.Empty;
        var iconPath = string.Empty;
        for (uint flag = 4; flag <= 0x40; flag <<= 1)
        {
            if ((flags & flag) == 0) continue;
            ReadExactly(source, size[..2]);
            var characterCount = BinaryPrimitives.ReadUInt16LittleEndian(size);
            var isPath = flag is 0x10 or 0x40;
            // ANSI fields stay with COM; do not guess their original system code page,
            // or accept an internally truncated boundary value as a confirmed path.
            if (!isUnicode && isPath && characterCount >= 259) throw InvalidStringData();
            var byteCount = characterCount * (isUnicode ? 2 : 1);
            if (!isUnicode || !isPath)
            {
                Skip(source, byteCount);
                continue;
            }
            var bytes = new byte[byteCount]; // CountCharacters is a bounded UInt16, never a file-sized allocation.
            ReadExactly(source, bytes);
            string text;
            try { text = Unicode.GetString(bytes); }
            catch (DecoderFallbackException) { throw InvalidStringData(); }
            if (text.Contains('\0')) throw InvalidStringData();
            // MS-SHLLINK bounds WORKING_DIR to MAX_PATH. Shell can misread following
            // arguments when an oversized working-directory field is injected into a file.
            if (flag == 0x10 && text.Length > 260) throw InvalidStringData();
            if (flag == 0x10) workingDirectory = text;
            else iconPath = text;
        }
        return isUnicode ? (workingDirectory, iconPath) : null;
    }

    private static void Skip(Stream source, long count)
    {
        if (count > source.Length - source.Position) throw InvalidStringData();
        source.Seek(count, SeekOrigin.Current);
    }

    private static void ReadExactly(Stream source, Span<byte> bytes)
    {
        if (bytes.Length > source.Length - source.Position) throw InvalidStringData();
        source.ReadExactly(bytes);
    }

    private static InvalidDataException InvalidStringData() =>
        new("The shortcut path cannot be confirmed from complete Unicode StringData.");
}
