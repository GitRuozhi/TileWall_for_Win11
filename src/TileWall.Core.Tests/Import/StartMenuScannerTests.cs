using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Import;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Import;

/// <summary>
/// T-SCAN（M8 设计 §5.2）：StartMenuScanner + InMemoryFileStore 目录树——递归、.lnk/.url 过滤、
/// 全路径去重、同名不同路径保留（C06 反例：不按名称/EXE 合并）、不可读目录跳过计数、
/// NotImportable 标记、流水线零 ReadAllBytes（夹具计数断言「不读目标」）。
/// </summary>
public sealed class StartMenuScannerTests
{
    /// <summary>包装夹具：对含标记的目录抛 IOException（模拟不可读）+ ReadAllBytes 调用计数。</summary>
    private sealed class ProbeFileStore(IFileStore inner) : IFileStore
    {
        public const string UnreadableMarker = "unreadable";

        public int ReadAllBytesCalls { get; private set; }

        public bool Exists(string path) => inner.Exists(path);

        public byte[] ReadAllBytes(string path)
        {
            ReadAllBytesCalls++;
            return inner.ReadAllBytes(path);
        }

        public void WriteAllBytes(string path, byte[] bytes) => inner.WriteAllBytes(path, bytes);

        public void Move(string src, string dst, bool overwrite) => inner.Move(src, dst, overwrite);

        public void Copy(string src, string dst, bool overwrite) => inner.Copy(src, dst, overwrite);

        public void Delete(string path) => inner.Delete(path);

        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public IReadOnlyList<string> ListDirectories(string directoryPath)
        {
            if (directoryPath.Contains(UnreadableMarker, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"注入：目录不可读 {directoryPath}");
            }

            return inner.ListDirectories(directoryPath);
        }

        public IReadOnlyList<string> ListFiles(string directoryPath)
        {
            if (directoryPath.Contains(UnreadableMarker, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"注入：目录不可读 {directoryPath}");
            }

            return inner.ListFiles(directoryPath);
        }

        public void DeleteDirectory(string directoryPath) => inner.DeleteDirectory(directoryPath);
    }

    private static string Dir(params string[] segments) => Path.Combine([.. segments]);

    /// <summary>构造两根目录树：user 根（含子目录与不可读目录）、common 根（含同名候选与保留名候选）。
    /// InMemoryFileStore 的目录存在性需显式 CreateDirectory（与真实文件系统的目录项等价）。</summary>
    private (ProbeFileStore Files, string UserRoot, string CommonRoot) BuildTree()
    {
        var files = new ProbeFileStore(new InMemoryFileStore());
        var userRoot = Dir("C:", "sm", "user", "Programs");
        var commonRoot = Dir("C:", "sm", "common", "Programs");
        var sub = Dir(userRoot, "Sub");
        var deeper = Dir(sub, "Deeper");
        var unreadable = Dir(userRoot, "unreadable");
        var bytes = new byte[] { 0x4C }; // 内容无关紧要：流水线零读取
        foreach (var directory in new[] { userRoot, sub, deeper, unreadable, commonRoot })
        {
            files.CreateDirectory(directory);
        }

        files.WriteAllBytes(Dir(userRoot, "工具A.lnk"), bytes);
        files.WriteAllBytes(Dir(userRoot, "网页B.url"), bytes);
        files.WriteAllBytes(Dir(userRoot, "readme.txt"), bytes);     // 非 .lnk/.url → 过滤
        files.WriteAllBytes(Dir(sub, "子目录C.lnk"), bytes);  // 递归枚举
        files.WriteAllBytes(Dir(deeper, "深层D.url"), bytes); // 两层递归
        files.WriteAllBytes(Dir(unreadable, "不可读E.lnk"), bytes);  // 目录不可读 → 跳过
        files.WriteAllBytes(Dir(commonRoot, "工具A.lnk"), bytes);    // 同名不同根 → 保留两条（C06）
        files.WriteAllBytes(Dir(commonRoot, "CON.lnk"), bytes);      // 保留设备名 → NotImportable
        return (files, userRoot, commonRoot);
    }

