using TileWall.Core.Grid;

namespace TileWall.Core.Configuration;

/// <summary>一条结构不变式违规：固定错误码 + 对象 Id（可空）+ 说明。</summary>
public sealed record ConfigViolation(string Code, string? ObjectId, string Message);

/// <summary>保存前校验失败（ConfigStore.Save 不落盘直接上抛，§5.3）。</summary>
public sealed class ConfigValidationException : Exception
{
    public ConfigValidationException(IReadOnlyList<ConfigViolation> violations)
        : base(BuildMessage(violations))
        => Violations = violations;

    public IReadOnlyList<ConfigViolation> Violations { get; }

    private static string BuildMessage(IReadOnlyList<ConfigViolation> violations) =>
        $"配置校验失败，共 {violations.Count} 项：{string.Join("; ", violations.Select(v => $"{v.Code}({v.ObjectId ?? "-"}) {v.Message}"))}";
}

/// <summary>
/// 配置结构校验器（设计 §2.4 结构条件、§4.2 单一真值规则、§5.3）。
/// 独立谓词逐条上报（一条违规一条记录），调用方据 Count==0 判定通过。
/// </summary>
public static class ConfigValidator
{
    // —— 固定错误码（验收映射：C20 → DUPLICATE_ID 等）——
    public const string SchemaVersionUnsupported = "SCHEMA_VERSION_UNSUPPORTED";
    public const string WallInvalid = "WALL_INVALID";
    public const string EmptyId = "EMPTY_ID";
    public const string DuplicateId = "DUPLICATE_ID";
    public const string ObjectOutOfWall = "OBJECT_OUT_OF_WALL";
    public const string ObjectCrossesColumn = "OBJECT_CROSSES_COLUMN";
    public const string TileTooWide = "TILE_TOO_WIDE";
    public const string GroupSizeOutOfRange = "GROUP_SIZE_OUT_OF_RANGE";
    public const string ObjectsOverlap = "OBJECTS_OVERLAP";
    public const string PartitionOutOfBounds = "PARTITION_OUT_OF_BOUNDS";
    public const string PartitionOverlap = "PARTITION_OVERLAP";
    public const string PartitionHole = "PARTITION_HOLE";
    public const string PartitionCover = "PARTITION_COVER";
    public const string TitleTruthConflict = "TITLE_TRUTH_CONFLICT";

    public static IReadOnlyList<ConfigViolation> Validate(TileWallConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var violations = new List<ConfigViolation>();

        if (config.SchemaVersion != TileWallConfig.CurrentSchemaVersion)
        {
            violations.Add(new ConfigViolation(
                SchemaVersionUnsupported, null,
                $"schemaVersion={config.SchemaVersion}，当前支持 {TileWallConfig.CurrentSchemaVersion}。"));
        }

        if (config.Wall.Columns < 1 || config.Wall.Rows < 1)
        {
            violations.Add(new ConfigViolation(
                WallInvalid, null,
                $"墙尺寸非法：{config.Wall.Columns} 栏 × {config.Wall.Rows} 行。"));
            return violations; // 无墙无法继续几何检查
        }

        var wall = new WallGrid(config.Wall.Columns, config.Wall.Rows);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var placedRects = new List<(string Id, GridRect Bounds)>();

        foreach (var o in config.Objects)
        {
            var id = o.Id;
            if (string.IsNullOrWhiteSpace(id))
            {
                violations.Add(new ConfigViolation(EmptyId, null, "对象 Id 为空或纯空白。"));
            }
            else if (!seenIds.Add(id))
            {
                violations.Add(new ConfigViolation(
                    DuplicateId, id, "配置内 Id 必须唯一（验收 C20：同名磁贴互不影响，以稳定标识隔离）。"));
            }

            // 墙级几何（独立谓词；GridPlacement 的固定次序属放置 API，此处逐条上报）
            if (!wall.Contains(o.Bounds))
            {
                violations.Add(new ConfigViolation(ObjectOutOfWall, id, $"对象矩形 {o.Bounds} 越出墙 {wall.Columns}×{wall.Rows}。"));
            }

            if (!wall.InSingleColumn(o.Bounds))
            {
                violations.Add(new ConfigViolation(ObjectCrossesColumn, id, $"对象矩形 {o.Bounds} 跨越竖栏分隔。"));
            }

            switch (o)
            {
                case TileObject when o.Bounds.Width > WallGrid.TileMaxCells:
                    violations.Add(new ConfigViolation(TileTooWide, id, $"独立磁贴宽 {o.Bounds.Width} 超过一栏八格。"));
                    break;
                case GroupObject when !GridPlacement.IsGroupSizeInRange(o.Bounds.Size):
                    violations.Add(new ConfigViolation(
                        GroupSizeOutOfRange, id,
                        $"组尺寸 {o.Bounds.Width}×{o.Bounds.Height} 超出界限（列 {WallGrid.GroupMinColumns}—{WallGrid.GroupMaxColumns}，行 {WallGrid.GroupMinRows}—{WallGrid.GroupMaxRows}）。"));
                    break;
            }

            if (o is GroupObject group)
            {
                ValidatePartitions(group, violations);
            }

            // 单一真值规则（schema 直接编码，§4.2）：有入口对象 TitleText 必须为 null
            if (o.Entry is not null && o.Visual.TitleText is not null)
            {
                violations.Add(new ConfigViolation(
                    TitleTruthConflict, id,
                    "有入口对象的名称真值在托管文件名主体，TitleText 必须为 null（设计 §16.2）。"));
            }

            placedRects.Add((id, o.Bounds));
        }

        // 墙面对象两两不重叠（设计 §2.4）
        for (var i = 0; i < placedRects.Count; i++)
        {
            for (var j = i + 1; j < placedRects.Count; j++)
            {
                if (placedRects[i].Bounds.Intersects(placedRects[j].Bounds))
                {
                    violations.Add(new ConfigViolation(
                        ObjectsOverlap,
                        placedRects[j].Id,
                        $"对象 {placedRects[i].Id} 与 {placedRects[j].Id} 的矩形相交：{placedRects[i].Bounds} / {placedRects[j].Bounds}。"));
                }
            }
        }

        return violations;
    }

