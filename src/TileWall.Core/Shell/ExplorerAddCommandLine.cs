namespace TileWall.Core.Shell;

/// <summary>
/// 冷启动命令行构造（M9 设计 §4.4）：`--add "路径1" "路径2" …`。
/// Windows 命令行整体长度上限 32767 字符——按单路径 ≤260 字符估，≥120 项才触界；
/// 触界时按整条路径截断（绝不切半条），截断事实经标记参数在墙上提示「请分批添加」，不静默丢项。
/// 按 Windows argv 引用规则编码反斜线和引号，不改变输入路径。纯字符串逻辑，可单测。
/// </summary>
public static class ExplorerAddCommandLine
{
    /// <summary>添加流程触发参数（与 --background 同为命令行开关）。</summary>
    public const string AddArgument = "--add";

    /// <summary>批次因命令行长度被截断的标记参数（墙上提示「请分批添加」的依据）。</summary>
    public const string TruncatedMarker = "--add-truncated";

    /// <summary>参数串长度上限（Windows 32767 上限留出安全余量，含 exe 路径）。</summary>
    public const int MaxArgumentsLength = 32000;

    /// <summary>
    /// 构造 --add 参数串：依序整条纳入，直到达到长度上限；超限首条也整条截断。
    /// 返回参数串（不含 exe 名）、纳入条数与是否发生截断（截断 = 后续条目放不下）。
    /// </summary>
    public static (string Arguments, int SelectedCount, bool Truncated) Build(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var builder = new System.Text.StringBuilder(AddArgument);
        var selected = 0;
        for (var i = 0; i < paths.Count; i++)
        {
            var candidate = " " + QuoteArgument(paths[i]);
            if (builder.Length + candidate.Length > MaxArgumentsLength)
            {
                break; // 后续条目放不下：整条截断（不切半条路径）
            }

            _ = builder.Append(candidate);
            selected++;
        }

        var truncated = selected < paths.Count;
        return (builder.ToString(), selected, truncated);
    }

    private static string QuoteArgument(string path)
    {
        var quoted = new System.Text.StringBuilder("\"");
        var slashes = 0;
        foreach (var character in path)
        {
            if (character == '\\') { slashes++; continue; }
            // Before a literal quote, escape both the preceding slashes and the quote itself.
            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            quoted.Append(character);
            slashes = 0;
        }
        // Trailing slashes must not escape the closing delimiter or absorb the next argument.
        quoted.Append('\\', slashes * 2);
        quoted.Append('"');
        return quoted.ToString();
    }
}
