namespace TileWall.Core.Shell;

/// <summary>
/// 冷启动命令行构造（M9 设计 §4.4）：`--add "路径1" "路径2" …`。
/// Windows 命令行整体长度上限 32767 字符——按单路径 ≤260 字符估，≥120 项才触界；
/// 触界时按整条路径截断（绝不切半条），截断事实经标记参数在墙上提示「请分批添加」，不静默丢项。
/// 路径不含引号字符（Windows 文件名禁止），逐条包裹引号安全。纯字符串逻辑，可单测。
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
    /// 构造 --add 参数串：依序整条纳入，直到达到长度上限；首条恒纳入（保证冷启动不空手）。
    /// 返回参数串（不含 exe 名）、纳入条数与是否发生截断（截断 = 后续条目放不下）。
    /// </summary>
    public static (string Arguments, int SelectedCount, bool Truncated) Build(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var builder = new System.Text.StringBuilder(AddArgument);
        var selected = 0;
        for (var i = 0; i < paths.Count; i++)
        {
            var candidate = $" \"{QuoteSafe(paths[i])}\"";
            if (selected > 0 && builder.Length + candidate.Length > MaxArgumentsLength)
            {
                break; // 后续条目放不下：整条截断（不切半条路径）
            }

            _ = builder.Append(candidate);
            selected++;
        }

        var truncated = selected < paths.Count;
        return (builder.ToString(), selected, truncated);
    }

    private static string QuoteSafe(string path) => path.Replace("\"", string.Empty); // 文件名不含引号；防御性剥离
}
