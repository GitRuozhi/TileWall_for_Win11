using System.IO.Compression;
using System.Security.Cryptography;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Wall;

namespace TileWall.Core.Exchange;

/// <summary>Write one selected snapshot, validate its whole ZIP, then publish at the chosen destination.</summary>
public sealed class WallPackageWriter
{
    private readonly WallPackageReader _reader;
    private readonly Action<string, string, string?> _publish;
    private readonly Action? _beforePublish;

    public WallPackageWriter(WallPackageReader reader) : this(reader, Publish) { }

    // Narrow publication fault seam: container creation and Reader validation still use real files.
    internal WallPackageWriter(WallPackageReader reader, Action<string, string, string?> publish, Action? beforePublish = null)
    {
        _reader = reader; _publish = publish; _beforePublish = beforePublish;
    }

    internal static void Publish(string temporary, string destination, string? backup)
    {
        if (backup is null) File.Move(temporary, destination, overwrite: false);
        else File.Replace(temporary, destination, backup);
    }

    public async Task<WallPackageWriteResult> WriteAsync(WallPackageSnapshot snapshot, string destination,
        string appVersion, IEnumerable<string>? additionalProtectedPaths = null, WallPackageLimits? limits = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string? temporary = null, originalHash = null, packageHash = null, contentFingerprint = null;
        HashSet<string>? protectedSources = null;
        var publishAttempted = false;
        var temporaryCreated = false;
        string? backup = null;
        try
        {
            token.ThrowIfCancellationRequested();
            snapshot.EnsureAvailable();
            limits ??= new(); limits.Validate();
            ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
            ArgumentException.ThrowIfNullOrWhiteSpace(destination);
            if (!Path.IsPathFullyQualified(destination) || !Path.GetExtension(destination).Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                destination.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Choose an absolute .zip file path.");
            destination = Path.GetFullPath(destination);
            protectedSources = ProtectedSourcePaths(snapshot, additionalProtectedPaths);
            GuardDestination(destination, snapshot.DataRoot, protectedSources);
            originalHash = await DestinationHashAsync(destination, snapshot.DataRoot, protectedSources, token).ConfigureAwait(false);
            var content = snapshot.Content;
            WallPackageJson.ValidateContent(content, limits, token);
            var wallBytes = WallPackageJson.Serialize(content);
            if (wallBytes.Length > limits.MaximumContentBytes) throw new InvalidDataException("Wall content exceeds its byte budget.");
            var files = new List<WallPackageFile> { new("wall.json", wallBytes.Length, WallPackageJson.Hash(wallBytes), "wall") };
            foreach (var member in content.Members.Where(member => member.EntryKind is not null))
            {
                var entry = snapshot.Entries[member.Id];
                files.Add(new(WallPackageJson.EntryPath(member), entry.Length, entry.Sha256, "entry"));
            }
            if (!content.Galleries.Select(gallery => gallery.Id).ToHashSet(StringComparer.Ordinal).SetEquals(snapshot.Galleries.Keys))
                throw new InvalidDataException("Frozen gallery closure differs from selected content.");
            WallPackageJson.ValidateGalleryExpansion(content, snapshot.Galleries.Values.Select(gallery => gallery.Manifest), limits);
            foreach (var gallery in content.Galleries)
            {
                var source = snapshot.Galleries[gallery.Id];
                if (source.Reference.ManifestSha256 != gallery.ManifestSha256) throw new InvalidDataException("Gallery manifest changed.");
                var bytes = source.ManifestBytes;
                files.Add(new(WallPackageJson.GalleryPath(gallery.Id), bytes.Length, WallPackageJson.Hash(bytes.Span), "gallery"));
            }
            var media = WallPackageJson.AllMedia(content, snapshot.Galleries.Values.Select(gallery => gallery.Manifest))
                .DistinctBy(reference => reference.Sha256, StringComparer.Ordinal).OrderBy(reference => reference.Sha256, StringComparer.Ordinal).ToArray();
            foreach (var reference in media)
                files.Add(new(WallPackageJson.MediaPath(reference), reference.Length, reference.Sha256, "media"));
            var fingerprint = WallPackageJson.Fingerprint(content, files);
            contentFingerprint = fingerprint;
            var manifest = new WallPackageManifest
            {
                AppVersion = appVersion, CreatedUtc = DateTimeOffset.UtcNow, ContentFingerprint = fingerprint,
                Files = files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(),
            };
            var manifestBytes = WallPackageJson.Serialize(manifest);
            if (manifestBytes.Length > limits.MaximumManifestBytes || files.Count + 1 > limits.MaximumEntries ||
                files.Any(file => file.Length > limits.MaximumFileBytes) ||
                checked(files.Sum(file => file.Length) + manifestBytes.Length) > limits.MaximumExpandedBytes)
                throw new InvalidDataException("Selected wall package exceeds its container budget.");
            temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".tilewall-" + Guid.NewGuid().ToString("N") + ".tmp");
            GuardDestination(temporary, snapshot.DataRoot, protectedSources);
            using (var output = new LocalFileStore().CreateNewStream(temporary))
            {
                temporaryCreated = true;
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    await WriteBytesAsync(zip, "manifest.json", manifestBytes, token).ConfigureAwait(false);
                    await WriteBytesAsync(zip, "wall.json", wallBytes, token).ConfigureAwait(false);
                    foreach (var member in content.Members.Where(member => member.EntryKind is not null))
                        await WriteBytesAsync(zip, WallPackageJson.EntryPath(member), snapshot.Entries[member.Id].Bytes, token).ConfigureAwait(false);
                    foreach (var gallery in content.Galleries)
                        await WriteBytesAsync(zip, WallPackageJson.GalleryPath(gallery.Id), snapshot.Galleries[gallery.Id].ManifestBytes, token).ConfigureAwait(false);
                    foreach (var reference in media)
                    {
                        token.ThrowIfCancellationRequested();
                        using var payload = zip.CreateEntry(WallPackageJson.MediaPath(reference), CompressionLevel.Optimal).Open();
                        await snapshot.Media(reference).CopyToAsync(payload, 64 * 1024, token).ConfigureAwait(false);
                    }
                }
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            GuardDestination(temporary, snapshot.DataRoot, protectedSources);
            var verified = await _reader.ReadPreviewAsync(temporary, limits, token).ConfigureAwait(false);
            if (verified.Status == WallPackageReadStatus.Cancelled) return new(WallPackageWriteStatus.Cancelled);
            if (verified.Status == WallPackageReadStatus.IoFailure)
                return new(WallPackageWriteStatus.IoFailure, Diagnostic: verified.Diagnostic);
            if (verified.Status != WallPackageReadStatus.Valid || verified.Preview!.Manifest.ContentFingerprint != fingerprint)
                return new(WallPackageWriteStatus.InvalidPackage, Diagnostic: verified.Diagnostic ?? "Generated wall file failed complete validation.");
            packageHash = verified.Preview.PackageSha256;
            token.ThrowIfCancellationRequested();
            if (await DestinationHashAsync(temporary, snapshot.DataRoot, protectedSources, token).ConfigureAwait(false) != packageHash)
                throw new IOException("Generated wall file changed after complete validation.");
            if (await DestinationHashAsync(destination, snapshot.DataRoot, protectedSources, token).ConfigureAwait(false) != originalHash)
                throw new IOException("Destination changed while the wall file was generated.");
            token.ThrowIfCancellationRequested();
            GuardDestination(temporary, snapshot.DataRoot, protectedSources);
            GuardDestination(destination, snapshot.DataRoot, protectedSources);
            if (originalHash is not null)
            {
                backup = Path.Combine(Path.GetDirectoryName(destination)!, ".tilewall-preserved-" + Guid.NewGuid().ToString("N") + ".bak");
                GuardDestination(backup, snapshot.DataRoot, protectedSources);
                if (File.Exists(backup)) throw new IOException("Export preservation path is occupied.");
            }
            _beforePublish?.Invoke();
            publishAttempted = true;
            _publish(temporary, destination, backup);
            temporary = null;
            if (backup is not null)
            {
                if (await DestinationHashAsync(backup, snapshot.DataRoot, protectedSources, CancellationToken.None).ConfigureAwait(false) != originalHash)
                    return new(WallPackageWriteStatus.DestinationChanged, Diagnostic: "The replaced destination changed concurrently; its contents were preserved.", PreservedPath: backup);
                GuardDestination(backup, snapshot.DataRoot, protectedSources);
                File.Delete(backup); backup = null;
            }
            // Publication is the commit point. A later cancellation cannot undo it.
            return new(WallPackageWriteStatus.Saved, fingerprint);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !publishAttempted)
        { return new(WallPackageWriteStatus.Cancelled); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException or ObjectDisposedException)
        { return new(WallPackageWriteStatus.InvalidPackage, Diagnostic: ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || ex is OperationCanceledException && publishAttempted)
        {
            if (publishAttempted)
            {
                try
                {
                    if (backup is not null && File.Exists(backup))
                    {
                        if (await DestinationHashAsync(backup, snapshot.DataRoot, protectedSources!, CancellationToken.None).ConfigureAwait(false) != originalHash)
                            return new(WallPackageWriteStatus.DestinationChanged, Diagnostic: ex.Message, PreservedPath: backup);
                    }
                    var actual = await DestinationHashAsync(destination, snapshot.DataRoot, protectedSources!, CancellationToken.None).ConfigureAwait(false);
                    if (actual == packageHash)
                    {
                        if (backup is not null && File.Exists(backup))
                        { GuardDestination(backup, snapshot.DataRoot, protectedSources); File.Delete(backup); backup = null; }
                        return new(WallPackageWriteStatus.Saved, contentFingerprint, "Publication confirmed by reading back the generated package.");
                    }
                    if (actual != originalHash)
                        return new(WallPackageWriteStatus.ResultUnknown, Diagnostic: "Could not confirm publication: the destination is neither the original nor the generated file.", PreservedPath: backup is not null && File.Exists(backup) ? backup : null);
                }
                catch (Exception check) when (check is IOException or UnauthorizedAccessException or ArgumentException)
                { return new(WallPackageWriteStatus.ResultUnknown, Diagnostic: "Could not read back publication: " + check.Message, PreservedPath: backup is not null && File.Exists(backup) ? backup : null); }
            }
            return new(WallPackageWriteStatus.IoFailure, Diagnostic: ex.Message);
        }
        finally
        {
            if (temporary is not null && temporaryCreated)
            {
                try
                {
                    GuardDestination(temporary, snapshot.DataRoot, protectedSources);
                    new LocalFileStore().Delete(temporary);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }
    }

    private static async Task WriteBytesAsync(ZipArchive zip, string path, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private static async Task<string?> DestinationHashAsync(string path, string dataRoot,
        IReadOnlySet<string> protectedSources, CancellationToken token)
    {
        GuardDestination(path, dataRoot, protectedSources);
        var files = new LocalFileStore();
        if (!files.GetPathInfo(path).Exists) return null;
        GuardDestination(path, dataRoot, protectedSources);
        using var stream = files.OpenReadProtected(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private static HashSet<string> ProtectedSourcePaths(WallPackageSnapshot snapshot, IEnumerable<string>? additional)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in snapshot.ProtectedSourcePaths.Concat(additional ?? []).Append(Environment.ProcessPath ?? ""))
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var expanded = Environment.ExpandEnvironmentVariables(candidate);
            if (!Path.IsPathFullyQualified(expanded)) continue;
            paths.Add(LaunchIdentityService.NormalizePath(expanded));
        }
        return paths;
    }

    private static void GuardDestination(string path, string dataRoot, IReadOnlySet<string>? protectedSources)
    {
        var normalized = LaunchIdentityService.NormalizePath(path);
        var root = LaunchIdentityService.NormalizePath(dataRoot);
        if (normalized.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Wall files cannot overwrite application data.");
        if (protectedSources?.Contains(normalized) == true)
            throw new IOException("Wall files cannot overwrite external source or launch-target files.");
        var files = new LocalFileStore();
        var cursor = Path.GetPathRoot(path)!;
        foreach (var part in path[cursor.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or ".." || part.IndexOfAny([':', '\0']) >= 0 || part.EndsWith('.') || part.EndsWith(' '))
                throw new IOException("Unsafe wall-file destination component.");
            cursor = Path.Combine(cursor, part);
            var info = files.GetPathInfo(cursor);
            if (info.IsReparsePoint || info.Exists && cursor != path && !info.IsDirectory || cursor == path && info.IsDirectory)
                throw new IOException("Unsafe wall-file destination path.");
        }
    }
}
