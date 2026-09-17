using BitKraken.Models;
using BitKraken.Services;
using Xunit;

namespace BitKraken.Tests;

public class SeedLimitTests
{
    [Fact]
    public void With_nothing_set_a_torrent_seeds_forever()
    {
        var limit = SeedLimit.For(new AppSettings(), new TorrentOverrides());

        Assert.True(limit.IsUnlimited);
        Assert.False(limit.IsReached(ratio: 99, seeded: TimeSpan.FromDays(30)));
        Assert.Equal("", limit.Describe());
    }

    [Fact]
    public void The_global_limits_apply_when_a_torrent_has_none_of_its_own()
    {
        var settings = new AppSettings { SeedRatioLimit = 2, SeedTimeLimitMinutes = 60 };
        var limit = SeedLimit.For(settings, new TorrentOverrides());

        Assert.Equal(2, limit.Ratio);
        Assert.Equal(TimeSpan.FromHours(1), limit.Time);
    }

    [Fact]
    public void A_torrents_own_limit_wins_over_the_global_one()
    {
        var settings = new AppSettings { SeedRatioLimit = 2, SeedTimeLimitMinutes = 60 };
        var overrides = new TorrentOverrides { SeedRatioLimit = 10, SeedTimeLimitMinutes = 0 };
        var limit = SeedLimit.For(settings, overrides);

        Assert.Equal(10, limit.Ratio);

        // Zero in the override is "never", not "fall back to the global".
        Assert.Equal(TimeSpan.Zero, limit.Time);
        Assert.False(limit.IsReached(ratio: 3, seeded: TimeSpan.FromDays(1)));
        Assert.True(limit.IsReached(ratio: 10, seeded: TimeSpan.Zero));
    }

    [Fact]
    public void Either_limit_on_its_own_is_enough_to_stop_seeding()
    {
        var limit = SeedLimit.For(
            new AppSettings { SeedRatioLimit = 2, SeedTimeLimitMinutes = 60 },
            new TorrentOverrides());

        Assert.False(limit.IsReached(ratio: 1.9, seeded: TimeSpan.FromMinutes(59)));
        Assert.True(limit.IsReached(ratio: 2.0, seeded: TimeSpan.FromMinutes(1)));
        Assert.True(limit.IsReached(ratio: 0.1, seeded: TimeSpan.FromMinutes(60)));
    }

    [Fact]
    public void A_limit_reads_the_way_it_was_set()
    {
        Assert.Equal("ratio 1.5", SeedLimit.For(new AppSettings { SeedRatioLimit = 1.5 }, new TorrentOverrides()).Describe());
        Assert.Equal("2h 0m", SeedLimit.For(new AppSettings { SeedTimeLimitMinutes = 120 }, new TorrentOverrides()).Describe());
        Assert.Equal(
            "ratio 2 or 30m",
            SeedLimit.For(new AppSettings { SeedRatioLimit = 2, SeedTimeLimitMinutes = 30 }, new TorrentOverrides()).Describe());
    }

    [Fact]
    public void The_ratio_is_what_went_out_over_what_came_in()
    {
        Assert.Equal(0.5, ShareRatio.Of(uploaded: 50, downloaded: 100, bytesOnDisk: 100));
        Assert.Equal(2, ShareRatio.Of(uploaded: 200, downloaded: 100, bytesOnDisk: 100));
    }

    [Fact]
    public void A_torrent_seeded_from_data_already_on_disk_has_a_finite_ratio()
    {
        // Nothing was downloaded, so dividing by what came in would make the ratio infinite from the
        // first block sent. The data we are seeding is the honest denominator.
        Assert.Equal(0.5, ShareRatio.Of(uploaded: 500, downloaded: 0, bytesOnDisk: 1000));
    }

    [Fact]
    public void A_torrent_with_nothing_on_either_side_has_no_ratio_yet()
    {
        Assert.Equal(0, ShareRatio.Of(uploaded: 0, downloaded: 0, bytesOnDisk: 0));
        Assert.Equal(0, ShareRatio.Of(uploaded: 10, downloaded: 0, bytesOnDisk: 0));
    }
}
