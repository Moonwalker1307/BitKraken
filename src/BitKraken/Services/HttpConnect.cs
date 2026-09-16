using System.Text;

namespace BitKraken.Services;

/// <summary>
/// The other half of proxy support: an HTTP proxy's CONNECT tunnel (RFC 9110 §9.3.6), which is all a
/// plain HTTP proxy can offer a protocol that isn't HTTP.
/// </summary>
internal static class HttpConnect
{
    /// <summary>A CONNECT request. The hostname goes out as a hostname, so the proxy does the DNS.</summary>
    public static byte[] Request(string host, int port, string username, string password)
    {
        var target = $"{FormatHost(host)}:{port}";
        var request = new StringBuilder()
            .Append($"CONNECT {target} HTTP/1.1\r\n")
            .Append($"Host: {target}\r\n");

        if (username.Length > 0 || password.Length > 0)
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            request.Append($"Proxy-Authorization: Basic {credentials}\r\n");
        }

        request.Append("Proxy-Connection: Keep-Alive\r\n\r\n");
        return Encoding.ASCII.GetBytes(request.ToString());
    }

    /// <summary>Accepts any 2xx; everything else is the proxy saying no, and says why.</summary>
    public static void EnsureConnected(string response)
    {
        var statusLine = response.Split("\r\n")[0];
        var parts = statusLine.Split(' ', 3);

        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) || !int.TryParse(parts[1], out var status))
            throw new ProxyException("The proxy did not answer as an HTTP proxy.");

        if (status is >= 200 and < 300) return;

        var reason = parts.Length > 2 && parts[2].Length > 0 ? $" ({parts[2].Trim()})" : "";
        throw new ProxyException(status switch
        {
            407 => "The proxy requires authentication. Check the username and password in Settings.",
            403 => $"The proxy refused the connection{reason}.",
            _ => $"The proxy refused the connection: HTTP {status}{reason}.",
        });
    }

    /// <summary>An IPv6 literal has to be bracketed in a request target.</summary>
    private static string FormatHost(string host)
        => host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
}