    [Fact]
    public void Enumerate_RecursiveFiltersAndKinds()
    {
        var (files, userRoot, commonRoot) = BuildTree();
        var result = StartMenuScanner.Enumerate(files, [(userRoot, SourceKind.User), (commonRoot, SourceKind.Common)]);

        var importable = result.Candidates.Where(c => c.IsImportable).ToList();
        Assert.Equal(5, importable.Count); // 工具A×2 + 网页B + 子目录C + 深层D（readme.txt/不可读E 已滤除）
        Assert.All(importable, c => Assert.NotEqual(EntryKind.None, c.Kind));
        var subC = importable.Single(c => c.DisplayName == "子目录C");
        Assert.Equal(EntryKind.Lnk, subC.Kind);
        Assert.Equal(SourceKind.User, subC.Source);
        Assert.Equal("Sub", subC.RelativeDir); // 来源根下的相对目录
        var deepD = importable.Single(c => c.DisplayName == "深层D");
        Assert.Equal(EntryKind.Url, deepD.Kind);
        Assert.Equal(Dir("Sub", "Deeper"), deepD.RelativeDir);
    }

    [Fact]
    public void Enumerate_SameNameDifferentPaths_BothKept()
    {
        var (files, userRoot, commonRoot) = BuildTree();
        var result = StartMenuScanner.Enumerate(files, [(userRoot, SourceKind.User), (commonRoot, SourceKind.Common)]);

        var sameNames = result.Candidates.Where(c => c.DisplayName == "工具A").ToList();
        Assert.Equal(2, sameNames.Count); // C06/F-B7：同名候选不合并（去重键与目标无关）
        Assert.Equal(2, sameNames.Select(c => c.Source).Distinct().Count());
    }

    [Fact]
    public void Enumerate_DeduplicatesByNormalizedFullPath()
    {
        var (files, userRoot, _) = BuildTree();
        var result = StartMenuScanner.Enumerate(files, [(userRoot, SourceKind.User), (userRoot, SourceKind.Common)]);

        Assert.Equal(result.Candidates.Count, result.Candidates.Select(c => c.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // 同一根传两次 → 全路径去重，同一快捷方式只出现一次
        Assert.Equal(4, result.Candidates.Count); // 工具A + 网页B + 子目录C + 深层D（不可读目录跳过）
    }

    [Fact]
    public void Enumerate_UnreadableDirectory_SkipsWithCount_AndContinues()
    {
        var (files, userRoot, commonRoot) = BuildTree();
        var result = StartMenuScanner.Enumerate(files, [(userRoot, SourceKind.User), (commonRoot, SourceKind.Common)]);

        Assert.Equal(1, result.SkippedDirectories); // 「读取失败 ≠ 候选丢失的沉默」
        Assert.DoesNotContain(result.Candidates, c => c.DisplayName == "不可读E");
        Assert.Contains(result.Candidates, c => c.DisplayName == "工具A"); // 其余目录照常
    }

    [Fact]
    public void Enumerate_ReservedName_MarkedNotImportableNotSilentlyDropped()
    {
        var (files, userRoot, commonRoot) = BuildTree();
        var result = StartMenuScanner.Enumerate(files, [(userRoot, SourceKind.User), (commonRoot, SourceKind.Common)]);

        var reserved = Assert.Single(result.Candidates, c => c.DisplayName == "CON");
        Assert.False(reserved.IsImportable);
        Assert.Contains(EntryNames.NameReserved, reserved.NotImportableReasons); // 保留显示、禁止勾选（不静默剔除）
    }

    [Fact]
    public void Enumerate_NeverReadsCandidateContent()
    {
        var (files, userRoot, commonRoot) = BuildTree();
        _ = StartMenuScanner.Enumerate(files, [(userRoot, SourceKind.User), (commonRoot, SourceKind.Common)]);
        Assert.Equal(0, files.ReadAllBytesCalls); // 流水线零 ReadAllBytes：不读目标、不执行（§5.2）
    }

    [Fact]
    public void Enumerate_NullRoot_IsSkipped()
    {
        var (files, userRoot, _) = BuildTree();
        var result = StartMenuScanner.Enumerate(files, [(userRoot, SourceKind.User), (string.Empty, SourceKind.Common)]);
        Assert.All(result.Candidates, c => Assert.Equal(SourceKind.User, c.Source)); // 查询失败的来源整体跳过
    }

    [Fact]
    public void Enumerate_MissingRoot_YieldsEmpty()
    {
        var files = new InMemoryFileStore();
        var result = StartMenuScanner.Enumerate(files, [(Dir("C:", "does", "not", "exist"), SourceKind.User)]);
        Assert.Empty(result.Candidates);
        Assert.Equal(0, result.SkippedDirectories); // 目录不存在 → 空列表（IFileStore 契约），非「不可读」
    }
}
