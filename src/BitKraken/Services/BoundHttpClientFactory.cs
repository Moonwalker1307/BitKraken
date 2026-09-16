using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace BitKraken.Services;

/// <summary>
/// The <see cref="HttpClient"/> MonoTorrent announces to HTTP trackers with, bound to the same interface
/// as peer traffic. A tracker announce carries your IP as surely as a peer connection does, so leaving it
/// on the default route would undo the binding.
/// </summary>
public sealed class BoundHttpClientFactory
{
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly NetworkBinding _binding;

    // One handler per address family, reused: the binding is read inside the connect callback, so a
    // cached handler still picks up an interface that changed since it was created.
    private readonly ConcurrentDictionary<AddressFamily, SocketsHttpHandler> _handlers = new();

    public BoundHttpClientFactory(NetworkBinding binding) => _binding = binding;

    public HttpClient Create(AddressFamily family)
    {
        var client = new HttpClient(_handlers.GetOrAdd(family, CreateHandler), disposeHandler: false);
        client.DefaultRequestHeaders.Add("User-Agent", $"BitKraken/{AppInfo.Version}");
        client.Timeout = RequestTimeout;
        return client;
    }

    private SocketsHttpHandler CreateHandler(AddressFamily family) => new()
    {
        PooledConnectionLifetime = ConnectionLifetime,
        ConnectCallback = (context, token) => ConnectAsync(family, context.DnsEndPoint, token),
    };

    private async ValueTask<Stream> ConnectAsync(AddressFamily family, DnsEndPoint endPoint, CancellationToken token)
    {
        var binding = _binding.Current;
        var candidates = await ResolveAsync(endPoint.Host, token).ConfigureAwait(false);

        var usable = candidates
            .Where(address => family is AddressFamily.Unspecified || address.AddressFamily == family)
            .Where(address => binding.Allows(address.AddressFamily))
            .ToList();

        if (usable.Count == 0)
            throw new SocketException((int)SocketError.NetworkUnreachable);

        Exception? last = null;
        foreach (var address in usable)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                if (binding.SourceAddress(address.AddressFamily) is { } source)
                    socket.Bind(new IPEndPoint(source, 0));

                await socket.ConnectAsync(new IPEndPoint(address, endPoint.Port), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
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
