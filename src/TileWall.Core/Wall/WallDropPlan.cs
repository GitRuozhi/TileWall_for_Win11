using TileWall.Core.Storage;

namespace TileWall.Core.Wall;

/// <summary>One release and the exact placement it displayed.</summary>
internal sealed record WallDropPlan(WallDocument Basis, WallLayoutResult Displayed, WallAction Action, bool PrependLane)
{
    internal WallProposal Proposal => Displayed.Proposal!;
    internal bool HasChange => !SamePlacement(Basis, Proposal.Document);

    internal WallProposal? Revalidate(WallDocument current, double width, double height, WallMetrics metrics)
    {
        if (current.Generation != Basis.Generation || !SamePlacement(current, Basis)) return null;
        var result = Calculate(current, Action, PrependLane, width, height, metrics);
        return result.Proposal is { } proposal && Matches(proposal.Document) ? proposal : null;
    }

    internal bool Matches(WallDocument document) => document.Generation == Proposal.Document.Generation &&
        SamePlacement(document, Proposal.Document);

    internal static WallLayoutResult Calculate(WallDocument basis, WallAction action, bool prependLane,
        double width, double height, WallMetrics metrics)
    {
        if (prependLane)
        {
            if (basis.Settings.LayoutLocked || basis.Columns >= 64)
                return new(null, WallRejection.NoSpace, null);
            basis = basis with { Columns = basis.Columns + 1,
                Lanes = new[] { new WallLane { Groups = [] } }.Concat(basis.Lanes).ToArray() };
        }
        return WallLayoutEngine.TryApply(basis, action, width, height, metrics);
    }

    private static bool SamePlacement(WallDocument left, WallDocument right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Columns != right.Columns || left.Rows != right.Rows || left.Lanes.Count != right.Lanes.Count ||
            left.Settings.AnchorCorner != right.Settings.AnchorCorner || left.Settings.LayoutLocked != right.Settings.LayoutLocked ||
            left.Settings.ExpandColumns != right.Settings.ExpandColumns || left.Settings.FullScreen != right.Settings.FullScreen ||
            left.Settings.MenuStart != right.Settings.MenuStart ||
            left.Settings.WindowedColumns != right.Settings.WindowedColumns ||
            left.Settings.WindowedRows != right.Settings.WindowedRows ||
            left.Settings.ManualWindowedViewportWidthDip != right.Settings.ManualWindowedViewportWidthDip ||
            left.Settings.WindowedViewportWidthDip != right.Settings.WindowedViewportWidthDip) return false;
        for (var lane = 0; lane < left.Lanes.Count; lane++)
        {
            var a = left.Lanes[lane];
            var b = right.Lanes[lane];
            if (a.ImportBatchId != b.ImportBatchId || a.Groups.Count != b.Groups.Count) return false;
            for (var group = 0; group < a.Groups.Count; group++)
            {
                var first = a.Groups[group];
                var second = b.Groups[group];
                if (first.Id != second.Id || first.Title != second.Title || first.ReservedRows != second.ReservedRows ||
                    !SameSource(first.ImportSource, second.ImportSource) || first.UserClaimed != second.UserClaimed ||
                    first.Members.Count != second.Members.Count) return false;
                for (var member = 0; member < first.Members.Count; member++)
                {
                    var x = first.Members[member];
                    var y = second.Members[member];
                    if (x.Id != y.Id || x.GetType() != y.GetType() || x.LocalBounds != y.LocalBounds ||
                        x.UserClaimed != y.UserClaimed || !SameSource(x.ImportSource, y.ImportSource) || !SameEntry(x.Entry, y.Entry) ||
                        x.TargetInput != y.TargetInput || x.SourceShortcutPath != y.SourceShortcutPath) return false;
                }
            }
        }
        return true;
    }

    private static bool SameSource(ImportSourceMetadata? a, ImportSourceMetadata? b) =>
        a == b || a is not null && b is not null && a with { ExtensionData = null } == b with { ExtensionData = null } &&
        ForwardDataCompatibility.FieldsEqual(a.ExtensionData, b.ExtensionData);

    private static bool SameEntry(ManagedEntryReference? a, ManagedEntryReference? b) =>
        a == b || a is not null && b is not null && a with { ExtensionData = null } == b with { ExtensionData = null } &&
        ForwardDataCompatibility.FieldsEqual(a.ExtensionData, b.ExtensionData);
}
