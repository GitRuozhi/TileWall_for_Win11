using System.Runtime.CompilerServices;
using TileWall.Core.Configuration;

namespace TileWall.Core.Tests.Fixtures;

/// <summary>配置保存失败注入点（设计 §5.2 / §9.1 T-STORE：原子保存三处失败注入；M4 F-4 复用）。</summary>
public enum FaultPoint
{
    None,

    /// <summary>写 config.json.tmp 之前（Save 第 3 步；M4 起不命中 Staging 路径）。</summary>
    BeforeWriteTemp,

    /// <summary>复制 Recovery/config.prev.json 之前（Save 第 4 步；不命中 Recovery/Entries）。</summary>
    BeforeBackup,

    /// <summary>Move 原子替换之前（Save 第 5 步，唯一提交点；仅命中 config.json 目标）。</summary>
    BeforeMove,
}

/// <summary>
/// 入口协议失败注入点（M4 设计 §9 F-1..F-3/F-8/F-14；按路径模式区分语义步骤，触发一次即解除——
/// 同步回滚/清扫重放不受同一故障二次阻断，与真实「单点损坏后恢复」语义一致）。
/// </summary>
public enum EntryFaultPoint
{
    None,

    /// <summary>暂存区写入前（WriteAllBytes/Copy 目标在 Staging 下：新建 .url / 导入副本 / 改 URL 行）。</summary>
    BeforeStageWrite,

    /// <summary>旧入口备份前（Copy 目标在 Recovery/Entries 下）。</summary>
    BeforeEntryBackup,

    /// <summary>入口生效中途（第 N 次 Staging→Objects 的 Move；N = EntrySwapMoveLimit）。</summary>
    BeforeEntrySwap,

    /// <summary>改名 Move 前（Objects 目录内的就地改名）。</summary>
    BeforeRenameMove,

    /// <summary>入口清理 Move 前（Objects → Recovery/…/removed/）。</summary>
    BeforeRemoveMove,
}

/// <summary>
/// 内存文件存储：路径字符串 → 字节内容的字典（路径不做文件系统语义解释，仅作键；目录仅记录存在性）。
/// M4 扩展：ListDirectories/DeleteDirectory（崩溃清扫枚举与清理）+ EntryFaultPoint 注入
/// （以路径段 \Staging\、\Recovery\Entries\、removed 识别语义步骤——路径一律由 EntryPaths/Path.Combine 构造）。
/// </summary>
public sealed class InMemoryFileStore : IFileStore
{
    private static readonly string StagingMarker = Path.DirectorySeparatorChar + "Staging" + Path.DirectorySeparatorChar;
    private static readonly string ObjectsMarker = Path.DirectorySeparatorChar + "Objects" + Path.DirectorySeparatorChar;
    private static readonly string RecoveryEntriesMarker =
        Path.DirectorySeparatorChar + "Recovery" + Path.DirectorySeparatorChar + "Entries" + Path.DirectorySeparatorChar;
    private static readonly string RemovedMarker = Path.DirectorySeparatorChar + "removed" + Path.DirectorySeparatorChar;

    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private EntryFaultPoint _entryFault = EntryFaultPoint.None;
    private int _entrySwapMoves;

    public FaultPoint InjectedFault { get; set; } = FaultPoint.None;

    public EntryFaultPoint EntryFault
    {
        get => _entryFault;
        set
        {
            _entryFault = value;
            _entrySwapMoves = 0;
        }
    }

    /// <summary>BeforeEntrySwap 在第几次暂存→正式区移动时触发（F-3「replace 第 2/3 个文件处」）。</summary>
    public int EntrySwapMoveLimit { get; set; } = 1;

    public bool Exists(string path) => _files.ContainsKey(path);

    public byte[] ReadAllBytes(string path)
    {
        if (!_files.TryGetValue(path, out var bytes))
        {
            throw new FileNotFoundException($"[内存存储] 文件不存在：{path}", path);
        }

        return bytes;
    }

