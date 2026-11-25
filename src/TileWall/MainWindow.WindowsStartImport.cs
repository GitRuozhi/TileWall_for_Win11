using TileWall.ApplicationModel;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Import;
using TileWall.Core.Shell;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Resources;
using TileWall.Views;

namespace TileWall;

public sealed partial class MainWindow
{
    internal bool HasPendingWindowsStartInvitation { get; private set; }
    internal event Action? WindowsStartInvitationReady;

    internal async Task OfferWindowsStartImportAsync()
    {
        if (!HasPendingWindowsStartInvitation || !AppWindow.IsVisible || _modal || !CanProcessExternalImport ||
            _windowClosing || ShutdownStarted || _persistenceAdmissionClosed) return;

        HasPendingWindowsStartInvitation = false;
        WindowsStartImportResult? result = null;
        try
        {
            if (!await BeginModalAsync()) return;
            var dialog = new WindowsStartImportInvitation(DialogOwner, ImportWindowsStartAsync);
            ActiveDialog = dialog;
            result = await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("WindowsStartImport", ex.Message);
            result = new(ProductStrings.Get("WindowsStartImport.Failed"), Failed: true);
        }
        finally { EndModal(); }

        if (result is not null && !_windowClosing && !ShutdownStarted)
        {
            if (result.Failed) Status(result.Message);
            else TransientStatus(result.Message);
        }
    }

