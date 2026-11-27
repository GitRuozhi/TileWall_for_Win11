using TileWall.Core.Entries;
using TileWall.Core.Import;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Resources;
using TileWall.Views;

namespace TileWall;

public sealed partial class MainWindow
{
    private StartGroup? FindEditedGroup(string id) =>
        Document.Lanes.SelectMany(lane => lane.Groups).FirstOrDefault(group => group.Id == id);

    private async Task EditGroupAsync(string id)
    {
        if (_modal || _viewBlocked || !_hasDocument || PersistenceInteractionBlocked) return;
        try
        {
            if (!await BeginModalAsync()) return;
            if (FindEditedGroup(id) is not { Members.Count: > 0 } group) return;
            var generation = Document.Generation;
            var dialog = new GroupEditorDialog(DialogOwner, group, Settings, _metrics, _puzzles.FrameFor,
                CreateGroupDetail, LoadGroupAutomaticFrameAsync,
                (drafts, sync) => SaveGroupAsync(id, generation, drafts, sync));
            ActiveDialog = dialog;
            await dialog.ShowAsync();
        }
        catch (Exception ex) { Status(string.Format(ProductStrings.Get("Wall.EditFailed"), ex.Message)); }
        finally { EndModal(); }
    }

    private Task<PuzzleFrameResult> LoadGroupAutomaticFrameAsync(GroupEditorDraft draft, int width, int height, CancellationToken token)
    {
        var source = draft.EntryChanges.OfType<EntryChange.CopyShortcut>().FirstOrDefault()?.SourcePath ??
            (draft.EntryChanges.Any(IsGroupTargetChange) ? draft.Member.TargetInput :
                _session.ManagedEntryPath(draft.Member) ?? draft.Member.TargetInput);
        return _images.LoadMemberFrameAsync(draft.Member, source, width, height, ct: token);
    }

