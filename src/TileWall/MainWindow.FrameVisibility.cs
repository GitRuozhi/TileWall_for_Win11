using TileWall.Core.Wall;

namespace TileWall;

public sealed partial class MainWindow
{
    private void UpdateVisibleWallFrames()
    {
        _puzzles.SetPreparationAllowed(!_windowClosing && !ShutdownStarted && _hasDocument && !_viewBlocked);
        var visible = new HashSet<string>(StringComparer.Ordinal);
        if (!_windowClosing && !ShutdownStarted && _hasDocument && !_viewBlocked)
        {
            if (AppWindow.IsVisible && Surface.Geometry is { } geometry)
            {
                // A small presentation margin starts approaching carousels.
                var left = WallScroll.HorizontalOffset - _metrics.LaneWidth / 2;
                var right = WallScroll.HorizontalOffset + Math.Max(1, _scrollRegion.Width) + _metrics.LaneWidth / 2;
                var top = WallScroll.VerticalOffset - _metrics.Pitch;
                var bottom = WallScroll.VerticalOffset + Math.Max(1, _scrollRegion.Height) + _metrics.Pitch;
                foreach (var member in geometry.Groups.SelectMany(group => group.Members))
                    if (member.Bounds.X < right && member.Bounds.X + member.Bounds.Width > left &&
                        member.Bounds.Y < bottom && member.Bounds.Y + member.Bounds.Height > top)
                        visible.Add(member.MemberId);
            }
            foreach (var wall in _secondaryWalls.Values) wall.AddVisibleMembers(visible, _metrics);
        }
        _puzzles.SetVisibleMembers(visible);
    }
}
