namespace BitKraken;

/// <summary>Human-friendly formatting helpers shared by view-models and controls.</summary>
public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "—";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {Units[unit]}" : $"{value:0.#} {Units[unit]}";
    }

    public static string Speed(long bytesPerSecond) => bytesPerSecond <= 0 ? "0 B/s" : $"{Bytes(bytesPerSecond)}/s";

    public static string Eta(TimeSpan? eta)
    {
        if (eta is null) return "∞";
        var t = eta.Value;
        if (t.TotalSeconds < 1) return "0s";
        if (t.TotalDays >= 30) return "∞";
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }

    public static string Percent(double value) => $"{Math.Clamp(value, 0, 100):0.#}%";

    public static string Ratio(long uploaded, long downloaded)
    {
        if (downloaded <= 0) return uploaded > 0 ? "∞" : "0.00";
        return $"{(double)uploaded / downloaded:0.00}";
    }
}
