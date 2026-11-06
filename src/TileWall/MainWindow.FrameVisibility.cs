using TileWall.Core.Wall;

namespace TileWall;

public sealed partial class MainWindow
{
    private void UpdateVisibleWallFrames()
    {
        _puzzles.SetPreparationAllowed(!_windowClosing && !ShutdownStarted && _hasDocument && !_viewBlocked);
        if (_windowClosing || ShutdownStarted || !_hasDocument || _viewBlocked || !AppWindow.IsVisible ||
            Surface.Geometry is not { } geometry)
        { _puzzles.SetVisibleMembers(new HashSet<string>(StringComparer.Ordinal)); return; }
        var width = Math.Max(1, _scrollRegion.Width);
        var height = Math.Max(1, _scrollRegion.Height);
        // A small presentation margin starts approaching carousels. Frame ownership
        // and current-image preparation cover the whole active wall independently.
        var left = WallScroll.HorizontalOffset - _metrics.LaneWidth / 2;
        var right = WallScroll.HorizontalOffset + width + _metrics.LaneWidth / 2;
        var top = WallScroll.VerticalOffset - _metrics.Pitch;
        var bottom = WallScroll.VerticalOffset + height + _metrics.Pitch;
        _puzzles.SetVisibleMembers(geometry.Groups.SelectMany(group => group.Members)
            .Where(member => member.Bounds.X < right && member.Bounds.X + member.Bounds.Width > left &&
                member.Bounds.Y < bottom && member.Bounds.Y + member.Bounds.Height > top)
            .Select(member => member.MemberId).ToHashSet(StringComparer.Ordinal));
    }
}