    private async Task<WindowsStartImportResult> ImportWindowsStartAsync(CancellationToken token)
    {
        var operation = new ImportOperation(ImportPageSourceKind.WindowsStart, automatic: true);
        WindowsStartImportResult Failure(string key, ImportOutcome outcome, string reason)
        {
            operation.Finish(outcome, reason);
            return new(ProductStrings.Get(key), Failed: true);
        }
        if (!_hasDocument || !_modal || _viewBlocked || PersistenceWriteBlocked)
            return Failure("Import.Blocked", ImportOutcome.Rejected, "Admission");

        var current = Document;
        var budget = ViewportBudget(current.Settings with { ExpandColumns = false });
        void RequireCurrent()
        {
            token.ThrowIfCancellationRequested();
            if (!_modal || _viewBlocked || PersistenceWriteBlocked || !ReferenceEquals(Document, current) ||
                budget != ViewportBudget(current.Settings with { ExpandColumns = false }))
                throw new ImportSourceChangedException("The wall or its viewport changed during Start import.");
        }

        try
        {
            RequireCurrent();
            var reader = new WindowsStartImportSource(NativeImports);
            operation.Stage("reading");
            var snapshot = await reader.ReadAsync(ProductStrings.Get("WindowsStartImport.Group"), token);
            RequireCurrent();
            if (snapshot.TotalCount == 0)
            {
                operation.ObserveSource(0, 0, 0, 0, 0, 0, 0, 0);
                operation.Finish(ImportOutcome.NoChange, "EmptyStart");
                return new(ProductStrings.Get("WindowsStartImport.Empty"));
            }

            var nativePins = snapshot.ToNativeEntries().Where(entry => entry.Error is null)
                .Select(entry => entry.NativeTarget).OfType<NativeShellTargetSnapshot>().Distinct().ToArray();
            var timing = WindowsStartImportSource.StartTiming();
            var existing = await ObserveWindowsStartDestinationAsync(current, nativePins, token);
            WindowsStartImportSource.TraceTiming(timing, "observe-wall");
            RequireCurrent();
            var duplicates = 0;
            var accepted = new Dictionary<string, NativeImportEntry>(StringComparer.Ordinal);
            var groups = new List<WindowsStartImportGroup>();
            foreach (var group in snapshot.Groups)
            {
                var tiles = new List<TileMember>();
                foreach (var entry in group.Entries)
                {
                    if (entry.Observation is not { Error: null, Identity: { } identity } observed) continue;
                    if (!existing.Identities.Add(identity.Key)) { duplicates++; continue; }
                    var id = Guid.NewGuid().ToString("N");
                    tiles.Add(new TileMember
                    {
                        Id = id,
                        LocalBounds = new(0, 0, 2, 2),
                        TargetInput = observed.Kind == NativeImportTarget.Shortcut ? identity.Target : observed.Target,
                        SourceShortcutPath = observed.SourcePath,
                        TargetDisplayName = observed.Name,
                        RotationInterval = MemberPresentationRules.NewRotationInterval,
                    });
                    accepted.Add(id, observed);
                }
                if (tiles.Count != 0) groups.Add(new(group.Title, tiles));
            }

            operation.ObserveSource(snapshot.TotalCount, snapshot.TotalCount - snapshot.UnresolvedCount,
                duplicates + snapshot.UnresolvedCount, accepted.Count, duplicates, 0, 0, snapshot.UnresolvedCount);
            if (accepted.Count != 0)
            {
                var plan = WindowsStartLayoutPlanner.TryPlan(current, groups, budget, _metrics);
                if (plan.Proposal is not { } proposal)
                {
                    operation.Plan(current, null, accepted.Count, plan.Rejection.ToString());
                    operation.Finish(ImportOutcome.Rejected, plan.Rejection.ToString());
                    return new(string.Format(ProductStrings.Get("WindowsStartImport.Capacity"),
                        ProductStrings.WallRejectionReason(plan.Rejection)), Failed: true);
                }
                var inputs = proposal.Document.EnumerateAllMembers()
                    .Where(member => accepted.ContainsKey(member.Id))
                    .Select(member => (Member: member, Input: accepted[member.Id].SourcePath, Inference: (TargetImageSourceResult?)null))
                    .ToArray();
                operation.Plan(current, proposal, accepted.Count, "Ready",
                    smallTiles: inputs[0].Member.LocalBounds.Width == 1);
                operation.Stage("preparation");
                timing = WindowsStartImportSource.StartTiming();
                var prepared = await PrepareAcceptedTargetImagesAsync(inputs, current.Generation, current.Settings, RequireCurrent, token);
                WindowsStartImportSource.TraceTiming(timing, "prepare-images");
                var confirmed = await reader.ConfirmAsync(snapshot, accepted.Values.Select(entry => entry.Key).ToArray(), token);
                RequireCurrent();
                timing = WindowsStartImportSource.StartTiming();
                await RequireTargetSourcesCurrentAsync(prepared.Sources, RequireCurrent, token);
                WindowsStartImportSource.TraceTiming(timing, "confirm-images");
                RequireCurrent();

                var changes = accepted.Select(pair => confirmed[pair.Value.Key].CreateEntryChange(pair.Key)).ToArray();
                timing = WindowsStartImportSource.StartTiming();
                operation.Prepared(inputs.Length);
                var error = await CommitProposalAsync(WithPreparedTargetImages(proposal, prepared.Members), changes,
                    preparedMedia: prepared.Assets, expectedEntrySha256: existing.Hashes, importOperation: operation);
                WindowsStartImportSource.TraceTiming(timing, "commit");
                if (error is not null) return new(error, Failed: true);
            }
            else operation.Finish(ImportOutcome.NoChange, "NoAddableItems");

            var message = string.Format(ProductStrings.Get("WindowsStartImport.Result"),
                accepted.Count, duplicates, snapshot.UnresolvedCount);
            if (duplicates == snapshot.TotalCount)
                message = ProductStrings.Get("WindowsStartImport.AllDuplicates") + " " + message;
            return new(message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            operation.Finish(ImportOutcome.Canceled, "OperationCanceled");
            throw;
        }
        catch (ImportSourceChangedException ex)
        {
            AppLog.Warn("WindowsStartImport", ex.Message);
            return Failure("Import.SourceChanged", ImportOutcome.Rejected, "SourceChanged");
        }
        catch (InvalidDataException ex)
        {
            AppLog.Warn("WindowsStartImport", ex.Message);
            return Failure("WindowsStartImport.Invalid", ImportOutcome.Failed, "InvalidStartLayout");
        }
        catch (Exception ex)
        {
            AppLog.Warn("WindowsStartImport", ex.Message);
            return Failure("WindowsStartImport.Failed", ImportOutcome.Failed, ex.GetType().Name);
        }
    }

    private Task<(HashSet<string> Identities, Dictionary<string, string> Hashes)> ObserveWindowsStartDestinationAsync(
        WallDocument current, IReadOnlyList<NativeShellTargetSnapshot> nativePins, CancellationToken token)
    {
        var executor = _storageExecutor ?? throw new InvalidOperationException("Storage worker is unavailable.");
        return executor.ExecuteAsync(() =>
        {
            var identities = ImportIdentities;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            var files = new LocalFileStore();
            var guarded = new GuardedWallFiles(files, new WallStoragePaths(files, _session.DataRoot));
            foreach (var member in current.EnumerateAllMembers())
            {
                token.ThrowIfCancellationRequested();
                if (member.Entry is not { } entry)
                {
                    if (identities.ResolveMember(member, null) is { } identity) keys.Add(identity.Key);
                    continue;
                }
                var observed = ObserveManagedImportEntry(member, entry, guarded, identities,
                    includeIdList: nativePins.Count != 0 && member.ResourceOrigin != MemberResourceOrigin.Package);
                keys.Add(observed.Identity.Key);
                hashes[member.Id] = observed.Hash;
                if (observed.Fields is not { } fields || observed.IdList.IsEmpty) continue;
                // Only a fresh AppsFolder pin and the existing data-only comparison
                // can authorize a match across raw PIDL presentation bytes.
                foreach (var pin in nativePins)
                    if (!keys.Contains(pin.Identity.Key) && pin.MatchesStoredBackgroundVariant(fields, observed.IdList.Span))
                        keys.Add(pin.Identity.Key);
            }
            return (keys, hashes);
        });
    }
}
