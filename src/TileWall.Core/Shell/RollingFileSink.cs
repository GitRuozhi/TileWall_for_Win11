using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TileWall.Core.Configuration;
using TileWall.Core.Storage;

namespace TileWall.Core.Shell;

/// <summary>
/// 滚动文件日志落地（C4/F-07）：&lt;dir&gt;/&lt;base&gt;.log 追加写；超过 maxFileBytes 滚动为
/// .1.log … .maxHistoryFiles.log（新→旧），最旧直接删除。线程安全（引擎单线程写 + Flush 并发）。
/// 单条一行：换行折叠为字面 \n，便于 grep。落地持续失败（磁盘满/权限）达上限后放弃——
/// 日志系统不得反过来拖垮宿主。同一路径的滚动writer由单一主实例拥有，不提供多进程滚动协调。
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
    private readonly string _trustedRoot;
    private readonly WallStoragePaths _paths;
    private readonly string _baseName;
    private readonly long _maxFileBytes;
    private readonly int _maxHistoryFiles;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private SafeFileHandle? _trustedRootHandle;
    private SafeFileHandle? _directoryHandle;
    private long _currentBytes;
    private int _consecutiveFailures;
    private bool _abandoned;
    private bool _disposed;

    public RollingFileSink(
        string directory,
        string baseName = "tilewall",
        long maxFileBytes = DefaultMaxFileBytes,
        int maxHistoryFiles = DefaultMaxHistoryFiles,
        string? trustedRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHistoryFiles);
        if (baseName.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || baseName is "." or ".." ||
            baseName.EndsWith('.') || baseName.EndsWith(' '))
            throw new ArgumentException("Log base name must be a single safe file-name component.", nameof(baseName));

        _directory = NormalizePath(directory);
        _trustedRoot = NormalizePath(trustedRoot ?? directory);
        if (!IsWithin(_directory, _trustedRoot))
            throw new ArgumentException("Log directory must be inside its trusted root.", nameof(directory));
        _paths = new WallStoragePaths(new LocalFileStore(), _trustedRoot);
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
                ValidateActiveWriter();
                var line = FormatLine(entry);
                _writer!.Write(line);
                ValidateActiveWriter();
                _writer.Flush(); // 失败链日志必须即时可见（崩溃/断电后仍可取证）
                _currentBytes += Encoding.UTF8.GetByteCount(line);
                if (_currentBytes >= _maxFileBytes)
                {
                    RollLocked();
                }

                _consecutiveFailures = 0;
            }
            catch (Exception ex) when (IsRecoverableIoFailure(ex))
            {
                CloseWriterLocked(flush: false);
                CloseDirectoryHandlesLocked();
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
                ValidateActiveWriter();
                _writer.Flush();
            }
            catch (Exception ex) when (IsRecoverableIoFailure(ex))
            {
                CloseWriterLocked(flush: false);
                CloseDirectoryHandlesLocked();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CloseWriterLocked();
            CloseDirectoryHandlesLocked();
        }
    }

    private void EnsureOpen()
    {
        if (_writer is not null)
        {
            return;
        }

        EnsureDirectoriesReady();
        var path = CurrentFilePath;
        GuardPath(path, expectDirectory: false, allowMissing: true);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        try
        {
            GuardPath(path, expectDirectory: false, allowMissing: false);
            ValidateFileHandle(stream.SafeFileHandle, path);
            ValidateDirectoryHandles();
            _currentBytes = stream.Length;
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
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

    private void MoveAside(string source, string destination)
    {
        EnsureDirectoriesReady();
        GuardPath(source, expectDirectory: false, allowMissing: true);
        GuardPath(destination, expectDirectory: false, allowMissing: true);
        if (!File.Exists(source))
        {
            return;
        }

        ValidateExistingFilePath(source);
        ValidateDirectoryHandles();
        GuardPath(source, expectDirectory: false, allowMissing: false);
        GuardPath(destination, expectDirectory: false, allowMissing: true);
        if (File.Exists(destination))
        {
            ValidateExistingFilePath(destination);
            ValidateDirectoryHandles();
            GuardPath(source, expectDirectory: false, allowMissing: false);
            GuardPath(destination, expectDirectory: false, allowMissing: false);
            File.Delete(destination);
            GuardPath(destination, expectDirectory: false, allowMissing: true);
        }

        ValidateDirectoryHandles();
        GuardPath(source, expectDirectory: false, allowMissing: false);
        GuardPath(destination, expectDirectory: false, allowMissing: true);
        File.Move(source, destination);
        GuardPath(destination, expectDirectory: false, allowMissing: false);
        ValidateExistingFilePath(destination);
    }

    private void EnsureDirectoriesReady()
    {
        GuardPath(_trustedRoot, expectDirectory: true, allowMissing: true);
        Directory.CreateDirectory(_trustedRoot);
        GuardPath(_trustedRoot, expectDirectory: true, allowMissing: false);
        EnsureDirectoryHandle(ref _trustedRootHandle, _trustedRoot);

        GuardPath(_directory, expectDirectory: true, allowMissing: true);
        Directory.CreateDirectory(_directory);
        GuardPath(_directory, expectDirectory: true, allowMissing: false);
        EnsureDirectoryHandle(ref _directoryHandle, _directory);
        ValidateDirectoryHandles();
    }

    private void EnsureDirectoryHandle(ref SafeFileHandle? cached, string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (cached is { IsInvalid: false, IsClosed: false })
        {
            ValidateDirectoryHandle(cached, path);
            return;
        }

        var opened = OpenDirectoryHandle(path);
        try
        {
            ValidateDirectoryHandle(opened, path);
            cached = opened;
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    private void ValidateDirectoryHandles()
    {
        GuardPath(_trustedRoot, expectDirectory: true, allowMissing: false);
        GuardPath(_directory, expectDirectory: true, allowMissing: false);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (_trustedRootHandle is null || _directoryHandle is null)
            throw new IOException("Log directory handles are not initialized.");
        ValidateDirectoryHandle(_trustedRootHandle, _trustedRoot);
        ValidateDirectoryHandle(_directoryHandle, _directory);
    }

    private void ValidateActiveWriter()
    {
        ValidateDirectoryHandles();
        var writer = _writer ?? throw new IOException("Log file is not open.");
        GuardPath(CurrentFilePath, expectDirectory: false, allowMissing: false);
        ValidateFileHandle(((FileStream)writer.BaseStream).SafeFileHandle, CurrentFilePath);
        ValidateDirectoryHandles();
    }

    private void ValidateExistingFilePath(string path)
    {
        GuardPath(path, expectDirectory: false, allowMissing: false);
        if (OperatingSystem.IsWindows())
        {
            using var handle = OpenFileAttributesHandle(path);
            ValidateFileHandle(handle, path);
        }
        else
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            ValidateFileHandle(file.SafeFileHandle, path);
        }
        ValidateDirectoryHandles();
    }

    private void CloseWriterLocked(bool flush = true)
    {
        var writer = _writer;
        if (writer is null)
        {
            return;
        }

        _writer = null;
        if (flush)
        {
            try
            {
                ValidateFileHandle(((FileStream)writer.BaseStream).SafeFileHandle, CurrentFilePath);
                ValidateDirectoryHandles();
                writer.Dispose();
                return;
            }
            catch (Exception ex) when (IsRecoverableIoFailure(ex))
            {
                // All log records are flushed before rotation/shutdown. If the path no longer
                // validates, close the handle without asking StreamWriter to flush again.
            }
        }

        try
        {
            ((FileStream)writer.BaseStream).Dispose(); // avoid an unguarded StreamWriter flush on failure
        }
        catch (Exception ex) when (IsRecoverableIoFailure(ex))
        {
            // The OS closes the handle when the process exits.
        }
    }

    private void CloseDirectoryHandlesLocked()
    {
        _directoryHandle?.Dispose();
        _directoryHandle = null;
        _trustedRootHandle?.Dispose();
        _trustedRootHandle = null;
    }

    private void GuardPath(string path, bool expectDirectory, bool allowMissing)
    {
        _paths.Guard(path);
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException) when (allowMissing)
        {
            return;
        }
        catch (DirectoryNotFoundException) when (allowMissing)
        {
            return;
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Reparse point in log path.");
        if (attributes.HasFlag(FileAttributes.Directory) != expectDirectory)
            throw new IOException("Unexpected log path type.");
    }

    private void ValidateDirectoryHandle(SafeFileHandle handle, string expectedPath)
    {
        var information = GetInformation(handle);
        if ((information.FileAttributes & FileAttributeReparsePoint) != 0 ||
            (information.FileAttributes & FileAttributeDirectory) == 0)
            throw new IOException("Unsafe log directory handle.");
        ValidateHandlePath(handle, expectedPath);
    }

    private void ValidateFileHandle(SafeFileHandle handle, string expectedPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (!PathEquals(NormalizePath(expectedPath), expectedPath))
                throw new IOException("Unnormalized log file path.");
            return;
        }

        var information = GetInformation(handle);
        if ((information.FileAttributes & FileAttributeReparsePoint) != 0 ||
            (information.FileAttributes & FileAttributeDirectory) != 0 || information.NumberOfLinks != 1)
            throw new IOException("Unsafe log file handle.");
        ValidateHandlePath(handle, expectedPath);
    }

    private void ValidateHandlePath(SafeFileHandle handle, string expectedPath)
    {
        var actualPath = NormalizeFinalPath(GetFinalPath(handle));
        var expected = NormalizePath(expectedPath);
        if (!PathEquals(actualPath, expected) || !IsWithin(actualPath, _trustedRoot))
            throw new IOException("Opened log handle resolves outside its expected path.");
    }

    private static SafeFileHandle OpenDirectoryHandle(string path)
    {
        var handle = CreateFile(path, FileReadAttributes, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        EnsureHandle(handle, "Could not open log directory handle.");
        return handle;
    }

    private static SafeFileHandle OpenFileAttributesHandle(string path)
    {
        var handle = CreateFile(path, FileReadAttributes, FileShareRead | FileShareDelete,
            IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint | FileFlagSequentialScan, IntPtr.Zero);
        EnsureHandle(handle, "Could not open log file handle.");
        return handle;
    }

    private static void EnsureHandle(SafeFileHandle handle, string message)
    {
        if (!handle.IsInvalid)
            return;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException(message, new Win32Exception(error));
    }

    private static ByHandleFileInformation GetInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new IOException("Could not inspect opened log handle.", new Win32Exception(Marshal.GetLastWin32Error()));
        return information;
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        while (true)
        {
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
                throw new IOException("Could not resolve opened log handle path.", new Win32Exception(Marshal.GetLastWin32Error()));
            if (length < buffer.Capacity)
                return buffer.ToString();
            buffer = new StringBuilder(checked((int)length + 1));
        }
    }

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string NormalizeFinalPath(string path)
    {
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            path = "\\\\" + path[8..];
        else if (path.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase) &&
                 path.Length >= 7 && path[5] == ':')
            path = path[4..];
        return Path.TrimEndingDirectorySeparator(path);
    }

    private static bool IsWithin(string path, string root)
    {
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return PathEquals(path, root) || path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsRecoverableIoFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or ObjectDisposedException or ArgumentException or
        NotSupportedException or Win32Exception or ExternalException;

    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);

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
