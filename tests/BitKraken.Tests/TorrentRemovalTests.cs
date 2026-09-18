using BitKraken.Models;
using BitKraken.Services;
using MonoTorrent;
using MonoTorrent.Client;
using Xunit;

namespace BitKraken.Tests;

/// <summary>
/// Removing a torrent, against a live engine, because that is where this keeps going wrong. The engine
/// refuses to unregister a torrent that is not stopped, and the reconcile loop is free to start one at
/// any moment - so removal is a small race, and a race is not something a fake can be asked about.
/// <para>
/// One class on purpose: xUnit runs tests in a class one at a time, and two live engines in one process
/// would fight over the listen port and the shared engine-state file.
/// </para>
/// </summary>
public class TorrentRemovalTests
{
    /// <summary>Long enough for a torrent to come up on a loaded CI machine, short enough to fail fast.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>A real, trackerless torrent over a real file, so nothing here touches the network.</summary>
    private static async Task<string> NewTorrentFileAsync(string directory, string name)
    {
        var payload = Path.Combine(directory, name + ".dat");
        await File.WriteAllBytesAsync(payload, new byte[256 * 1024]);

        var dictionary = await new TorrentCreator(TorrentType.V1Only).CreateAsync(new TorrentFileSource(payload));

        var torrent = Path.Combine(directory, name + ".torrent");
        await File.WriteAllBytesAsync(torrent, dictionary.Encode());
        return torrent;
    }

    private static async Task<TorrentService> NewServiceAsync(string root)
    {
        // Shared between every engine in this process, so a previous test's torrents do not get restored
        // into this one's.
        if (File.Exists(SettingsService.EngineStatePath)) File.Delete(SettingsService.EngineStatePath);

        var settings = new SettingsService(root);
        settings.Load();
        await settings.SaveAsync(new AppSettings
        {
            DownloadDirectory = Path.Combine(root, "downloads"),
        });

        var service = new TorrentService(settings, new TorrentPreferences(root), new NetworkBinding(settings));
        await service.InitializeAsync();
        return service;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }

        return condition();
    }

    [Fact]
    public async Task A_running_torrent_can_be_removed()
    {
        var root = TestEnvironment.NewDirectory();
        await using var service = await NewServiceAsync(root);

        var manager = await service.AddTorrentFileAsync(await NewTorrentFileAsync(root, "plain"));
        Assert.True(await WaitForAsync(() => manager.State != TorrentState.Stopped));

        await service.RemoveAsync(manager, deleteData: false);

        Assert.DoesNotContain(manager, service.Engine.Torrents);
    }

    /// <summary>
    /// The regression. Removal stops the torrent and then hands it to the engine, which insists it be
    /// stopped - and the reconcile loop, which runs on its own two-second timer, is entitled to start it
    /// again in between. When it did, removal came back as "The manager must be stopped before it can be
    /// unregistered", or hung on to its stop until it gave up and said the torrent would not settle.
    /// <para>
    /// The churn below is what the timer does, only without waiting two seconds for it: every pass is a
    /// fresh chance to start the torrent being removed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_torrent_can_be_removed_while_the_queue_keeps_trying_to_start_it()
    {
        var root = TestEnvironment.NewDirectory();
        await using var service = await NewServiceAsync(root);

        var manager = await service.AddTorrentFileAsync(await NewTorrentFileAsync(root, "contended"));
        Assert.True(await WaitForAsync(() => manager.State != TorrentState.Stopped));

        using var churning = new CancellationTokenSource();
        var churn = Task.Run(async () =>
        {
            while (!churning.IsCancellationRequested)
            {
                await service.StartAllAsync();
            }
        });

        try
        {
            await service.RemoveAsync(manager, deleteData: false);
        }
        finally
        {
            churning.Cancel();
            await churn;
        }

        Assert.DoesNotContain(manager, service.Engine.Torrents);
    }
}
