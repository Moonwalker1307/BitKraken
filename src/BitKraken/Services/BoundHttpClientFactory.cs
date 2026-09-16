using System.Collections.Concurrent;
using System.Net.Sockets;

namespace BitKraken.Services;

/// <summary>
/// The <see cref="HttpClient"/> MonoTorrent announces to HTTP trackers with, taking the same route as
/// peer traffic. A tracker announce carries your IP as surely as a peer connection does, so leaving it
/// on the default route would undo the binding - and skipping the proxy would undo the proxy.
/// </summary>
public sealed class BoundHttpClientFactory
{
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly OutboundConnector _outbound;

    // One handler per address family, reused: the route is worked out inside the connect callback, so a
    // cached handler still picks up an interface or a proxy that changed since it was created.
    private readonly ConcurrentDictionary<AddressFamily, SocketsHttpHandler> _handlers = new();

    public BoundHttpClientFactory(OutboundConnector outbound) => _outbound = outbound;

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
        ConnectCallback = async (context, token) =>
        {
            // Unspecified means "either family will do"; the connector then picks from what it may use.
            AddressFamily? wanted = family is AddressFamily.Unspecified ? null : family;

            var socket = await _outbound
                .ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, wanted, token)
                .ConfigureAwait(false);

            return new NetworkStream(socket, ownsSocket: true);
        },
    };
}
