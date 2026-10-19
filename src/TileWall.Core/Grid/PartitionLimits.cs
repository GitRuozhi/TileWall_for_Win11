namespace TileWall.Core.Grid;

/// <summary>Permitted puzzle partition dimensions.</summary>
public static class PartitionLimits
{
    public const int GroupMinColumns = TileWall.Core.Wall.MemberSizeRules.PuzzleMinimumColumns;
    public const int GroupMaxColumns = TileWall.Core.Wall.MemberSizeRules.PuzzleMaximumColumns;
    public const int GroupMinRows = TileWall.Core.Wall.MemberSizeRules.PuzzleMinimumRows;
    public const int GroupMaxRows = TileWall.Core.Wall.MemberSizeRules.PuzzleMaximumRows;
}
