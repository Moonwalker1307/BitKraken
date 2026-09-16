using System.Text.Json.Serialization;

namespace BitKraken.Models;

/// <summary>How BitKraken reaches peers and trackers.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProxyMode>))]
public enum ProxyMode
{
    /// <summary>Straight out, subject to the interface binding.</summary>
    None,

    /// <summary>SOCKS5 (RFC 1928), what VPN providers hand out for torrent clients.</summary>
    Socks5,

    /// <summary>An HTTP proxy's CONNECT tunnel.</summary>
    Http,
}

/// <summary>User-configurable settings persisted to disk as JSON.</summary>
public sealed class AppSettings
{
    public string DownloadDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "BitKraken");

    public int ListenPort { get; set; } = 51413;

    /// <summary>
    /// Name of the network interface all torrent traffic must leave from ("wg0", "utun4", "Wi-Fi").
    /// Empty means whatever the routing table picks. Point it at a VPN tunnel and BitKraken stops
    /// talking to peers and trackers the moment that tunnel is gone.
    /// </summary>
    public string NetworkInterface { get; set; } = "";

    /// <summary>
    /// Route every peer connection and tracker announce through a proxy. While one is set, DHT, local
    /// peer discovery, UDP trackers and the incoming listener are all off: none of them can be carried
    /// by a TCP proxy, and running them anyway would put your own address back on the wire.
    /// </summary>
    public ProxyMode ProxyMode { get; set; } = ProxyMode.None;

    public string ProxyHost { get; set; } = "";

    public int ProxyPort { get; set; } = 1080;

    public string ProxyUsername { get; set; } = "";

    /// <summary>
    /// Stored as-is in settings.json, which is written user-readable only on macOS and Linux. It is not
    /// encrypted - treat it like any other password kept in a config file.
    /// </summary>
    public string ProxyPassword { get; set; } = "";

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
