using System.Text;
using TileWall.Core.Configuration;
using TileWall.Core.Settings;
using TileWall.Core.Storage;

namespace TileWall.Core.Shell;

internal sealed record ExplorerMenuPreferenceState(bool Enabled, string? Error = null)
{
    public bool ReadSucceeded => Error is null;
}

/// <summary>The physical Explorer menu preference and its protected companion files.</summary>
internal sealed class ExplorerMenuPreference(IFileStore files, string dataRoot)
{
    internal const string FileName = "explorer-menu.state";
    private const string LanguageFileName = "explorer-menu.language";
    private const string LanguageHeader = "TileWall.ExplorerMenuLanguage/1\n";
    private const string HandoffFileName = "explorer-menu.handoff";
    private const int MaximumHandoffBytes = 64 * 1024;
    private static ReadOnlySpan<byte> EnabledBytes => "TileWall.ExplorerMenu/1\n1\n"u8;
    private static ReadOnlySpan<byte> DisabledBytes => "TileWall.ExplorerMenu/1\n0\n"u8;
    private readonly WallStoragePaths _paths = new(files, dataRoot);

    internal ExplorerMenuPreferenceState Read()
    {
        try { return new(ReadOverride() ?? true); }
        catch (Exception ex) when (IsIoError(ex)) { return new(false, ex.Message); }
    }

    // Retain the distinction between an explicitly enabled menu and the enabled-by-default
    // missing file. Transfer recovery must be able to restore the original state.
    internal bool? ReadOverride()
    {
        var bytes = ReadDocument(FileName, 64);
        if (bytes is null) return null;
        if (bytes.AsSpan().SequenceEqual(EnabledBytes)) return true;
        if (bytes.AsSpan().SequenceEqual(DisabledBytes)) return false;
        throw new IOException("Invalid Explorer menu preference format.");
    }

    internal ExplorerMenuPreferenceState Write(bool enabled)
    {
        try
        {
            // A corrupt/unreadable preference is never silently replaced by an ordinary toggle.
            WriteOverride(enabled);
            return new(enabled);
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            var actual = Read();
            return actual with { Error = actual.Error ?? ex.Message };
        }
    }

    internal void WriteOverride(bool? enabled)
    {
        if (ReadOverride() == enabled) return;
        if (enabled is null) DeleteDocument(FileName);
        else ReplaceDocument(FileName, (enabled.Value ? EnabledBytes : DisabledBytes).ToArray());
        if (ReadOverride() != enabled) throw new IOException("The Explorer menu preference changed while publishing.");
    }

    internal bool PublishLanguage(string language)
    {
        if (!WallLanguages.TryParseTag(language, out var mode) || language != WallLanguages.ResolveTag(mode))
            throw new ArgumentException("Unsupported Explorer menu language.", nameof(language));
        var desired = Encoding.ASCII.GetBytes(LanguageHeader + language + "\n");
        if (ReadDocument(LanguageFileName, 64) is { } previous && previous.AsSpan().SequenceEqual(desired))
            return false;
        ReplaceDocument(LanguageFileName, desired);
        if (ReadDocument(LanguageFileName, 64) is not { } actual || !actual.AsSpan().SequenceEqual(desired))
            throw new IOException("The Explorer menu language changed while publishing.");
        return true;
    }

    internal byte[]? ReadHandoff() => ReadDocument(HandoffFileName, MaximumHandoffBytes);

    internal void WriteHandoff(byte[]? bytes)
    {
        if (bytes is null) DeleteDocument(HandoffFileName);
        else
        {
            if (bytes.Length > MaximumHandoffBytes) throw new IOException("The Explorer menu handoff is too large.");
            ReplaceDocument(HandoffFileName, bytes);
        }
    }

    private byte[]? ReadDocument(string name, int limit)
    {
        var path = _paths.Under(name);
        _paths.Guard(path);
        var info = files.GetPathInfo(path);
        if (!info.Exists) return null;
        if (info.IsDirectory) throw new IOException($"The Explorer menu file {name} is a directory.");
        _paths.Guard(path);
        using var stream = files.OpenReadProtected(path);
        var bytes = new byte[limit + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var count = stream.Read(bytes.AsSpan(length));
            if (count == 0) break;
            length += count;
        }
        _paths.Guard(path);
        if (length > limit) throw new IOException($"The Explorer menu file {name} is too large.");
        return bytes[..length];
    }

    private void DeleteDocument(string name)
    {
        var path = _paths.Under(name);
        _paths.Guard(path);
        files.Delete(path);
    }

    private void ReplaceDocument(string name, byte[] bytes)
    {
        string? temporary = null;
        try
        {
            var target = _paths.Under(name);
            _paths.Guard(_paths.Root);
            if (files.GetPathInfo(_paths.Root) is { Exists: true, IsDirectory: false })
                throw new IOException("The application data root is not a directory.");
            files.CreateDirectory(_paths.Root);
            _paths.Guard(target);
            temporary = _paths.Under($".{name}.{Guid.NewGuid():N}.tmp");
            _paths.Guard(temporary);
            files.WriteAllBytes(temporary, bytes);
            // The actual replace is the sole commit point. Recheck both paths at that boundary.
            _paths.Guard(temporary);
            _paths.Guard(target);
            if (files.GetPathInfo(target) is { Exists: true, IsDirectory: true })
                throw new IOException($"The Explorer menu file {name} is a directory.");
            files.Move(temporary, target, overwrite: true);
            temporary = null;
        }
        finally
        {
            if (temporary is not null)
            {
                try { _paths.Guard(temporary); files.Delete(temporary); }
                catch (Exception ex) when (IsIoError(ex)) { }
            }
        }
    }

    private static bool IsIoError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException or NotSupportedException;
}
