namespace TileWall.Core.Settings;

/// <summary>Current HKCU Run policy, with the registry access supplied by the Windows adapter.</summary>
public sealed record RunLoginState(bool? Enabled, string? Error = null);

public sealed class RunLoginStartup(IRunKeyValueStore store)
{
    public RunLoginState Read()
    {
        try { return new(store.Read() is not null); }
        catch (Exception ex) { return new(null, ex.Message); }
    }

    public RunLoginState Set(bool enabled, string executablePath, string? testRoot = null)
    {
        try
        {
            if (enabled)
            {
                ArgumentException.ThrowIfNullOrEmpty(executablePath);
                var command = $"\"{executablePath}\" --background";
                if (testRoot is not null) command += $" --test-root \"{testRoot}\"";
                store.Write(command);
            }
            else store.Delete();

            var actual = Read();
            return actual.Enabled == enabled ? actual : actual with
            { Error = actual.Error ?? "Run value did not match the requested state." };
        }
        catch (Exception ex)
        {
            var actual = Read();
            return actual with { Error = ex.Message };
        }
    }
}
