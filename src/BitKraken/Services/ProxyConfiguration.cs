using BitKraken.Models;

namespace BitKraken.Services;

/// <summary>The proxy BitKraken should send everything through, as resolved from the user's settings.</summary>
public sealed record ProxyConfiguration(ProxyMode Mode, string Host, int Port, string Username, string Password)
{
    public static readonly ProxyConfiguration None = new(ProxyMode.None, "", 0, "", "");

    /// <summary>
    /// True as soon as a mode is chosen - even if the host is blank. A half-configured proxy fails every
    /// connection with a clear error, which is the safe way round: quietly going direct is the one
    /// outcome someone who set a proxy never wants.
    /// </summary>
    public bool IsEnabled => Mode != ProxyMode.None;

    public bool HasCredentials => Username.Length > 0 || Password.Length > 0;

    public string Describe() => IsEnabled ? $"{(Mode == ProxyMode.Socks5 ? "SOCKS5" : "HTTP")} {Host}:{Port}" : "";

    public static ProxyConfiguration From(AppSettings settings) => new(
        settings.ProxyMode,
        settings.ProxyHost?.Trim() ?? "",
        settings.ProxyPort,
        settings.ProxyUsername ?? "",
        settings.ProxyPassword ?? "");
}
