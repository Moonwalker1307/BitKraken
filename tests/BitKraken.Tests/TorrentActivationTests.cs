using BitKraken.Services;
using Xunit;

namespace BitKraken.Tests;

public class TorrentActivationTests
{
    [Fact]
    public void Normally_a_start_request_starts_the_torrent()
    {
        var activation = new TorrentActivation();

        Assert.True(activation.RequestStart("a"));
        Assert.True(activation.RequestRestore("a"));
        Assert.False(activation.IsSuspended);
    }

    [Fact]
    public void A_paused_torrent_is_not_restored_but_can_still_be_started_by_hand()
    {
        var activation = new TorrentActivation();
        activation.Pause("a");

        Assert.False(activation.RequestRestore("a"));
        Assert.True(activation.RequestStart("a"));
        Assert.False(activation.IsPausedByUser("a"));
    }

    [Fact]
    public void While_suspended_nothing_starts_and_what_was_running_comes_back()
    {
        var activation = new TorrentActivation();
        Assert.True(activation.Suspend());
        Assert.False(activation.Suspend());   // already suspended

        activation.Defer("running");
        Assert.False(activation.RequestStart("asked-for-while-down"));

        var owed = activation.Resume();

        Assert.False(activation.IsSuspended);
        Assert.Equal(new[] { "asked-for-while-down", "running" }, owed.OrderBy(k => k));
    }

    [Fact]
    public void A_torrent_the_user_paused_is_not_resumed_by_the_network_coming_back()
    {
        // The whole point of keeping the two apart: the kill switch may only undo what it did.
        var activation = new TorrentActivation();
        activation.Pause("paused");
        activation.Suspend();
        activation.Defer("paused");
        activation.Defer("running");

        Assert.Equal(new[] { "running" }, activation.Resume());
    }

    [Fact]
    public void Pausing_while_suspended_cancels_the_pending_restart()
    {
        var activation = new TorrentActivation();
        activation.Suspend();
        activation.RequestStart("a");
        activation.Pause("a");

        Assert.Empty(activation.Resume());
        Assert.True(activation.IsPausedByUser("a"));
    }

    [Fact]
    public void A_removed_torrent_is_forgotten_by_both_halves()
    {
        var activation = new TorrentActivation();
        activation.Pause("a");
        activation.Suspend();
        activation.Defer("b");

        activation.Forget("a");
        activation.Forget("b");

        Assert.False(activation.IsPausedByUser("a"));
        Assert.Empty(activation.Resume());
    }

    [Fact]
    public void Only_user_paused_torrents_are_persisted()
    {
        var activation = new TorrentActivation();
        activation.Pause("paused");
        activation.Suspend();
        activation.Defer("held-by-the-network");

        // A torrent the network stopped must not come back as "paused" after a restart.
        Assert.Equal(new[] { "paused" }, activation.PausedByUser.ToArray());
    }

    [Fact]
    public void The_paused_set_survives_a_restart()
    {
        var activation = new TorrentActivation();
        activation.LoadPaused(["a", "b"]);

        Assert.True(activation.IsPausedByUser("a"));
        Assert.False(activation.RequestRestore("b"));
    }
}
