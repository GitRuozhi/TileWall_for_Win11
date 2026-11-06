using TileWall.Core.Configuration;
using TileWall.Core.Storage;

namespace TileWall.Core.Shell;

internal sealed record ExplorerMenuPreferenceState(bool Enabled, string? Error = null)
{
    public bool ReadSucceeded => Error is null;
}

/// <summary>The package's single menu preference. Only the owning application instance writes it;
/// Explorer hosts read the same strict, bounded protocol independently.</summary>
internal sealed class ExplorerMenuPreference(IFileStore files, string dataRoot)
{
    internal const string FileName = "explorer-menu.state";
    private static ReadOnlySpan<byte> EnabledBytes => "TileWall.ExplorerMenu/1\n1\n"u8;
    private static ReadOnlySpan<byte> DisabledBytes => "TileWall.ExplorerMenu/1\n0\n"u8;
    private readonly WallStoragePaths _paths = new(files, dataRoot);

    internal ExplorerMenuPreferenceState Read()
    {
        try
        {
            var path = _paths.Under(FileName);
            _paths.Guard(path);
            var info = files.GetPathInfo(path);
            if (!info.Exists) return new(true);
            if (info.IsDirectory) throw new IOException("The Explorer menu preference is a directory.");
            _paths.Guard(path);
            using var stream = files.OpenReadProtected(path);
            Span<byte> bytes = stackalloc byte[65];
            var length = 0;
            while (length < bytes.Length)
            {
                var count = stream.Read(bytes[length..]);
                if (count == 0) break;
                length += count;
            }
            _paths.Guard(path);
            return bytes[..length].SequenceEqual(EnabledBytes) ? new(true)
                : bytes[..length].SequenceEqual(DisabledBytes) ? new(false)
                : new(false, "Invalid Explorer menu preference format.");
        }
        catch (Exception ex) when (IsIoError(ex)) { return new(false, ex.Message); }
    }

    internal ExplorerMenuPreferenceState Write(bool enabled)
    {
        string? temporary = null;
        try
        {
            // A corrupt/unreadable preference is never silently replaced by an ordinary toggle.
            var before = Read();
            if (!before.ReadSucceeded) return before;
            var target = _paths.Under(FileName);
            _paths.Guard(_paths.Root);
            if (files.GetPathInfo(_paths.Root) is { Exists: true, IsDirectory: false })
                throw new IOException("The application data root is not a directory.");
            files.CreateDirectory(_paths.Root);
            _paths.Guard(target);
            temporary = _paths.Under($".{FileName}.{Guid.NewGuid():N}.tmp");
            _paths.Guard(temporary);
            files.WriteAllBytes(temporary, (enabled ? EnabledBytes : DisabledBytes).ToArray());
            // The actual replace is the sole commit point. Recheck both paths at that boundary.
            _paths.Guard(temporary);
            _paths.Guard(target);
            if (files.GetPathInfo(target) is { Exists: true, IsDirectory: true })
                throw new IOException("The Explorer menu preference is a directory.");
            files.Move(temporary, target, overwrite: true);
            temporary = null;
            return Read();
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            var actual = Read();
            return actual with { Error = actual.Error ?? ex.Message };
        }
        finally
        {
            if (temporary is not null)
            {
                try { _paths.Guard(temporary); files.Delete(temporary); }
                catch (Exception ex) when (IsIoError(ex)) { }
            }
        }
    }

    private static bool IsIoError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException or NotSupportedException;
}
