using TileWall.Core.Grid;

namespace TileWall.Core.Wall;

/// <summary>Shared creation, editing and persistence size contract.</summary>
public static class MemberSizeRules
{
    public const int TileMaximumColumns = 4;
    public const int TileMaximumRows = 4;
    public const int PartitionMaximumColumns = 4;
    public const int PartitionMaximumRows = 4;
    public const int PuzzleMinimumColumns = 2;
    public const int PuzzleMinimumRows = 2;
    public const int PuzzleMaximumColumns = 8;
    public const int PuzzleMaximumRows = 12;
    public static bool IsTileSize(int columns, int rows) => columns is >= 1 and <= TileMaximumColumns && rows is >= 1 and <= TileMaximumRows;
    public static bool IsPuzzleSize(int columns, int rows) => columns is >= PuzzleMinimumColumns and <= PuzzleMaximumColumns && rows is >= PuzzleMinimumRows and <= PuzzleMaximumRows;
    public static bool IsPartitionSize(int columns, int rows) => columns is >= 1 and <= PartitionMaximumColumns && rows is >= 1 and <= PartitionMaximumRows;
    public static bool IsValid(WallMember member) => member switch
    {
        TileMember => IsTileSize(member.LocalBounds.Width, member.LocalBounds.Height),
        PuzzleMember puzzle => IsPuzzleSize(puzzle.LocalBounds.Width, puzzle.LocalBounds.Height) && puzzle.Partitions is not null && puzzle.Partitions.All(p => IsPartitionSize(p.Width, p.Height)),
        null => false,
        _ => true,
    };
}
