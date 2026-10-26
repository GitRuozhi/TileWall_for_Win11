using TileWall.Core.Storage;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Views;

namespace TileWall;

public sealed partial class MainWindow
{
    private Action<MemberImageSource, PuzzleCandidateResult>? _editorGalleryHandler;
    private SharedMemberEditorDialog? _galleryEditor;

    private static IReadOnlyList<PreparedMediaAsset> AcceptedMediaAssets(WallDocument document,
        IReadOnlyList<PreparedMediaAsset> local, IReadOnlyList<PreparedMediaAsset>? imported)
    {
        var references = document.EnumerateAllMembers().Select(member => member.Images.Media)
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

    private async Task<(WallProposal Proposal, IReadOnlyList<PreparedMediaAsset> Assets)> PrepareFormalMediaAsync(WallProposal proposal)
    {
        var document = proposal.Document;
        var pending = document.Lanes.SelectMany(lane => lane.Groups).SelectMany(group => group.Members)
            .Where(member => member.Images.Kind == MemberImageSourceKind.Single && member.Images.Media is null).ToArray();
        var background = document.Settings.BackgroundMode == WallBackgroundMode.Custom && document.Settings.BackgroundMedia is null;
        if (pending.Length == 0 && !background) return (proposal, []);
        var assets = new Dictionary<string, PreparedMediaAsset>(StringComparer.Ordinal);
        var images = new Dictionary<string, MemberImageSource>(StringComparer.Ordinal);
        foreach (var member in pending)
        {
            var asset = await _images.PrepareMediaAsync(member.Images.Path ?? throw new IOException("Missing image source."));
            assets.TryAdd(asset.Reference.Sha256, asset);
            images.Add(member.Id, member.Images with { Media = asset.Reference });
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
