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

/// <summary>What becomes of a <c>.torrent</c> file once the watch folder has handed it over.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WatchFolderAction>))]
public enum WatchFolderAction
{
    /// <summary>Rename it to <c>&lt;name&gt;.torrent.added</c>, which leaves the file but takes it out of the filter.</summary>
    MarkAsAdded,

    /// <summary>Delete it.</summary>
    Delete,
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

    /// <summary>
    /// How many torrents may download at once; the rest wait in the queue. 0 = no limit.
    /// A torrent still fetching metadata counts as a download, because it is using the network.
    /// </summary>
    public int MaxActiveDownloads { get; set; } = 3;

    /// <summary>
    /// How many finished torrents may seed at once. 0 = no limit, which is the default: seeding costs
    /// little, and a limit here would quietly stop torrents that were happily seeding before.
    /// </summary>
    public int MaxActiveSeeds { get; set; }

    /// <summary>
    /// Stop seeding once the share ratio reaches this. 0 = seed forever. Counted from the totals
    /// BitKraken keeps per torrent, so it survives a restart.
    /// </summary>
    public double SeedRatioLimit { get; set; }

    /// <summary>Stop seeding after this many minutes of seeding, added up across sessions. 0 = seed forever.</summary>
    public int SeedTimeLimitMinutes { get; set; }

    /// <summary>Download pieces in order for newly added torrents, so a partial file plays from the start.</summary>
    public bool SequentialDownload { get; set; }

    /// <summary>Put BitKraken in the system tray / menu bar.</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>Minimizing the window hides it to the tray instead.</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>
    /// Closing the window hides it to the tray instead of quitting. Off by default: a desktop with no
    /// tray would otherwise leave BitKraken running with no way to get the window back.
    /// </summary>
    public bool CloseToTray { get; set; }

    /// <summary>Ask the desktop to show a notification when a torrent finishes.</summary>
    public bool NotifyOnComplete { get; set; } = true;

    /// <summary>Ask the desktop to show a notification when a torrent fails.</summary>
    public bool NotifyOnError { get; set; } = true;

    /// <summary>Folder watched for <c>.torrent</c> files to add. Empty means no watch folder.</summary>
    public string WatchFolder { get; set; } = "";

    /// <summary>What to do with a <c>.torrent</c> file once it has been added.</summary>
    public WatchFolderAction WatchFolderAction { get; set; } = WatchFolderAction.MarkAsAdded;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
