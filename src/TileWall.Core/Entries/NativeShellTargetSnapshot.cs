using System.Security.Cryptography;

namespace TileWall.Core.Entries;

/// <summary>A process-local copy of a PIDL obtained directly from Windows, never from persisted input.</summary>
public sealed partial class NativeShellTargetSnapshot
{
    private readonly byte[] _idList;
    private readonly Action? _confirmCurrent;

    private NativeShellTargetSnapshot(byte[] idList, LaunchIdentity identity, string parsingName, Action? confirmCurrent)
    {
        _idList = idList;
        Identity = identity;
        ParsingName = parsingName;
        IdListSha256 = Convert.ToHexString(SHA256.HashData(idList));
        _confirmCurrent = confirmCurrent;
    }

    public LaunchIdentity Identity { get; }
    public string ParsingName { get; }
    public string IdListSha256 { get; }

    // Only fresh Shell-owned PIDLs may cross this boundary. No byte-array or JSON constructor exists.
    internal static NativeShellTargetSnapshot Capture(IntPtr shellOwnedPidl, LaunchIdentity identity,
        string parsingName, Action? confirmCurrent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parsingName);
        return new(ShellLinkFileService.CopyIdList(shellOwnedPidl), identity, parsingName, confirmCurrent);
    }

    internal byte[] CopyIdList() => (byte[])_idList.Clone();

    internal void ConfirmCurrent() => _confirmCurrent?.Invoke();
}
