// Adapted from ReStart (MIT), commits 297c8f5 / b9c2718 and their base.
// Copyright (c) 2026 ReStart contributors. See Licenses/ReStartLicense.txt.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using TileWall.Shell.Interop;

namespace TileWall.ApplicationModel;

/// <summary>Same-executable, no-XAML crash guard. Its command never enters ordinary application startup.</summary>
internal sealed class TaskbarRecoveryCommand(TaskbarRecoveryStore store)
{
    internal const string Argument = "--taskbar-recovery";
    private Process? _process;
    private string _nonce = Guid.NewGuid().ToString("N");
    private TaskbarLeaseState? _owner;
    private sealed record Ready(int ParentId, long ParentStarted, string Nonce);
    private sealed class Protection : IDisposable { public void Dispose() { } }

    [SupportedOSPlatform("windows")]
    internal static TaskbarRecoveryStore CreateStore()
    {
        using var current = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new IOException("The taskbar recovery user is unavailable.");
        var directory = Path.Combine(KnownFolderPaths.LocalAppData, "TileWall", "Runtime", $"Taskbar-{current.SessionId}");
        return new(directory, $"Local\\TileWall.TaskbarRecovery.{sid}.{current.SessionId}");
    }

    internal static bool IsRequested(string[] args) => args.Any(value =>
        value.StartsWith(Argument, StringComparison.OrdinalIgnoreCase));

    internal static bool TryParse(string[] args, out int parentId, out long parentStarted, out string nonce)
    {
        parentId = 0; parentStarted = 0; nonce = string.Empty;
        if (args.Length != 4 || !args[0].Equals(Argument, StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out parentId) || parentId <= 0
            || !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out parentStarted) || parentStarted <= 0
            || !Guid.TryParseExact(args[3], "N", out _)) return false;
        nonce = args[3];
        return true;
    }

    [SupportedOSPlatform("windows")]
    internal static int Run(string[] args)
    {
        if (!TryParse(args, out var id, out var started, out var nonce)) return 2;
        try { return RunCoreAsync(id, started, nonce).GetAwaiter().GetResult(); }
        catch (Exception error) when (TaskbarLease.Expected(error) || error is ArgumentException or System.Security.SecurityException) { return 1; }
    }

    internal bool IsReady => _process is { HasExited: false } && _owner is { } owner && ReadReady(owner);

    internal IDisposable Protect(TaskbarLeaseState state)
    {
        using var parent = Process.GetCurrentProcess();
        if (state.ParentId != parent.Id || state.ParentStarted != parent.StartTime.ToUniversalTime().Ticks)
            throw new InvalidOperationException("Unexpected taskbar recovery owner.");
        if (_process is { HasExited: false })
        {
            if (ReadReady(state)) { _owner = state; return new Protection(); }
            // Only a helper created here, before readiness and before applying effects.
            _process.Kill();
        }
        _process?.Dispose();
        _process = null; _owner = null;
        _nonce = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { Argument, state.ParentId.ToString(CultureInfo.InvariantCulture),
                     state.ParentStarted.ToString(CultureInfo.InvariantCulture), _nonce }) start.ArgumentList.Add(argument);
        _process = Process.Start(start) ?? throw new IOException("Taskbar recovery helper could not start.");
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5) && !_process.HasExited)
        {
            if (ReadReady(state)) { _owner = state; return new Protection(); }
            Thread.Sleep(25);
        }
        if (!_process.HasExited) _process.Kill();
        _process.Dispose(); _process = null;
        throw new IOException("Taskbar recovery helper did not become ready.");
    }

    private bool ReadReady(TaskbarLeaseState owner)
    {
        var path = ReadyPath(store, _nonce);
        try
        {
            var bytes = TaskbarRecoveryStore.ReadBounded(path, 1024);
            return bytes != null && JsonSerializer.Deserialize<Ready>(bytes) == new Ready(owner.ParentId, owner.ParentStarted, _nonce);
        }
        catch (Exception error) when (TaskbarLease.Expected(error) || error is JsonException) { return false; }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunCoreAsync(int id, long started, string nonce)
    {
        using var self = Process.GetCurrentProcess();
        using var parent = Process.GetProcessById(id);
        if (parent.Id == self.Id || parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != started
            || parent.SessionId != self.SessionId
            || !string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
            || ReadSid(parent) is not { } parentSid || parentSid != ReadSid(self)) return 2;
        var recovery = CreateStore();
        // Acquisition still owns the transaction lock while waiting for ready.
        // Read the atomically published prepared record without taking that lock.
        var prepared = recovery.Read();
        if (prepared == null || prepared.ParentId != id || prepared.ParentStarted != started) return 2;
        var path = ReadyPath(recovery, nonce);
        var created = false;
        try
        {
            TaskbarRecoveryStore.CheckLinks(path);
            Directory.CreateDirectory(recovery.DirectoryPath);
            TaskbarRecoveryStore.CheckLinks(path);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Ready(id, started, nonce));
            using (var ready = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { created = true; ready.Write(bytes); ready.Flush(flushToDisk: true); }
            await parent.WaitForExitAsync().ConfigureAwait(false);
            var backend = new TaskbarActivationBackend();
            while (!TaskbarRecovery.Run(recovery, backend, IsAlive, (id, started)))
            {
                // A real Explorer menu can stay open for arbitrarily long. Retain
                // the single-purpose recovery obligation until it can be returned.
                await Task.Delay(1000).ConfigureAwait(false);
                var state = recovery.Read();
                if (state == null || state.ParentId != id || state.ParentStarted != started) return 0;
            }
            return 0;
        }
        finally
        {
            try { if (created) { TaskbarRecoveryStore.CheckLinks(path); File.Delete(path); } }
            catch (Exception error) when (TaskbarLease.Expected(error)) { }
        }
    }

    internal static bool IsAlive(int id, long started)
    {
        try { using var process = Process.GetProcessById(id); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == started; }
        catch (ArgumentException) { return false; }
        catch (Exception error) when (TaskbarLease.Expected(error) || error is System.Security.SecurityException) { return true; }
    }

    private static string ReadyPath(TaskbarRecoveryStore recovery, string nonce) =>
        Path.Combine(recovery.DirectoryPath, "ready-" + nonce + ".json");

    [SupportedOSPlatform("windows")]
    private static string? ReadSid(Process process)
    {
        if (!OpenProcessToken(process.SafeHandle, 0x0008, out var token)) return null;
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle())) return identity.User?.Value;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
