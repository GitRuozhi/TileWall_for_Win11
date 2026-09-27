using System.Text;

namespace TileWall.Core.Shell;

/// <summary>
/// 滚动文件日志落地（C4/F-07）：&lt;dir&gt;/&lt;base&gt;.log 追加写；超过 maxFileBytes 滚动为
/// .1.log … .maxHistoryFiles.log（新→旧），最旧直接删除。线程安全（引擎单线程写 + Flush 并发）。
/// 单条一行：换行折叠为字面 \n，便于 grep。落地持续失败（磁盘满/权限）达上限后放弃——
/// 日志系统不得反过来拖垮宿主。多进程同机各写同一路径时按追加模式尽力而为（行级交错可容忍）。
/// </summary>
public sealed class RollingFileSink : ILogSink, IDisposable
{
    /// <summary>单文件上限默认 1 MB（失败链日志量级：KB/天）。</summary>
    public const long DefaultMaxFileBytes = 1 << 20;

    /// <summary>历史保留默认 3 份（当前 + 3 ≈ 4 MB 上限）。</summary>
    public const int DefaultMaxHistoryFiles = 3;

    /// <summary>连续落地失败达到该次数后放弃写（防每条日志都撞同一堵墙）。</summary>
    public const int MaxConsecutiveFailures = 5;

    private readonly string _directory;
    private readonly string _baseName;
    private readonly long _maxFileBytes;
    private readonly int _maxHistoryFiles;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private long _currentBytes;
    private int _consecutiveFailures;
    private bool _abandoned;
    private bool _disposed;

    public RollingFileSink(
        string directory,
        string baseName = "tilewall",
        long maxFileBytes = DefaultMaxFileBytes,
        int maxHistoryFiles = DefaultMaxHistoryFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHistoryFiles);
        _directory = directory;
        _baseName = baseName;
        _maxFileBytes = maxFileBytes;
        _maxHistoryFiles = maxHistoryFiles;
    }

    /// <summary>当前活动文件完整路径。</summary>
    public string CurrentFilePath => Path.Combine(_directory, _baseName + ".log");

    /// <summary>放弃写之后的只读诊断（已达连续失败上限）。</summary>
    public bool Abandoned => _abandoned;

    public void Write(AppLogEntry entry)
    {
        lock (_gate)
        {
            if (_abandoned || _disposed)
            {
                return;
            }

            try
            {
                EnsureOpen();
                var line = FormatLine(entry);
                _writer!.Write(line);
                _writer.Flush(); // 失败链日志必须即时可见（崩溃/断电后仍可取证）
                _currentBytes += Encoding.UTF8.GetByteCount(line);
                if (_currentBytes >= _maxFileBytes)
                {
                    RollLocked();
                }

                _consecutiveFailures = 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                CloseWriterLocked();
                _consecutiveFailures++;
                if (_consecutiveFailures >= MaxConsecutiveFailures)
                {
                    _abandoned = true; // 放弃但绝不抛出：宿主业务优先于日志
                }
            }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                _writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // 尽力刷新；失败不致命
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CloseWriterLocked();
        }
    }

    private void EnsureOpen()
    {
        if (_writer is not null)
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        _currentBytes = File.Exists(CurrentFilePath) ? new FileInfo(CurrentFilePath).Length : 0;
        _writer = new StreamWriter(
            new FileStream(CurrentFilePath, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>关闭当前文件并按「新→旧」移位历史：当前→.1，.i→.(i+1)，超出保留数的最旧删除。</summary>
    private void RollLocked()
    {
        CloseWriterLocked();
        for (var i = _maxHistoryFiles; i >= 2; i--)
        {
            MoveAside(Path.Combine(_directory, $"{_baseName}.{i - 1}.log"), Path.Combine(_directory, $"{_baseName}.{i}.log"));
        }

        MoveAside(CurrentFilePath, Path.Combine(_directory, $"{_baseName}.1.log"));
        _currentBytes = 0;
    }

    private static void MoveAside(string source, string destination)
    {
        if (!File.Exists(source))
        {
            return;
        }

        File.Delete(destination);
        File.Move(source, destination);
    }

    private void CloseWriterLocked()
    {
        if (_writer is null)
        {
            return;
        }

        try
        {
            _writer.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 关闭失败只能放弃（句柄由进程退出兜底回收）
        }

        _writer = null;
    }

    /// <summary>单行格式：`时刻 [等级] [分类] 消息`；换行折叠为字面 \n（单条单行便于检索）。</summary>
    public static string FormatLine(AppLogEntry entry)
    {
        var level = entry.Level switch
        {
            AppLogLevel.Error => "ERROR",
            AppLogLevel.Warn => "WARN ",
            _ => "INFO ",
        };
        var message = entry.Message
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\n", StringComparison.Ordinal);
        return $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] [{entry.Category}] {message}\n";
    }
}
