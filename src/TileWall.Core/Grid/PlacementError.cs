namespace TileWall.Core.Grid;

/// <summary>放置判定结果（设计 §5.4 统一入口；错误码即测试断言点）。</summary>
public enum PlacementError
{
    None,
    OutOfWall,
    CrossesColumn,
    OverlapsOccupied,
    TileTooWide,
    GroupSizeOutOfRange,
}

/// <summary>布局对象种类（独立磁贴 / 拼图组）。</summary>
public enum ObjectKind
{
    Tile,
    Group,
}
