using System.Net;
using System.Net.Sockets;

namespace BitKraken.Services;

/// <summary>
/// The one place BitKraken opens an outgoing TCP connection: it applies the interface binding, and
/// routes through the proxy when one is configured. Peer connections and tracker announces both go
/// through here, so neither can quietly take a different path than the other.
/// </summary>
public sealed class OutboundConnector
{
    private readonly NetworkBinding _binding;
    private readonly SettingsService _settings;

    public OutboundConnector(NetworkBinding binding, SettingsService settings)
    {
        _binding = binding;
        _settings = settings;
    }

    public ProxyConfiguration Proxy => ProxyConfiguration.From(_settings.Current);

    /// <summary>
    /// Connects to <paramref name="host"/>, which may be a hostname. With a proxy configured the name is
    /// handed to the proxy to resolve; <paramref name="family"/> then describes the hop to the proxy and
    /// is ignored, since the proxy's own address decides that.
    /// </summary>
    public async Task<Socket> ConnectAsync(string host, int port, AddressFamily? family, CancellationToken token)
    {
        var proxy = Proxy;
        if (!proxy.IsEnabled)
            return await ConnectDirectAsync(host, port, family, token).ConfigureAwait(false);

        if (proxy.Host.Length == 0)
            throw new ProxyException("No proxy address is set. Add one in Settings, or turn the proxy off.");

        var socket = await ConnectDirectAsync(proxy.Host, proxy.Port, null, token).ConfigureAwait(false);
        try
        {
            await ProxyClient.HandshakeAsync(socket, proxy, host, port, token).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return socket;
    }

    private async Task<Socket> ConnectDirectAsync(string host, int port, AddressFamily? family, CancellationToken token)
    {
        var binding = _binding.Current;
        var candidates = (await ResolveAsync(host, token).ConfigureAwait(false))
            .Where(address => family is null || address.AddressFamily == family)
            .Where(address => binding.Allows(address.AddressFamily))
            .ToList();

        // Either the name didn't resolve to anything we may use, or the bound interface has no address
        // of that family. Both mean "not over this interface", never "go direct instead".
        if (candidates.Count == 0)
            throw new SocketException((int)SocketError.NetworkUnreachable);

        Exception? last = null;
        foreach (var address in candidates)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                if (binding.SourceAddress(address.AddressFamily) is { } source)
                    socket.Bind(new IPEndPoint(source, 0));

                await socket.ConnectAsync(new IPEndPoint(address, port), token).ConfigureAwait(false);
                return socket;
            }
            catch (Exception ex)
            {
                socket.Dispose();
                if (token.IsCancellationRequested) throw;
                last = ex;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostUnreachable);
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken token)
    {
        if (IPAddress.TryParse(host, out var literal))
            return [literal];

        return await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
    }
}
