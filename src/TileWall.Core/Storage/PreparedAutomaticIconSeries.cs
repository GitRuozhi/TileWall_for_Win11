using TileWall.Core.Wall;

namespace TileWall.Core.Storage;

/// <summary>Draft native icon bytes join the ordinary media transaction only on confirmation.</summary>
public sealed record PreparedAutomaticIconSeries(AutomaticIconSeries Series, IReadOnlyList<PreparedMediaAsset> Assets);
