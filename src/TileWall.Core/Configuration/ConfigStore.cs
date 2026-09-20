using System.Text.Json;

namespace TileWall.Core.Configuration;

/// <summary>加载结果状态（设计 §5.3 恢复链）。</summary>
public enum ConfigLoadStatus
{
    /// <summary>config.json 不存在（首启；墙尺寸由 UI 调 WallSizing.InitialForWorkArea 后 CreateInitial）。</summary>
    Fresh,

    /// <summary>反序列化 + 校验全通过。</summary>
    Loaded,

    /// <summary>config.json 失败，Recovery/config.prev.json 兜底成功（UI 须提示）。</summary>
    RecoveredFromBackup,

    /// <summary>主文件与备份都失败；原文件保留不动（设计 §17.2 不覆盖成空墙）。</summary>
    Corrupt,

    /// <summary>schemaVersion 高于当前支持；不自动迁移、不覆盖文件（设计 §16.6）。</summary>
    FutureVersion,
}

/// <param name="Diagnostics">诊断信息（恢复/失败路径非空，供 UI 提示）。</param>
public sealed record ConfigLoadResult(
    TileWallConfig? Config,
    ConfigLoadStatus Status,
    IReadOnlyList<string> Diagnostics);

/// <param name="BackupPath">本次保存创建的备份路径；首保存（无上一版）为 null。</param>
public sealed record ConfigSaveReport(bool BackupCreated, string ConfigPath, string? BackupPath);

/// <summary>
/// 原子保存与损坏恢复（设计 §16.3「暂存、替换和恢复」、§5.3）。
/// Save：校验 → 序列化 → 写 .tmp（FlushToDisk）→ 上一版 Copy 到 Recovery/config.prev.json →
/// File.Move(overwrite:true) 同卷原子替换（唯一提交点）。任一步失败删 .tmp 上抛，config.json 保持旧内容。
/// Load 恢复链：config.json → Recovery/config.prev.json → Fresh/Corrupt；
/// Corrupt / FutureVersion 保留原文件，绝不以空墙覆盖（设计 §17.2）。
/// </summary>
public sealed class ConfigStore
{
    public const string ConfigFileName = "config.json";
    public const string TempFileName = "config.json.tmp";
    public const string BackupFileName = "config.prev.json";

    private readonly IFileStore _files;
    private readonly IDataDirectory _dirs;

