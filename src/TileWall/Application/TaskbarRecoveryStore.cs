// Adapted from ReStart (MIT), commits 297c8f5 / b9c2718 and their base.
// Copyright (c) 2026 ReStart contributors. See Licenses/ReStartLicense.txt.
using System.Text.Json;

namespace TileWall.ApplicationModel;

/// <summary>Persists one bounded, private recovery transaction without overwriting another owner or following links.</summary>
/// <param name="directory">Owned per-session TileWall runtime directory; inert fixtures use a fresh temporary directory.</param>
internal sealed class TaskbarRecoveryStore(string directory, string? mutexName = null) : ITaskbarRecoveryStore
{
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 8 };
    internal string DirectoryPath { get; } = Path.GetFullPath(directory);
    internal string MarkerPath => Path.Combine(DirectoryPath, "taskbar-recovery.json");

    public IDisposable Acquire()
    {
        Mutex? mutex = null;
        if (mutexName != null)
        {
            mutex = new Mutex(false, mutexName);
            try
            {
                try { if (!mutex.WaitOne(0)) throw new IOException("Another taskbar recovery operation is in progress."); }
                catch (AbandonedMutexException) { }
            }
            catch { mutex.Dispose(); throw; }
        }
        var path = Path.Combine(DirectoryPath, "taskbar-recovery.lock");
        try
        {
            CheckLinks(path); Directory.CreateDirectory(DirectoryPath); CheckLinks(path);
            return new Ownership(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), mutex);
        }
        catch { if (mutex != null) { mutex.ReleaseMutex(); mutex.Dispose(); } throw; }
    }

    private sealed class Ownership(FileStream file, Mutex? mutex) : IDisposable
    {
        public void Dispose()
        {
            try { file.Dispose(); }
            finally { if (mutex != null) { mutex.ReleaseMutex(); mutex.Dispose(); } }
        }
    }

    public TaskbarLeaseState? Read()
    {
        var bytes = ReadBounded(MarkerPath, 32768);
        if (bytes == null) return null;
        try
        {
            var state = JsonSerializer.Deserialize<TaskbarLeaseState>(bytes, Json);
            if (state == null || !state.IsValid()) throw new InvalidDataException("Invalid taskbar recovery state.");
            return state;
        }
        catch (JsonException) { throw new InvalidDataException("Invalid taskbar recovery state."); }
    }

    public void Save(TaskbarLeaseState state)
    {
        CheckLinks(MarkerPath);
        if (File.Exists(MarkerPath)) throw new IOException("An existing taskbar transaction requires recovery.");
        Write(state, replace: false);
    }

    public void Update(TaskbarLeaseState state)
    {
        if (Read() is not { } current || current.Token != state.Token || current.ParentId != state.ParentId
            || current.ParentStarted != state.ParentStarted || current.Mode != state.Mode)
            throw new InvalidDataException("Taskbar recovery ownership changed.");
        Write(state, replace: true);
    }

    private void Write(TaskbarLeaseState state, bool replace)
    {
        if (!state.IsValid()) throw new InvalidDataException("Invalid taskbar recovery state.");
        CheckLinks(MarkerPath); Directory.CreateDirectory(DirectoryPath); CheckLinks(MarkerPath);
        var temporary = Path.Combine(DirectoryPath, "taskbar-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Json);
            if (bytes.Length > 32768) throw new InvalidDataException("Invalid taskbar recovery size.");
            CheckLinks(temporary);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(flushToDisk: true); }
            CheckLinks(temporary); CheckLinks(MarkerPath);
            File.Move(temporary, MarkerPath, overwrite: replace);
        }
        finally
        {
            try { CheckLinks(temporary); File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Delete(string token)
    {
        if (Read() is not { } state) return;
        if (state.Token != token) throw new InvalidDataException("Taskbar recovery ownership changed.");
        CheckLinks(MarkerPath);
        File.Delete(MarkerPath);
    }

    internal static byte[]? ReadBounded(string path, int maximum)
    {
        CheckLinks(path);
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = new byte[maximum + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = input.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            if (count > maximum) throw new InvalidDataException("The taskbar recovery file exceeds its size limit.");
            return bytes[..count];
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal static void CheckLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked taskbar recovery path.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
