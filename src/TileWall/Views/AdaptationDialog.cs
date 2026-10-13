using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TileWall.Core.Entries;
using TileWall.Core.Storage;
using TileWall.Core.Grid;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Resources;

namespace TileWall.Views;

/// <summary>A list of draft objects, never a second scrollable wall. Only final confirmation commits.</summary>
internal sealed class AdaptationDialog : IWallDialogSession
{
    private sealed record Item(string Id, string Label) { public override string ToString() => Label; }
    private sealed record Destination(string? GroupId, int Lane, string Label) { public override string ToString() => Label; }
    private readonly WallDocument _original;
    private WallDocument _draft;
    private readonly WallMetrics _metrics;
    private readonly double _draftWidth;
    private readonly double _draftHeight;
    private readonly Func<WallDocument, IReadOnlyList<EntryChange>, string?> _save;
    private readonly ContentDialog _dialog;
    private readonly DialogExitPrompt _exit;
    private readonly ComboBox _members = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _destination = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox _column = Number(0, 7);
    private readonly NumberBox _row = Number(0, 255);
    private readonly NumberBox _width = Number(1, 8);
    private readonly NumberBox _height = Number(1, 256);
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _apply = new(), _remove = new(), _reset = new();
    private bool _changed;
    private bool _saved;
    private bool _refreshing;
    private bool _fieldsDirty;
    private bool _allowDiscard;
    private bool _discarded;

