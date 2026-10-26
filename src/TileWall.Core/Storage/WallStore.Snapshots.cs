using TileWall.Core.Wall;

namespace TileWall.Core.Storage;

public sealed partial class WallStore
{
    private static readonly string[] SnapshotFileNames = ["settings.json", "wall.json", "recycle-bin.json"];

    private WallDocument ReadSnapshotDocument(byte[] manifestBytes)
    {
        var manifest = UserDataSnapshotJson.ReadManifest(manifestBytes);
        return ReadSnapshotAt(manifest, _paths.Under("Snapshots", manifest.SnapshotId));
    }

    private WallDocument ReadSnapshotAt(UserDataManifest manifest, string directory, Func<string, byte[]>? read = null)
    {
        _paths.GuardTree(directory);
        var info = _files.GetPathInfo(directory);
        if (!info.Exists || !info.IsDirectory || _files.ListDirectories(directory).Count != 0 ||
            !_files.ListFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal)
                .SequenceEqual(SnapshotFileNames.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Snapshot directory is incomplete or contains unknown files.");
        read ??= _files.ReadAllBytes;
        return UserDataSnapshotJson.Read(manifest, name => read(Path.Combine(directory, name)), _paths.Gallery);
    }

    private void StageSnapshot(PreparedUserDataSnapshot snapshot, string stageDirectory)
    {
        var directory = Path.Combine(stageDirectory, "snapshot");
        _files.CreateDirectory(directory);
        foreach (var (name, bytes) in snapshot.Files) _files.WriteAllBytes(Path.Combine(directory, name), bytes);
        _ = ReadSnapshotAt(snapshot.Manifest, directory);
    }

    private void InstallSnapshot(UserDataManifest manifest, string stageDirectory)
    {
        var destination = _paths.Under("Snapshots", manifest.SnapshotId);
        if (_files.GetPathInfo(destination).Exists)
        {
            _ = ReadSnapshotAt(manifest, destination);
            return;
        }
        var source = Path.Combine(stageDirectory, "snapshot");
        _ = ReadSnapshotAt(manifest, source);
        _files.CreateDirectory(_paths.Snapshots);
        _files.MoveDirectory(source, destination);
        _ = ReadSnapshotAt(manifest, destination);
    }
}
