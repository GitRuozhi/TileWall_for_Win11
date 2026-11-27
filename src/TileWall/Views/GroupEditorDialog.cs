using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Imaging;
using TileWall.Core.Shell;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Resources;
using Windows.Foundation;

namespace TileWall.Views;

internal sealed record GroupEditorDraft(WallMember Member, PuzzleFrame? Frame, IReadOnlyList<EntryChange> EntryChanges,
    IReadOnlyList<PreparedMediaAsset> Assets, string? ExpectedEntryHash);

internal sealed partial class GroupEditorDialog : IWallDialogSession
{
    private const string Target = "Editor.Target", Title = "Editor.Title", Foreground = "Editor.Foreground";
    private const string Background = "Tile.Background", Opacity = "Editor.Opacity", Size = "Editor.TileSize";
    private const string Fit = "Editor.ImageFit", Ratio = "Editor.ImageRatio";
    private static readonly string[] Fields = [Target, Title, Foreground, Background, Opacity, Size, Fit, Ratio];
    private readonly StartGroup _group;
    private readonly WallMetrics _metrics;
    private readonly WallSettings _settings;
    private readonly Dictionary<string, GroupEditorDraft> _drafts;
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly Func<GroupEditorDraft, string, Action<GroupEditorDraft>, SharedMemberEditorDialog> _createDetail;
    private readonly Func<GroupEditorDraft, int, int, CancellationToken, Task<PuzzleFrameResult>> _loadAutomaticFrame;
    private readonly CancellationTokenSource _previewLifetime = new();
    private readonly Func<IReadOnlyList<GroupEditorDraft>, bool, Task<string?>> _save;
    private readonly ContentDialog _dialog;
    private readonly DialogExitPrompt _exit;
    private readonly Canvas _preview = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly TextBlock _selectionCount = new() { Margin = new Thickness(12, 6, 0, 8) };
    private readonly StackPanel _cards = new() { Spacing = 8 };
    private readonly StatusOverlay _status = new();
    private readonly Button _saveButton = new(), _cancelButton = new();
    private readonly Button _undoButton = new(), _redoButton = new(), _selectAllButton = new(), _selectNoneButton = new();
    private readonly TextBlock _toolsCaption = new() { Margin = new Thickness(24, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _tools;
    private readonly Dictionary<string, SharedMemberEditorDialog> _details = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _detailTitles = new(StringComparer.Ordinal);
    private readonly List<ContentControl> _detailHosts = [];
    private readonly List<Action> _refreshOpacityControls = [];
    private readonly List<Func<bool>> _commitInputs = [];
    private bool _closing, _saved, _committing;
    private Task _pending = Task.CompletedTask;
    private string? _expanded;

    internal GroupEditorDialog(Window owner, StartGroup group, WallSettings settings, WallMetrics metrics,
        Func<string, PuzzleFrame?> frame, Func<GroupEditorDraft, string, Action<GroupEditorDraft>, SharedMemberEditorDialog> createDetail,
        Func<GroupEditorDraft, int, int, CancellationToken, Task<PuzzleFrameResult>> loadAutomaticFrame,
        Func<IReadOnlyList<GroupEditorDraft>, bool, Task<string?>> save)
    {
        _group = group;
        _settings = settings;
        _metrics = metrics;
        _drafts = group.Members.ToDictionary(member => member.Id,
            member => new GroupEditorDraft(member, frame(member.Id), [], [], null), StringComparer.Ordinal);
        _createDetail = createDetail;
        _loadAutomaticFrame = loadAutomaticFrame;
        _save = save;
        var previewScroll = new ScrollViewer
        {
            Content = _preview, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto,
        };
        var preview = new Grid();
        preview.RowDefinitions.Add(new() { Height = GridLength.Auto });
        preview.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        preview.Children.Add(_selectionCount);
        Grid.SetRow(previewScroll, 1);
        Grid.SetRow(_status, 1);
        preview.Children.Add(previewScroll);
        preview.Children.Add(_status);
        AutomationProperties.SetAutomationId(_selectionCount, "batch-selection-count");
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tools.Children.Add(_undoButton);
        tools.Children.Add(_redoButton);
        tools.Children.Add(_selectAllButton);
        tools.Children.Add(_selectNoneButton);
        _tools = VisualStyles.Card(HeaderRow(_toolsCaption, tools));
        var budget = OwnedDialogWindow.ContentBudget(owner, previewPage: true);
        var page = VisualStyles.ResizablePreviewPage(preview, _cards, budget, 780, _saveButton, _cancelButton);
        _dialog = new ContentDialog
        {
            XamlRoot = (owner.Content as FrameworkElement)?.XamlRoot, Content = page, DefaultButton = ContentDialogButton.None,
        };
        _dialog.Resources["ContentDialogMaxWidth"] = budget.Width + 48;
        _dialog.Resources["ContentDialogMaxHeight"] = budget.Height + 180;
        OwnedDialogWindow.EnablePageChrome(_dialog, page, inlineToolTips: true);
        OwnedDialogWindow.EnableUserResize(_dialog);
        page.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (_pointer is not null && e.Key == Windows.System.VirtualKey.Escape)
            {
                CancelGesture();
                e.Handled = true;
            }
        }), true);
        _exit = new(_dialog);
        AutomationProperties.SetAutomationId(_dialog, "batch-editor");
        AutomationProperties.SetAutomationId(_preview, "batch-preview");
        AutomationProperties.SetAutomationId(_saveButton, "batch-save");
        AutomationProperties.SetAutomationId(_cancelButton, "batch-cancel");
        _saveButton.Click += (_, _) => StartSave();
        _cancelButton.Click += (_, _) => RequestDismiss();
        _undoButton.Click += (_, _) => RunDetailTransition(() => RestoreHistory(redo: false));
        _redoButton.Click += (_, _) => RunDetailTransition(() => RestoreHistory(redo: true));
        _selectAllButton.Click += (_, _) => SelectAllMembers(true);
        _selectNoneButton.Click += (_, _) => SelectAllMembers(false);
        AutomationProperties.SetAutomationId(_undoButton, "batch-undo");
        AutomationProperties.SetAutomationId(_redoButton, "batch-redo");
        AutomationProperties.SetAutomationId(_selectAllButton, "batch-select-all");
        AutomationProperties.SetAutomationId(_selectNoneButton, "batch-select-none");
        _dialog.PrimaryButtonClick += (_, e) => { e.Cancel = true; _exit.Resolve(ExitDecision.SaveAndExit); };
        _dialog.SecondaryButtonClick += (_, e) => { e.Cancel = true; _exit.Resolve(ExitDecision.DiscardAndExit); };
        _dialog.CloseButtonClick += (_, e) => { e.Cancel = true; _exit.Resolve(ExitDecision.CancelExit); };
        _dialog.Closing += (_, e) =>
        {
            if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.CancelExit); }
            else if (_committing) e.Cancel = true;
            else CancelGesture();
        };
        _dialog.Opened += (_, _) => RefreshPreview();
        AttachGestures();
        RefreshStrings();
    }

    private WallMember[] Members() => MemberLayoutOrder.Order(_drafts.Values.Select(draft => draft.Member)).ToArray();
    private StartGroup DraftGroup() => _group with { Members = Members() };
    private static string T(string key) => ProductStrings.Get(key);

    internal async Task ShowAsync()
    {
        try { await OwnedDialogWindow.ShowAsync(_dialog); }
        finally
        {
            _closing = true;
            _previewLifetime.Cancel();
            _previewLifetime.Dispose();
            await CloseDetailsAsync();
            _status.Clear();
        }
    }

    public bool HasUnsavedDraft => !_saved && (_details.Values.Any(detail => detail.HasUnsavedDraft) ||
        _group.Members.Any(original => _drafts[original.Id].Member != original ||
            _drafts[original.Id].EntryChanges.Count != 0));

    public async Task WaitForPendingAsync()
    {
        OwnedDialogWindow.ReleaseTextFocus(_dialog);
        await _pending;
        await WaitForDetailsAsync();
    }

    public Task<ExitDecision> ConfirmExitAsync() => _exit.ConfirmAsync();
    public void DiscardDraft() => Close();
    public void RequestDismiss() => Close();
    public void Close()
    {
        if (_committing) return;
        _closing = true;
        _dialog.Hide();
    }

    public void RefreshStrings()
    {
        if (_exit.IsActive) return;
        OwnedDialogWindow.SetPageName(_dialog, T("Batch.Edit"));
        ActionIcon.Set(_saveButton, T("Common.Save"), ActionIcon.Save);
        ActionIcon.Set(_cancelButton, T("Common.Cancel"), ActionIcon.Cancel);
        ActionIcon.SetIcon(_undoButton, T("Layout.Undo"), ActionIcon.Undo);
        ActionIcon.SetIcon(_redoButton, T("Layout.Redo"), ActionIcon.Redo);
        ActionIcon.SetIcon(_selectAllButton, T("Import.SelectAll"), ActionIcon.CheckedBox);
        ActionIcon.SetIcon(_selectNoneButton, T("Batch.SelectNone"), ActionIcon.Checkbox, overlayGlyph: ActionIcon.Cancel);
        _toolsCaption.Text = T("Wall.Edit");
        RefreshCards();
    }

    private IReadOnlyList<GroupEditorDraft> Snapshot() => Members().Select(member => _drafts[member.Id]).ToArray();

    public string? TrySaveNow()
    {
        if (!CommitInputs()) return T("Common.CheckInput");
        if (SaveDetails() is { } detailError) return detailError;
        try
        {
            var error = _save(Snapshot(), true).GetAwaiter().GetResult();
            if (error is null) _saved = true;
            else ShowError(error);
            return error;
        }
        catch (Exception ex)
        {
            var error = string.Format(T("Common.SaveFailed"), ex.Message);
            ShowError(error);
            return error;
        }
    }

    private void StartSave()
    {
        if (_committing || _exit.IsActive) return;
        OwnedDialogWindow.ReleaseTextFocus(_dialog);
        if (!CommitInputs()) return;
        _pending = SaveAsync();
    }

    private async Task SaveAsync()
    {
        _committing = true;
        _saveButton.IsEnabled = _cancelButton.IsEnabled = _cards.IsHitTestVisible = _preview.IsHitTestVisible = false;
        try
        {
            await WaitForDetailsAsync();
            if (SaveDetails() is { } detailError) ShowError(detailError);
            else if (await _save(Snapshot(), false) is { } failure) ShowError(failure);
            else _saved = true;
        }
        catch (Exception ex) { ShowError(string.Format(T("Common.SaveFailed"), ex.Message)); }
        finally
        {
            _committing = false;
            _saveButton.IsEnabled = _cancelButton.IsEnabled = _cards.IsHitTestVisible = _preview.IsHitTestVisible = true;
        }
        if (_saved) _dialog.Hide();
    }

    private void ShowError(string message) => _status.Show(message);

    private async Task WaitForDetailsAsync()
    {
        foreach (var detail in _details.Values.ToArray())
            await detail.WaitForPendingAsync();
    }

    private string? SaveDetails()
    {
        foreach (var detail in _details.Values)
            if (detail.HasUnsavedDraft && detail.TrySaveNow() is { } error) return error;
        return null;
    }

    private async Task CloseDetailsAsync()
    {
        var details = _details.Values.ToArray();
        _details.Clear();
        await Task.WhenAll(details.Select(detail => detail.CloseEmbeddedAsync()));
    }

    private void RunDetailTransition(Action apply)
    {
        if (_committing || _exit.IsActive) return;
        OwnedDialogWindow.ReleaseTextFocus(_dialog);
        if (!CommitInputs()) return;
        _pending = SettleDetailsAsync(apply);
    }

    private bool CommitInputs() => _commitInputs.ToArray().All(commit => commit());

    private async Task SettleDetailsAsync(Action apply)
    {
        _committing = true;
        _saveButton.IsEnabled = _cancelButton.IsEnabled = _cards.IsHitTestVisible = _preview.IsHitTestVisible = false;
        try
        {
            await WaitForDetailsAsync();
            if (SaveDetails() is { } error) { ShowError(error); RefreshCards(); return; }
            await CloseDetailsAsync();
            apply();
            RefreshCards();
        }
        catch (Exception ex) { ShowError(string.Format(T("Wall.EditFailed"), ex.Message)); }
        finally
        {
            _committing = false;
            _saveButton.IsEnabled = _cancelButton.IsEnabled = _cards.IsHitTestVisible = _preview.IsHitTestVisible = true;
        }
    }

    private void RefreshCards()
    {
        if (_closing) return;
        foreach (var host in _detailHosts) host.Content = null;
        _detailHosts.Clear();
        _detailTitles.Clear();
        _refreshOpacityControls.Clear();
        _commitInputs.Clear();
        _cards.Children.Clear();
        _cards.Children.Add(_tools);
        RefreshEditTools();
        var ordered = Members();
        var selection = ordered.Where(member => _selected.Contains(member.Id)).ToArray();
        foreach (var field in Fields)
        {
            var applicable = selection.Where(member => Supports(member, field)).Select(member => member.Id).ToArray();
            var values = new StackPanel { Spacing = 6 };
            var expanded = _expanded == field;
            var control = field is Target or Title ? null : BuildControl(field, applicable, detail: false);
            values.Children.Add(HeaderRow(FieldHeading(field, expanded), control));
            if (expanded)
            {
                foreach (var member in selection)
                {
                    var detail = Supports(member, field) ? BuildControl(field, [member.Id], detail: true) : new TextBlock();
                    var title = new TextBlock
                    {
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    AutomationProperties.SetAutomationId(title, $"batch-detail-title-{member.Id}");
                    _detailTitles.Add(member.Id, title);
                    UpdateDetailTitle(member.Id, WallMemberVisualFactory.ResolveTitle(
                        _details.GetValueOrDefault(member.Id)?.EmbeddedDraft ?? member));
                    var row = VisualStyles.FormRow(title, detail, 88, 6);
                    row.Margin = new Thickness(32, 0, 0, 0);
                    if (_details.GetValueOrDefault(member.Id)?.EmbeddedSupplement is { } supplement)
                    {
                        row.RowDefinitions.Add(new() { Height = GridLength.Auto });
                        row.RowDefinitions.Add(new() { Height = GridLength.Auto });
                        row.RowSpacing = 0;
                        var host = HostDetailElement(supplement);
                        Grid.SetColumn(host, 1);
                        Grid.SetRow(host, 1);
                        row.Children.Add(host);
                    }
                    values.Children.Add(row);
                }
            }
            _cards.Children.Add(VisualStyles.Card(values));
        }
    }

    private static Grid HeaderRow(FrameworkElement heading, FrameworkElement? controls)
    {
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new());
        row.Children.Add(heading);
        if (controls is not null)
        {
            Grid.SetColumn(controls, 1);
            row.Children.Add(controls);
        }
        return row;
    }

    private void UpdateDetailTitle(string id, string title)
    {
        if (!_detailTitles.TryGetValue(id, out var label)) return;
        var name = title.Length == 0 ? T("Recycle.Untitled") : title;
        if (label.Text == name) return;
        label.Text = name;
        ActionIcon.SetTip(label, name);
    }

    private ToggleButton FieldHeading(string field, bool expanded)
    {
        var toggle = new ToggleButton
        {
            Style = (Style)Application.Current.Resources["TileWallSectionHeadingStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch, IsChecked = expanded,
        };
        var label = ActionIcon.Label(T(field), expanded ? "\uE70D" : "\uE76C");
        ((TextBlock)label.Children[1]).TextWrapping = TextWrapping.Wrap;
        toggle.Content = label;
        AutomationProperties.SetName(toggle, T(field));
        AutomationProperties.SetAutomationId(toggle, "batch-field-" + field);
        toggle.Click += (_, _) =>
        {
            toggle.IsChecked = expanded;
            RunDetailTransition(() => _expanded = expanded ? null : field);
        };
        return toggle;
    }

    private void SelectAllMembers(bool select)
    {
        if (_committing) return;
        _selected.Clear();
        if (select) _selected.UnionWith(_drafts.Keys);
        RefreshChanged();
    }

    private static bool Supports(WallMember member, string field) => field switch
    {
        Size => member is TileMember,
        Title => true,
        _ => member is TileMember or PuzzleMember,
    };

    private FrameworkElement BuildControl(string field, string[] ids, bool detail)
    {
        var members = ids.Select(id => _drafts[id].Member).ToArray();
        if (field is Title && detail && members[0] is ClockMember clock)
        {
            var title = new TextBox { Text = WallMemberVisualFactory.ResolveTitle(clock), IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
            title.TextChanging += (_, _) => Update([clock.Id], member => member with
            {
                Visual = member.Visual with { TitleMode = string.IsNullOrWhiteSpace(title.Text) ? MemberTitleMode.Automatic : MemberTitleMode.Manual,
                    Title = string.IsNullOrWhiteSpace(title.Text) ? null : title.Text.Trim() },
            }, refreshCards: false);
            return title;
        }
        if (detail && field is Target or Title or Foreground)
            return EmbeddedProperty(members[0].Id, field);
        return field switch
        {
            Foreground => ForegroundControl(ids, members),
            Background => BackgroundControl(ids, members),
            Opacity => OpacityControl(ids),
            Size => SizeControl(ids, members),
            Fit => FitControl(ids),
            Ratio => RatioControl(ids),
            _ => new TextBlock(),
        };
    }

    private FrameworkElement EmbeddedProperty(string id, string field)
    {
        if (!_details.TryGetValue(id, out var editor))
        {
            try
            {
                var baseline = _drafts[id];
                editor = _createDetail(baseline, field, edit =>
                {
                    var before = _drafts[id];
                    _drafts[id] = edit with { Member = MergeDetailMember(before.Member, baseline.Member, edit.Member) };
                    RememberEdit([before]);
                });
                editor.EmbeddedPreviewChanged += RefreshPreview;
                editor.EmbeddedDraftChanged += RefreshEditTools;
                editor.EmbeddedEditConfirmed += () =>
                {
                    if (!_closing && !_committing && editor.HasUnsavedDraft)
                        editor.TrySaveNow();
                    RefreshEditTools();
                };
                _details.Add(id, editor);
            }
            catch (Exception ex)
            {
                ShowError(string.Format(T("Wall.EditFailed"), ex.Message));
                return new TextBlock();
            }
        }
        var host = HostDetailElement(editor.EmbeddedContent!);
        AutomationProperties.SetAutomationId(host, $"batch-detail-{field}-{id}");
        return host;
    }

    private ContentControl HostDetailElement(FrameworkElement content)
    {
        var host = new ContentControl { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _detailHosts.Add(host);
        return host;
    }

    private static WallMember MergeDetailMember(WallMember current, WallMember baseline, WallMember replacement) =>
        replacement with
        {
            LocalBounds = current.LocalBounds,
            ImageDisplay = current.ImageDisplay == baseline.ImageDisplay ? replacement.ImageDisplay : current.ImageDisplay,
            Visual = current.Visual with
            {
                Title = replacement.Visual.Title,
                TitleMode = replacement.Visual.TitleMode,
                BackgroundMode = current.Visual.BackgroundMode == baseline.Visual.BackgroundMode
                    ? replacement.Visual.BackgroundMode : current.Visual.BackgroundMode,
                BackgroundColor = current.Visual.BackgroundColor == baseline.Visual.BackgroundColor
                    ? replacement.Visual.BackgroundColor : current.Visual.BackgroundColor,
                BackgroundTransparency = current.Visual.BackgroundTransparency == baseline.Visual.BackgroundTransparency
                    ? replacement.Visual.BackgroundTransparency : current.Visual.BackgroundTransparency,
            },
        };

    private FrameworkElement BackgroundControl(string[] ids, WallMember[] members)
    {
        var picker = new MemberBackgroundPicker();
        var badge = ids.Length > 1 ? CreateMixedBadge() : null;
        picker.RefreshStrings();
        var modes = members.Select(member => member.Visual.BackgroundMode).Distinct().ToArray();
        var colors = members.Select(member => member.Visual.BackgroundColor).Distinct().ToArray();
        picker.Mode.SelectedIndex = modes.Length == 1 ? (int)modes[0] : -1;
        picker.Color.Color = MemberBackgroundPicker.ParseColor(colors.Length == 1 ? colors[0] : null);
        ActionIcon.Set(picker.ColorButton, T("Tile.CustomColor"), ActionIcon.Color);
        picker.ColorButton.IsEnabled = modes.Length == 1 && modes[0] == MemberBackgroundMode.Color;
        picker.Mode.SetEnabled(ids.Length > 0);
        void RefreshMixed()
        {
            if (badge is null) return;
            var visuals = ids.Select(id => _drafts[id].Member.Visual);
            var mixed = visuals.Select(visual => (visual.BackgroundMode,
                visual.BackgroundMode == MemberBackgroundMode.Color ? visual.BackgroundColor : null)).Distinct().Skip(1).Any();
            badge.Visibility = mixed ? Visibility.Visible : Visibility.Collapsed;
        }
        RefreshMixed();
        picker.Mode.SelectionChanged += (_, _) =>
        {
            if (picker.Mode.SelectedIndex < 0) return;
            var mode = (MemberBackgroundMode)picker.Mode.SelectedIndex;
            var fallbackColor = MemberBackgroundPicker.ColorText(picker.Color.Color);
            Update(ids, member => member with { Visual = member.Visual with
            {
                BackgroundMode = mode,
                BackgroundColor = mode == MemberBackgroundMode.Color ? member.Visual.BackgroundColor ?? fallbackColor : member.Visual.BackgroundColor,
            } }, refreshCards: mode != MemberBackgroundMode.Color);
            if (mode == MemberBackgroundMode.Color)
            {
                picker.ColorButton.IsEnabled = true;
                picker.ColorFlyout.ShowAt(picker.ColorButton);
            }
            RefreshMixed();
        };
        picker.ColorFlyout.Closed += (_, _) => RefreshCards();
        picker.Color.ColorChanged += (_, e) =>
        {
            Update(ids, member => member with
            { Visual = member.Visual with { BackgroundColor = MemberBackgroundPicker.ColorText(e.NewColor) } }, refreshCards: false);
            RefreshMixed();
        };
        return badge is null ? picker.Row : PlaceMixedBadge(picker.Row, badge);
    }

    private FrameworkElement ForegroundControl(string[] ids, WallMember[] members)
    {
        var selector = SharedMemberEditorDialog.CreateForegroundSelector();
        SharedMemberEditorDialog.SetForegroundNames(selector);
        selector.SetEnabled(ids.Length > 0);
        selector.SetItemEnabled((int)MemberImageSourceKind.Single, false);
        selector.SetItemEnabled((int)MemberImageSourceKind.Folder, false);
        var values = members.Select(member => member.Images.Kind).Distinct().ToArray();
        selector.SelectedIndex = values.Length == 1 && values[0] != MemberImageSourceKind.None ? (int)values[0] : -1;
        selector.SelectionChanged += (_, _) => RunDetailTransition(() =>
        {
            EditDrafts(() =>
            {
                foreach (var id in ids)
                {
                    var draft = _drafts[id];
                    if (draft.Member.Images.Kind == MemberImageSourceKind.Automatic) continue;
                    var updated = draft with
                    {
                        Member = MemberPresentationRules.ApplyImageSource(draft.Member with
                            { ResourceOrigin = MemberResourceOrigin.Local }, new MemberImageSource(), applyDefaults: false),
                        Frame = null, Assets = [],
                    };
                    _drafts[id] = updated;
                    _ = RefreshAutomaticFrameAsync(updated);
                }
            });
            RefreshPreview();
        });
        return WithMixedBadge(selector, values.Length > 1);
    }

    private FrameworkElement OpacityControl(string[] ids)
    {
        var input = SharedMemberEditorDialog.CreateTransparencyInput();
        var slider = SharedMemberEditorDialog.CreateTransparencySlider();
        var badge = ids.Length > 1 ? CreateMixedBadge() : null;
        input.IsEnabled = slider.IsEnabled = ids.Length > 0;
        AutomationProperties.SetName(input, T(Opacity));
        AutomationProperties.SetName(slider, T(Opacity));
        var syncing = false;
        void Refresh()
        {
            var values = ids.Select(id => _drafts[id].Member.Visual.BackgroundTransparency).Distinct().ToArray();
            syncing = true;
            input.Value = values.Length == 1 ? values[0] : double.NaN;
            slider.Value = values.Length == 1 ? values[0] : 50;
            if (badge is not null) badge.Visibility = values.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
            syncing = false;
        }
        void Apply(double value)
        {
            if (syncing || !double.IsFinite(value)) return;
            var percent = (int)Math.Round(value);
            Update(ids, member => member with { Visual = member.Visual with { BackgroundTransparency = percent } }, refreshCards: false);
            foreach (var refresh in _refreshOpacityControls) refresh();
        }
        Refresh();
        _refreshOpacityControls.Add(Refresh);
        input.ValueChanged += (_, e) => Apply(e.NewValue);
        slider.ValueChanged += (_, e) => Apply(e.NewValue);
        var controls = SharedMemberEditorDialog.TransparencyRow(input, slider);
        return badge is null ? controls : PlaceMixedBadge(controls, badge);
    }

    private FrameworkElement SizeControl(string[] ids, WallMember[] members)
    {
        var sizes = members.Select(member => member.LocalBounds.Size).Distinct().ToArray();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (preset, glyph) in SharedMemberEditorDialog.TilePresets)
        {
            var size = MemberSizeRules.TileBounds(preset).Size;
            var button = new ToggleButton { IsChecked = sizes.Length == 1 && sizes[0] == size, IsEnabled = sizes.Length > 0 };
            ActionIcon.SetIcon(button, T($"Editor.Preset{preset}"), glyph);
            button.Click += (_, _) => { Resize(ids, size); button.IsChecked = sizes.Length == 1 && sizes[0] == size; };
            row.Children.Add(button);
        }
        return WithMixedBadge(row, sizes.Length > 1);
    }

    private FrameworkElement FitControl(string[] ids)
    {
        ids = ids.Where(id => _drafts[id].Frame is not null).ToArray();
        var modes = ids.Select(id => _drafts[id].Member.ImageDisplay.Mode).Distinct().ToArray();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (mode, key, glyph) in SharedMemberEditorDialog.ImageModes)
        {
            var button = new ToggleButton { IsChecked = modes.Length == 1 && modes[0] == mode, IsEnabled = modes.Length > 0 };
            ActionIcon.SetIcon(button, T(key), glyph);
            button.Click += (_, _) =>
            {
                var scale = _dialog.XamlRoot?.RasterizationScale ?? 1;
                Update(ids, member => member with
                {
                    ImageDisplay = SharedMemberEditorDialog.ChangeImageMode(member, _drafts[member.Id].Frame, scale, mode),
                });
            };
            row.Children.Add(button);
        }
        return WithMixedBadge(row, modes.Length > 1);
    }

    private FrameworkElement RatioControl(string[] ids)
    {
        ids = ids.Where(id => _drafts[id].Frame is not null).ToArray();
        var input = SharedMemberEditorDialog.CreateRatioInput();
        var scale = _dialog.XamlRoot?.RasterizationScale ?? 1;
        var values = ids.Select(id => SharedMemberEditorDialog.ImageRatioPercent(_drafts[id].Member, _drafts[id].Frame!, scale))
            .Select(value => value.ToString("0.########", CultureInfo.CurrentCulture)).Distinct().ToArray();
        input.IsEnabled = values.Length > 0;
        var initial = values.Length == 1 ? values[0] : string.Empty;
        input.Text = initial;
        AutomationProperties.SetName(input, T(Ratio));
        bool Apply()
        {
            if (input.Text == initial) return true;
            if (!int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var percent) || percent is < 1 or > 10000)
            {
                ShowError(T("Editor.IntegerRatio"));
                return false;
            }
            initial = input.Text;
            Update(ids, member => member with
            { ImageDisplay = new MemberImageDisplay { Mode = ImageDisplayMode.Custom, CustomScale = percent / 100d } });
            return true;
        }
        _commitInputs.Add(Apply);
        input.LostFocus += (_, _) => Apply();
        input.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { Apply(); e.Handled = true; } };
        return WithMixedBadge(SharedMemberEditorDialog.ImageRatioRow(input), values.Length > 1);
    }

    private static FrameworkElement WithMixedBadge(FrameworkElement controls, bool mixed) =>
        mixed ? PlaceMixedBadge(controls, CreateMixedBadge()) : controls;

    private static Border CreateMixedBadge()
    {
        var badge = (Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                Background="{ThemeResource AccentFillColorDefaultBrush}" VerticalAlignment="Center" Padding="6,2"
                Margin="6,0,0,0" IsHitTestVisible="False">
                <TextBlock FontSize="12" FontWeight="SemiBold" Foreground="{ThemeResource TextOnAccentFillColorPrimaryBrush}" />
            </Border>
            """);
        ((TextBlock)badge.Child).Text = T("Batch.Mixed");
        return badge;
    }

    private static Grid PlaceMixedBadge(FrameworkElement controls, Border badge)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new());
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(controls);
        Grid.SetColumn(badge, 1);
        row.Children.Add(badge);
        return row;
    }

    private void Resize(string[] ids, GridSize size)
    {
        var sizes = ids.Where(id => _drafts[id].Member.LocalBounds.Size != size)
            .ToDictionary(id => id, _ => size, StringComparer.Ordinal);
        if (sizes.Count == 0) return;
        var resized = WallLayoutEngine.ResizeGroupMembers(DraftGroup(), sizes, _selected, _settings.LayoutLocked);
        if (resized is null) ShowError(T(_settings.LayoutLocked ? "Wall.LayoutLocked" : "Wall.Rejection.NoSpace"));
        else ApplyLayout(resized);
    }

    private void ApplyLayout(IReadOnlyList<WallMember> members)
    {
        EditDrafts(() =>
        {
            foreach (var member in members)
                _drafts[member.Id] = _drafts[member.Id] with { Member = member };
        });
        RefreshChanged();
    }

    private void Update(string[] ids, Func<WallMember, WallMember> change, bool refreshCards = true)
    {
        EditDrafts(() =>
        {
            foreach (var id in ids)
            {
                var draft = _drafts[id];
                _drafts[id] = draft with { Member = change(draft.Member) };
            }
        });
        RefreshChanged(refreshCards);
    }

    private void RefreshChanged(bool refreshCards = true)
    {
        RefreshPreview();
        RefreshEditTools();
        if (refreshCards) _dialog.DispatcherQueue.TryEnqueue(RefreshCards);
    }
}
