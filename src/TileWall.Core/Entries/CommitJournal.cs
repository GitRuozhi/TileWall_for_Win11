using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TileWall.Core.Configuration;

namespace TileWall.Core.Entries;

/// <summary>
/// 提交日志单条操作（M4 设计 §5.5 步骤 3）。相对路径一律 "Objects/&lt;id&gt;/&lt;名&gt;" 正斜杠形式。
/// Kind 固定五种：add / replace / rename / remove / edit。
/// </summary>
public sealed record JournalOp(
    string Kind,
    string ObjectId,
    string? From,       // 旧入口相对路径（add 为 null）
    string? To,         // 新入口相对路径（remove 为 null；edit = 正式入口路径）
    string? OldTarget,  // edit 专用：回滚/撤销时恢复的旧目标
    string? NewTarget); // edit 专用：生效时写入的新目标

/// <summary>
/// 提交日志：一次联合提交的操作清单（崩溃清扫与撤销回放按逆序重放）。
/// ConfigFingerprint = 步骤 6 拟写入的新配置序列化字节 SHA-256（hex）：Sweep 据此区分
/// 「步骤 5 后、步骤 6 前崩溃」（当前 config 指纹 ≠ 日志指纹 → 回滚）与
/// 「步骤 6 后、步骤 7 前崩溃」（相等 → 已完成，仅清理）；覆盖 add 孤儿与 From==To
/// replace（改 URL 行+属性同批）等「配置引用存在性」无法判定的子场景。null（手写/旧日志）
/// 时回退到「配置有效且全部入口存在」判定。
/// </summary>
public sealed record CommitJournal(string CommitId, IReadOnlyList<JournalOp> Ops, string? ConfigFingerprint = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CommitJournal))]
internal sealed partial class CommitJournalJsonContext : JsonSerializerContext
{
}

/// <summary>
/// 提交日志文件读写：暂存区内的 commit.json（步骤 3 落盘，先于一切正式区操作——
/// 无日志即必然未动过正式区）与撤销材料内的 journal.json（步骤 7 移交，供撤销回放）。
/// </summary>
public static class CommitJournalFile
{
    public const string StagingFileName = "commit.json";
    public const string UndoFileName = "journal.json";

    public static void Write(IFileStore files, string path, CommitJournal journal)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(journal);
        files.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(journal, CommitJournalJsonContext.Default.CommitJournal));
    }

    /// <summary>不存在或不可解析 → false（Sweep 按无日志/坏日志分别处理）。</summary>
    public static bool TryRead(IFileStore files, string path, out CommitJournal? journal)
    {
        ArgumentNullException.ThrowIfNull(files);
        try
        {
            if (!files.Exists(path))
            {
                journal = null;
                return false;
            }

            journal = JsonSerializer.Deserialize(files.ReadAllBytes(path), CommitJournalJsonContext.Default.CommitJournal);
            return journal is not null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            journal = null;
            return false;
        }
    }

    /// <summary>配置指纹 = 序列化字节（ConfigJson 唯一出口，确定性）的 SHA-256 hex。</summary>
    public static string Fingerprint(TileWallConfig config) => FingerprintOfBytes(ConfigJson.Serialize(config));

    public static string FingerprintOfBytes(ReadOnlySpan<byte> utf8Config) =>
        Convert.ToHexString(SHA256.HashData(utf8Config));
}
