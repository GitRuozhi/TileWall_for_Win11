using TileWall.Core.Grid;

namespace TileWall.Core.Wall;

/// <summary>One provisional DIP scale for the 0.2.1 wall. Title and spacing values await visual calibration.</summary>
public sealed record WallMetrics
{
    public double Cell { get; init; } = 48;
    public double Gap { get; init; } = 4;
    public double ColumnGap { get; init; } = 12;
    public double Margin { get; init; } = 12;
    public double TitleHeight { get; init; } = 32;
    public double TitleToMembersGap { get; init; } = 8;
    public double GroupGap { get; init; } = 16;

    public double Pitch => Cell + Gap;
    public double LaneWidth => WidthOf(8);
    public double WidthOf(int cells) => cells * Cell + (cells - 1) * Gap;
    public double HeightOf(int cells) => WidthOf(cells);
    public double WallWidth(int columns) => 2 * Margin + columns * LaneWidth + (columns - 1) * ColumnGap;
    public double WallHeight(int rows) => 2 * Margin + HeightOf(rows);
    public double MemberX(int column) => column * Pitch;
    public double MemberY(int row) => row * Pitch;
    public DipRect MemberRect(GridRect cells, double groupX, double membersY) =>
        new(groupX + MemberX(cells.Column), membersY + MemberY(cells.Row), WidthOf(cells.Width), HeightOf(cells.Height));

    public bool IsValid =>
        double.IsFinite(Cell) && Cell > 0 && double.IsFinite(Gap) && Gap >= 0 &&
        double.IsFinite(ColumnGap) && ColumnGap >= 0 && double.IsFinite(Margin) && Margin >= 0 &&
        double.IsFinite(TitleHeight) && TitleHeight > 0 &&
        double.IsFinite(TitleToMembersGap) && TitleToMembersGap >= 0 &&
        double.IsFinite(GroupGap) && GroupGap >= 0 &&
        double.IsFinite(Pitch) && double.IsFinite(LaneWidth) &&
        double.IsFinite(WallWidth(64)) && double.IsFinite(WallHeight(256));
}

public sealed record MemberPlacement(string MemberId, DipRect Bounds);
public sealed record GroupPlacement(
    string GroupId,
    int LaneIndex,
    DipRect TitleBounds,
    DipRect Bounds,
    IReadOnlyList<MemberPlacement> Members);

public sealed record WallGeometry(
    double WallWidth,
    double WallHeight,
    IReadOnlyList<GroupPlacement> Groups);
