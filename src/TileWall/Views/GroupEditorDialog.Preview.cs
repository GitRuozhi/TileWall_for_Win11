using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Wall;
using Windows.Foundation;

namespace TileWall.Views;

internal sealed partial class GroupEditorDialog
{
    private Pointer? _pointer;
    private Point _pressedAt;
    private WallMember? _pressedMember;
    private IReadOnlyList<WallMember>? _dropPreview;
    private Rect? _marquee;
    private bool _removeFromSelection, _dragging;

    private void RefreshPreview()
    {
        _preview.Children.Clear();
        var members = _dropPreview ?? Members();
        _preview.Width = _metrics.LaneWidth;
        var rows = Math.Max(_group.ReservedRows ?? 0, members.Select(member => member.LocalBounds.Bottom).DefaultIfEmpty().Max());
        _preview.Height = _metrics.HeightOf(Math.Max(5, rows + 1));
        var scale = _dialog.XamlRoot?.RasterizationScale ?? 1;
        foreach (var layoutMember in members)
        {
            var detail = _details.GetValueOrDefault(layoutMember.Id);
            var member = detail is null ? layoutMember : MergeDetailMember(layoutMember, detail.EmbeddedOriginal, detail.EmbeddedDraft);
            var frame = detail is null ? _drafts[member.Id].Frame : detail.DraftFrame;
            var rect = _metrics.MemberRect(member.LocalBounds, 0, 0);
            var tile = new Grid { Width = rect.Width, Height = rect.Height };
            var title = WallMemberVisualFactory.ResolveTitle(member);
            AutomationProperties.SetAutomationId(tile, $"batch-member-{member.Id}");
            AutomationProperties.SetName(tile, title);
            UpdateDetailTitle(member.Id, title);
            IReadOnlyList<Grid> selectionSurfaces = [tile];
            if (member is ClockMember clock)
                tile.Children.Add(new ClockMemberView(clock, _metrics, scale, _settings.VisualStyle));
            else
            {
                var view = WallMemberVisualFactory.Create(member, frame,
                    _metrics, scale, _settings.VisualStyle, interactive: false, previewHover: true);
                tile.Children.Add(view);
                if (view.PreviewPartitions is { Count: > 0 } partitions)
                    selectionSurfaces = partitions;
            }
            if (_selected.Contains(member.Id))
            {
                foreach (var surface in selectionSurfaces)
                {
                    var (outline, check) = WallMemberVisualFactory.AddSelection(surface, _settings.VisualStyle);
                    outline.Visibility = check.Visibility = Visibility.Visible;
                }
            }
            Canvas.SetLeft(tile, rect.X);
            Canvas.SetTop(tile, rect.Y);
            _preview.Children.Add(tile);
        }
        if (_marquee is { } area)
        {
            var accent = WallSurface.SystemAccentColor();
            var border = new Border
            {
                Width = area.Width, Height = area.Height, BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(accent),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(36, accent.R, accent.G, accent.B)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(border, area.X);
            Canvas.SetTop(border, area.Y);
            _preview.Children.Add(border);
        }
    }

    private async Task RefreshAutomaticFrameAsync(GroupEditorDraft requested)
    {
        var token = _previewLifetime.Token;
        var rect = _metrics.MemberRect(requested.Member.LocalBounds, 0, 0);
        var scale = _dialog.XamlRoot?.RasterizationScale ?? 1;
        try
        {
            var result = await _loadAutomaticFrame(requested, (int)Math.Ceiling(rect.Width * scale),
                (int)Math.Ceiling(rect.Height * scale), token);
            if (token.IsCancellationRequested || result.Frame is not { } frame) return;
            var current = _drafts[requested.Member.Id];
            if (!ReferenceEquals(current.Member.Images, requested.Member.Images) ||
                current.Member.TargetInput != requested.Member.TargetInput) return;
            _drafts[requested.Member.Id] = current with { Frame = frame };
            RefreshChanged();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) ShowError(string.Format(T("Wall.EditFailed"), ex.Message));
        }
    }

    private void AttachGestures()
    {
        _preview.PointerPressed += BeginGesture;
        _preview.PointerMoved += (_, e) => UpdateGesture(e);
        _preview.PointerReleased += EndGesture;
        _preview.PointerCanceled += (_, _) => CancelGesture();
        _preview.PointerCaptureLost += (_, _) => CancelGesture();
        _preview.ContextRequested += (_, args) => args.Handled = true;
    }

