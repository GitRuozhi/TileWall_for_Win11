using TileWall.Core.Wall;

namespace TileWall.Core.Imaging;

/// <summary>Pure queue preparation. Callers adopt the returned state only after decoding succeeds.</summary>
public static class ImageCarouselSequence
{
    public static ImageCarouselState? Next(IEnumerable<string> candidateIds, ImageCarouselState? current,
        DateTimeOffset switchedUtc, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(candidateIds);
        random ??= Random.Shared;
        var candidates = candidateIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (candidates.Length == 0) return null;
        var candidateSet = candidates.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reusable = current is not null && current.CandidateIds is not null && current.Queue is not null &&
            current.NextIndex >= 0 && current.NextIndex <= current.Queue.Count &&
            current.CandidateIds.Count == candidates.Length && candidateSet.SetEquals(current.CandidateIds) &&
            current.Queue.Count == candidates.Length && candidateSet.SetEquals(current.Queue) &&
            current.Queue.Distinct(StringComparer.OrdinalIgnoreCase).Count() == candidates.Length;
        var queue = reusable ? current!.Queue!.ToArray() : candidates.ToArray();
        var index = reusable ? current!.NextIndex : 0;
        var round = current?.Round ?? 0;
        if (!reusable || index == queue.Length)
        {
            // Fisher-Yates supplies a uniform permutation; condition the first element at a round boundary.
            for (var i = queue.Length - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (queue[i], queue[j]) = (queue[j], queue[i]);
            }
            if (queue.Length > 1 && string.Equals(queue[0], current?.CurrentImageId, StringComparison.OrdinalIgnoreCase))
            {
                var next = random.Next(1, queue.Length);
                (queue[0], queue[next]) = (queue[next], queue[0]);
            }
            index = 0;
            round = checked(round + 1);
        }
        return new ImageCarouselState
        {
            CurrentImageId = queue[index], LastSwitchUtc = switchedUtc,
            CandidateIds = candidates, Queue = queue, NextIndex = index + 1, Round = round,
        };
    }
}
