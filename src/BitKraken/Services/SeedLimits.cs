using BitKraken.Models;

namespace BitKraken.Services;

/// <summary>The limits in force for one torrent, once its own overrides have been folded into the globals.</summary>
/// <param name="Ratio">Share ratio to stop at. 0 or less means never.</param>
/// <param name="Time">How long to seed for. <see cref="TimeSpan.Zero"/> or less means forever.</param>
internal readonly record struct SeedLimit(double Ratio, TimeSpan Time)
{
    public bool IsUnlimited => Ratio <= 0 && Time <= TimeSpan.Zero;

    /// <summary>A torrent's own limit wins over the global one; null in the override means "use the global".</summary>
    public static SeedLimit For(AppSettings settings, TorrentOverrides overrides) => new(
        overrides.SeedRatioLimit ?? settings.SeedRatioLimit,
        TimeSpan.FromMinutes(overrides.SeedTimeLimitMinutes ?? settings.SeedTimeLimitMinutes));

    /// <summary>True once this torrent has given back as much as it was asked to.</summary>
    public bool IsReached(double ratio, TimeSpan seeded) =>
        (Ratio > 0 && ratio >= Ratio) || (Time > TimeSpan.Zero && seeded >= Time);

    /// <summary>How the limit reads in the UI, or an empty string when there isn't one.</summary>
    public string Describe()
    {
        if (IsUnlimited) return "";
        if (Ratio > 0 && Time > TimeSpan.Zero) return $"ratio {Ratio:0.##} or {Format.Duration(Time)}";
        return Ratio > 0 ? $"ratio {Ratio:0.##}" : Format.Duration(Time);
    }
}

/// <summary>Share ratio arithmetic, kept in one place so the UI and the seeding limit agree on it.</summary>
internal static class ShareRatio
{
    /// <summary>
    /// Uploaded over downloaded. A torrent that was already on disk - added, re-checked and seeded
    /// without ever downloading - has nothing to divide by, so the data we have counts instead;
    /// otherwise its ratio would be infinite from the first block it sent.
    /// </summary>
    public static double Of(long uploaded, long downloaded, long bytesOnDisk)
    {
        var basis = Math.Max(downloaded, bytesOnDisk);
        return basis <= 0 ? 0 : uploaded / (double)basis;
    }
}
