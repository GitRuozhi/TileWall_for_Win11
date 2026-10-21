using TileWall.Core.Imaging;

namespace TileWall.Core.Wall;

/// <summary>Model rules shared by persistence validation, editors and rendering.</summary>
public static class MemberPresentationRules
{
    public static string? Validate(WallMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (member.Visual is not { } visual) return "VISUAL_NULL";
        if (!Enum.IsDefined(visual.TitleMode) || !Enum.IsDefined(visual.BackgroundMode)) return "VISUAL_ENUM";
        if (visual.BackgroundTransparency is < 0 or > 100) return "VISUAL_TRANSPARENCY";
        if (visual.BackgroundMode == MemberBackgroundMode.Color && !IsColor(visual.BackgroundColor))
            return "VISUAL_COLOR";
        if (member.Images is not { } images || !Enum.IsDefined(images.Kind)) return "IMAGE_SOURCE";
        if ((images.Kind == MemberImageSourceKind.Folder ||
            images.Kind == MemberImageSourceKind.Single && images.Media is null) && string.IsNullOrWhiteSpace(images.Path))
            return "IMAGE_SOURCE_PATH";
        if (images.Media is not null && images.Kind != MemberImageSourceKind.Single) return "IMAGE_SOURCE_MEDIA";
        if (member.ImageDisplay is not { } display || !Enum.IsDefined(display.Mode) ||
            !double.IsFinite(display.CustomScale) || display.CustomScale <= 0 ||
            display.Mode == ImageDisplayMode.Custom && (display.CustomScale < MemberImageGeometry.MinimumCustomScale || display.CustomScale > MemberImageGeometry.MaximumCustomScale)) return "IMAGE_DISPLAY";
        if (!Enum.IsDefined(member.RotationInterval)) return "IMAGE_ROTATION_INTERVAL";
        if (member.Carousel is { } state && (state.CandidateIds is null || state.Queue is null ||
            state.Round < 0 || state.Round == long.MaxValue || state.NextIndex < 0 || state.NextIndex > state.Queue.Count ||
            state.CandidateIds.Any(string.IsNullOrWhiteSpace) || state.Queue.Any(string.IsNullOrWhiteSpace) ||
            state.CandidateIds.Count != state.Queue.Count ||
            state.CandidateIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.CandidateIds.Count ||
            !state.CandidateIds.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(state.Queue) ||
            state.Queue.Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Queue.Count ||
            state.CurrentImageId is not null && !state.CandidateIds.Contains(state.CurrentImageId, StringComparer.OrdinalIgnoreCase)))
            return "IMAGE_CAROUSEL";
        return ValidateImportSource(member.ImportSource, memberSource: true);
    }

    public static string? ValidateImportSource(ImportSourceMetadata? source, bool memberSource = false)
    {
        if (source is null) return null;
        if (!Enum.IsDefined(source.Kind) || string.IsNullOrWhiteSpace(source.BatchId) ||
            string.IsNullOrWhiteSpace(source.GroupBucketKey) || string.IsNullOrWhiteSpace(source.OriginGroupId))
            return "IMPORT_SOURCE";
        if (memberSource && (string.IsNullOrWhiteSpace(source.SourceRoot) || string.IsNullOrWhiteSpace(source.RelativePath)))
            return "IMPORT_MEMBER_SOURCE";
        return null;
    }

    /// <summary>Automatic carousel progress and source bookkeeping never claim a user's content.</summary>
    public static bool HasUserPropertyChanges(WallMember original, WallMember replacement)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(replacement);
        if (original.GetType() != replacement.GetType() || original.LocalBounds != replacement.LocalBounds ||
            original.Visual != replacement.Visual || original.Entry != replacement.Entry ||
            original.TargetInput != replacement.TargetInput || original.Images != replacement.Images ||
            original.ImageDisplay != replacement.ImageDisplay || original.RotationInterval != replacement.RotationInterval)
            return true;
        return (original, replacement) switch
        {
            (PuzzleMember a, PuzzleMember b) => !a.Partitions.SequenceEqual(b.Partitions),
            (ClockMember a, ClockMember b) => a.TimeFormat != b.TimeFormat || a.DateFormat != b.DateFormat,
            _ => false,
        };
    }

    public static string ResolveTitle(WallMember member, bool targetIsFolder = false) =>
        member.Visual.TitleMode == MemberTitleMode.Manual ? member.Visual.Title ?? string.Empty :
            AutomaticTitle(member.TargetInput, targetIsFolder);

    /// <summary>Uses the selected shortcut's own name, never its resolved executable's name.</summary>
    public static string AutomaticTitle(string? targetInput, bool targetIsFolder = false)
    {
        if (string.IsNullOrWhiteSpace(targetInput)) return string.Empty;
        var input = targetInput.Trim();
        if (!LooksLikeWindowsDrivePath(input) && Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
            !uri.Scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(uri.Host) ? uri.Scheme : uri.Host;
        }
        var leaf = input.Replace('\\', '/').TrimEnd('/').Split('/')[^1];
        if (targetIsFolder) return leaf;
        var dot = leaf.LastIndexOf('.');
        return dot > 0 ? leaf[..dot] : leaf;
    }

    private static bool IsColor(string? value) => value is not null && value.Length is 7 or 9 &&
        value[0] == '#' && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    private static bool LooksLikeWindowsDrivePath(string value) =>
        value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && (value[2] is '\\' or '/');
}
