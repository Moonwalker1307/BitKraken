namespace BitKraken.Models;

/// <summary>User-configurable settings persisted to disk as JSON.</summary>
public sealed class AppSettings
{
    public string DownloadDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "BitKraken");

    public int ListenPort { get; set; } = 51413;

    /// <summary>Global download cap in KiB/s. 0 = unlimited.</summary>
    public int MaxDownloadRateKiB { get; set; }

    /// <summary>Global upload cap in KiB/s. 0 = unlimited.</summary>
    public int MaxUploadRateKiB { get; set; }

    public int MaxConnections { get; set; } = 200;

    public bool EnableDht { get; set; } = true;
    public bool EnablePex { get; set; } = true;
    public bool EnableLocalPeerDiscovery { get; set; } = true;
    public bool EnablePortForwarding { get; set; } = true;
    public bool RequireEncryption { get; set; }

    /// <summary>
    /// Append a small set of well-known public trackers to public torrents as they're added.
    /// Usually the biggest cut to time-to-first-peer for magnets. Never applied to private torrents.
    /// </summary>
    public bool AddFallbackTrackers { get; set; } = true;

    public bool StartTorrentsAutomatically { get; set; } = true;
    public bool AutoStartMagnetFromClipboard { get; set; } = true;

    /// <summary>Register BitKraken with the desktop as the handler for magnet links and .torrent files.</summary>
    public bool HandleMagnetLinks { get; set; } = true;

    /// <summary>Enable the animated aurora background. Can be turned off on low-end machines.</summary>
    public bool AnimatedBackground { get; set; } = true;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
