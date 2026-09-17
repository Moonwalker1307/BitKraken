namespace BitKraken.Models;

/// <summary>
/// What BitKraken remembers about one torrent that MonoTorrent's own state does not cover: whether the
/// user paused it, where it sits in the queue, the limits that apply only to it, and the transfer
/// totals the ratio is measured from.
/// </summary>
public sealed class TorrentOverrides
{
    /// <summary>The user pressed pause. Nothing but the user starts it again.</summary>
    public bool PausedByUser { get; set; }

    /// <summary>Position in the queue: lower runs first. Assigned in the order torrents are added.</summary>
    public long QueueOrder { get; set; }

    /// <summary>Download in piece order. Null follows <see cref="AppSettings.SequentialDownload"/>.</summary>
    public bool? Sequential { get; set; }

    /// <summary>Download cap in KiB/s for this torrent alone. Null means no per-torrent cap, 0 means unlimited.</summary>
    public int? MaxDownloadRateKiB { get; set; }

    /// <summary>Upload cap in KiB/s for this torrent alone. Null means no per-torrent cap, 0 means unlimited.</summary>
    public int? MaxUploadRateKiB { get; set; }

    /// <summary>Ratio to stop seeding at. Null follows <see cref="AppSettings.SeedRatioLimit"/>, 0 means never.</summary>
    public double? SeedRatioLimit { get; set; }

    /// <summary>Minutes to seed for. Null follows <see cref="AppSettings.SeedTimeLimitMinutes"/>, 0 means forever.</summary>
    public int? SeedTimeLimitMinutes { get; set; }

    /// <summary>Set when a seeding limit stopped this torrent, so it is not started again.</summary>
    public bool SeedLimitReached { get; set; }

    /// <summary>Bytes of torrent data uploaded, added up across every session.</summary>
    public long TotalUploaded { get; set; }

    /// <summary>Bytes of torrent data downloaded, added up across every session.</summary>
    public long TotalDownloaded { get; set; }

    /// <summary>Seconds spent seeding, added up across every session.</summary>
    public long SecondsSeeded { get; set; }

    public TorrentOverrides Clone() => (TorrentOverrides)MemberwiseClone();
}