    public void WriteAllBytes(string path, byte[] bytes)
    {
        if (InjectedFault == FaultPoint.BeforeWriteTemp && !path.Contains(StagingMarker, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("注入的 IO 失败：BeforeWriteTemp");
        }

        TripEntryFault(EntryFaultPoint.BeforeStageWrite, path.Contains(StagingMarker, StringComparison.OrdinalIgnoreCase), $"WriteAllBytes {path}");
        _files[path] = [.. bytes];
    }

    public void Move(string src, string dst, bool overwrite)
    {
        if (InjectedFault == FaultPoint.BeforeMove && dst.EndsWith(Path.DirectorySeparatorChar + ConfigStore.ConfigFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("注入的 IO 失败：BeforeMove");
        }

        if (src.Contains(StagingMarker, StringComparison.OrdinalIgnoreCase))
        {
            // 暂存 → 正式区：入口生效步骤（可注入「中途失败」）
            _entrySwapMoves++;
            if (_entryFault == EntryFaultPoint.BeforeEntrySwap && _entrySwapMoves >= EntrySwapMoveLimit)
            {
                _entryFault = EntryFaultPoint.None;
                var at = _entrySwapMoves;
                _entrySwapMoves = 0;
                throw new IOException($"注入的 IO 失败：BeforeEntrySwap（第 {at} 次 Staging→Objects 移动）");
            }
        }
        else if (src.Contains(ObjectsMarker, StringComparison.OrdinalIgnoreCase)
                 && dst.Contains(ObjectsMarker, StringComparison.OrdinalIgnoreCase)
                 && Path.GetDirectoryName(src) == Path.GetDirectoryName(dst))
        {
            TripEntryFault(EntryFaultPoint.BeforeRenameMove, condition: true, $"Rename {src} → {dst}");
        }
        else if (dst.Contains(RecoveryEntriesMarker, StringComparison.OrdinalIgnoreCase)
                 && dst.Contains(RemovedMarker, StringComparison.OrdinalIgnoreCase))
        {
            TripEntryFault(EntryFaultPoint.BeforeRemoveMove, condition: true, $"Remove {src} → {dst}");
        }

        if (!_files.TryGetValue(src, out var bytes))
        {
            throw new FileNotFoundException($"[内存存储] 源文件不存在：{src}", src);
        }

        if (!overwrite && _files.ContainsKey(dst))
        {
            throw new IOException($"[内存存储] 目标已存在：{dst}");
        }

        _files.Remove(src);
        _files[dst] = bytes;
    }

    public void Copy(string src, string dst, bool overwrite)
    {
        if (InjectedFault == FaultPoint.BeforeBackup
            && dst.Contains(Path.DirectorySeparatorChar + "Recovery" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !dst.Contains(RecoveryEntriesMarker, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("注入的 IO 失败：BeforeBackup");
        }

        if (dst.Contains(StagingMarker, StringComparison.OrdinalIgnoreCase))
        {
            TripEntryFault(EntryFaultPoint.BeforeStageWrite, condition: true, $"Copy→Staging {dst}");
        }
        else if (dst.Contains(RecoveryEntriesMarker, StringComparison.OrdinalIgnoreCase))
        {
            TripEntryFault(EntryFaultPoint.BeforeEntryBackup, condition: true, $"Backup {dst}");
        }

        if (!_files.TryGetValue(src, out var bytes))
        {
            throw new FileNotFoundException($"[内存存储] 源文件不存在：{src}", src);
        }

        if (!overwrite && _files.ContainsKey(dst))
        {
            throw new IOException($"[内存存储] 目标已存在：{dst}");
        }

        _files[dst] = [.. bytes];
    }

    public void Delete(string path) => _files.Remove(path);

    public void CreateDirectory(string path) => _directories.Add(path);

    public IReadOnlyList<string> ListDirectories(string directoryPath)
    {
        var prefix = directoryPath + Path.DirectorySeparatorChar;
        return [.. _directories
            .Where(d => d.StartsWith(prefix, StringComparison.Ordinal))
            .Where(d => !d[prefix.Length..].Contains(Path.DirectorySeparatorChar))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>M6（§6.1）：文件键中位于 directoryPath 直下（无更深分隔符）者，Ordinal 确定性排序。</summary>
    public IReadOnlyList<string> ListFiles(string directoryPath)
    {
        var prefix = directoryPath + Path.DirectorySeparatorChar;
        return [.. _files.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Where(k => !k[prefix.Length..].Contains(Path.DirectorySeparatorChar))
            .Order(StringComparer.Ordinal)];
    }

    public void DeleteDirectory(string directoryPath)
    {
        var prefix = directoryPath + Path.DirectorySeparatorChar;
        foreach (var key in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            _files.Remove(key);
        }

        _directories.Remove(directoryPath);
        _directories.RemoveWhere(d => d.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>测试断言辅助：目录是否被创建过。</summary>
    public bool DirectoryCreated(string path) => _directories.Contains(path);

    /// <summary>INV-E6 断言基线：全量文件快照（逐字节深拷贝）。</summary>
    public Dictionary<string, byte[]> SnapshotFiles() =>
        _files.ToDictionary(pair => pair.Key, pair => (byte[])pair.Value.Clone(), StringComparer.Ordinal);

    public IReadOnlyList<string> FilePaths => [.. _files.Keys.Order(StringComparer.Ordinal)];

    private void TripEntryFault(EntryFaultPoint point, bool condition, string detail)
    {
        if (!condition || _entryFault != point)
        {
            return;
        }

        _entryFault = EntryFaultPoint.None; // 一次性：触发即解除，回滚路径不受同一故障二次阻断
        throw new IOException($"注入的 IO 失败：{point}（{detail}）");
    }
}

/// <summary>测试用数据目录提供者：以任意字符串为根（无文件系统语义）。</summary>
public sealed class FakeDataDirectoryProvider(string root) : IDataDirectoryProvider
{
    public IDataDirectory GetDefault() => new FileSystemDataDirectory(root);
}
