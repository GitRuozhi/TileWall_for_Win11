using TileWall.Core.Entries;
using TileWall.Core.Import;
using TileWall.Core.Shell;
using TileWall.Core.Storage;
using TileWall.Core.Wall;

namespace TileWall.Core.Application;

internal sealed record StartMenuPreparationStrings(string InvalidTarget, string UnsupportedProtocol);

internal sealed record StartMenuPreparationInput(WallDocument Document, StartMenuScanResult Scan,
    string DataRoot, LaunchIdentityService Identities, StartMenuPreparationStrings Strings,
    Func<string, bool> SupportsProtocol);

/// <summary>Frozen import inputs and synchronous, cancellable preparation for the existing STA queue.</summary>
internal static class StartMenuImportPreparation
{
    internal static StartMenuPreparationInput Capture(WallDocument document, StartMenuScanResult scan,
        string dataRoot, LaunchIdentityService identities, StartMenuPreparationStrings strings,
        Func<string, bool> supportsProtocol) => new(WallJson.Freeze(document),
            scan with { Candidates = Array.AsReadOnly(scan.Candidates.Select(candidate => candidate with
            {
                NotImportableReasons = Array.AsReadOnly(candidate.NotImportableReasons.ToArray()),
            }).ToArray()) }, dataRoot, identities, strings, supportsProtocol);

    internal static async Task<IReadOnlyList<StartMenuImportCandidate>> PrepareAsync(
        IWallStorageExecutor executor, StartMenuPreparationInput input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = await executor.ExecuteAsync(() => Prepare(input, token)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return result;
    }

    private static IReadOnlyList<StartMenuImportCandidate> Prepare(StartMenuPreparationInput input, CancellationToken token)
    {
        // A cancelled job can still be sitting behind synchronous COM/storage work.
        token.ThrowIfCancellationRequested();
        var pruned = StartMenuReimport.Prune(input.Document);
        var retained = IdentitySet(pruned.Document, input.DataRoot, input.Identities, token);
        var replacing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in pruned.Removed)
        {
            token.ThrowIfCancellationRequested();
            if (TryIdentity(member, input.DataRoot, input.Identities, token) is { } identity) replacing.Add(identity.Key);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var companies = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var output = new List<StartMenuImportCandidate>();
        foreach (var candidate in input.Scan.Candidates.OrderBy(c => c.Source)
                     .ThenBy(c => c.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            LaunchIdentity? identity = null;
            string? error = null;
            var readFailed = false;
            try
            {
                identity = input.Identities.ResolveInput(candidate.FullPath);
                token.ThrowIfCancellationRequested();
                if (identity is null) { error = input.Strings.InvalidTarget; readFailed = true; }
                else if (identity.IsUrl && !input.SupportsProtocol(identity.Target))
                    error = input.Strings.UnsupportedProtocol;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
            { error = ex.Message; readFailed = true; }
            token.ThrowIfCancellationRequested();
            var duplicate = identity is not null && !seen.Add(identity.Key);
            var company = Company(identity);
            token.ThrowIfCancellationRequested();
            output.Add(new(candidate, new StartMenuBucket("pending", string.Empty, 0), identity,
                identity is null ? StartMenuExclusion.None : StartMenuSelectionRules.Classify(candidate.DisplayName, identity),
                duplicate, identity is not null && retained.Contains(identity.Key),
                identity is not null && replacing.Contains(identity.Key), error, readFailed, company));
        }
        token.ThrowIfCancellationRequested();
        return output.AsReadOnly();

        string? Company(LaunchIdentity? identity)
        {
            token.ThrowIfCancellationRequested();
            if (identity is null || identity.IsUrl || !Path.IsPathFullyQualified(identity.Target)) return null;
            if (companies.TryGetValue(identity.Target, out var cached)) return cached;
            string? company = null;
            try
            {
                var exists = File.Exists(identity.Target);
                token.ThrowIfCancellationRequested();
                if (exists) company = System.Diagnostics.FileVersionInfo.GetVersionInfo(identity.Target).CompanyName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) { }
            token.ThrowIfCancellationRequested();
            companies[identity.Target] = company;
            return company;
        }
    }

    internal static HashSet<string> IdentitySet(WallDocument snapshot, string dataRoot,
        LaunchIdentityService identities, CancellationToken token = default)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in snapshot.Lanes.SelectMany(l => l.Groups).SelectMany(g => g.Members))
        {
            token.ThrowIfCancellationRequested();
            if (TryIdentity(member, dataRoot, identities, token) is { } identity) result.Add(identity.Key);
        }
        return result;
    }

    internal static LaunchIdentity? TryIdentity(WallMember member, string dataRoot,
        LaunchIdentityService identities, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        LaunchIdentity? identity;
        try
        {
            // Match the session's existing managed-path boundary without reading
            // its live state from the worker. Actual reads retain the supplied IO.
            var managed = member.Entry is { } entry && EntryPathGuard.IsEntryRelativePathAllowed(dataRoot, member.Id, entry.RelativePath)
                ? Path.GetFullPath(Path.Combine(dataRoot, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar))) : null;
            identity = identities.ResolveMember(member, managed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Warn("LaunchIdentity", $"member={member.Id} identity unreadable: {ex.Message}");
            identity = null;
        }
        token.ThrowIfCancellationRequested();
        return identity;
    }
}