    private void BeginGesture(object sender, PointerRoutedEventArgs e)
    {
        if (_pointer is not null || _committing) return;
        var point = e.GetCurrentPoint(_preview);
        var properties = point.Properties;
        if (!properties.IsLeftButtonPressed && !properties.IsRightButtonPressed) return;
        var member = Members().FirstOrDefault(candidate =>
        {
            var bounds = _metrics.MemberRect(candidate.LocalBounds, 0, 0);
            return point.Position.X >= bounds.X && point.Position.X < bounds.X + bounds.Width &&
                point.Position.Y >= bounds.Y && point.Position.Y < bounds.Y + bounds.Height;
        });
        if (properties.IsRightButtonPressed && member is not null) return;
        if (!_preview.CapturePointer(e.Pointer)) return;
        _pointer = e.Pointer;
        _pressedMember = member;
        _pressedAt = point.Position;
        _removeFromSelection = properties.IsRightButtonPressed;
        _dragging = false;
        e.Handled = true;
    }

    private void UpdateGesture(PointerRoutedEventArgs e)
    {
        if (_pointer?.PointerId != e.Pointer.PointerId) return;
        var position = e.GetCurrentPoint(_preview).Position;
        var horizontal = position.X - _pressedAt.X;
        var vertical = position.Y - _pressedAt.Y;
        _dragging |= Math.Abs(horizontal) + Math.Abs(vertical) >= 3;
        if (!_dragging) return;
        if (_pressedMember is { } member)
        {
            var rect = member.LocalBounds;
            var valid = position.X >= 0 && position.X < _preview.Width && position.Y >= 0 && position.Y < _preview.Height;
            var col = Math.Clamp(rect.Column + (int)Math.Round(horizontal / _metrics.Pitch), 0,
                WallLayoutEngine.CellsPerLane - rect.Width);
            var row = Math.Max(0, rect.Row + (int)Math.Round(vertical / _metrics.Pitch));
            var proposed = valid ? WallLayoutEngine.MoveWithinEditedGroup(DraftGroup(), member.Id,
                rect with { Column = col, Row = row }, _settings.LayoutLocked) : null;
            var oldDraftBounds = _dropPreview?.FirstOrDefault(candidate => candidate.Id == member.Id)?.LocalBounds;
            var newDraftBounds = proposed?.FirstOrDefault(candidate => candidate.Id == member.Id)?.LocalBounds;
            if (oldDraftBounds == newDraftBounds) return;
            _dropPreview = proposed;
        }
        else
        {
            _marquee = new Rect(Math.Min(_pressedAt.X, position.X), Math.Min(_pressedAt.Y, position.Y),
                Math.Abs(horizontal), Math.Abs(vertical));
        }
        RefreshPreview();
        e.Handled = true;
    }

    private void EndGesture(object sender, PointerRoutedEventArgs e)
    {
        if (_pointer?.PointerId != e.Pointer.PointerId) return;
        UpdateGesture(e);
        var item = _pressedMember;
        var placement = _dropPreview;
        if (item is not null)
        {
            if (_dragging) _selected.Add(item.Id);
            else if (!_selected.Add(item.Id)) _selected.Remove(item.Id);
        }
        else if (_marquee is { } area)
        {
            foreach (var member in Members())
            {
                var bounds = _metrics.MemberRect(member.LocalBounds, 0, 0);
                if (bounds.X >= area.Right || bounds.X + bounds.Width <= area.Left ||
                    bounds.Y >= area.Bottom || bounds.Y + bounds.Height <= area.Top) continue;
                if (_removeFromSelection) _selected.Remove(member.Id);
                else _selected.Add(member.Id);
            }
        }
        CancelGesture();
        if (placement is not null) ApplyLayout(placement);
        else RefreshChanged();
        e.Handled = true;
    }

    private void CancelGesture()
    {
        if (_pointer is not { } pointer) return;
        _pointer = null;
        _pressedMember = null;
        _dropPreview = null;
        _marquee = null;
        _preview.ReleasePointerCapture(pointer);
        RefreshPreview();
    }
}