    public AdaptationDialog(Window owner, WallDocument original, double workWidth, double workHeight,
        WallMetrics metrics, Func<WallDocument, IReadOnlyList<EntryChange>, string?> save)
    {
        _original = original;
        _draft = original;
        _metrics = metrics;
        _draftWidth = Math.Max(workWidth, metrics.WallWidth(original.Columns));
        _draftHeight = Math.Max(workHeight, metrics.WallHeight(original.Rows));
        _save = save;
        var body = new StackPanel { Spacing = 10, MinWidth = 420 };
        body.Children.Add(_hint);
        body.Children.Add(_members);
        body.Children.Add(_destination);
        var position = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        position.Children.Add(_column); position.Children.Add(_row);
        position.Children.Add(_width); position.Children.Add(_height);
        body.Children.Add(position);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(_apply); actions.Children.Add(_remove); actions.Children.Add(_reset);
        body.Children.Add(actions); body.Children.Add(_status);
        _dialog = new ContentDialog
        {
            XamlRoot = (owner.Content as FrameworkElement)?.XamlRoot,
            Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            DefaultButton = ContentDialogButton.Primary
        };
        _exit = new DialogExitPrompt(_dialog);
        Id(_dialog, "dialog"); Id(_members, "member"); Id(_destination, "destination");
        Id(_column, "column"); Id(_row, "row"); Id(_width, "width"); Id(_height, "height");
        Id(_apply, "apply"); Id(_remove, "remove"); Id(_reset, "reset"); Id(_status, "status");
        _members.SelectionChanged += (_, e) =>
        {
            if (_refreshing) return;
            if (_fieldsDirty && e.RemovedItems.FirstOrDefault() is Item previous)
            {
                var desired = (_members.SelectedItem as Item)?.Id;
                _refreshing = true; _members.SelectedItem = previous; _refreshing = false;
                if (!ApplySelection()) return;
                _refreshing = true;
                _members.SelectedItem = _members.Items.Cast<Item>().FirstOrDefault(i => i.Id == desired);
                _refreshing = false;
            }
            PopulateSelected();
        };
        foreach (var number in new[] { _column, _row, _width, _height })
            number.ValueChanged += (_, _) => FieldsChanged();
        _destination.SelectionChanged += (_, _) => FieldsChanged();
        _apply.Click += (_, _) => ApplySelection();
        _remove.Click += (_, _) => RemoveSelection();
        _reset.Click += (_, _) =>
        { _draft = _original; _changed = false; ResetDiscardConfirmation(); RefreshMembers(); _status.Text = string.Empty; };
        _dialog.PrimaryButtonClick += (_, e) =>
        {
            if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.SaveAndExit); return; }
            if (TrySaveNow() is not null) e.Cancel = true;
        };
        _dialog.SecondaryButtonClick += (_, e) =>
        { if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.DiscardAndExit); } };
        _dialog.CloseButtonClick += (_, e) =>
        {
            if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.CancelExit); return; }
            if (!HasUnsavedDraft) return;
            if (_allowDiscard) { _discarded = true; return; }
            e.Cancel = true;
            OfferDiscard();
        };
        _dialog.Closing += (_, e) =>
        {
            if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.CancelExit); }
            else if (!_discarded && HasUnsavedDraft) { e.Cancel = true; OfferDiscard(); }
        };
        RefreshStrings();
        RefreshMembers();
    }

    public Task ShowAsync() => ShowCoreAsync();
    private async Task ShowCoreAsync() { await OwnedDialogWindow.ShowAsync(_dialog); }
    public bool HasUnsavedDraft => !_saved && (_changed || _fieldsDirty);
    public Task<ExitDecision> ConfirmExitAsync() => _exit.ConfirmAsync();
    public Task WaitForPendingAsync() => Task.CompletedTask;
    public void RequestDismiss() => _dialog.Hide();
    public void Close() { _discarded = true; _dialog.Hide(); }
    public void DiscardDraft() { _draft = _original; _changed = false; _fieldsDirty = false; _discarded = true; }
    public string? TrySaveNow()
    {
        if (_fieldsDirty && !ApplySelection()) return _status.Text;
        var remaining = _draft.Lanes.SelectMany(l => l.Groups).SelectMany(g => g.Members).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var removals = _original.Lanes.SelectMany(l => l.Groups).SelectMany(g => g.Members)
            .Where(m => m.Entry is not null && !remaining.Contains(m.Id))
            .Select(m => (EntryChange)new EntryChange.Remove(m.Id)).ToArray();
        var error = _save(_draft, removals);
        if (error is null) _saved = true;
        else _status.Text = error;
        return error;
    }

    private void ShowRejection(string stage, WallLayoutResult result)
    {
        AppLog.Warn("Adaptation", $"{stage} rejection={result.Rejection} detail={result.Detail}");
        _status.Text = ProductStrings.WallRejectionReason(result.Rejection);
    }

    public void RefreshStrings()
    {
        if (_exit.IsActive) return;
        _dialog.Title = ProductStrings.Get("Wall.AdaptTitle");
        _dialog.PrimaryButtonText = ProductStrings.Get("Wall.AdaptConfirm");
        _dialog.CloseButtonText = ProductStrings.Get(_allowDiscard ? "Common.Discard" : "Common.Cancel");
        _hint.Text = ProductStrings.Get("Wall.AdaptHint");
        _members.Header = ProductStrings.Get("Wall.AdaptMember");
        _destination.Header = ProductStrings.Get("Wall.AdaptDestination");
        _column.Header = ProductStrings.Get("Wall.AdaptColumn"); _row.Header = ProductStrings.Get("Wall.AdaptRow");
        _width.Header = ProductStrings.Get("Wall.AdaptWidth"); _height.Header = ProductStrings.Get("Wall.AdaptHeight");
        _apply.Content = ProductStrings.Get("Wall.AdaptApply");
        _remove.Content = ProductStrings.Get("Wall.AdaptRemove");
        _reset.Content = ProductStrings.Get("Wall.AdaptReset");
    }

    private void FieldsChanged()
    {
        if (_refreshing) return;
        _fieldsDirty = true;
        ResetDiscardConfirmation();
    }

    private void ResetDiscardConfirmation()
    {
        _allowDiscard = false;
        _discarded = false;
        RefreshStrings();
    }

    private void OfferDiscard()
    {
        _allowDiscard = true;
        _status.Text = ProductStrings.Get("Common.UnsavedDraft");
        RefreshStrings();
    }

    private static void Id(DependencyObject item, string id) =>
        AutomationProperties.SetAutomationId(item, "adaptation-" + id);

    private static NumberBox Number(int minimum, int maximum) => new()
    { Minimum = minimum, Maximum = maximum, Value = minimum, SmallChange = 1, Width = 92,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };

    private void RefreshMembers(string? selected = null)
    {
        _refreshing = true;
        _members.Items.Clear();
        for (var lane = 0; lane < _draft.Lanes.Count; lane++)
        foreach (var group in _draft.Lanes[lane].Groups)
        foreach (var member in group.Members)
            _members.Items.Add(new Item(member.Id, $"{lane + 1} / {GroupLabel(group, lane)} / {MemberLabel(member)} ({member.LocalBounds.Width}×{member.LocalBounds.Height})"));
        _members.SelectedIndex = Math.Max(0, _members.Items.Cast<Item>().ToList().FindIndex(i => i.Id == selected));
        _refreshing = false;
        PopulateSelected();
    }

    private WallMember? Selected => _members.SelectedItem is Item item
        ? _draft.Lanes.SelectMany(l => l.Groups).SelectMany(g => g.Members).FirstOrDefault(m => m.Id == item.Id) : null;

    private void PopulateSelected()
    {
        if (_refreshing) return;
        _refreshing = true;
        var member = Selected;
        _apply.IsEnabled = _remove.IsEnabled = member is not null;
        _destination.Items.Clear();
        if (member is null) { _refreshing = false; _fieldsDirty = false; return; }
        for (var lane = 0; lane < _draft.Lanes.Count; lane++)
        {
            foreach (var group in _draft.Lanes[lane].Groups)
            {
                var option = new Destination(group.Id, lane, $"{lane + 1} / {GroupLabel(group, lane)}");
                _destination.Items.Add(option);
                if (group.Members.Any(m => m.Id == member.Id)) _destination.SelectedItem = option;
            }
            _destination.Items.Add(new Destination(null, lane,
                string.Format(ProductStrings.Get("Wall.AdaptNewGroup"), lane + 1)));
        }
        _width.Minimum = _height.Minimum = member is PuzzleMember ? 2 : 1;
        _height.Maximum = member is PuzzleMember ? 25 : 256;
        _column.Value = member.LocalBounds.Column; _row.Value = member.LocalBounds.Row;
        _width.Value = member.LocalBounds.Width; _height.Value = member.LocalBounds.Height;
        _refreshing = false;
        _fieldsDirty = false;
    }

    private bool ApplySelection()
    {
        if (Selected is not { } member || _destination.SelectedItem is not Destination target) return true;
        if (new[] { _column.Value, _row.Value, _width.Value, _height.Value }.Any(v => !double.IsFinite(v) || v != Math.Truncate(v)))
        { _status.Text = ProductStrings.Get("Wall.AdaptIntegers"); return false; }
        var width = (int)_width.Value; var height = (int)_height.Value;
        if (width < _width.Minimum || width > 8 || height < _height.Minimum || height > _height.Maximum ||
            _column.Value < 0 || _column.Value > 7 || _row.Value < 0 || _row.Value > 255)
        { _status.Text = ProductStrings.Get("Wall.AdaptIntegers"); return false; }
        WallMember resized = member;
        var bounds = new GridRect((int)_column.Value, (int)_row.Value, width, height);
        if (member is PuzzleMember puzzle)
        {
            var layout = new PartitionEditSession(puzzle.LocalBounds.Size, puzzle.Partitions);
            while (layout.Current.Size.Columns < width) if (!layout.AddColumn()) return false;
            while (layout.Current.Size.Columns > width) if (!layout.RemoveColumn()) return false;
            while (layout.Current.Size.Rows < height) if (!layout.AddRow()) return false;
            while (layout.Current.Size.Rows > height) if (!layout.RemoveRow()) return false;
            resized = puzzle with { LocalBounds = bounds, Partitions = layout.Current.Partitions };
        }
        else if (member is TileMember tile) resized = tile with { LocalBounds = bounds };
        else if (member is ClockMember clock) resized = clock with { LocalBounds = bounds };
        var source = _draft.Lanes.SelectMany((lane, laneIndex) => lane.Groups.Select((group, groupIndex) =>
            (Group: group, Lane: laneIndex, Index: groupIndex))).First(g => g.Group.Members.Any(m => m.Id == member.Id));
        // Pure draft removal followed by final placement avoids testing the new size at the old location.
        // The same member and entry IDs survive into the final document; no entry removal is committed.
        var removed = WallLayoutEngine.TryApply(_draft,
            new RemoveMemberAction(_draft.Generation, member.Id), _draftWidth, _draftHeight, _metrics);
        if (removed.Proposal is null) { ShowRejection("RemoveBeforeApply", removed); return false; }
        var intermediate = removed.Proposal.Document;
        var recreateSource = target.GroupId == source.Group.Id && source.Group.Members.Count == 1;
        var newGroupId = recreateSource ? source.Group.Id : target.GroupId is null ? $"group-{Guid.NewGuid():N}" : null;
        var movement = WallLayoutEngine.TryApply(intermediate, new AddMemberAction(intermediate.Generation,
            resized, recreateSource ? null : target.GroupId, target.Lane,
            recreateSource ? source.Index : intermediate.Lanes[target.Lane].Groups.Count, newGroupId),
            _draftWidth, _draftHeight, _metrics);
        if (movement.Proposal is null) { ShowRejection("PlaceAfterRemove", movement); return false; }
        _draft = movement.Proposal.Document;
        if (target.GroupId == source.Group.Id) _draft = _draft with
        {
            Lanes = _draft.Lanes.Select(lane => lane with
            {
                Groups = lane.Groups.Select(group => group.Id == source.Group.Id
                    ? group with { Title = source.Group.Title,
                        Members = source.Group.Members.Select(old => group.Members.First(current => current.Id == old.Id)).ToArray() }
                    : group).ToArray()
            }).ToArray()
        };
        _changed = true;
        ResetDiscardConfirmation();
        _status.Text = ProductStrings.Get("Wall.AdaptDraftChanged");
        RefreshMembers(member.Id);
        return true;
    }

    private string GroupLabel(StartGroup group, int lane) => !string.IsNullOrWhiteSpace(group.Title)
        ? group.Title : $"{ProductStrings.Get("Wall.UntitledGroup")} {_draft.Lanes[lane].Groups.ToList().FindIndex(g => g.Id == group.Id) + 1}";
    private string MemberLabel(WallMember member) => !string.IsNullOrWhiteSpace(member.Visual.Title)
        ? member.Visual.Title : $"{ProductStrings.Get(member switch { TileMember => "Wall.Tile", PuzzleMember => "Wall.Puzzle", _ => "Wall.Clock" })} {_members.Items.Count + 1}";

    private void RemoveSelection()
    {
        if (Selected is not { } member) return;
        var result = WallLayoutEngine.TryApply(_draft, new RemoveMemberAction(_draft.Generation, member.Id),
            _draftWidth, _draftHeight, _metrics);
        if (result.Proposal is null) { ShowRejection("RemoveSelection", result); return; }
        _draft = result.Proposal.Document;
        _changed = true;
        ResetDiscardConfirmation();
        _status.Text = ProductStrings.Get("Wall.AdaptDraftChanged");
        RefreshMembers();
    }
}
