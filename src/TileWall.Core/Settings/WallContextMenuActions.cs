namespace TileWall.Core.Settings;

[Flags]
public enum WallContextMenuActions : uint
{
    None = 0,
    // These values are stored in the local settings snapshot. Keep assigned bits stable.
    NewTile = 1 << 0,
    AddComponent = 1 << 1,
    NewClock = 1 << 2,
    Import = 1 << 3,
    Settings = 1 << 4,
    NextImage = 1 << 5,
    // Bit 6 is reserved for the retired Open command.
    Admin = 1 << 7,
    Location = 1 << 8,
    Size = 1 << 9,
    SizeSmall = 1 << 10,
    SizeMedium = 1 << 11,
    SizeWide = 1 << 12,
    SizeLarge = 1 << 13,
    Edit = 1 << 14,
    Remove = 1 << 15,
    MenuHidden = 1 << 16,
    RenameGroup = 1 << 17,
    EditGroup = 1 << 18,
    UnpinGroup = 1 << 19,
}
