using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TileWall.Core.Entries;

public sealed partial class NativeShellTargetSnapshot
{
    private static readonly Guid AppsFolderId = new("4234D49B-0245-4DF3-B780-3893943456E1");
    private static readonly Guid TilePropertiesId = new("86D40B4D-9069-443C-819A-2A54090DCCEC");
    private static readonly Guid StringNamePropertiesId = new("D5CDD505-2E9C-101B-9397-08002B2CF9AE");

    /// <summary>Only a live native template authorizes this data-only comparison; persisted bytes are never bound to Shell.</summary>
    internal bool MatchesStoredBackgroundVariant(LinkFields fields, ReadOnlySpan<byte> storedIdList) =>
        MatchesBackgroundVariant(_idList, Identity, fields, storedIdList);

    internal static bool MatchesBackgroundVariant(ReadOnlySpan<byte> freshIdList, LaunchIdentity identity,
        LinkFields fields, ReadOnlySpan<byte> storedIdList)
    {
        if (!fields.HasIdList || !string.IsNullOrEmpty(fields.TargetPath) || fields.Arguments != identity.Arguments ||
            fields.WorkingDirectory != identity.WorkingDirectory || fields.ShellIdListHash is null ||
            freshIdList.Length != storedIdList.Length ||
            Convert.ToHexString(SHA256.HashData(storedIdList)) != fields.ShellIdListHash ||
            !TryBackgroundValueOffset(freshIdList, out var background)) return false;
        // The complete envelope, names, registration, resources and other property
        // payloads must be identical. Only this parsed VT_UI4 presentation value may differ.
        return freshIdList[..background].SequenceEqual(storedIdList[..background]) &&
            freshIdList[(background + sizeof(uint))..].SequenceEqual(storedIdList[(background + sizeof(uint))..]);
    }

    private static bool TryBackgroundValueOffset(ReadOnlySpan<byte> pidl, out int background)
    {
        background = -1;
        // Recognize only the Windows AppsFolder envelope captured by the native
        // provider. An unknown SHITEMID/APPS layout keeps ordinary raw matching.
        const int rootLength = 20, childHeaderLength = 22, childTrailerLength = 2;
        if (pidl.Length < rootLength + childHeaderLength + childTrailerLength + sizeof(ushort) || pidl.Length > ushort.MaxValue ||
            BinaryPrimitives.ReadUInt16LittleEndian(pidl) != rootLength || pidl[2] != 0x1f || pidl[3] != 0x80 ||
            new Guid(pidl.Slice(4, 16)) != AppsFolderId) return false;
        var child = pidl[rootLength..^sizeof(ushort)];
        if (BinaryPrimitives.ReadUInt16LittleEndian(pidl[^sizeof(ushort)..]) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(child) != child.Length || child[2] != 0 || child[3] != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(child[4..]) != child.Length - 6 ||
            !child.Slice(6, 4).SequenceEqual("APPS"u8) ||
            !child.Slice(12, 10).SequenceEqual(new byte[] { 8, 0, 3, 0, 0, 0, 0, 0, 0, 0 }) ||
            BinaryPrimitives.ReadUInt16LittleEndian(child[^childTrailerLength..]) != 0) return false;
        var storageLength = BinaryPrimitives.ReadUInt16LittleEndian(child[10..]);
        if (storageLength != child.Length - childHeaderLength - childTrailerLength) return false;
        var store = child.Slice(childHeaderLength, storageLength);
        var formats = new HashSet<Guid>();
        var offset = 0;
        while (store.Length - offset >= sizeof(uint))
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(store[offset..]);
            if (size == 0) return offset + sizeof(uint) == store.Length && background >= 0;
            if (size < 28 || size > store.Length - offset) return false;
            var storage = store.Slice(offset, (int)size);
            if (BinaryPrimitives.ReadUInt32LittleEndian(storage[4..]) != 0x53505331) return false;
            var format = new Guid(storage.Slice(8, 16));
            if (!formats.Add(format) || format == StringNamePropertiesId) return false;
            var identifiers = new HashSet<uint>();
            var valueOffset = 24;
            var terminated = false;
            while (storage.Length - valueOffset >= sizeof(uint))
            {
                var valueSize = BinaryPrimitives.ReadUInt32LittleEndian(storage[valueOffset..]);
                if (valueSize == 0)
                {
                    terminated = valueOffset + sizeof(uint) == storage.Length;
                    break;
                }
                if (valueSize < 13 || valueSize > storage.Length - valueOffset) return false;
                var value = storage.Slice(valueOffset, (int)valueSize);
                var id = BinaryPrimitives.ReadUInt32LittleEndian(value[4..]);
                if (!identifiers.Add(id) || value[8] != 0 || BinaryPrimitives.ReadUInt16LittleEndian(value[11..]) != 0)
                    return false;
                if (format == TilePropertiesId && id == 4)
                {
                    // PKEY_Tile_Background, VT_UI4; MS-PROPSTORE integer-name
                    // header (9 bytes) + MS-OLEPS typed UI4 (8 bytes).
                    if (background >= 0 || valueSize != 17 || BinaryPrimitives.ReadUInt16LittleEndian(value[9..]) != 19)
                        return false;
                    background = rootLength + childHeaderLength + offset + valueOffset + 13;
                }
                valueOffset += (int)valueSize;
            }
            if (!terminated) return false;
            offset += (int)size;
        }
        return false;
    }
}
