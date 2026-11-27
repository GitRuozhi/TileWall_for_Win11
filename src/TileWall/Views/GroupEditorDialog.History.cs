using Microsoft.UI.Xaml.Automation;
using TileWall.Core.Wall;

namespace TileWall.Views;

internal sealed partial class GroupEditorDialog
{
    private const int MaximumHistory = 100;
    private sealed record DraftChange(GroupEditorDraft Before, GroupEditorDraft After);

    private readonly List<DraftChange[]> _undo = [], _redo = [];

    private void EditDrafts(Action edit)
    {
        var before = _drafts.Values.ToArray();
        edit();
        RememberEdit(before);
    }

    private void RememberEdit(IEnumerable<GroupEditorDraft> before)
    {
        var changes = before.Select(draft => new DraftChange(draft, _drafts[draft.Member.Id]))
            .Where(change => change.Before.Member != change.After.Member ||
                !change.Before.EntryChanges.SequenceEqual(change.After.EntryChanges) ||
                !change.Before.Assets.SequenceEqual(change.After.Assets) ||
                change.Before.ExpectedEntryHash != change.After.ExpectedEntryHash)
            .ToArray();
        if (changes.Length == 0) return;
        _undo.Add(changes);
        if (_undo.Count > MaximumHistory) _undo.RemoveAt(0);
        _redo.Clear();
        RefreshEditTools();
    }

    private void RestoreHistory(bool redo)
    {
        var from = redo ? _redo : _undo;
        if (from.Count == 0) return;
        var changes = from[^1];
        from.RemoveAt(from.Count - 1);
        (redo ? _undo : _redo).Add(changes);
        foreach (var change in changes)
        {
            var draft = redo ? change.After : change.Before;
            var current = _drafts[draft.Member.Id];
            if (current.Frame is not null && current.Member.Images == draft.Member.Images &&
                current.Member.TargetInput == draft.Member.TargetInput)
                draft = draft with { Frame = current.Frame };
            _drafts[draft.Member.Id] = draft;
            if (draft.Frame is null && draft.Member.Images.Kind == MemberImageSourceKind.Automatic)
                _ = RefreshAutomaticFrameAsync(draft);
        }
        _status.Clear();
        RefreshPreview();
    }

    private void RefreshEditTools()
    {
        _selectionCount.Text = $"{_selected.Count}/{_drafts.Count}";
        AutomationProperties.SetName(_selectionCount,
            string.Format(T("Batch.SelectionCount"), _drafts.Count, _selected.Count));
        _undoButton.IsEnabled = _undo.Count != 0 || _details.Values.Any(detail => detail.HasUnsavedDraft);
        _redoButton.IsEnabled = _redo.Count != 0;
        _selectAllButton.IsEnabled = _selected.Count < _drafts.Count;
        _selectNoneButton.IsEnabled = _selected.Count != 0;
    }
}
