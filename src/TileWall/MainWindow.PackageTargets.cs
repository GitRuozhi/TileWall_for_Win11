using System.Runtime.InteropServices;
using TileWall.Core.Entries;
using TileWall.Core.Import;
using TileWall.Shell.Interop;

namespace TileWall;

public sealed partial class MainWindow
{
    private Task<IReadOnlySet<string>> ConfirmWallPackageApplicationsAsync(IReadOnlyList<ImportSourceCandidate> candidates,
        IEnumerable<LaunchIdentity> observedEntries, CancellationToken token)
    {
        var sharedIds = candidates.Select(candidate => candidate.Identity?.AppsFolderApplicationId).OfType<string>()
            .Intersect(observedEntries.Select(identity => identity.AppsFolderApplicationId).OfType<string>(), StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sharedIds.Count == 0) return Task.FromResult<IReadOnlySet<string>>(registered);
        var catalog = NativeImports;
        return Task.Run<IReadOnlySet<string>>(() =>
        {
            IReadOnlyDictionary<string, ShellItemCatalog.Item> current;
            try { current = ShellItemCatalog.FindApplicationsById(sharedIds, token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or COMException)
            { return registered; }

            foreach (var (id, item) in current)
            {
                token.ThrowIfCancellationRequested();
                // Only the native enumeration result is observed through Shell.
                var application = catalog.ObservePinnedApplication(item, token);
                if (application is { Error: null, PackageFullName.Length: > 0, PackageInstallPath.Length: > 0 } &&
                    string.Equals(application.NativeTarget?.AppsFolderApplicationId, id, StringComparison.OrdinalIgnoreCase))
                    registered.Add(id);
            }
            return registered;
        }, token);
    }
}