    public ConfigStore(IFileStore files, IDataDirectoryProvider dirs)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(dirs);
        _files = files;
        _dirs = dirs.GetDefault();
    }

    public string ConfigPath => Path.Combine(_dirs.RootPath, ConfigFileName);

    public string TempPath => Path.Combine(_dirs.RootPath, TempFileName);

    public string BackupPath => Path.Combine(_dirs.RecoveryPath, BackupFileName);

    public ConfigLoadResult Load()
    {
        if (!_files.Exists(ConfigPath))
        {
            return new ConfigLoadResult(null, ConfigLoadStatus.Fresh, []);
        }

        byte[] mainBytes;
        try
        {
            mainBytes = _files.ReadAllBytes(ConfigPath);
        }
        catch (Exception ex)
        {
            return Corrupt($"读取 config.json 失败：{ex.Message}", backupDiagnostic: null);
        }

        if (TryParse(mainBytes, "config.json", out var config, out var mainDiagnostic))
        {
            if (config.SchemaVersion > TileWallConfig.CurrentSchemaVersion)
            {
                return new ConfigLoadResult(
                    null,
                    ConfigLoadStatus.FutureVersion,
                    [$"config.json 的 schemaVersion={config.SchemaVersion} 高于当前支持版本 {TileWallConfig.CurrentSchemaVersion}；不自动迁移，保留原文件（设计 §16.6）。"]);
            }

            var violations = ConfigValidator.Validate(config);
            if (violations.Count == 0)
            {
                return new ConfigLoadResult(config, ConfigLoadStatus.Loaded, []);
            }

            mainDiagnostic = $"config.json 校验失败：{string.Join("; ", violations.Select(v => v.Code))}";
        }

        // 恢复链：主文件失败 → Recovery/config.prev.json
        if (_files.Exists(BackupPath))
        {
            try
            {
                var backupBytes = _files.ReadAllBytes(BackupPath);
                if (TryParse(backupBytes, BackupFileName, out var backupConfig, out var backupDiagnostic)
                    && ConfigValidator.Validate(backupConfig).Count == 0)
                {
                    return new ConfigLoadResult(
                        backupConfig,
                        ConfigLoadStatus.RecoveredFromBackup,
                        [mainDiagnostic, $"已从 {BackupFileName} 恢复上一有效配置；请检查主配置文件。"]);
                }

                return Corrupt(mainDiagnostic, $"备份 {BackupFileName} 亦不可用：{backupDiagnostic}");
            }
            catch (Exception ex)
            {
                return Corrupt(mainDiagnostic, $"备份 {BackupFileName} 读取失败：{ex.Message}");
            }
        }

        return Corrupt(mainDiagnostic, backupDiagnostic: null);
    }

    /// <summary>
    /// 校验 → 序列化 → 写 .tmp（落盘刷新）→ 备份上一版 → Move 原子替换。
    /// 校验有 Error 即抛 <see cref="ConfigValidationException"/>，磁盘零改动；
    /// 任一文件步骤失败：尽力删除 .tmp，异常上抛，config.json 保持旧内容。
    /// </summary>
    public ConfigSaveReport Save(TileWallConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var violations = ConfigValidator.Validate(config);
        if (violations.Count > 0)
        {
            throw new ConfigValidationException(violations);
        }

        var bytes = ConfigJson.Serialize(config);
        try
        {
            _files.CreateDirectory(_dirs.RootPath);

            // 3. 写临时文件（LocalFileStore 内 FlushToDisk）
            _files.WriteAllBytes(TempPath, bytes);

            // 4. 备份上一版（首保存无上一版 → 跳过）
            string? backupPath = null;
            if (_files.Exists(ConfigPath))
            {
                _files.CreateDirectory(_dirs.RecoveryPath);
                _files.Copy(ConfigPath, BackupPath, overwrite: true);
                backupPath = BackupPath;
            }

            // 5. 唯一提交点：同卷原子替换
            _files.Move(TempPath, ConfigPath, overwrite: true);
            return new ConfigSaveReport(backupPath is not null, ConfigPath, backupPath);
        }
        catch
        {
            TryDeleteTemp();
            throw;
        }
    }

    /// <summary>启动时清理残留 .tmp（尽力，不影响结果；§5.3 崩溃恢复语义）。</summary>
    public void CleanupTempFiles()
    {
        try
        {
            if (_files.Exists(TempPath))
            {
                _files.Delete(TempPath);
            }
        }
        catch
        {
            // 尽力而为：清理失败不影响加载/保存结果
        }
    }

    private void TryDeleteTemp()
    {
        try
        {
            if (_files.Exists(TempPath))
            {
                _files.Delete(TempPath);
            }
        }
        catch
        {
            // 尽力而为：原异常继续上抛
        }
    }

    private ConfigLoadResult Corrupt(string mainDiagnostic, string? backupDiagnostic)
    {
        var diagnostics = new List<string> { mainDiagnostic, "保留原文件不动，绝不以空墙覆盖（设计 §17.2）。" };
        if (backupDiagnostic is not null)
        {
            diagnostics.Add(backupDiagnostic);
        }

        return new ConfigLoadResult(null, ConfigLoadStatus.Corrupt, diagnostics);
    }

    private bool TryParse(byte[] bytes, string source, out TileWallConfig config, out string diagnostic)
    {
        try
        {
            config = ConfigJson.Deserialize(bytes);
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            config = null!;
            diagnostic = $"{source} 解析失败：{ex.Message}";
            return false;
        }
    }
}
