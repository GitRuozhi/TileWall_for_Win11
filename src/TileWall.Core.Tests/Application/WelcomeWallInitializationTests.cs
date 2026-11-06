using System.Text;
using System.Security.Cryptography;
using TileWall.Core.Application;
using TileWall.Core.Configuration;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Application;

public sealed class WelcomeWallInitializationTests
{
    private static (WallSession Session, InMemoryFileStore Files, string Root) Harness()
    {
        var files = new InMemoryFileStore();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "welcome-session-" + Guid.NewGuid().ToString("N")));
        return (new(root, files, new FakeLinkFileService(files)), files, root);
    }

    [Fact]
    public void OnlySuccessfulFreshInitializationReportsTheTransientFact()
    {
        var (session, files, root) = Harness();
        var first = session.Open(1, 8);
        Assert.Equal(WallOpenStatus.Loaded, first.Status);
        Assert.True(first.InitializedFresh);
        Assert.Same(session.Current, first.Document);
        var persisted = Encoding.UTF8.GetString(files.ReadAllBytes(Path.Combine(root, "state.json")));
        Assert.DoesNotContain("initializedFresh", persisted, StringComparison.OrdinalIgnoreCase);
        var reopened = session.Open(1, 8);
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.False(reopened.InitializedFresh);
        Assert.Empty(session.Current.EnumerateAllMembers());
    }

    [Fact]
    public void FailedInitializationAndUnsupportedDataCannotReportFreshSuccess()
    {
        var (session, files, root) = Harness();
        files.InjectedFault = FaultPoint.BeforeMove;
        var failed = session.Open(1, 8);
        Assert.Equal(WallOpenStatus.OpenFailed, failed.Status);
        Assert.False(failed.InitializedFresh);
        files.InjectedFault = FaultPoint.None;
        files.WriteAllBytes(Path.Combine(root, "state.json"), Encoding.UTF8.GetBytes("{\"schemaVersion\":999}"));
        var unsupported = session.Open(1, 8);
        Assert.Equal(WallOpenStatus.UnsupportedFormatBlocked, unsupported.Status);
        Assert.False(unsupported.InitializedFresh);
    }

    [Fact]
    public void RecoveryAndExistingUnpinnedContentCannotBecomeFirstInitialization()
    {
        var (session, files, root) = Harness();
        session.Open(1, 8);
        var retained = new TileMember { Id = "retained", LocalBounds = new(0, 0, 2, 2) };
        var candidate = session.Current with { Generation = 1 };
        var manifest = files.ReadAllBytes(Path.Combine(root, "state.json"));
        var recycle = new RecycleBinSnapshot(session.Current.Generation,
            Convert.ToHexString(SHA256.HashData(manifest)), [retained]);
        Assert.True(session.Commit(new(candidate, WallLayoutEngine.Measure(candidate)) { RecycleBin = recycle }).Committed);
        Assert.False(session.Open(1, 8).InitializedFresh);
        var second = session.Current with { Generation = 2 };
        Assert.True(session.Commit(new(second, WallLayoutEngine.Measure(second))).Committed);
        files.WriteAllBytes(Path.Combine(root, "state.json"),
            Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"generation\":2}"));
        var recovered = session.Open(1, 8);
        Assert.Equal(WallOpenStatus.RecoveredPrevious, recovered.Status);
        Assert.False(recovered.InitializedFresh);
        Assert.Empty(session.Current.InactiveMembers);
        Assert.Equal("retained", Assert.Single(SnapshotTestData.Read(files, root,
            files.ReadAllBytes(Path.Combine(root, "state.json"))).InactiveMembers).Id);
        Assert.Equal(session.Current.Generation,
            UserDataSnapshotJson.ReadManifest(files.ReadAllBytes(Path.Combine(root, "state.json"))).Generation);
    }
}
