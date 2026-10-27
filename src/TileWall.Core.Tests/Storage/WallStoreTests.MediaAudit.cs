using System.Diagnostics;
using System.Text.Json;
using TileWall.Core.Configuration;
using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Storage;

public sealed class WallStoreMediaAuditTests
{
    private const string AuditFolder = "artifacts/validation/v0.2.9/media-audit";

    [Fact]
    public void RetainedJournalMediaReadsAreMeasuredByCommitPhase()
    {
        var validationRoot = Path.Combine(FindRepositoryRoot(), AuditFolder.Replace('/', Path.DirectorySeparatorChar));
        var runsRoot = Path.Combine(validationRoot, "runs");
        Directory.CreateDirectory(runsRoot);
        var dataRoot = Path.Combine(runsRoot, "measure-" + Guid.NewGuid().ToString("N"));
        var io = new MediaReadMeter(new LocalFileStore(), dataRoot);
        var store = new WallStore(io, new FakeLinkFileService(new LocalFileStore()), dataRoot);
        var assets = RepresentativeAssets();

        try
        {
            var initial = WallDocument.Empty(1, 4);
            Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
            var firstDocument = initial with { Generation = 1, RecycleBinEnabled = true, InactiveMembers = MediaMembers(assets) };
            var first = store.Commit(initial, new(0, firstDocument, [], assets));
            Assert.Equal(WallCommitStatus.Committed, first.Status);

            var current = first.CommittedDocument!;
            current = CommitSettings(store, current, !current.Settings.ShowGrid).CommittedDocument!;
            Assert.Single(io.ListDirectories(Path.Combine(dataRoot, "Recovery", "Entries")));

            io.ClearMediaReads();
            var candidate = current with
            {
                Generation = current.Generation + 1,
                Settings = current.Settings with { ShowGrid = !current.Settings.ShowGrid },
            };
            var timings = new WallCommitTimings();
            var measured = store.Commit(current, new(current.Generation, candidate, []), timings: timings);
            Assert.Equal(WallCommitStatus.Committed, measured.Status);

            var phaseReads = io.MediaReads.GroupBy(read => read.Phase, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => new
                {
                    Count = group.Count(),
                    Bytes = group.Sum(read => (long)read.Bytes),
                    ReadWallMs = group.Sum(read => read.ReadWallMs),
                }, StringComparer.Ordinal);
            var expectedPerJournal = assets.Length;
            Assert.Equal(expectedPerJournal, phaseReads["audit"].Count);
            Assert.Equal(expectedPerJournal, phaseReads["plan"].Count);
            Assert.Equal(expectedPerJournal, phaseReads["install"].Count);
            Assert.DoesNotContain(io.MediaReads, read => read.Phase == "unclassified");
            Assert.Equal(assets.Sum(asset => asset.Reference.Length), phaseReads["audit"].Bytes);
            Assert.Equal(assets.Sum(asset => asset.Reference.Length), phaseReads["plan"].Bytes);
            Assert.Equal(assets.Sum(asset => asset.Reference.Length), phaseReads["install"].Bytes);

            var mediaTimings = timings.Snapshot()
                .Where(timing => timing.Stage.StartsWith("store.media.", StringComparison.Ordinal))
                .OrderBy(timing => timing.Stage, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "store.media.audit", "store.media.install", "store.media.plan", "store.media.stage" },
                mediaTimings.Select(timing => timing.Stage).Order(StringComparer.Ordinal).ToArray());
            foreach (var timing in mediaTimings)
                Assert.Equal(1, timing.Count);

            var label = Environment.GetEnvironmentVariable("TILEWALL_MEDIA_AUDIT_LABEL") ?? "latest";
            Assert.Matches("^[a-z0-9-]+$", label);
            var report = new
            {
                Label = label,
                Input = "Deterministic representative-size payloads read and written through LocalFileStore; not image-decoder input.",
                RetainedJournals = 1,
                Media = assets.Select(asset => new { asset.Reference.Sha256, asset.Reference.Extension, asset.Reference.Length }).ToArray(),
                ReadsByPhase = phaseReads,
                MediaPhaseTiming = mediaTimings,
                TotalReads = io.MediaReads.Count,
                TotalBytes = io.MediaReads.Sum(read => (long)read.Bytes),
            };
            File.WriteAllText(Path.Combine(validationRoot, label + ".json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public void MediaTamperedAfterPlanningRefusesCommitAndBlocksRecoveryOpen()
    {
        var validationRoot = Path.Combine(FindRepositoryRoot(), AuditFolder.Replace('/', Path.DirectorySeparatorChar));
        var runsRoot = Path.Combine(validationRoot, "runs");
        Directory.CreateDirectory(runsRoot);
        var dataRoot = Path.Combine(runsRoot, "tamper-" + Guid.NewGuid().ToString("N"));
        var local = new LocalFileStore();
        var io = new TamperingFileStore(local);
        var store = new WallStore(io, new FakeLinkFileService(local), dataRoot);
        var asset = PreparedMediaAsset.FromVerifiedImage(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, ".png");
        try
        {
            var initial = WallDocument.Empty(1, 4);
            Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
            var withMedia = initial with { Generation = 1, RecycleBinEnabled = true, InactiveMembers = MediaMembers([asset]) };
            var added = store.Commit(initial, new(0, withMedia, [], [asset]));
            Assert.Equal(WallCommitStatus.Committed, added.Status);
            var current = added.CommittedDocument!;
            var configPath = Path.Combine(dataRoot, "state.json");
            var configBefore = local.ReadAllBytes(configPath);
            var mediaPath = store.ManagedMediaPath(asset.Reference);
            var mediaBefore = local.ReadAllBytes(mediaPath);
            var tampered = mediaBefore.ToArray();
            tampered[0] ^= 0x80;
            io.Arm(mediaPath, tampered);

            var candidate = current with
            {
                Generation = current.Generation + 1,
                Settings = current.Settings with { ShowGrid = !current.Settings.ShowGrid },
            };
            var rejected = store.Commit(current, new(current.Generation, candidate, []));
            Assert.Equal(WallCommitStatus.Rejected, rejected.Status);
            Assert.True(io.TamperApplied);
            Assert.Equal(configBefore, local.ReadAllBytes(configPath));
            Assert.Equal(tampered, local.ReadAllBytes(mediaPath));

            var reopened = store.Open();
            Assert.Equal(WallOpenStatus.RecoveryBlocked, reopened.Status);
            Assert.Equal(configBefore, local.ReadAllBytes(configPath));
            Assert.Equal(tampered, local.ReadAllBytes(mediaPath));
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static WallCommitResult CommitSettings(WallStore store, WallDocument current, bool showGrid)
    {
        var candidate = current with
        {
            Generation = current.Generation + 1,
            Settings = current.Settings with { ShowGrid = showGrid },
        };
        var result = store.Commit(current, new(current.Generation, candidate, []));
        Assert.Equal(WallCommitStatus.Committed, result.Status);
        return result;
    }

    private static PreparedMediaAsset[] RepresentativeAssets() =>
    [
        PreparedMediaAsset.FromVerifiedImage(Payload(256 * 1024, 17), ".png"),
        PreparedMediaAsset.FromVerifiedImage(Payload(1024 * 1024, 41), ".jpg"),
        PreparedMediaAsset.FromVerifiedImage(Payload(4 * 1024 * 1024, 73), ".png"),
    ];

    private static byte[] Payload(int length, uint seed)
    {
        var bytes = new byte[length];
        var state = seed;
        for (var index = 0; index < bytes.Length; index++)
        {
            state = unchecked(1664525 * state + 1013904223);
            bytes[index] = (byte)(state >> 24);
        }
        return bytes;
    }

    private static IReadOnlyList<WallMember> MediaMembers(IReadOnlyList<PreparedMediaAsset> assets) =>
        assets.Select((asset, index) => (WallMember)new TileMember
        {
            Id = "media-" + index,
            LocalBounds = new GridRect(0, 0, 1, 1),
            Images = new MemberImageSource { Kind = MemberImageSourceKind.Single, Media = asset.Reference },
        }).ToArray();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TileWall.sln")) &&
                File.Exists(Path.Combine(directory.FullName, "src", "TileWall.Core.Tests", "TileWall.Core.Tests.csproj")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the TileWall repository root from the test assembly.");
    }

    private sealed class MediaReadMeter(IFileStore inner, string dataRoot) : IFileStore
    {
        private readonly List<MediaRead> _reads = [];
        private readonly string _mediaRoot = Path.Combine(dataRoot, "Media") + Path.DirectorySeparatorChar;
        public IReadOnlyList<MediaRead> MediaReads => _reads;
        public FileStorePathInfo GetPathInfo(string path) => inner.GetPathInfo(path);
        public bool Exists(string path) => inner.Exists(path);
        public byte[] ReadAllBytes(string path)
        {
            var started = Stopwatch.GetTimestamp();
            var bytes = inner.ReadAllBytes(path);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (path.StartsWith(_mediaRoot, StringComparison.OrdinalIgnoreCase))
                _reads.Add(new(PhaseForRead(), bytes.Length, elapsed));
            return bytes;
        }
        public void ClearMediaReads() => _reads.Clear();
        public void WriteAllBytes(string path, byte[] bytes) => inner.WriteAllBytes(path, bytes);
        public void Move(string src, string dst, bool overwrite) => inner.Move(src, dst, overwrite);
        public void Copy(string src, string dst, bool overwrite) => inner.Copy(src, dst, overwrite);
        public void Delete(string path) => inner.Delete(path);
        public void MoveDirectory(string source, string destination) => inner.MoveDirectory(source, destination);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public IReadOnlyList<string> ListDirectories(string path) => inner.ListDirectories(path);
        public IReadOnlyList<string> ListFiles(string path) => inner.ListFiles(path);
        public void DeleteDirectory(string path) => inner.DeleteDirectory(path);

        private static string PhaseForRead()
        {
            var phaseMethods = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AuditRecoveryMaterials"] = "audit",
                ["PlanMedia"] = "plan",
                ["StageMedia"] = "stage",
                ["InstallMedia"] = "install",
            };
            foreach (var frame in new StackTrace(2, false).GetFrames() ?? [])
            {
                var method = frame.GetMethod();
                if (method?.DeclaringType?.FullName != "TileWall.Core.Storage.WallStore") continue;
                if (phaseMethods.TryGetValue(method.Name, out var phase)) return phase;
            }
            return "unclassified";
        }
    }

    private sealed record MediaRead(string Phase, int Bytes, double ReadWallMs);

    private sealed class TamperingFileStore(IFileStore inner) : IFileStore
    {
        private string? _mediaPath;
        private byte[]? _tamperedBytes;
        private bool _armed;
        public bool TamperApplied { get; private set; }
        public void Arm(string mediaPath, byte[] tamperedBytes)
        {
            _mediaPath = mediaPath;
            _tamperedBytes = tamperedBytes.ToArray();
            _armed = true;
        }
        public FileStorePathInfo GetPathInfo(string path) => inner.GetPathInfo(path);
        public bool Exists(string path) => inner.Exists(path);
        public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);
        public void WriteAllBytes(string path, byte[] bytes)
        {
            inner.WriteAllBytes(path, bytes);
            if (!_armed || TamperApplied || !path.EndsWith(Path.DirectorySeparatorChar + "state.tmp", StringComparison.OrdinalIgnoreCase)) return;
            inner.WriteAllBytes(_mediaPath!, _tamperedBytes!);
            TamperApplied = true;
        }
        public void Move(string src, string dst, bool overwrite) => inner.Move(src, dst, overwrite);
        public void Copy(string src, string dst, bool overwrite) => inner.Copy(src, dst, overwrite);
        public void Delete(string path) => inner.Delete(path);
        public void MoveDirectory(string source, string destination) => inner.MoveDirectory(source, destination);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public IReadOnlyList<string> ListDirectories(string path) => inner.ListDirectories(path);
        public IReadOnlyList<string> ListFiles(string path) => inner.ListFiles(path);
        public void DeleteDirectory(string path) => inner.DeleteDirectory(path);
    }
}
