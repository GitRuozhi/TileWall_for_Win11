using TileWall.Core.Application;
using TileWall.Core.Import;

namespace TileWall.ApplicationModel;

/// <summary>Reprojects one completed scan snapshot for the import dialog's display switches.</summary>
internal static class StartMenuImportProjection
{
    internal static IReadOnlyList<StartMenuImportCandidate> Project(
        IReadOnlyList<StartMenuImportCandidate> candidates, bool autoTrim, bool recognizeGroups)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return StartMenuCandidateProjection.Project(candidates, autoTrim, recognizeGroups);
    }
}
