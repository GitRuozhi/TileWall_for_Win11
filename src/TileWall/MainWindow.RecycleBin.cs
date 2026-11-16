using System.Security.Cryptography;
using Microsoft.UI.Xaml.Controls;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using TileWall.Resources;
using TileWall.Views;

namespace TileWall;

public sealed partial class MainWindow
{
    private async void BeginUnpinMembers(IReadOnlyList<string> ids)
    {
        if (PersistenceInteractionBlocked || _modal || _viewBlocked || !_hasDocument) return;
        try
        {
            var selection = ids.ToArray();
            var needsConfirmation = false;
            await QueueUserMutationAsync(async () =>
                needsConfirmation = await UnpinMembersAsync(selection, confirmOverflow: false));
            if (!needsConfirmation || _modal || ShutdownStarted || _windowClosing) return;
            // Entering a modal waits for the user queue, so never do it from
            // inside that same queue. Reobserve after admission and confirmation.
            try
            {
                if (!await BeginModalAsync()) return;
                await UnpinMembersAsync(selection, confirmOverflow: true);
            }
            finally { EndModal(); }
        }
        catch (Exception ex) { if (!_windowClosing) Status(string.Format(ProductStrings.Get("Common.SaveFailed"), ex.Message)); }
    }

    private async Task<bool> UnpinMembersAsync(IReadOnlyList<string> ids, bool confirmOverflow)
    {
        var timing = Environment.GetEnvironmentVariable("TILEWALL_PERF") == "1" ? System.Diagnostics.Stopwatch.StartNew() : null;
        void Trace(string stage) { if (timing is not null) Core.Shell.AppLog.Info("UnpinTiming", $"stage={stage} elapsedMs={timing.Elapsed.TotalMilliseconds:F3}"); }
        var before = Document;
        var recycle = await _session.ReadRecycleBinAsync(before.Generation, CancellationToken.None);
        Trace("recycle-read");
        if (!ReferenceEquals(before, Document) || PersistenceInteractionBlocked || ShutdownStarted) return false;
        var overflow = RecycleBinRules.Overflow(before, recycle, ids);
        if (overflow.Count != 0)
        {
            if (!confirmOverflow) return true;
            var body = string.Format(ProductStrings.Get("Recycle.OverflowConfirm"), overflow.Count,
                RecycleBinRules.Capacity) + "\n\n" + MemberList(overflow) + "\n\n" +
                ProductStrings.Get("Recycle.UnpinBatch") + "\n" + MemberList(RecycleBinRules.OrderedActive(before, ids)) +
                "\n\n" + ProductStrings.Get("Recycle.PhysicalRetention");
            var confirmation = new RecycleBinConfirmationDialog(DialogOwner, ProductStrings.Get("Recycle.Title"), body);
            ActiveDialog = confirmation;
            if (!await confirmation.ShowAsync()) return false;
        }
        if (!ReferenceEquals(before, Document) || PersistenceInteractionBlocked || ShutdownStarted) return false;
        var result = RecycleBinRules.Propose(before, recycle, new UnpinMembersAction(before.Generation, ids, ConfirmOverflow: true),
            CurrentViewportBudget.MaxWidthDip, CurrentViewportBudget.MaxHeightDip, _metrics);
        if (result.Proposal is not { } proposal) { Status(RecycleFailure(result)); return false; }
        var preview = !_modal;
        try
        {
            if (preview)
            {
                // This is a reversible presentation, just like a released drag.
                // Session.Current remains the only owner of committed state.
                Surface.Render(proposal.Document, proposal.Geometry);
                ApplyViewport(proposal.Document, proposal.Geometry, animateMode: false);
                Trace("removal-preview");
            }
            var error = await CommitProposalAsync(proposal, [],
                settle: preview ? error => SettleReleasePersistence(error) : null, allowInteraction: preview);
            Trace(error is null ? "committed" : "failed");
            if (error is not null) Status(error);
            else if (proposal.RecycleBin is { } updated && updated.Members.Count >= RecycleBinRules.WarningCount)
                Status(string.Format(ProductStrings.Get("Recycle.NearlyFull"), updated.Members.Count, RecycleBinRules.Capacity));
        }
        catch
        {
            if (preview && !_viewBlocked && !_windowClosing) { Surface.Render(Document); ApplyViewport(animateMode: false); }
            throw;
        }
        return false;
    }

    private async Task<string?> RestoreRecycledMembersAsync(IReadOnlyList<string> ids,
        long generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (PersistenceInteractionBlocked || _viewBlocked || !_hasDocument || ShutdownStarted || Document.Generation != generation)
            return ProductStrings.Get("Recycle.Changed");
        var before = Document;
        var recycle = await _session.ReadRecycleBinAsync(generation, token);
        if (!ReferenceEquals(before, Document) || PersistenceInteractionBlocked || ShutdownStarted)
            return ProductStrings.Get("Recycle.Changed");
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        var observed = await (_storageExecutor ?? throw new InvalidOperationException()).ExecuteAsync(() =>
            {
                var files = new LocalFileStore();
                var guarded = new GuardedWallFiles(files, new WallStoragePaths(files, _session.DataRoot));
                var identities = ImportIdentities;
                var keys = new Dictionary<string, string>(StringComparer.Ordinal);
                var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var member in recycle.Members.Where(member => selected.Contains(member.Id)))
                {
                    token.ThrowIfCancellationRequested();
                    if (member.Entry is not { } entry) continue;
                    var path = _session.ManagedEntryPath(member) ?? throw new IOException("Invalid formal entry path.");
                    using var stream = guarded.OpenReadProtected(path);
                    if (stream.Length is < 1 or > EntryObservation.MaximumBytes) throw new InvalidDataException("Invalid formal entry length.");
                    var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
                    var fields = entry.Kind == ManagedEntryKind.Lnk ? _session.LinkFiles.Read(path) : null;
                    keys[member.Id] = EntryObservation.Observe(identities, entry.Kind, path, bytes, fields).Key;
                    fingerprints[member.Id] = Convert.ToHexString(SHA256.HashData(bytes));
                }
                return (Keys: keys, Hashes: fingerprints);
            });
        var action = new RestoreRecycledMembersAction(generation, ids, "restored-" + Guid.NewGuid().ToString("N"),
            ProductStrings.Get("Recycle.RestoredGroup"), observed.Keys);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(before, Document) || PersistenceInteractionBlocked || ShutdownStarted)
            return ProductStrings.Get("Recycle.Changed");
        var result = RecycleBinRules.Propose(before, recycle, action, CurrentViewportBudget.MaxWidthDip,
            CurrentViewportBudget.MaxHeightDip, _metrics);
        if (result.Proposal is not { } proposal) return RecycleFailure(result);
        var error = await CommitProposalAsync(proposal, [], expectedEntrySha256: observed.Hashes);
        if (error is null) TransientStatus(string.Format(ProductStrings.Get("Recycle.RestoredCount"), ids.Count));
        return error;
    }

    private string RecycleFailure(WallLayoutResult result) => result.Detail switch
    {
        "RECYCLE_DUPLICATE_LAUNCH" => ProductStrings.Get("Recycle.Duplicate"),
        "RECYCLE_IDENTITY_UNAVAILABLE" => ProductStrings.Get("Recycle.AssetUnavailable"),
        _ => result.Rejection == WallRejection.StaleGeneration ? ProductStrings.Get("Recycle.Changed") : ViewportFailure(result),
    };

    private static string MemberList(IEnumerable<WallMember> members) => string.Join("\n", members.Select(member =>
        "• " + RecycleBinPresentation.Title(member)));
}
