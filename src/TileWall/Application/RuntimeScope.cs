using System.Security.Cryptography;
using System.Text;

namespace TileWall.ApplicationModel;

/// <summary>
/// Resolves one process identity and its writable directories before logging or WinUI starts.
/// A test root is explicit so development runs cannot touch the active user's application state.
/// </summary>
internal sealed class RuntimeScope
{
    private RuntimeScope(string dataRoot, bool isTest)
    {
        DataRoot = dataRoot;
        IsTest = isTest;
        LogRoot = Path.Combine(dataRoot, "logs");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataRoot.ToUpperInvariant())))[..24];
        InstanceMutexName = $@"Local\TileWall.Instance.{hash}";
        ShellWindowName = $"TileWall.ShellHost.{hash}";
        LoginValueName = isTest ? $"TileWall.Test.{hash}" : "TileWall";
    }

    public string DataRoot { get; }
    public string LogRoot { get; }
    public string InstanceMutexName { get; }
    public string ShellWindowName { get; }
    public string LoginValueName { get; }
    public bool IsTest { get; }

    public static RuntimeScope FromArguments(string[] args)
    {
        var normalRoot = ResolveNormalDataRoot();
        var index = Array.FindIndex(args, value => string.Equals(value, "--test-root", StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return new RuntimeScope(normalRoot, isTest: false);
        }

        if (index + 1 >= args.Length || !Path.IsPathFullyQualified(args[index + 1]))
        {
            throw new ArgumentException("--test-root requires an absolute directory path.");
        }

        var testRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[index + 1]));
        var volumeRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(testRoot)
            ?? throw new ArgumentException("--test-root has no volume root."));
        if (testRoot.Equals(volumeRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--test-root cannot be a volume root.");
        }

        var windowsRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (IsWithin(testRoot, windowsRoot) || IsWithin(windowsRoot, testRoot))
        {
            throw new ArgumentException("--test-root cannot contain the Windows system directory.");
        }

        if (IsWithin(testRoot, normalRoot) || IsWithin(normalRoot, testRoot))
        {
            throw new ArgumentException("--test-root must be separate from the normal application data directory.");
        }

        for (var ancestor = new DirectoryInfo(testRoot); ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.Exists && ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new ArgumentException("--test-root cannot use a reparse point in its path.");
            }
        }

        return new RuntimeScope(testRoot, isTest: true);
    }

    private static bool IsWithin(string candidate, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return candidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveNormalDataRoot()
    {
#if TILEWALL_PACKAGED
        return Windows.Storage.ApplicationData.Current.LocalFolder.Path;
#else
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TileWall");
#endif
    }
}
