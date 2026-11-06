using TileWall.Core.Storage;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Resources;
using TileWall.Views;

namespace TileWall;

public sealed partial class MainWindow
{
    private Action<MemberImageSource, PuzzleCandidateResult>? _editorGalleryHandler;
    private SharedMemberEditorDialog? _galleryEditor;

    private static IReadOnlyList<PreparedMediaAsset> AcceptedMediaAssets(WallDocument document,
        IReadOnlyList<PreparedMediaAsset> local, IReadOnlyList<PreparedMediaAsset>? imported)
    {
        var references = document.EnumerateAllMembers().SelectMany(member =>
                (member.Images.IconSeries?.Frames.Select(frame => frame.Media) ?? [])
                .Append(member.Images.Media).OfType<ManagedMediaReference>())
            .Append(document.Settings.BackgroundMedia).OfType<ManagedMediaReference>()
            .GroupBy(reference => reference.Sha256, StringComparer.Ordinal).ToDictionary(group => group.Key, group =>
            {
                var first = group.First();
                if (group.Any(reference => reference != first)) throw new InvalidDataException("Conflicting original image references.");
                return first;
            }, StringComparer.Ordinal);
        var assets = new Dictionary<string, PreparedMediaAsset>(StringComparer.Ordinal);
        foreach (var asset in local.Concat(imported ?? []))
        {
            if (!references.TryGetValue(asset.Reference.Sha256, out var required)) continue;
            if (required != asset.Reference) throw new InvalidDataException("Prepared original image differs from its reference.");
            assets.TryAdd(asset.Reference.Sha256, asset);
        }
        return assets.Values.ToArray();
    }

