using System.Net;
using System.Net.Sockets;
using MonoTorrent.Connections;
using ReusableTasks;

namespace BitKraken.Services;

/// <summary>
/// Makes outgoing peer connections leave from the bound interface. MonoTorrent's own connector lets the
/// routing table pick the source address, so binding only the listener would still send peer traffic out
/// of the default route - the leak that makes "bind to my VPN" worth having in the first place.
/// </summary>
public sealed class BoundSocketConnector : ISocketConnector
{
    private readonly NetworkBinding _binding;

    public BoundSocketConnector(NetworkBinding binding) => _binding = binding;

    public async ReusableTask<Socket> ConnectAsync(Uri uri, CancellationToken token)
    {
        var family = uri.Scheme == "ipv6" ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        var binding = _binding.Current;

        // No address on the bound interface: refuse rather than quietly fall back to the default route.
        if (!binding.Allows(family))
            throw new SocketException((int)SocketError.NetworkUnreachable);

        var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (binding.SourceAddress(family) is { } source)
                socket.Bind(new IPEndPoint(source, 0));

            var endPoint = new IPEndPoint(IPAddress.Parse(uri.DnsSafeHost), uri.Port);
            await socket.ConnectAsync(endPoint, token).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return socket;
    }
}
