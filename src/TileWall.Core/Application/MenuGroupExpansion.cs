namespace TileWall.Core.Application;

/// <summary>Oldest-first expanded group IDs. Wall content and shortcut ownership stay in WallDocument.</summary>
public sealed class MenuGroupExpansion
{
    public const int MaximumExpanded = 5;
    private readonly List<string> _expanded;

    public MenuGroupExpansion(IEnumerable<string>? expanded = null) => _expanded = (expanded ?? [])
        .Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 200)
        .Distinct(StringComparer.Ordinal).TakeLast(MaximumExpanded).ToList();

    public IReadOnlyList<string> Expanded => _expanded.ToArray();
    public bool Contains(string id) => _expanded.Contains(id, StringComparer.Ordinal);

    public void Toggle(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200) throw new ArgumentException("Invalid group ID.", nameof(id));
        if (_expanded.Remove(id)) return;
        _expanded.Add(id);
        if (_expanded.Count > MaximumExpanded) _expanded.RemoveAt(0);
    }

    public bool Retain(IEnumerable<string> groupIds)
    {
        var existing = groupIds.ToHashSet(StringComparer.Ordinal);
        return _expanded.RemoveAll(id => !existing.Contains(id)) > 0;
    }
}
