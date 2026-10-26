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
            var before = Document;
            var overflow = RecycleBinRules.Overflow(before, ids);
            if (overflow.Count != 0)
            {
                if (!await BeginModalAsync()) return;
                try
                {
                    var body = string.Format(ProductStrings.Get("Recycle.OverflowConfirm"), overflow.Count,
                        RecycleBinRules.Capacity) + "\n\n" + MemberList(overflow) + "\n\n" +
                        ProductStrings.Get("Recycle.UnpinBatch") + "\n" + MemberList(RecycleBinRules.OrderedActive(before, ids)) +
                        "\n\n" + ProductStrings.Get("Recycle.PhysicalRetention");
                    var confirmation = new RecycleBinConfirmationDialog(DialogOwner, ProductStrings.Get("Recycle.Title"), body);
                    ActiveDialog = confirmation;
                    if (!await confirmation.ShowAsync()) return;
                }
                finally { EndModal(); }
            }
            if (!ReferenceEquals(before, Document) || PersistenceInteractionBlocked || ShutdownStarted) return;
            var result = WallLayoutEngine.TryApply(before, new UnpinMembersAction(before.Generation, ids, ConfirmOverflow: true),
                CurrentViewportBudget.MaxWidthDip, CurrentViewportBudget.MaxHeightDip, _metrics);
            if (result.Proposal is not { } proposal) { Status(RecycleFailure(result)); return; }
            var error = await CommitRecycleProposalAsync(before, proposal);
            if (error is not null) Status(error);
            else if (before.RecycleBinEnabled && proposal.Document.InactiveMembers.Count >= RecycleBinRules.WarningCount)
                Status(string.Format(ProductStrings.Get("Recycle.NearlyFull"), proposal.Document.InactiveMembers.Count, RecycleBinRules.Capacity));
        }
        catch (Exception ex) { if (!_windowClosing) Status(string.Format(ProductStrings.Get("Common.SaveFailed"), ex.Message)); }
    }

    private async Task<string?> RestoreRecycledMembersAsync(IReadOnlyList<string> ids,
        long generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (PersistenceInteractionBlocked || _viewBlocked || !_hasDocument || ShutdownStarted || Document.Generation != generation)
            return ProductStrings.Get("Recycle.Changed");
        var before = Document;
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        var observed = await (_storageExecutor ?? throw new InvalidOperationException()).ExecuteAsync(() =>
            {
                var files = new LocalFileStore();
                var guarded = new GuardedWallFiles(files, new WallStoragePaths(files, _session.DataRoot));
                var identities = ImportIdentities;
                var keys = new Dictionary<string, string>(StringComparer.Ordinal);
                var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var member in before.InactiveMembers.Where(member => selected.Contains(member.Id)))
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
        var result = WallLayoutEngine.TryApply(before, action, CurrentViewportBudget.MaxWidthDip,
            CurrentViewportBudget.MaxHeightDip, _metrics);
        return result.Proposal is { } proposal ? await CommitRecycleProposalAsync(before, proposal, observed.Hashes) : RecycleFailure(result);
    }

    private Task<string?> CommitRecycleProposalAsync(WallDocument before, WallProposal proposal,
        IReadOnlyDictionary<string, string>? hashes = null)
    {
        var nextIds = proposal.Document.EnumerateAllMembers().Select(member => member.Id).ToHashSet(StringComparer.Ordinal);
        var removed = before.EnumerateAllMembers().Where(member => member.Entry is not null && !nextIds.Contains(member.Id))
            .Select(member => (EntryChange)new EntryChange.Remove(member.Id)).ToArray();
        return CommitProposalAsync(proposal, removed, expectedEntrySha256: hashes);
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
