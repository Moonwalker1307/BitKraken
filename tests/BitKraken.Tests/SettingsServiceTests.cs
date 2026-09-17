using BitKraken.Models;
using BitKraken.Services;
using Xunit;

namespace BitKraken.Tests;

public class SettingsServiceTests
{
    private static AppSettings SettingsIn(string directory) => new()
    {
        DownloadDirectory = Path.Combine(directory, "downloads"),
    };

    [Fact]
    public async Task Save_then_load_round_trips_every_field()
    {
        var dir = TestEnvironment.NewDirectory();
        var saved = SettingsIn(dir);
        saved.ListenPort = 6881;
        saved.MaxDownloadRateKiB = 512;
        saved.MaxUploadRateKiB = 64;
        saved.MaxConnections = 42;
        saved.EnableDht = false;
        saved.EnablePex = false;
        saved.EnableLocalPeerDiscovery = false;
        saved.EnablePortForwarding = false;
        saved.RequireEncryption = true;
        saved.AddFallbackTrackers = false;
        saved.StartTorrentsAutomatically = false;
        saved.AutoStartMagnetFromClipboard = false;
        saved.HandleMagnetLinks = false;
        saved.AnimatedBackground = false;
        saved.NetworkInterface = "wg0";
        saved.ProxyMode = ProxyMode.Socks5;
        saved.ProxyHost = "127.0.0.1";
        saved.ProxyPort = 9050;
        saved.ProxyUsername = "user";
        saved.ProxyPassword = "secret";
        saved.MaxActiveDownloads = 7;
        saved.MaxActiveSeeds = 9;
        saved.SeedRatioLimit = 2.5;
        saved.SeedTimeLimitMinutes = 180;
        saved.SequentialDownload = true;
        saved.ShowTrayIcon = false;
        saved.MinimizeToTray = true;
        saved.CloseToTray = true;
        saved.NotifyOnComplete = false;
        saved.NotifyOnError = false;
        saved.WatchFolder = Path.Combine(dir, "watch");
        saved.WatchFolderAction = WatchFolderAction.Delete;

        await new SettingsService(dir).SaveAsync(saved);

        var reloaded = new SettingsService(dir);
        reloaded.Load();

        var loaded = reloaded.Current;
        Assert.Equal(saved.DownloadDirectory, loaded.DownloadDirectory);
        Assert.Equal(6881, loaded.ListenPort);
        Assert.Equal(512, loaded.MaxDownloadRateKiB);
        Assert.Equal(64, loaded.MaxUploadRateKiB);
        Assert.Equal(42, loaded.MaxConnections);
        Assert.False(loaded.EnableDht);
        Assert.False(loaded.EnablePex);
        Assert.False(loaded.EnableLocalPeerDiscovery);
        Assert.False(loaded.EnablePortForwarding);
        Assert.True(loaded.RequireEncryption);
        Assert.False(loaded.AddFallbackTrackers);
        Assert.False(loaded.StartTorrentsAutomatically);
        Assert.False(loaded.AutoStartMagnetFromClipboard);
        Assert.False(loaded.HandleMagnetLinks);
        Assert.False(loaded.AnimatedBackground);
        Assert.Equal("wg0", loaded.NetworkInterface);
        Assert.Equal(ProxyMode.Socks5, loaded.ProxyMode);
        Assert.Equal("127.0.0.1", loaded.ProxyHost);
        Assert.Equal(9050, loaded.ProxyPort);
        Assert.Equal("user", loaded.ProxyUsername);
        Assert.Equal("secret", loaded.ProxyPassword);
        Assert.Equal(7, loaded.MaxActiveDownloads);
        Assert.Equal(9, loaded.MaxActiveSeeds);
        Assert.Equal(2.5, loaded.SeedRatioLimit);
        Assert.Equal(180, loaded.SeedTimeLimitMinutes);
        Assert.True(loaded.SequentialDownload);
        Assert.False(loaded.ShowTrayIcon);
        Assert.True(loaded.MinimizeToTray);
        Assert.True(loaded.CloseToTray);
        Assert.False(loaded.NotifyOnComplete);
        Assert.False(loaded.NotifyOnError);
        Assert.Equal(Path.Combine(dir, "watch"), loaded.WatchFolder);
        Assert.Equal(WatchFolderAction.Delete, loaded.WatchFolderAction);
    }

