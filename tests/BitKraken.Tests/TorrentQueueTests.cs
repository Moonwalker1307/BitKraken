using BitKraken.Services;
using Xunit;

namespace BitKraken.Tests;

public class TorrentQueueTests
{
    private static QueueCandidate Download(string key, long order) => new(key, IsSeed: false, order);
    private static QueueCandidate Seed(string key, long order) => new(key, IsSeed: true, order);

    [Fact]
    public void Nothing_wanting_to_run_means_nothing_running()
    {
        var plan = TorrentQueue.Plan([], maxDownloads: 3, maxSeeds: 3);

        Assert.Empty(plan.Running);
        Assert.Empty(plan.Waiting);
    }

    [Fact]
    public void Everything_runs_when_there_is_no_limit()
    {
        var plan = TorrentQueue.Plan(
            [Download("a", 1), Download("b", 2), Seed("c", 3)],
            maxDownloads: 0,
            maxSeeds: 0);

        Assert.Equal(["a", "b", "c"], plan.Running.OrderBy(k => k));
        Assert.Empty(plan.Waiting);
    }

    [Fact]
    public void The_limit_is_filled_in_queue_order_and_the_rest_wait_in_line()
    {
        var plan = TorrentQueue.Plan(
            [Download("third", 3), Download("first", 1), Download("second", 2), Download("fourth", 4)],
            maxDownloads: 2,
            maxSeeds: 0);

        Assert.Equal(["first", "second"], plan.Running.OrderBy(k => k));
        Assert.Equal(1, plan.Waiting["third"]);
        Assert.Equal(2, plan.Waiting["fourth"]);
    }

    [Fact]
    public void Seeds_cannot_crowd_out_a_download()
    {
        // Every seed is ahead of the download in queue order, and it still gets its slot: the two
        // queues are counted separately, which is the whole reason they are separate.
        var plan = TorrentQueue.Plan(
            [Seed("s1", 1), Seed("s2", 2), Seed("s3", 3), Download("d", 99)],
            maxDownloads: 1,
            maxSeeds: 1);

        Assert.Equal(["d", "s1"], plan.Running.OrderBy(k => k));
        Assert.Equal(1, plan.Waiting["s2"]);
        Assert.Equal(2, plan.Waiting["s3"]);
        Assert.DoesNotContain("d", plan.Waiting.Keys);
    }

    [Fact]
    public void A_torrent_moved_to_the_top_takes_the_next_slot()
    {
        // What MoveToTop does to the store: a queue order below everything else.
        var plan = TorrentQueue.Plan(
            [Download("a", 1), Download("b", 2), Download("promoted", -1)],
            maxDownloads: 1,
            maxSeeds: 0);

        Assert.Equal(["promoted"], plan.Running);
        Assert.Equal(1, plan.Waiting["a"]);
        Assert.Equal(2, plan.Waiting["b"]);
    }

    [Fact]
    public void Torrents_sharing_a_queue_position_are_still_ordered_the_same_way_every_time()
    {
        // An unstable plan would start and stop the same torrents on alternate ticks.
        var first = TorrentQueue.Plan([Download("b", 0), Download("a", 0)], maxDownloads: 1, maxSeeds: 0);
        var second = TorrentQueue.Plan([Download("a", 0), Download("b", 0)], maxDownloads: 1, maxSeeds: 0);

        Assert.Equal(first.Running, second.Running);
        Assert.Equal(["a"], first.Running);
    }

    [Fact]
    public void A_negative_limit_is_treated_as_no_limit()
    {
        var plan = TorrentQueue.Plan([Download("a", 1), Download("b", 2)], maxDownloads: -1, maxSeeds: -1);

        Assert.Equal(2, plan.Running.Count);
    }

    [Fact]
    public void Keys_are_matched_without_regard_to_case()
    {
        // Info hashes reach us as hex from several places, and not always in the same case.
        var plan = TorrentQueue.Plan([Download("ABC", 1)], maxDownloads: 1, maxSeeds: 0);

        Assert.Contains("abc", plan.Running);
    }
}