    private async Task<(WallProposal Proposal, IReadOnlyList<PreparedMediaAsset> Assets)> PrepareFormalMediaAsync(
        WallProposal proposal, IReadOnlyList<EntryChange>? changes, double scale)
    {
        var document = proposal.Document;
        var pending = document.Lanes.SelectMany(lane => lane.Groups).SelectMany(group => group.Members)
            .Where(member => member.Images.Kind == MemberImageSourceKind.Single && member.Images.Media is null).ToArray();
        var background = document.Settings.BackgroundMode == WallBackgroundMode.Custom && document.Settings.BackgroundMedia is null;
        var assets = new Dictionary<string, PreparedMediaAsset>(StringComparer.Ordinal);
        var images = new Dictionary<string, MemberImageSource>(StringComparer.Ordinal);
        foreach (var member in pending)
        {
            var asset = await _images.PrepareMediaAsync(member.Images.Path ?? throw new IOException("Missing image source."));
            assets.TryAdd(asset.Reference.Sha256, asset);
            images.Add(member.Id, member.Images with { Media = asset.Reference });
        }
        var previous = Document.EnumerateAllMembers().ToDictionary(member => member.Id, StringComparer.Ordinal);
        foreach (var member in document.EnumerateAllMembers().Where(member =>
                     member is TileMember or PuzzleMember && member.Images.Kind == MemberImageSourceKind.Automatic))
        {
            var width = Math.Max(1, (int)Math.Ceiling(_metrics.WidthOf(member.LocalBounds.Width) * scale));
            var height = Math.Max(1, (int)Math.Ceiling(_metrics.HeightOf(member.LocalBounds.Height) * scale));
            if (member.Images.IconSeries is { } series)
            {
                images[member.Id] = member.Images with { IconSeries = AutomaticIconSeriesRules.SelectForSize(series,
                    member.LocalBounds.Width, member.LocalBounds.Height, width, height) };
                continue;
            }
            // Existing unbound members are captured when their first current frame
            // is prepared. Unrelated saves must not repeatedly reread broken tiles.
            if (member.ResourceOrigin != MemberResourceOrigin.Local || previous.TryGetValue(member.Id, out var old) &&
                old.Images == member.Images && old.TargetInput == member.TargetInput && old.Entry == member.Entry &&
                old.SourceShortcutPath == member.SourceShortcutPath) continue;
            var entry = changes?.OfType<EntryChange.CopyShortcut>().FirstOrDefault(change => change.MemberId == member.Id)?.SourcePath
                ?? (changes?.Any(change => change.MemberId == member.Id && change is EntryChange.CreateLink or EntryChange.CreateShellLink or EntryChange.CreateUrl or EntryChange.ChangeTarget) == true
                    ? member.TargetInput : _session.ManagedEntryPath(member) ?? member.SourceShortcutPath ?? member.TargetInput);
            try
            {
                if (await _images.PrepareAutomaticIconSeriesAsync(member, entry, width, height) is not { } prepared) continue;
                images[member.Id] = member.Images with { IconSeries = prepared.Series };
                foreach (var asset in prepared.Assets) assets.TryAdd(asset.Reference.Sha256, asset);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
            { AppLog.Warn("AutomaticIconCapture", $"member={member.Id} {ex.Message}"); }
        }
        WallMember Map(WallMember member) => images.TryGetValue(member.Id, out var source) ? member with { Images = source } : member;
        var settings = document.Settings;
        if (background)
        {
            var asset = await _images.PrepareMediaAsync(settings.BackgroundImagePath ?? throw new IOException("Missing background source."));
            assets.TryAdd(asset.Reference.Sha256, asset);
            settings = settings with { BackgroundMedia = asset.Reference };
        }
        return (proposal with { Document = document with { Settings = settings,
            Lanes = document.Lanes.Select(lane => lane with { Groups = lane.Groups.Select(group => group with
                { Members = group.Members.Select(Map).ToArray() }).ToArray() }).ToArray() } }, assets.Values.ToArray());
    }

    private async Task<string?> PersistAutomaticIconSeries(WallMember requested, PreparedAutomaticIconSeries prepared,
        Func<bool> stillCurrent)
    {
        await DrainPersistenceAsync();
        if (!stillCurrent() || FindMember(requested.Id) is not { } current || current.Images != requested.Images ||
            current.TargetInput != requested.TargetInput || current.Entry != requested.Entry ||
            current.SourceShortcutPath != requested.SourceShortcutPath || current.ResourceOrigin != MemberResourceOrigin.Local)
            return null;
        var replacement = current with { Images = current.Images with { IconSeries = prepared.Series } };
        var result = _session.Propose(new ReplaceMemberAction(Document.Generation, current.Id, replacement),
            WorkWidthDip, WorkHeightDip, _metrics);
        if (result.Proposal is null) return ProductStrings.Get("Wall.PuzzleMissing");
        return await CommitProposalAsync(result.Proposal, preparedMedia: prepared.Assets);
    }

    private void AttachGalleryEditor(SharedMemberEditorDialog dialog)
    {
        DetachGalleryEditor();
        _galleryEditor = dialog;
        dialog.GallerySourceChanged += RefreshEditorGallerySubscription;
        RefreshEditorGallerySubscription();
    }

    private void RefreshEditorGallerySubscription()
    {
        if (_editorGalleryHandler is { } previous) _puzzles.GalleryCandidatesChanged -= previous;
        _editorGalleryHandler = null;
        if (_galleryEditor is not { } dialog) return;
        var revision = dialog.GallerySourceRevision;
        if (dialog.GallerySource is { } source)
        {
            _editorGalleryHandler = (updated, snapshot) =>
            {
                if (ReferenceEquals(_galleryEditor, dialog)) _ = dialog.RefreshGalleryCandidatesAsync(updated, snapshot, revision);
            };
            _puzzles.GalleryCandidatesChanged += _editorGalleryHandler;
            _puzzles.SetEditorGallerySource(source);
        }
        else _puzzles.SetEditorGallerySource(null);
    }

    private void DetachGalleryEditor()
    {
        if (_galleryEditor is { } editor) editor.GallerySourceChanged -= RefreshEditorGallerySubscription;
        if (_editorGalleryHandler is { } handler) _puzzles.GalleryCandidatesChanged -= handler;
        _galleryEditor = null; _editorGalleryHandler = null;
        _puzzles.SetEditorGallerySource(null);
    }
}
