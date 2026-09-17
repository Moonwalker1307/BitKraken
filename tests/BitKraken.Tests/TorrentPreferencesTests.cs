using System.Text.Json;
using BitKraken.Services;
using Xunit;

namespace BitKraken.Tests;

public class TorrentPreferencesTests
{
    private static TorrentPreferences Loaded(string directory)
    {
        var preferences = new TorrentPreferences(directory);
        preferences.Load();
        return preferences;
    }

    [Fact]
    public void An_unknown_torrent_reads_as_defaults_without_being_remembered()
    {
        var dir = TestEnvironment.NewDirectory();
        var preferences = Loaded(dir);

        var entry = preferences.Get("nope");

        Assert.False(entry.PausedByUser);
        Assert.Null(entry.Sequential);
        Assert.Null(entry.MaxDownloadRateKiB);

        // Reading about a torrent must not create a file full of torrents we never had.
        preferences.Save();
        Assert.False(File.Exists(Path.Combine(dir, "torrents.json")));
    }

    [Fact]
    public void Everything_remembered_about_a_torrent_survives_a_restart()
    {
        var dir = TestEnvironment.NewDirectory();
        var preferences = Loaded(dir);

        preferences.Update("aa", e =>
        {
            e.PausedByUser = true;
            e.Sequential = true;
            e.MaxDownloadRateKiB = 512;
            e.MaxUploadRateKiB = 0;
            e.SeedRatioLimit = 1.5;
            e.SeedTimeLimitMinutes = 90;
            e.SeedLimitReached = true;
            e.TotalUploaded = 1234;
            e.TotalDownloaded = 5678;
            e.SecondsSeeded = 4242;
        });
        preferences.Save();

        var entry = Loaded(dir).Get("aa");

        Assert.True(entry.PausedByUser);
        Assert.True(entry.Sequential);
        Assert.Equal(512, entry.MaxDownloadRateKiB);
        Assert.Equal(0, entry.MaxUploadRateKiB);
        Assert.Equal(1.5, entry.SeedRatioLimit);
        Assert.Equal(90, entry.SeedTimeLimitMinutes);
        Assert.True(entry.SeedLimitReached);
        Assert.Equal(1234, entry.TotalUploaded);
        Assert.Equal(5678, entry.TotalDownloaded);
        Assert.Equal(4242, entry.SecondsSeeded);
    }

    [Fact]
    public void The_paused_set_written_by_an_older_build_is_picked_up()
    {
        var dir = TestEnvironment.NewDirectory();
        File.WriteAllText(Path.Combine(dir, "paused.json"), JsonSerializer.Serialize(new[] { "aa", "bb" }));

        var preferences = Loaded(dir);

        Assert.True(preferences.Get("aa").PausedByUser);
        Assert.True(preferences.Get("bb").PausedByUser);
        Assert.False(preferences.Get("cc").PausedByUser);
    }

    [Fact]
    public void Once_the_new_store_exists_the_older_builds_file_is_ignored()
    {
        // The old file is only ever read to migrate from it, which happens once. After that it is a
        // leftover, and a stale copy of it must not resurrect torrents the user has since unpaused.
        var dir = TestEnvironment.NewDirectory();
        File.WriteAllText(Path.Combine(dir, "paused.json"), JsonSerializer.Serialize(new[] { "stale" }));
        File.WriteAllText(
            Path.Combine(dir, "torrents.json"),
            """{"current":{"PausedByUser":true,"QueueOrder":1}}""");

        var preferences = Loaded(dir);

        Assert.True(preferences.Get("current").PausedByUser);
        Assert.False(preferences.Get("stale").PausedByUser);
    }

    [Fact]
    public void Migrating_the_older_builds_file_carries_the_paused_set_into_the_new_store()
    {
        var dir = TestEnvironment.NewDirectory();
        File.WriteAllText(Path.Combine(dir, "paused.json"), JsonSerializer.Serialize(new[] { "aa" }));

        var migrated = Loaded(dir);
        migrated.Update("bb", e => e.PausedByUser = true);
        migrated.Save();

        // Both the migrated entry and the new one are in the store that gets read from now on.
        var preferences = Loaded(dir);
        Assert.True(preferences.Get("aa").PausedByUser);
        Assert.True(preferences.Get("bb").PausedByUser);
    }

    [Fact]
    public void A_corrupt_store_costs_the_queue_order_but_never_the_startup()
    {
        var dir = TestEnvironment.NewDirectory();
        File.WriteAllText(Path.Combine(dir, "torrents.json"), "{ this is not json");

        var preferences = Loaded(dir);

        Assert.False(preferences.Get("aa").PausedByUser);
        preferences.Update("aa", e => e.PausedByUser = true);
        Assert.True(preferences.Get("aa").PausedByUser);
    }

    [Fact]
    public void Torrents_keep_the_order_they_arrived_in()
    {
        var dir = TestEnvironment.NewDirectory();
        var preferences = Loaded(dir);

        preferences.EnsureTracked("first");
        preferences.EnsureTracked("second");
        preferences.EnsureTracked("third");

        Assert.True(preferences.Get("first").QueueOrder < preferences.Get("second").QueueOrder);
        Assert.True(preferences.Get("second").QueueOrder < preferences.Get("third").QueueOrder);
    }

    [Fact]
    public void Tracking_a_torrent_twice_does_not_move_it_down_the_queue()
    {
        var dir = TestEnvironment.NewDirectory();
        var preferences = Loaded(dir);

        preferences.EnsureTracked("a");
        var order = preferences.Get("a").QueueOrder;
        preferences.EnsureTracked("b");
        preferences.EnsureTracked("a");

        Assert.Equal(order, preferences.Get("a").QueueOrder);
    }

    [Fact]
    public void Moving_to_the_top_or_bottom_puts_a_torrent_outside_everything_else()
    {
        var dir = TestEnvironment.NewDirectory();
        var preferences = Loaded(dir);

        preferences.EnsureTracked("a");
        preferences.EnsureTracked("b");
        preferences.EnsureTracked("c");

        preferences.MoveToTop("c");
        Assert.True(preferences.Get("c").QueueOrder < preferences.Get("a").QueueOrder);

        preferences.MoveToBottom("c");
        Assert.True(preferences.Get("c").QueueOrder > preferences.Get("b").QueueOrder);
    }

    [Fact]
    public void The_queue_order_of_torrents_that_were_already_there_is_never_reused()
    {
        var dir = TestEnvironment.NewDirectory();
        var first = Loaded(dir);
        first.EnsureTracked("old");
        first.MoveToBottom("old");
        var oldOrder = first.Get("old").QueueOrder;
        first.Save();

        var second = Loaded(dir);
        second.EnsureTracked("new");

        Assert.True(second.Get("new").QueueOrder > oldOrder);
    }

    [Fact]
    public void A_removed_torrent_is_forgotten()
    {
        var dir = TestEnvironment.NewDirectory();
        var preferences = Loaded(dir);
        preferences.Update("aa", e => e.PausedByUser = true);

        preferences.Forget("aa");
        preferences.Save();

        Assert.False(Loaded(dir).Get("aa").PausedByUser);
    }

    [Fact]
    public void Info_hashes_are_matched_without_regard_to_case()
    {
        var dir = TestEnvironment.NewDirectory();
        var preferences = Loaded(dir);

        preferences.Update("AABB", e => e.PausedByUser = true);

        Assert.True(preferences.Get("aabb").PausedByUser);
    }
}
