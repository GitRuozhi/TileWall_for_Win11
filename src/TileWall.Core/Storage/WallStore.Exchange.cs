using TileWall.Core.Entries;
using TileWall.Core.Exchange;
using TileWall.Core.Wall;
using System.Security.Cryptography;

namespace TileWall.Core.Storage;

public sealed partial class WallStore
{
    public IReadOnlyList<ManagedMediaReference> ReusablePackageMedia(WallDocument current)
    {
        if (!CurrentMatchesDisk(current, current.Generation, out _)) throw new IOException("Committed wall changed before original reuse observation.");
        CheckMediaReferences(current);
        var references = MediaById(current).Values.Concat(GalleryReferences(current).SelectMany(reference => CheckGallery(reference).Files.Select(file => file.Media)));
        var result = new List<ManagedMediaReference>();
        foreach (var group in references.GroupBy(reference => reference.Sha256, StringComparer.Ordinal))
        {
            if (group.Select(reference => reference.Length).Distinct().Count() != 1) throw new InvalidDataException("Conflicting length for a reusable original.");
            result.Add(group.First());
        }
        if (!CurrentMatchesDisk(current, current.Generation, out _)) throw new IOException("Committed wall changed during original reuse observation.");
        return result.AsReadOnly();
    }

    /// <summary>Capture on the existing storage queue; compression runs after its reservation ends.</summary>
    public WallPackageSnapshot CapturePackageSnapshot(WallDocument current, long expectedGeneration,
        IEnumerable<string> selectedMemberIds, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!CurrentMatchesDisk(current, expectedGeneration, out _) || Validate(current).Count != 0)
            throw new IOException("Committed wall changed or is unavailable for export.");
        var content = WallPackageContent.FromSelection(current, selectedMemberIds);
        WallPackageJson.ValidateContent(content);
        var limits = new WallPackageLimits();
        var selected = content.Members.Select(member => member.Id).ToHashSet(StringComparer.Ordinal);
        var members = current.Lanes.SelectMany(lane => lane.Groups).SelectMany(group => group.Members)
            .Where(member => selected.Contains(member.Id)).ToArray();
        var references = members.Select(member => member.Images.Media).OfType<ManagedMediaReference>().Distinct().ToArray();
        var galleryReferences = members.Select(member => member.Images.ManagedGallery).OfType<ManagedGalleryReference>().Distinct().ToArray();
        var resourceLease = AcquireResourceLeases(references.Select(reference => "media:" + reference.Sha256)
            .Concat(galleryReferences.Select(reference => "source:" + reference.ContentId)));
        long payloadBytes = WallPackageJson.Serialize(content).LongLength;
        var entries = new Dictionary<string, WallPackageSnapshotEntry>(StringComparer.Ordinal);
        var media = new Dictionary<string, Stream>(StringComparer.Ordinal);
        var mediaLengths = new Dictionary<string, long>(StringComparer.Ordinal);
        var galleries = new Dictionary<string, WallPackageSnapshotGallery>(StringComparer.Ordinal);
        var additionalHandles = new List<Stream>();
        var protectedPaths = current.EnumerateAllMembers().SelectMany(member => new[] { member.TargetInput, member.Images.Path })
            .Append(current.Settings.BackgroundImagePath).OfType<string>().ToList();
        try
        {
            if (payloadBytes > limits.MaximumContentBytes) throw new InvalidDataException("Export content exceeds its budget.");
            void CaptureOriginal(ManagedMediaReference reference, string path)
            {
                token.ThrowIfCancellationRequested();
                var stream = _files.OpenReadProtected(path);
                additionalHandles.Add(stream);
                if (stream.Length != reference.Length || Convert.ToHexString(SHA256.HashData(stream)) != reference.Sha256)
                    throw new IOException("Selected formal original is missing or changed.");
                stream.Position = 0;
                if (mediaLengths.TryGetValue(reference.Sha256, out var length))
                {
                    if (length != reference.Length) throw new InvalidDataException("Conflicting length for an original digest.");
                }
                else
                {
                    media.Add(reference.Sha256, stream); mediaLengths.Add(reference.Sha256, reference.Length);
                    additionalHandles.RemoveAt(additionalHandles.Count - 1); // Snapshot owns this unique payload handle.
                    payloadBytes = checked(payloadBytes + reference.Length);
                }
                protectedPaths.Add(path);
                if (payloadBytes > limits.MaximumExpandedBytes) throw new InvalidDataException("Export selection exceeds its expanded-byte budget.");
            }
            foreach (var member in members)
            {
                token.ThrowIfCancellationRequested();
                if (member.Entry is not { } entry) continue;
                var path = _paths.Entry(entry);
                using var source = _files.OpenReadProtected(path);
                if (source.Length is < 1 or > EntryObservation.MaximumBytes) throw new InvalidDataException("Shortcut exceeds its byte budget.");
                payloadBytes = checked(payloadBytes + source.Length);
                if (payloadBytes > limits.MaximumExpandedBytes) throw new InvalidDataException("Export selection exceeds its expanded-byte budget.");
                var bytes = new byte[(int)source.Length]; source.ReadExactly(bytes);
                var identity = EntryObservation.Observe(_identities, entry.Kind, path, bytes);
                entries.Add(member.Id, new(bytes, identity)); protectedPaths.Add(identity.Target);
            }
            foreach (var group in content.Groups)
            {
                var identities = group.MemberIds.Where(entries.ContainsKey).Select(id => entries[id].Identity.Key).ToArray();
                if (identities.Distinct(StringComparer.Ordinal).Count() != identities.Length)
                    throw new InvalidDataException("Duplicate launch identities in one selected group.");
            }
            foreach (var reference in references) CaptureOriginal(reference, _paths.Media(reference));
            foreach (var reference in galleryReferences)
            {
                token.ThrowIfCancellationRequested();
                var directory = _paths.Gallery(reference);
                _paths.GuardTree(directory);
                var manifestPath = Path.Combine(directory, "manifest.json");
                var stream = _files.OpenReadProtected(manifestPath); additionalHandles.Add(stream);
                if (stream.Length is < 1 or > 1024 * 1024) throw new InvalidDataException("Invalid gallery manifest size.");
                var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); stream.Position = 0;
                var gallery = new WallPackageSnapshotGallery(reference, bytes);
                var expectedNames = gallery.Manifest.Files.Select(file => file.RelativePath).Append("manifest.json").Order(StringComparer.Ordinal);
                if (_files.ListDirectories(directory).Count != 0 || !_files.ListFiles(directory).Select(Path.GetFileName)
                        .Order(StringComparer.Ordinal).SequenceEqual(expectedNames)) throw new InvalidDataException("Selected gallery is incomplete or contains extra files.");
                galleries.Add(reference.ContentId, gallery);
                payloadBytes = checked(payloadBytes + bytes.LongLength);
                foreach (var file in gallery.Manifest.Files) CaptureOriginal(file.Media, Path.Combine(directory, file.RelativePath));
            }
            if (entries.Count + media.Count + galleries.Count + 2 > limits.MaximumEntries)
                throw new InvalidDataException("Export selection exceeds its entry-count budget.");
            token.ThrowIfCancellationRequested();
            if (!CurrentMatchesDisk(current, expectedGeneration, out _)) throw new IOException("Committed wall changed during export capture.");
            var snapshot = new WallPackageSnapshot(_paths.Root, expectedGeneration, content, entries, media, protectedPaths, galleries);
            snapshot.AttachResourceLease(new ResourceLease(() =>
            {
                try { foreach (var handle in additionalHandles) handle.Dispose(); }
                finally { resourceLease.Dispose(); }
            }));
            return snapshot;
        }
        catch
        {
            foreach (var stream in media.Values) stream.Dispose();
            foreach (var stream in additionalHandles) stream.Dispose();
            resourceLease.Dispose();
            throw;
        }
    }

    /// <summary>Run on the existing STA storage executor; only bounded temporary bytes reach COM.</summary>
    public LaunchIdentity ObservePackageEntry(ManagedEntryKind kind, ReadOnlyMemory<byte> originalBytes,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(kind) || originalBytes.Length is < 1 or > EntryObservation.MaximumBytes)
            throw new InvalidDataException("Invalid or oversized package shortcut.");
        var bytes = originalBytes.ToArray();
        var directory = _paths.Under("Cache", "Exchange");
        var temporary = _paths.Under("Cache", "Exchange", Guid.NewGuid().ToString("N") +
            (kind == ManagedEntryKind.Lnk ? ".lnk" : ".url"));
        if (kind == ManagedEntryKind.Url)
            return EntryObservation.Observe(_identities, kind, temporary, bytes);
        try
        {
            _files.CreateDirectory(directory);
            _files.WriteAllBytes(temporary, bytes);
            token.ThrowIfCancellationRequested();
            var fields = _links.Read(temporary);
            token.ThrowIfCancellationRequested();
            // The parser saw a protected file containing exactly the observed bytes.
            if (Hash(_files.ReadAllBytes(temporary)) != Hash(bytes)) throw new IOException("Temporary shortcut changed.");
            return EntryObservation.Observe(_identities, kind, temporary, bytes, fields);
        }
        finally { _files.Delete(temporary); }
    }
}