    private SharedMemberEditorDialog CreateGroupDetail(GroupEditorDraft draft, string field, Action<GroupEditorDraft> accept)
    {
        var member = draft.Member;
        var pendingTarget = draft.EntryChanges.Any(IsGroupTargetChange);
        var original = FindMember(member.Id) ?? throw new InvalidOperationException(ProductStrings.Get("Wall.PuzzleMissing"));
        (LinkFields? Link, string? Url, string Sha256)? observed = null;
        try { observed = _session.ObserveManagedEntry(member.Id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            System.Runtime.InteropServices.COMException) { }
        var editable = pendingTarget ? member with { Entry = null } : member;
        var asset = draft.Assets.FirstOrDefault(item => item.Reference == member.Images.Media);
        var arguments = draft.EntryChanges.Select(change => change switch
        {
            EntryChange.CreateLink link => link.Arguments,
            EntryChange.ChangeTarget changeTarget => changeTarget.Arguments ?? observed?.Link?.Arguments,
            EntryChange.CopyShortcut copy => copy.Arguments ?? (Path.GetExtension(copy.SourcePath).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                ? _session.LinkFiles.Read(copy.SourcePath).Arguments : null),
            _ => null,
        }).FirstOrDefault(value => value is not null);
        SharedMemberEditorDialog? dialog = null;
        dialog = new SharedMemberEditorDialog(DialogOwner, editable, _images,
            pendingTarget ? null : _session.ManagedEntryPath(original), _session.LinkFiles, MaxMemberRows(),
            (replacement, changes, assets, expectedHash) =>
            {
                var changedTarget = changes.Any(IsGroupTargetChange) ||
                    !string.Equals(replacement.TargetInput, editable.TargetInput, StringComparison.Ordinal);
                var pending = changedTarget
                    ? changes.Where(IsGroupTargetChange).ToArray()
                    : draft.EntryChanges.Where(IsGroupTargetChange).ToArray();
                if (changedTarget && string.IsNullOrEmpty(replacement.TargetInput) && original.Entry is not null)
                    pending = [new EntryChange.Remove(original.Id)];
                else if (changedTarget && draft.EntryChanges.OfType<EntryChange.CopyShortcut>().FirstOrDefault() is { } copy &&
                    replacement.TargetInput == editable.TargetInput && pending.OfType<EntryChange.CreateLink>().FirstOrDefault() is { } link)
                    pending = [copy with { Arguments = link.Arguments }];
                var renamed = original.Visual.Title != replacement.Visual.Title &&
                    replacement.Visual.TitleMode == MemberTitleMode.Manual && !string.IsNullOrEmpty(replacement.Visual.Title);
                if (renamed && original.Entry is not null && pending.All(change => change is EntryChange.ChangeTarget))
                    pending = [..pending, new EntryChange.Rename(original.Id)];
                accept(new(replacement with { Entry = string.IsNullOrEmpty(replacement.TargetInput) ? null : original.Entry },
                    dialog!.DraftFrame, pending, assets, expectedHash ?? (changedTarget ? null : draft.ExpectedEntryHash)));
                return null;
            }, Settings.VisualStyle, initialFrame: draft.Frame, managedEntry: pendingTarget ? null : observed,
            showUnavailableControls: Settings.ShowUnavailableControls, detailField: field,
            initialSourceAsset: asset, originalArguments: arguments);
        return dialog;
    }

    private Task<string?> SaveGroupAsync(string id, long generation, IReadOnlyList<GroupEditorDraft> drafts, bool sync)
    {
        if (Document.Generation != generation || FindEditedGroup(id) is not { } group)
            return Task.FromResult<string?>(ProductStrings.Get("Recycle.Changed"));
        var byId = group.Members.ToDictionary(member => member.Id, StringComparer.Ordinal);
        var changes = drafts.SelectMany(draft => draft.EntryChanges).ToArray();
        if (changes.Length == 0 && drafts.All(draft => byId[draft.Member.Id] == draft.Member))
            return Task.FromResult<string?>(null);
        var hashes = drafts.Where(draft => draft.ExpectedEntryHash is not null)
            .ToDictionary(draft => draft.Member.Id, draft => draft.ExpectedEntryHash!, StringComparer.Ordinal);
        if (changes.Any(IsGroupTargetChange))
        {
            var identities = ImportIdentities;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var draft in drafts)
            {
                var original = byId[draft.Member.Id];
                if (original.Entry is not null && _session.ObserveManagedEntry(original.Id) is { } observed)
                {
                    if (hashes.TryGetValue(original.Id, out var hash) && hash != observed.Sha256)
                        return Task.FromResult<string?>(ProductStrings.Get("Import.SourceChanged"));
                    hashes[original.Id] = observed.Sha256;
                }
                var identity = DesiredEditorIdentity(original, draft.Member, draft.EntryChanges, identities, hashes.GetValueOrDefault(original.Id));
                if (identity is not null && !keys.Add(identity.Key))
                    return Task.FromResult<string?>(ProductStrings.Get("SharedEditor.Duplicate"));
            }
        }
        var result = WallLayoutEngine.ProposeGroupEdit(Document, id,
            drafts.Select(draft => draft.Member).ToArray(), RecoverableViewportBudget(Settings), _metrics);
        if (result.Proposal is not { } proposal)
            return Task.FromResult<string?>(result.Detail == "LAYOUT_LOCKED" ? ProductStrings.Get("Wall.LayoutLocked") : ViewportFailure(result));
        var prepared = drafts.SelectMany(draft => draft.Assets).DistinctBy(asset => asset.Reference.Sha256).ToArray();
        return sync ? Task.FromResult(CommitProposal(proposal, changes, prepared, hashes)) :
            CommitProposalAsync(proposal, changes, preparedMedia: prepared, expectedEntrySha256: hashes);
    }

    private static bool IsGroupTargetChange(EntryChange change) =>
        change is EntryChange.CopyShortcut or EntryChange.CreateLink or EntryChange.CreateUrl or EntryChange.ChangeTarget or EntryChange.Remove;
}
