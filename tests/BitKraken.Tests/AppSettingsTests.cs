using BitKraken.Models;
using Xunit;

namespace BitKraken.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Clone_is_independent_of_the_original()
    {
        // The settings dialog edits a clone and only commits it on OK, so a discarded edit
        // must not reach the live instance.
        var original = new AppSettings { ListenPort = 51413, MaxConnections = 200, EnableDht = true };

        var clone = original.Clone();
        clone.ListenPort = 6881;
        clone.MaxConnections = 50;
        clone.EnableDht = false;
        clone.DownloadDirectory = "/somewhere/else";

        Assert.Equal(51413, original.ListenPort);
        Assert.Equal(200, original.MaxConnections);
        Assert.True(original.EnableDht);
        Assert.NotEqual("/somewhere/else", original.DownloadDirectory);
    }

    [Fact]
    public void Defaults_are_the_documented_ones()
    {
        var settings = new AppSettings();

        Assert.Equal(51413, settings.ListenPort);
        Assert.Equal(200, settings.MaxConnections);
        Assert.Equal(0, settings.MaxDownloadRateKiB);   // 0 means unlimited
        Assert.Equal(0, settings.MaxUploadRateKiB);
        Assert.True(settings.EnableDht);
        Assert.True(settings.EnablePex);
        Assert.True(settings.EnableLocalPeerDiscovery);
        Assert.True(settings.EnablePortForwarding);
        Assert.False(settings.RequireEncryption);
        Assert.True(settings.AddFallbackTrackers);
        Assert.True(settings.StartTorrentsAutomatically);
        Assert.True(settings.HandleMagnetLinks);
        Assert.Equal("", settings.NetworkInterface);   // no interface binding until the user picks one
        Assert.Equal(ProxyMode.None, settings.ProxyMode);
        Assert.Equal(1080, settings.ProxyPort);        // the usual SOCKS5 port, once a proxy is chosen
        Assert.False(string.IsNullOrWhiteSpace(settings.DownloadDirectory));
    }
}
