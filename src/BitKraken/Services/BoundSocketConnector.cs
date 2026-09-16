using System.Net.Sockets;
using MonoTorrent.Connections;
using ReusableTasks;

namespace BitKraken.Services;

/// <summary>
/// Outgoing peer connections. MonoTorrent's own connector lets the routing table pick the source
/// address and knows nothing about proxies, so this one hands the work to <see cref="OutboundConnector"/>
/// instead - binding only the listener would still send peer traffic out of the default route.
/// </summary>
public sealed class BoundSocketConnector : ISocketConnector
{
    private readonly OutboundConnector _outbound;

    public BoundSocketConnector(OutboundConnector outbound) => _outbound = outbound;

    public async ReusableTask<Socket> ConnectAsync(Uri uri, CancellationToken token)
    {
        var family = uri.Scheme == "ipv6" ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;

        return await _outbound.ConnectAsync(uri.DnsSafeHost, uri.Port, family, token).ConfigureAwait(false);
    }
}
