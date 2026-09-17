namespace BitKraken.Services;

/// <summary>One torrent that wants to run, as the queue sees it.</summary>
/// <param name="Key">Info hash.</param>
/// <param name="IsSeed">True once the torrent is complete, which puts it in the seeding queue instead.</param>
/// <param name="Order">Queue position: lower runs first.</param>
internal readonly record struct QueueCandidate(string Key, bool IsSeed, long Order);

/// <summary>What the queue decided: who runs, and where everyone else is waiting.</summary>
/// <param name="Running">The torrents that may run right now.</param>
/// <param name="Waiting">Everyone held back, with their 1-based place in their own queue.</param>
internal sealed record QueuePlan(IReadOnlySet<string> Running, IReadOnlyDictionary<string, int> Waiting)
{
    public static QueuePlan Empty { get; } =
        new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Decides which of the torrents that want to run actually may. Downloads and seeds are counted
/// separately, so a shelf full of finished torrents can never crowd out a download.
/// </summary>
internal static class TorrentQueue
{
    /// <param name="candidates">Every torrent that is neither paused nor held by the kill switch.</param>
    /// <param name="maxDownloads">Concurrent downloads allowed. 0 or less means no limit.</param>
    /// <param name="maxSeeds">Concurrent seeds allowed. 0 or less means no limit.</param>
    public static QueuePlan Plan(IEnumerable<QueueCandidate> candidates, int maxDownloads, int maxSeeds)
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var waiting = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Ordering by key as well keeps the plan stable when two torrents share a queue position,
        // which matters because an unstable plan would stop and start torrents on alternate ticks.
        var ordered = candidates
            .OrderBy(c => c.Order)
            .ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Fill(ordered.Where(c => !c.IsSeed), maxDownloads);
        Fill(ordered.Where(c => c.IsSeed), maxSeeds);

        return new QueuePlan(running, waiting);

        void Fill(IEnumerable<QueueCandidate> queue, int limit)
        {
            var index = 0;
            foreach (var candidate in queue)
            {
                if (limit <= 0 || index < limit)
                    running.Add(candidate.Key);
                else
                    waiting[candidate.Key] = index - limit + 1;

                index++;
            }
        }
    }
}
