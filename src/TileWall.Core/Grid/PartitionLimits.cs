namespace TileWall.Core.Grid;

/// <summary>Permitted puzzle partition dimensions.</summary>
public static class PartitionLimits
{
    public const int GroupMinColumns = 1;
    public const int GroupMaxColumns = TileWall.Core.Wall.MemberSizeRules.PuzzleMaximumColumns;
    public const int GroupMinRows = 1;
    public const int GroupMaxRows = TileWall.Core.Wall.MemberSizeRules.PuzzleMaximumRows;
}