    [Fact]
    public async Task Save_creates_the_download_directory()
    {
        var dir = TestEnvironment.NewDirectory();
        var settings = SettingsIn(dir);

        await new SettingsService(dir).SaveAsync(settings);

        Assert.True(Directory.Exists(settings.DownloadDirectory));
    }

    [Fact]
    public async Task Save_raises_changed()
    {
        var dir = TestEnvironment.NewDirectory();
        var service = new SettingsService(dir);
        var raised = 0;
        service.Changed += (_, _) => raised++;

        await service.SaveAsync(SettingsIn(dir));

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Load_falls_back_to_defaults_when_the_file_is_corrupt()
    {
        // Startup must survive a half-written settings.json rather than throwing before any window shows.
        var dir = TestEnvironment.NewDirectory();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ this is not json");

        var service = new SettingsService(dir);
        service.Load();

        Assert.Equal(51413, service.Current.ListenPort);
        Assert.True(service.Current.EnableDht);
    }

    [Fact]
    public void Load_falls_back_to_defaults_when_the_file_is_the_json_literal_null()
    {
        // Deserialize returns null here rather than throwing, which is a separate branch from the catch.
        var dir = TestEnvironment.NewDirectory();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "null");

        var service = new SettingsService(dir);
        service.Load();

        Assert.Equal(51413, service.Current.ListenPort);
    }

    [Fact]
    public void Load_uses_defaults_when_there_is_no_file_yet()
    {
        var service = new SettingsService(TestEnvironment.NewDirectory());
        service.Load();

        Assert.Equal(51413, service.Current.ListenPort);
        Assert.True(service.Current.AddFallbackTrackers);
    }

    [Fact]
    public void Load_creates_the_cache_directory()
    {
        var dir = TestEnvironment.NewDirectory();

        new SettingsService(dir).Load();

        Assert.True(Directory.Exists(Path.Combine(dir, "cache")));
    }

    [Fact]
    public void Load_defaults_settings_an_older_build_never_wrote()
    {
        // A settings.json written before AddFallbackTrackers existed must come back with the field
        // at its default (on), not at default(bool). This is the trap every new setting walks into.
        var dir = TestEnvironment.NewDirectory();
        var downloads = Path.Combine(dir, "downloads");
        File.WriteAllText(Path.Combine(dir, "settings.json"), $$"""
            {
              "DownloadDirectory": {{System.Text.Json.JsonSerializer.Serialize(downloads)}},
              "ListenPort": 51413,
              "MaxConnections": 200,
              "EnableDht": true
            }
            """);

        var service = new SettingsService(dir);
        service.Load();

        Assert.True(service.Current.AddFallbackTrackers);
        Assert.True(service.Current.AnimatedBackground);
        Assert.True(service.Current.HandleMagnetLinks);
        Assert.True(service.Current.ShowTrayIcon);
        Assert.True(service.Current.NotifyOnComplete);
        Assert.True(service.Current.NotifyOnError);
        Assert.Equal(3, service.Current.MaxActiveDownloads);
        Assert.Equal(downloads, service.Current.DownloadDirectory);

        // Hiding the window is the one thing that must never switch itself on for an existing user:
        // a desktop with no tray would leave them with no window and no way back to it.
        Assert.False(service.Current.MinimizeToTray);
        Assert.False(service.Current.CloseToTray);

        // Nor may a seeding limit appear out of nowhere and stop torrents that were running fine.
        Assert.Equal(0, service.Current.MaxActiveSeeds);
        Assert.Equal(0, service.Current.SeedRatioLimit);
        Assert.Equal(0, service.Current.SeedTimeLimitMinutes);
    }

    [Fact]
    public void Load_ignores_settings_it_does_not_understand()
    {
        // Downgrading to an older build must not wipe the user's config.
        var dir = TestEnvironment.NewDirectory();
        File.WriteAllText(Path.Combine(dir, "settings.json"), $$"""
            {
              "DownloadDirectory": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(dir, "downloads"))}},
              "ListenPort": 6881,
              "SomeSettingFromTheFuture": 7
            }
            """);

        var service = new SettingsService(dir);
        service.Load();

        Assert.Equal(6881, service.Current.ListenPort);
    }
}
