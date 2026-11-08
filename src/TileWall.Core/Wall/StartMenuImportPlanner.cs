using TileWall.Core.Import;

namespace TileWall.Core.Wall;

/// <summary>Packs complete import destination groups without changing member-local geometry.</summary>
public static class StartMenuImportPlanner
{
    private const int MaxColumns = 64;
    private const int MaxRows = 256;

    public static WallLayoutResult TryPlan(WallDocument current, long expectedGeneration,
        IReadOnlyList<StartGroup> importedGroups, WallMetrics? metrics = null, WallViewportBudget? budget = null,
        ImportInsertionPosition insertionPosition = ImportInsertionPosition.Head, bool preserveExistingGroups = false,
        bool preferCurrentHeight = false)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(importedGroups);
        metrics ??= new();
        var result = TryPlanCore(current, expectedGeneration, importedGroups, metrics, budget, insertionPosition,
            preserveExistingGroups, preferCurrentHeight);
        if (result.Proposal is not null || current.Settings.ExpandColumns || budget is null ||
            result.Rejection is not (WallRejection.NoSpace or WallRejection.WorkAreaTooSmall) ||
            (preserveExistingGroups && result.Detail != "PACKAGE_WIDTH_LIMIT") ||
            result.Detail?.StartsWith("GROUP_TOO_TALL:", StringComparison.Ordinal) == true ||
            importedGroups.Any(group => group is null || metrics.TopMargin + metrics.Margin + metrics.TitleHeight +
                metrics.TitleToMembersGap + metrics.HeightOf(WallLayoutEngine.EffectiveGroupRows(group)) > budget.MaxHeightDip + 1e-8))
            return result;
        // Only import capacity gets this fallback. The source/selection and height budget stay unchanged.
        var expanded = current with { Settings = current.Settings with { ExpandColumns = true } };
        return TryPlanCore(expanded, expectedGeneration, importedGroups, metrics,
            budget with { MaxColumns = MaxColumns, MaxWidthDip = metrics.WallWidth(MaxColumns) }, insertionPosition,
            preserveExistingGroups, preferCurrentHeight);
    }

    private static WallLayoutResult TryPlanCore(WallDocument current, long expectedGeneration,
        IReadOnlyList<StartGroup> importedGroups, WallMetrics metrics, WallViewportBudget? budget,
        ImportInsertionPosition insertionPosition, bool preserveExistingGroups, bool preferCurrentHeight)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(importedGroups);
        if (!metrics.IsValid || !Enum.IsDefined(insertionPosition)) return Reject(WallRejection.InvalidAction);
        if (expectedGeneration != current.Generation) return Reject(WallRejection.StaleGeneration);
        var errors = WallLayoutEngine.ValidateGeometry(current, metrics);
        if (errors.Count != 0) return Reject(WallRejection.InvalidDocument, string.Join("; ", errors));
        var groups = importedGroups.Where(group => group is not null && group.Members is not null && (group.Members.Count > 0 || group.ReservedRows is > 0)).ToArray();
        if (groups.Length != importedGroups.Count || groups.Length == 0) return Reject(WallRejection.InvalidAction);
        if (groups.Select(group => group.Id).Distinct(StringComparer.Ordinal).Count() != groups.Length)
            return Reject(WallRejection.DuplicateId);
        foreach (var group in groups)
        {
            if (group.Members.Any(member => member is null || !MemberSizeRules.IsValid(member)))
                return Reject(WallRejection.InvalidAction, "MEMBER_SIZE_LIMIT");
            if (group.Members.Any(member => member.LocalBounds.Column < 0 || member.LocalBounds.Row < 0 ||
                    member.LocalBounds.Width < 1 || member.LocalBounds.Height < 1 ||
                    (long)member.LocalBounds.Column + member.LocalBounds.Width > 8 ||
                    (long)member.LocalBounds.Row + member.LocalBounds.Height > MaxRows) ||
                group.Members.SelectMany((member, index) => group.Members.Skip(index + 1)
                    .Select(other => member.LocalBounds.Intersects(other.LocalBounds))).Any(overlap => overlap))
                return Reject(WallRejection.InvalidAction, "INVALID_SOURCE_GEOMETRY");
        }
        if (current.Settings.LayoutLocked && !preserveExistingGroups)
            return TryProtected(current, groups, metrics, budget, insertionPosition);
        var maxRows = Math.Min(MaxRows, budget?.MaxRows ?? current.Rows);
        var maxColumns = Math.Min(MaxColumns, budget?.MaxColumns ?? MaxColumns);
        if (budget is not null)
        {
            while (maxRows > 0 && metrics.WallHeight(maxRows) > budget.MaxHeightDip + 1e-8) maxRows--;
            while (maxColumns > 0 && metrics.WallWidth(maxColumns) > budget.MaxWidthDip + 1e-8) maxColumns--;
        }
        if (preserveExistingGroups)
            return TryPlacePackageGroups(current, groups, metrics, budget, insertionPosition, maxRows, maxColumns);
        if (maxRows < 1 || maxColumns < 1 ||
            insertionPosition == ImportInsertionPosition.Head && maxRows < current.Rows)
            return Reject(WallRejection.WorkAreaTooSmall, "CONTENT_TOO_LARGE");
        var contentColumns = maxColumns - (current.Settings.ExpandColumns ? 1 : 0);
        if (contentColumns < 1) return Reject(WallRejection.WorkAreaTooSmall, "CONTENT_TOO_LARGE");
        var involved = groups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
        if (preferCurrentHeight)
            return TryPlaceAtCurrentHeight(current, groups, involved, maxRows, contentColumns, metrics, budget,
                insertionPosition);
        if (insertionPosition == ImportInsertionPosition.Tail)
            return TryTailWithCompaction(current, expectedGeneration, groups, involved, maxRows, contentColumns, metrics, budget);
        var ordered = groups.Concat(current.Lanes.SelectMany(lane => lane.Groups).Where(group => !involved.Contains(group.Id))).ToArray();
        foreach (var group in ordered)
        {
            var height = metrics.TopMargin + metrics.Margin + metrics.TitleHeight + metrics.TitleToMembersGap +
                metrics.HeightOf(WallLayoutEngine.EffectiveGroupRows(group));
            if (height > metrics.WallHeight(maxRows) + 1e-8)
                return Reject(WallRejection.NoSpace, "GROUP_TOO_TALL:" + (string.IsNullOrWhiteSpace(group.Title) ? group.Id : group.Title));
        }
        var firstLaneCount = Math.Max(1, Math.Min(current.Columns, contentColumns));
        for (var rows = current.Rows; rows <= maxRows; rows++)
        for (var columns = firstLaneCount; columns <= contentColumns; columns++)
        {
            if (!TryPack(ordered, columns, rows, metrics, out var lanes)) continue;
            var proposed = current with
            {
                Generation = expectedGeneration + 1,
                Columns = lanes.Count,
                Rows = rows,
                Lanes = lanes.Select(lane => new WallLane { Groups = lane, ImportBatchId = CommonImportBatchId(lane) }).ToArray(),
            };
            var normalized = WallLayoutEngine.NormalizeExpanded(proposed, metrics);
            if (normalized.Proposal is not { } proposal) return normalized;
            var validated = budget is null ? normalized : WallViewportPolicy.Validate(proposal, budget, metrics);
            if (validated.Proposal is not null) return validated;
        }
        return Reject(WallRejection.NoSpace, "CONTENT_TOO_LARGE");
    }

    private static WallLayoutResult TryPlacePackageGroups(WallDocument current, StartGroup[] incoming,
        WallMetrics metrics, WallViewportBudget? budget, ImportInsertionPosition position, int maxRows, int maxColumns)
    {
        var updates = incoming.ToDictionary(group => group.Id, StringComparer.Ordinal);
        var lanes = current.Lanes.Select(lane => lane with
        {
            Groups = lane.Groups.Select(group => updates.Remove(group.Id, out var updated) ? updated : group).ToArray(),
        }).ToArray();
        var added = incoming.Where(group => updates.ContainsKey(group.Id)).ToArray();
        var firstOccupied = Array.FindIndex(lanes, lane => lane.Groups.Count != 0);
        var lastOccupied = Array.FindLastIndex(lanes, lane => lane.Groups.Count != 0);
        var height = WallLayoutEngine.MeasureSource(lanes, metrics).WallHeight;
        var locked = current.Settings.LayoutLocked;
        var expanded = current.Settings.ExpandColumns;
        var contentColumns = maxColumns - (expanded ? 1 : 0);
        if (locked) maxRows = Math.Min(maxRows, current.Rows);
        if (current.Rows > maxRows || height > metrics.WallHeight(maxRows))
            return Reject(WallRejection.NoSpace, locked ? "LAYOUT_LOCKED_NO_SPACE" : "PACKAGE_HEIGHT_LIMIT");

        var widthLimited = false;
        for (var rows = current.Rows; rows <= maxRows; rows++)
        {
            var rowHeight = metrics.WallHeight(rows);
            if (height > rowHeight) continue;
            var packed = added.Length == 0 ? Array.Empty<WallLane>() :
                WallLayoutEngine.ArrangeSourceLanes(added, rowHeight, metrics, allowOversizedGroups: false);
            if (packed is null) continue;
            var atHead = position == ImportInsertionPosition.Head && firstOccupied >= 0;
            var shift = atHead ? Math.Max(0, packed.Count - firstOccupied) : 0;
            var start = packed.Count == 0 ? 0 : atHead ? Math.Max(0, firstOccupied - packed.Count) : lastOccupied + 1;
            if (locked && shift != 0) continue;
            var occupied = Math.Max(lastOccupied + 1 + shift, start + packed.Count);
            var count = Math.Max(lanes.Length + shift, start + packed.Count);
            if (occupied > contentColumns || count > MaxColumns || !expanded && count > maxColumns ||
                locked && !expanded && count > current.Columns)
            {
                widthLimited = true;
                continue;
            }

            var placed = new WallLane[count];
            Array.Fill(placed, new WallLane { Groups = [] });
            Array.Copy(lanes, 0, placed, shift, lanes.Length);
            for (var index = 0; index < packed.Count; index++)
                placed[start + index] = packed[index];
            // Expansion normally compacts empty interior lanes. A package may use an empty
            // edge lane, but expanding must not relocate old groups through unrelated holes.
            if (expanded && !locked && placed.Take(occupied).Any(lane => lane.Groups.Count == 0) &&
                !placed.SelectMany(lane => lane.Groups).Any(group => group.ReservedRows is not null))
                return Reject(WallRejection.NoSpace, "PACKAGE_WIDTH_LIMIT");

            var candidate = current with { Generation = current.Generation + 1, Columns = count, Rows = rows, Lanes = placed };
            var normalized = WallLayoutEngine.NormalizeExpanded(candidate, metrics);
            if (normalized.Proposal is not { } proposal) return normalized;
            if (WallLayoutEngine.ValidateLockedChange(current, proposal.Document, metrics) is { } lockError)
                return Reject(WallRejection.InvalidAction, lockError);
            return budget is null ? normalized : WallViewportPolicy.Validate(proposal, budget, metrics);
        }
        return Reject(WallRejection.NoSpace, widthLimited ? "PACKAGE_WIDTH_LIMIT" :
            locked ? "LAYOUT_LOCKED_NO_SPACE" : "PACKAGE_HEIGHT_LIMIT");
    }


    private static WallLayoutResult TryProtected(WallDocument current, StartGroup[] incoming, WallMetrics metrics,
        WallViewportBudget? budget, ImportInsertionPosition position)
    {
        var width = budget?.MaxWidthDip ?? metrics.WallWidth(MaxColumns);
        var height = Math.Min(budget?.MaxHeightDip ?? metrics.WallHeight(current.Rows), metrics.WallHeight(current.Rows));
        var lanes = current.Lanes.Select(lane => lane with { Groups = lane.Groups.ToArray() }).ToList();
        var pending = new List<StartGroup>();
        var used = current.EnumerateAllMembers().Select(member => member.Id)
            .Concat(current.Lanes.SelectMany(lane => lane.Groups).Select(group => group.Id))
            .Concat(incoming.Select(group => group.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in incoming)
        {
            var laneIndex = lanes.FindIndex(lane => lane.Groups.Any(old => old.Id == group.Id));
            if (laneIndex < 0) { pending.Add(group); continue; }
            var groupIndex = lanes[laneIndex].Groups.ToList().FindIndex(old => old.Id == group.Id);
            var old = lanes[laneIndex].Groups[groupIndex];
            var oldMembers = old.Members.ToDictionary(member => member.Id, StringComparer.Ordinal);
            if (old.Members.Any(member => !group.Members.Any(next => next.Id == member.Id)) ||
                group.Members.Any(member => oldMembers.TryGetValue(member.Id, out var prior) &&
                    member.LocalBounds.Size != prior.LocalBounds.Size))
                return Reject(WallRejection.InvalidAction, "LAYOUT_LOCKED");
            var replacement = group with
            {
                ReservedRows = Math.Max(old.ReservedRows ?? 0, group.ReservedRows ?? 0) is var rows && rows > 0 ? rows : null,
                Members = group.Members.Select(member => oldMembers.TryGetValue(member.Id, out var prior)
                    ? member with { LocalBounds = prior.LocalBounds } : member).ToArray(),
            };
            var groups = lanes[laneIndex].Groups.ToArray();
            groups[groupIndex] = replacement;
            var trial = lanes.ToArray(); trial[laneIndex] = lanes[laneIndex] with { Groups = groups };
            if (Finish(trial).Proposal is not null) { lanes = trial.ToList(); continue; }
            var added = replacement.Members.Where(member => !oldMembers.ContainsKey(member.Id)).ToArray();
            if (added.Length == 0) return Reject(WallRejection.NoSpace, "LAYOUT_LOCKED_NO_SPACE");
            groups[groupIndex] = replacement with { Members = replacement.Members.Where(member => oldMembers.ContainsKey(member.Id)).ToArray(),
                ReservedRows = old.ReservedRows };
            lanes[laneIndex] = lanes[laneIndex] with { Groups = groups };
            var placed = new List<WallMember>();
            foreach (var member in added)
            {
                var bounds = MemberLayoutOrder.FindAppendBounds(placed.Select(item => item.LocalBounds).ToArray(),
                    member.LocalBounds.Width, member.LocalBounds.Height, current.Rows);
                if (bounds is null) return Reject(WallRejection.NoSpace, "CONTENT_TOO_LARGE");
                placed.Add(member with { LocalBounds = bounds.Value });
            }
            var suffix = 1;
            var id = "append-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(group.Id))).ToLowerInvariant()[..24];
            var stem = id;
            while (!used.Add(id)) id = stem + "-" + suffix++;
            pending.Add(group with { Id = id, Title = null, Members = placed.ToArray(), ReservedRows = null });
        }
        foreach (var group in pending)
        {
            if (metrics.TopMargin + metrics.Margin + metrics.TitleHeight + metrics.TitleToMembersGap +
                metrics.HeightOf(WallLayoutEngine.EffectiveGroupRows(group)) > height + 1e-8)
                return Reject(WallRejection.NoSpace, "GROUP_TOO_TALL:" + (group.Title ?? group.Id));
            var lastContent = lanes.FindLastIndex(lane => lane.Groups.Count != 0);
            var order = position == ImportInsertionPosition.Head
                ? Enumerable.Range(0, lanes.Count).Where(index => lanes[index].Groups.Count == 0)
                    .Concat(Enumerable.Range(Math.Max(0, lastContent), Math.Max(0, lanes.Count - Math.Max(0, lastContent))))
                : Enumerable.Range(Math.Max(0, lastContent), Math.Max(0, lanes.Count - Math.Max(0, lastContent)));
            var accepted = false;
            foreach (var lane in order.Concat([lanes.Count]).Distinct())
            {
                if (lane >= (current.Settings.ExpandColumns ? MaxColumns - 1 : current.Columns)) continue;
                var trial = lanes.ToList();
                while (trial.Count <= lane) trial.Add(new WallLane { Groups = [] });
                trial[lane] = trial[lane] with { Groups = [.. trial[lane].Groups, group] };
                if (Finish(trial).Proposal is null) continue;
                lanes = trial; accepted = true; break;
            }
            if (!accepted) return Reject(WallRejection.NoSpace, "CONTENT_TOO_LARGE");
        }
        return Finish(lanes);

        WallLayoutResult Finish(IReadOnlyList<WallLane> value)
        {
            var next = current with { Generation = current.Generation + 1, Columns = value.Count, Lanes = value.ToArray() };
            var normalized = WallLayoutEngine.NormalizeExpanded(next, metrics);
            if (normalized.Proposal is not { } proposal) return normalized;
            if (WallLayoutEngine.ValidateLockedChange(current, proposal.Document, metrics) is { } error)
                return Reject(WallRejection.InvalidAction, error);
            return WallViewportPolicy.Validate(proposal, new(width, height, MaxColumns, current.Rows, current.Settings.FullScreen), metrics);
        }
    }

    private static bool TryPack(IReadOnlyList<StartGroup> groups, int laneCount, int rows,
        WallMetrics metrics, out IReadOnlyList<IReadOnlyList<StartGroup>> lanes)
    {
        var packed = WallLayoutEngine.ArrangeSourceLanes(groups, metrics.WallHeight(rows), metrics,
            maximumLanes: laneCount, allowOversizedGroups: false);
        lanes = packed?.Select(lane => lane.Groups).ToArray() ?? [];
        return packed is not null;
    }

    private static WallLayoutResult TryPlaceAtCurrentHeight(WallDocument current, StartGroup[] incoming,
        HashSet<string> involved, int maxRows, int maxColumns, WallMetrics metrics, WallViewportBudget? budget,
        ImportInsertionPosition position)
    {
        var lanes = current.Lanes.Select(lane => lane.Groups.Any(group => involved.Contains(group.Id))
            ? lane with { Groups = lane.Groups.Where(group => !involved.Contains(group.Id)).ToArray(), ImportBatchId = null }
            : lane).ToArray();
        var firstContent = Array.FindIndex(lanes, lane => lane.Groups.Count != 0);
        var lastContent = Array.FindLastIndex(lanes, lane => lane.Groups.Count != 0);
        var untouched = lanes[..(lastContent + 1)];
        var existing = untouched.SelectMany(lane => lane.Groups);
        var atHead = position == ImportInsertionPosition.Head;
        var ordered = (atHead ? incoming.Concat(existing) : existing.Concat(incoming)).ToArray();
        var offsets = new int[untouched.Length + 1];
        var prefixHeights = new double[untouched.Length + 1];
        for (var index = 0; index < untouched.Length; index++)
        {
            offsets[index + 1] = offsets[index] + untouched[index].Groups.Count;
            prefixHeights[index + 1] = Math.Max(prefixHeights[index],
                WallLayoutEngine.RequiredLaneHeight(untouched[index].Groups, metrics));
        }

        var tooTall = ordered.FirstOrDefault(group =>
            WallLayoutEngine.RequiredLaneHeight([group], metrics) > metrics.WallHeight(maxRows));
        if (tooTall is not null)
            return Reject(WallRejection.NoSpace, "GROUP_TOO_TALL:" + (string.IsNullOrWhiteSpace(tooTall.Title) ? tooTall.Id : tooTall.Title));

        var minimumColumns = ImportWidthFloor(current, maxColumns, metrics);
        for (var rows = Math.Min(current.Rows, maxRows); rows <= maxRows; rows++)
        {
            var height = metrics.WallHeight(rows);
            // A greedy packing of the complete sequence needs the fewest lanes at this height.
            // Only search for a smaller reflow once this height is feasible.
            var full = Pack(ordered, maxColumns, height);
            if (full is null) continue;
            var placed = (atHead ? PlaceAtHead(full, height) : PlaceAtTail(full, height)).ToList();
            while (placed.Count < minimumColumns) placed.Add(new WallLane { Groups = [] });
            var result = FinishImport(current, current.Generation, placed, rows, metrics, budget);
            if (result.Success) return result;
        }
        return Reject(WallRejection.NoSpace, "CONTENT_TOO_LARGE");

        WallLane[] PlaceAtHead(WallLane[] full, double height)
        {
            if (firstContent < 0) return full;
            WallLane[]? shifted = null;
            var leastShift = int.MaxValue;
            for (var keepFrom = firstContent; keepFrom < untouched.Length; keepFrom++)
            {
                var available = maxColumns - (untouched.Length - keepFrom);
                if (available < 1) continue;
                var prefix = Pack(ordered[..(incoming.Length + offsets[keepFrom])], available, height,
                    fromEnd: keepFrom != firstContent);
                if (prefix is null) continue;
                var shift = prefix.Length - keepFrom;
                // The first feasible unshifted prefix can only leave original leading blanks.
                if (shift <= 0) return [.. untouched.Take(-shift), .. prefix, .. untouched.Skip(keepFrom)];
                if (shift >= leastShift) continue;
                shifted = [.. prefix, .. untouched.Skip(keepFrom)];
                leastShift = shift;
            }
            return shifted ?? full;
        }

        WallLane[] PlaceAtTail(WallLane[] full, double height)
        {
            for (var start = Math.Min(untouched.Length, maxColumns - 1); start > 0; start--)
            {
                if (prefixHeights[start] > height) continue;
                var suffix = Pack(ordered[offsets[start]..], maxColumns - start, height);
                if (suffix is not null) return [.. untouched.Take(start), .. suffix];
            }
            return full;
        }

        WallLane[]? Pack(StartGroup[] groups, int columns, double height, bool fromEnd = false)
        {
            var packed = WallLayoutEngine.ArrangeSourceLanes(fromEnd ? groups.Reverse().ToArray() : groups,
                height, metrics, columns, allowOversizedGroups: false);
            if (packed is null) return null;
            var occupied = packed.TakeWhile(lane => lane.Groups.Count != 0);
            if (fromEnd) occupied = occupied.Reverse().Select(lane => lane with { Groups = lane.Groups.Reverse().ToArray() });
            return occupied.Select(lane => lane with { ImportBatchId = CommonImportBatchId(lane.Groups) }).ToArray();
        }
    }

    private static int ImportWidthFloor(WallDocument current, int maxColumns, WallMetrics metrics)
    {
        if (current.Settings.FullScreen || current.Settings.ExpandColumns) return 1;
        if (current.Settings.ManualWindowedViewportWidthDip is { } manualWidth)
        {
            var tilesWidth = manualWidth - WallViewportPolicy.FixedWidth(current.Settings.MenuStart);
            return Math.Clamp(WallViewportPolicy.CompleteColumns(tilesWidth, metrics), 1, Math.Min(current.Columns, maxColumns));
        }
        // Older data may have intentionally empty tail lanes without a saved manual width.
        return current.Lanes[^1].Groups.Count == 0 ? Math.Min(current.Columns, maxColumns) : 1;
    }

    private static WallLayoutResult TryTailWithCompaction(WallDocument current, long generation, StartGroup[] incoming,
        HashSet<string> involved, int maxRows, int maxColumns, WallMetrics metrics, WallViewportBudget? budget)
    {
        var original = current.Lanes.Select(lane => lane with
            { Groups = lane.Groups.Where(group => !involved.Contains(group.Id)).ToArray() }).ToArray();
        var lastContent = Array.FindLastIndex(original, lane => lane.Groups.Count != 0);
        var prefix = original.Take(lastContent + 1).ToArray();
        var minimumColumns = Math.Max(1, Math.Min(current.Columns, maxColumns));
        var firstRows = Math.Min(current.Rows, maxRows);

        // Keep all recorded whitespace and every untouched lane first. An empty tail is
        // reused at lastContent + 1; it is never stranded between the two content blocks.
        for (var rows = firstRows; rows <= maxRows; rows++)
        {
            var appended = WallLayoutEngine.ArrangeSourceLanes(incoming, metrics.WallHeight(rows), metrics,
                allowOversizedGroups: false);
            if (appended is null || prefix.Length + appended.Count > maxColumns) continue;
            var lanes = prefix.Concat(appended.Select(lane => lane with
                { ImportBatchId = CommonImportBatchId(lane.Groups) })).ToList();
            var targetCount = Math.Max(minimumColumns, lanes.Count);
            while (lanes.Count < targetCount) lanes.Add(new WallLane { Groups = [] });
            var result = Finish(lanes, rows);
            if (result.Proposal is not null) return result;
        }

        // Reuse the wall's existing compaction order: necessary empty lanes and complete
        // blank member rows first, then (only if that fails) continuous whole-group reflow.
        // Start each probe from immutable input, because the fitter mutates its lane lists.
        var minimalIncoming = WallLayoutEngine.ArrangeSourceLanes(incoming.Select(WallLayoutEngine.CompactGroupFully).ToArray(),
            metrics.WallHeight(maxRows), metrics, allowOversizedGroups: false);
        var incomingById = incoming.ToDictionary(group => group.Id, StringComparer.Ordinal);
        var incomingLanes = minimalIncoming?.Select(lane => lane.Groups.Select(group => incomingById[group.Id]).ToList())
            ?? incoming.Select(group => new List<StartGroup> { group });
        var seed = prefix.Select(lane => lane.Groups.ToList()).Concat(incomingLanes).ToArray();
        foreach (var reflow in new[] { false, true })
        for (var columns = minimumColumns; columns <= maxColumns; columns++)
        {
            var lanes = seed.Select(lane => lane.ToList()).ToList();
            if (WallLayoutEngine.FitImportLanes(lanes, columns, maxRows, current.Rows, metrics, reflow) != WallRejection.None)
                continue;
            var result = Finish(lanes.Select(lane => new WallLane
                { Groups = lane.ToArray(), ImportBatchId = CommonImportBatchId(lane) }).ToList(), maxRows, trimHeight: true);
            if (result.Proposal is not null) return result;
        }
        var tooTall = prefix.SelectMany(lane => lane.Groups).Concat(incoming).FirstOrDefault(group =>
            metrics.TopMargin + metrics.Margin + metrics.TitleHeight + metrics.TitleToMembersGap +
            metrics.HeightOf(WallLayoutEngine.MinimumGroupRows(group)) > metrics.WallHeight(maxRows) + 1e-8);
        return tooTall is not null
            ? Reject(WallRejection.NoSpace, "GROUP_TOO_TALL:" + (string.IsNullOrWhiteSpace(tooTall.Title) ? tooTall.Id : tooTall.Title))
            : Reject(WallRejection.NoSpace, "CONTENT_TOO_LARGE");

        WallLayoutResult Finish(IReadOnlyList<WallLane> lanes, int rows, bool trimHeight = false)
        {
            if (trimHeight)
            {
                // Fit against the full physical budget to remove only necessary gaps,
                // then discard newly introduced wall-tail whitespace without moving content.
                var required = WallLayoutEngine.MeasureSource(lanes, metrics).WallHeight;
                rows = Math.Max(firstRows, (int)Math.Ceiling(
                    (required - metrics.TopMargin - metrics.Margin + metrics.Gap) / metrics.Pitch - 1e-10));
            }
            return FinishImport(current, generation, lanes, rows, metrics, budget);
        }
    }

    private static WallLayoutResult FinishImport(WallDocument current, long generation, IReadOnlyList<WallLane> lanes,
        int rows, WallMetrics metrics, WallViewportBudget? budget)
    {
        var proposal = current with { Generation = generation + 1, Columns = lanes.Count, Rows = rows, Lanes = lanes.ToArray() };
        var result = WallLayoutEngine.NormalizeExpanded(proposal, metrics);
        return result.Proposal is { } normalized && budget is not null
            ? WallViewportPolicy.Validate(normalized, budget, metrics) : result;
    }

    private static string? CommonImportBatchId(IReadOnlyList<StartGroup> groups)
    {
        var batchId = groups.FirstOrDefault()?.ImportSource?.BatchId;
        return !string.IsNullOrWhiteSpace(batchId) && groups.All(group => group.ImportSource?.BatchId == batchId) ? batchId : null;
    }

    private static WallLayoutResult Reject(WallRejection rejection, string? detail = null) => new(null, rejection, detail);
}
