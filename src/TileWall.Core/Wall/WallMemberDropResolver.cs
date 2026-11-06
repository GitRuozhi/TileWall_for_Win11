using TileWall.Core.Grid;

namespace TileWall.Core.Wall;

internal static class WallMemberDropResolver
{
    public static DipRect DragBounds(DipPoint pointer, DipPoint grabOffset, GridSize size, WallMetrics metrics) =>
        new(pointer.X - grabOffset.X, pointer.Y - grabOffset.Y, metrics.WidthOf(size.Columns), metrics.HeightOf(size.Rows));
}

/// <summary>The member's single snapped footprint in the gesture's original logical grid.</summary>
internal readonly record struct WallMemberDropSlot(int LaneIndex, GridRect Bounds);

/// <summary>Snaps once, then resolves group ownership from the same immutable grid footprint.</summary>
internal sealed class WallMemberDropSession
{
    private sealed record GroupRows(string Id, int Lane, int Title, int Members, int End);

    private readonly WallDocument _document;
    private readonly string _memberId, _sourceId, _newGroupId;
    private readonly bool _sourceDisappears;
    private readonly GridSize _size;
    private readonly GridRect _originalBounds;
    private readonly WallMemberDropSlot _originalSlot;
    private readonly WallMetrics _metrics;
    private readonly GroupRows[] _groups;
    private WallMemberDropSlot? _displayedSlot;

    public WallMemberDropSession(WallDocument document, string memberId, string newGroupId, WallMetrics metrics)
    {
        _document = document;
        _memberId = memberId;
        _newGroupId = newGroupId;
        _metrics = metrics;
        var source = document.Lanes.SelectMany(lane => lane.Groups)
            .Single(group => group.Members.Any(member => member.Id == memberId));
        _sourceId = source.Id;
        _sourceDisappears = source.Members.Count == 1;
        _originalBounds = source.Members.Single(member => member.Id == memberId).LocalBounds;
        _size = _originalBounds.Size;
        var geometry = WallLayoutEngine.Measure(document, metrics);
        _groups = document.Lanes.SelectMany((lane, index) => lane.Groups.Select(group =>
        {
            var placement = geometry.Groups.Single(placed => placed.GroupId == group.Id);
            var title = GridRow(placement.TitleBounds.Y);
            var members = GridRow(placement.TitleBounds.Y + metrics.TitleHeight + metrics.TitleToMembersGap);
            var occupied = group.Members.Where(member => member.Id != memberId)
                .Select(member => member.LocalBounds.Bottom).DefaultIfEmpty(0).Max();
            return new GroupRows(group.Id, index, title, members, members + occupied);
        })).ToArray();
        var sourceRows = _groups.Single(group => group.Id == _sourceId);
        _originalSlot = new(sourceRows.Lane, _originalBounds with { Row = sourceRows.Members + _originalBounds.Row });
    }

    public WallMemberDropSlot Snap(DipRect dragged)
    {
        var lanePitch = _metrics.LaneWidth + _metrics.ColumnGap;
        var nearby = Math.Clamp((int)Math.Floor((dragged.X - _metrics.Margin) / lanePitch), -1, 64);
        var lane = -1;
        var column = 0;
        var distance = double.PositiveInfinity;
        for (var candidateLane = Math.Max(-1, nearby - 1); candidateLane <= Math.Min(64, nearby + 1); candidateLane++)
        {
            var laneX = _metrics.Margin + candidateLane * lanePitch;
            var relative = (dragged.X - laneX) / _metrics.Pitch;
            var candidateColumn = (int)Math.Floor(relative);
            if (relative - candidateColumn > .5) candidateColumn++;
            candidateColumn = Math.Clamp(candidateColumn, 0, 8 - _size.Columns);
            var candidateDistance = Math.Abs(dragged.X - laneX - candidateColumn * _metrics.Pitch);
            if (candidateDistance >= distance) continue;
            distance = candidateDistance;
            lane = candidateLane;
            column = candidateColumn;
        }

        var row = GridRow(dragged.Y);
        if (_displayedSlot is { } displayed && displayed.LaneIndex == lane &&
            Math.Abs(dragged.Y - _metrics.TopMargin - displayed.Bounds.Row * _metrics.Pitch) <=
                _metrics.Pitch / 2 + _metrics.Gap)
            row = displayed.Bounds.Row;
        return new(lane, new GridRect(column, row, _size.Columns, _size.Rows));
    }

    public MoveMemberAction Resolve(WallMemberDropSlot slot)
    {
        if (slot == _originalSlot)
            return new(_document.Generation, _memberId, _originalBounds, _sourceId);
        var row = slot.Bounds.Row;
        var groups = _groups.Where(group => group.Lane == slot.LaneIndex).ToArray();
        // The first free member row can also be the following group's title row.
        // Appending to the preceding group owns that shared grid position.
        var target = groups.FirstOrDefault(group => row == group.End)
            ?? groups.FirstOrDefault(group => row >= group.Title && row < group.End);
        // A new group needs one complete title row above its entire footprint.
        // Whole existing group bands also protect their internal empty rows.
        target ??= groups.FirstOrDefault(group => row - 1 < group.End &&
            group.Title < slot.Bounds.Bottom);
        if (target is not null)
            return new(_document.Generation, _memberId,
                slot.Bounds with { Row = Math.Max(0, row - target.Members) }, target.Id);

        var preceding = groups.Where(group => (!_sourceDisappears || group.Id != _sourceId) && group.Title < row).ToArray();
        var titleRow = preceding.Length == 0 ? 0 : preceding[^1].End;
        return new(_document.Generation, _memberId, slot.Bounds with { Row = row - titleRow - 1 },
            null, slot.LaneIndex, preceding.Length, _newGroupId);
    }

    public void SetDisplayed(WallMemberDropSlot? slot) => _displayedSlot = slot;

    private int GridRow(double y) => (int)Math.Floor((y - _metrics.TopMargin) / _metrics.Pitch + .5);
}