    private static void ValidatePartitions(GroupObject group, List<ConfigViolation> violations)
    {
        var bounds = group.Bounds;
        var id = group.Id;

        // 越界（含 0/负尺寸）
        foreach (var p in group.Partitions)
        {
            if (p.Width < 1 || p.Height < 1 || p.Column < 0 || p.Row < 0 || p.Right > bounds.Width || p.Bottom > bounds.Height)
            {
                violations.Add(new ConfigViolation(
                    PartitionOutOfBounds, id,
                    $"分区 {p} 越出组 Bounds {bounds}（组内相对坐标）。"));
            }
        }

        // 两两不交
        for (var i = 0; i < group.Partitions.Count; i++)
        {
            for (var j = i + 1; j < group.Partitions.Count; j++)
            {
                if (group.Partitions[i].Intersects(group.Partitions[j]))
                {
                    violations.Add(new ConfigViolation(
                        PartitionOverlap, id,
                        $"分区 {group.Partitions[i]} 与 {group.Partitions[j]} 相交（设计 §2.4 互不重叠）。"));
                }
            }
        }

        // 完整覆盖：格占据表 → 覆盖数 == 面积；空洞 = 未覆盖格在某行/列上两侧均有覆盖格
        var width = bounds.Width;
        var height = bounds.Height;
        var covered = new bool[width * height];
        foreach (var p in group.Partitions)
        {
            for (var row = Math.Max(0, p.Row); row < Math.Min(height, p.Bottom); row++)
            {
                var baseIndex = row * width;
                for (var col = Math.Max(0, p.Column); col < Math.Min(width, p.Right); col++)
                {
                    covered[baseIndex + col] = true;
                }
            }
        }

        var coveredCount = 0;
        for (var i = 0; i < covered.Length; i++)
        {
            if (covered[i])
            {
                coveredCount++;
            }
        }

        if (coveredCount == width * height)
        {
            return;
        }

        violations.Add(new ConfigViolation(
            PartitionCover, id,
            $"分区未完整覆盖组 Bounds：覆盖 {coveredCount}/{width * height} 格（设计 §2.4）。"));

        var hasHole = false;
        for (var row = 0; row < height && !hasHole; row++)
        {
            for (var col = 0; col < width && !hasHole; col++)
            {
                if (covered[(row * width) + col])
                {
                    continue;
                }

                var coveredLeft = false;
                var coveredRight = false;
                for (var c = 0; c < width; c++)
                {
                    if (covered[(row * width) + c])
                    {
                        coveredLeft |= c < col;
                        coveredRight |= c > col;
                    }
                }

                var coveredAbove = false;
                var coveredBelow = false;
                for (var r = 0; r < height; r++)
                {
                    if (covered[(r * width) + col])
                    {
                        coveredAbove |= r < row;
                        coveredBelow |= r > row;
                    }
                }

                hasHole = (coveredLeft && coveredRight) || (coveredAbove && coveredBelow);
            }
        }

        if (hasHole)
        {
            violations.Add(new ConfigViolation(
                PartitionHole, id,
                "分区存在内部空洞（设计 §2.4 无空洞）。"));
        }
    }
}
