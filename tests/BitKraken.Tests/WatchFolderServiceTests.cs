using BitKraken.Models;
using BitKraken.Services;
using Xunit;

namespace BitKraken.Tests;

public class WatchFolderServiceTests
{
    /// <summary>Long enough for the watcher's first sweep on a loaded CI machine, short enough to fail fast.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static async Task<SettingsService> SettingsForAsync(string root, string watchFolder, WatchFolderAction action)
    {
        var settings = new SettingsService(root);
        settings.Load();
        await settings.SaveAsync(new AppSettings
        {
            DownloadDirectory = Path.Combine(root, "downloads"),
            WatchFolder = watchFolder,
            WatchFolderAction = action,
        });
        return settings;
    }

    /// <summary>Waits for <paramref name="condition"/> rather than for a fixed time, so the tests aren't flaky.</summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }

        return condition();
    }

    [Fact]
    public async Task A_torrent_already_in_the_folder_is_picked_up()
    {
        var root = TestEnvironment.NewDirectory();
        var watched = Path.Combine(root, "watch");
        Directory.CreateDirectory(watched);
        var torrent = Path.Combine(watched, "example.torrent");
        await File.WriteAllTextAsync(torrent, "d4:infod4:name7:examplee");

        var settings = await SettingsForAsync(root, watched, WatchFolderAction.MarkAsAdded);
        var offered = new List<string>();

        await using var service = new WatchFolderService(settings, path =>
        {
            lock (offered) offered.Add(path);
            return Task.FromResult(true);
        });
        service.Apply();

        Assert.True(await WaitForAsync(() => { lock (offered) return offered.Count > 0; }));
        Assert.Equal(torrent, offered[0]);
    }

    [Fact]
    public async Task An_added_torrent_is_renamed_so_it_is_not_offered_again()
    {
        var root = TestEnvironment.NewDirectory();
        var watched = Path.Combine(root, "watch");
        Directory.CreateDirectory(watched);
        var torrent = Path.Combine(watched, "example.torrent");
        await File.WriteAllTextAsync(torrent, "d4:infoe");

        var settings = await SettingsForAsync(root, watched, WatchFolderAction.MarkAsAdded);
        var offers = 0;

        await using var service = new WatchFolderService(settings, _ =>
        {
            Interlocked.Increment(ref offers);
            return Task.FromResult(true);
        });
        service.Apply();

        Assert.True(await WaitForAsync(() => File.Exists(torrent + WatchFolderService.AddedSuffix)));
        Assert.False(File.Exists(torrent));
        Assert.Equal(1, Volatile.Read(ref offers));
    }

    [Fact]
    public async Task An_added_torrent_is_deleted_when_that_is_what_was_asked_for()
    {
        var root = TestEnvironment.NewDirectory();
        var watched = Path.Combine(root, "watch");
        Directory.CreateDirectory(watched);
        var torrent = Path.Combine(watched, "example.torrent");
        await File.WriteAllTextAsync(torrent, "d4:infoe");

        var settings = await SettingsForAsync(root, watched, WatchFolderAction.Delete);

        await using var service = new WatchFolderService(settings, _ => Task.FromResult(true));
        service.Apply();

        Assert.True(await WaitForAsync(() => !File.Exists(torrent)));
        Assert.False(File.Exists(torrent + WatchFolderService.AddedSuffix));
    }

    [Fact]
    public async Task A_torrent_that_could_not_be_added_is_left_where_it_is()
    {
        var root = TestEnvironment.NewDirectory();
        var watched = Path.Combine(root, "watch");
        Directory.CreateDirectory(watched);
        var torrent = Path.Combine(watched, "broken.torrent");
        await File.WriteAllTextAsync(torrent, "not a torrent");

        var settings = await SettingsForAsync(root, watched, WatchFolderAction.Delete);
        var offers = 0;

        await using var service = new WatchFolderService(settings, _ =>
        {
            Interlocked.Increment(ref offers);
            return Task.FromResult(false);
        });
        service.Apply();

        Assert.True(await WaitForAsync(() => Volatile.Read(ref offers) > 0));

        // Refusing to add it must not throw the file away - the user still has to be able to see it.
        Assert.True(File.Exists(torrent));
    }

    [Fact]
    public async Task A_file_that_is_still_being_written_waits_until_it_is_finished()
    {
        var root = TestEnvironment.NewDirectory();
        var watched = Path.Combine(root, "watch");
        Directory.CreateDirectory(watched);
        var torrent = Path.Combine(watched, "slow.torrent");

        var settings = await SettingsForAsync(root, watched, WatchFolderAction.MarkAsAdded);
        var offers = 0;

        // Held open for writing, the way a browser holds a download it hasn't finished.
        var writer = File.Open(torrent, FileMode.Create, FileAccess.Write, FileShare.Read);
        await writer.WriteAsync("d4:info"u8.ToArray());
        await writer.FlushAsync();

        await using var service = new WatchFolderService(settings, _ =>
        {
            Interlocked.Increment(ref offers);
            return Task.FromResult(true);
        });
        service.Apply();

        // A couple of sweeps' worth of chances to get it wrong.
        await Task.Delay(1500);
        Assert.Equal(0, Volatile.Read(ref offers));

        await writer.DisposeAsync();

        Assert.True(await WaitForAsync(() => Volatile.Read(ref offers) > 0));
    }

    [Fact]
    public async Task An_empty_file_is_not_a_torrent_yet()
    {
        var root = TestEnvironment.NewDirectory();
        var watched = Path.Combine(root, "watch");
        Directory.CreateDirectory(watched);
        await File.WriteAllBytesAsync(Path.Combine(watched, "placeholder.torrent"), []);

        var settings = await SettingsForAsync(root, watched, WatchFolderAction.MarkAsAdded);
        var offers = 0;

        await using var service = new WatchFolderService(settings, _ =>
        {
            Interlocked.Increment(ref offers);
            return Task.FromResult(true);
        });
        service.Apply();

        await Task.Delay(1500);
        Assert.Equal(0, Volatile.Read(ref offers));
    }

    [Fact]
    public async Task Files_that_are_not_torrents_are_left_alone()
    {
        var root = TestEnvironment.NewDirectory();
        var watched = Path.Combine(root, "watch");
        Directory.CreateDirectory(watched);
        await File.WriteAllTextAsync(Path.Combine(watched, "notes.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(watched, "done.torrent" + WatchFolderService.AddedSuffix), "d4:infoe");

        var settings = await SettingsForAsync(root, watched, WatchFolderAction.Delete);
        var offers = 0;

        await using var service = new WatchFolderService(settings, _ =>
        {
            Interlocked.Increment(ref offers);
            return Task.FromResult(true);
        });
        service.Apply();

        await Task.Delay(1500);

        Assert.Equal(0, Volatile.Read(ref offers));
        Assert.True(File.Exists(Path.Combine(watched, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(watched, "done.torrent" + WatchFolderService.AddedSuffix)));
    }

    [Fact]
    public async Task No_folder_configured_means_nothing_is_watched()
    {
        var root = TestEnvironment.NewDirectory();
        var settings = await SettingsForAsync(root, "", WatchFolderAction.MarkAsAdded);
        var offers = 0;

        await using var service = new WatchFolderService(settings, _ =>
        {
            Interlocked.Increment(ref offers);
            return Task.FromResult(true);
        });
        service.Apply();

        await Task.Delay(500);
        Assert.Equal(0, Volatile.Read(ref offers));
    }

    [Fact]
    public async Task The_folder_is_created_if_it_is_not_there_yet()
    {
        var root = TestEnvironment.NewDirectory();
        var watched = Path.Combine(root, "not-yet");
        var settings = await SettingsForAsync(root, watched, WatchFolderAction.MarkAsAdded);

        await using var service = new WatchFolderService(settings, _ => Task.FromResult(true));
        service.Apply();

        Assert.True(Directory.Exists(watched));
    }

    [Fact]
    public async Task Changing_the_folder_in_settings_re_points_the_watcher()
    {
        var root = TestEnvironment.NewDirectory();
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        var settings = await SettingsForAsync(root, first, WatchFolderAction.MarkAsAdded);
        var offered = new List<string>();

        await using var service = new WatchFolderService(settings, path =>
        {
            lock (offered) offered.Add(path);
            return Task.FromResult(true);
        });
        service.Apply();

        var moved = settings.Current.Clone();
        moved.WatchFolder = second;
        await settings.SaveAsync(moved);

        var torrent = Path.Combine(second, "example.torrent");
        await File.WriteAllTextAsync(torrent, "d4:infoe");

        Assert.True(await WaitForAsync(() => { lock (offered) return offered.Contains(torrent); }));
    }
}
