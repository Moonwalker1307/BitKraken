using System.Net;
using System.Net.Sockets;
using System.Text;
using BitKraken.Models;
using BitKraken.Services;
using Xunit;

namespace BitKraken.Tests;

/// <summary>
/// Drives the real connector against a listener on loopback that plays the proxy. The handshake has to
/// consume exactly its own bytes and not one more, so each test has the "proxy" send payload straight
/// after the handshake and checks it arrives intact.
/// </summary>
public class OutboundConnectorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Without_a_proxy_the_connection_goes_straight_out()
    {
        using var cts = new CancellationTokenSource(Timeout);
        using var listener = new Listener();

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptAsync(cts.Token);
            await client.GetStream().WriteAsync("hello"u8.ToArray(), cts.Token);
        }, cts.Token);

        var outbound = await ConnectorAsync(ProxyMode.None, "", 0);
        using var socket = await outbound.ConnectAsync("127.0.0.1", listener.Port, null, cts.Token);

        Assert.Equal("hello", await ReadAsync(socket, 5, cts.Token));
        await server;
    }

    [Fact]
    public async Task A_socks5_proxy_is_asked_to_reach_the_host_by_name()
    {
        using var cts = new CancellationTokenSource(Timeout);
        using var listener = new Listener();

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptAsync(cts.Token);
            var stream = client.GetStream();

            var greeting = new byte[3];
            await stream.ReadExactlyAsync(greeting, cts.Token);
            Assert.Equal(new byte[] { 0x05, 0x01, 0x00 }, greeting);
            await stream.WriteAsync(new byte[] { 0x05, 0x00 }, cts.Token);

            var head = new byte[5];
            await stream.ReadExactlyAsync(head, cts.Token);
            Assert.Equal(new byte[] { 0x05, 0x01, 0x00, 0x03 }, head[..4]);

            var target = new byte[head[4] + 2];
            await stream.ReadExactlyAsync(target, cts.Token);
            Assert.Equal("tracker.example.org", Encoding.ASCII.GetString(target[..^2]));
            Assert.Equal(6969, (target[^2] << 8) | target[^1]);

            // Success, with a bound address the client has to read off and discard...
            await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0x1A, 0xE1 }, cts.Token);
            // ...before this, which is the first byte of the tunnel proper.
            await stream.WriteAsync("hello"u8.ToArray(), cts.Token);
        }, cts.Token);

        var outbound = await ConnectorAsync(ProxyMode.Socks5, "127.0.0.1", listener.Port);
        using var socket = await outbound.ConnectAsync("tracker.example.org", 6969, null, cts.Token);

        Assert.Equal("hello", await ReadAsync(socket, 5, cts.Token));
        await server;
    }

    [Fact]
    public async Task A_socks5_proxy_that_refuses_reports_why()
    {
        using var cts = new CancellationTokenSource(Timeout);
        using var listener = new Listener();

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptAsync(cts.Token);
            var stream = client.GetStream();

            var greeting = new byte[3];
            await stream.ReadExactlyAsync(greeting, cts.Token);
            await stream.WriteAsync(new byte[] { 0x05, 0x00 }, cts.Token);

            var head = new byte[5];
            await stream.ReadExactlyAsync(head, cts.Token);
            var target = new byte[head[4] + 2];
            await stream.ReadExactlyAsync(target, cts.Token);

            await stream.WriteAsync(new byte[] { 0x05, 0x02, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, cts.Token);
        }, cts.Token);

        var outbound = await ConnectorAsync(ProxyMode.Socks5, "127.0.0.1", listener.Port);

        var error = await Assert.ThrowsAsync<ProxyException>(
            () => outbound.ConnectAsync("tracker.example.org", 6969, null, cts.Token));

        Assert.Contains("not allowed by ruleset", error.Message);
        await server;
    }

    [Fact]
    public async Task An_http_proxy_gets_a_CONNECT_and_the_tunnel_starts_after_the_blank_line()
    {
        using var cts = new CancellationTokenSource(Timeout);
        using var listener = new Listener();

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptAsync(cts.Token);
            var stream = client.GetStream();

            var request = new StringBuilder();
            var one = new byte[1];
            while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                await stream.ReadExactlyAsync(one, cts.Token);
                request.Append((char)one[0]);
            }

            Assert.StartsWith("CONNECT tracker.example.org:443 HTTP/1.1\r\n", request.ToString());

            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), cts.Token);
            await stream.WriteAsync("hello"u8.ToArray(), cts.Token);
        }, cts.Token);

        var outbound = await ConnectorAsync(ProxyMode.Http, "127.0.0.1", listener.Port);
        using var socket = await outbound.ConnectAsync("tracker.example.org", 443, null, cts.Token);

        Assert.Equal("hello", await ReadAsync(socket, 5, cts.Token));
        await server;
    }

    [Fact]
    public async Task A_proxy_with_no_address_fails_instead_of_going_direct()
    {
        // Fail closed: silently going direct is the one outcome someone who set a proxy never wants.
        using var cts = new CancellationTokenSource(Timeout);
        var outbound = await ConnectorAsync(ProxyMode.Socks5, "", 1080);

        var error = await Assert.ThrowsAsync<ProxyException>(
            () => outbound.ConnectAsync("tracker.example.org", 6969, null, cts.Token));

        Assert.Contains("No proxy address is set", error.Message);
    }

    private static async Task<OutboundConnector> ConnectorAsync(ProxyMode mode, string host, int port)
    {
        var directory = TestEnvironment.NewDirectory();
        var settings = new SettingsService(directory);
        await settings.SaveAsync(new AppSettings
        {
            DownloadDirectory = Path.Combine(directory, "downloads"),
            ProxyMode = mode,
            ProxyHost = host,
            ProxyPort = port,
        });

        return new OutboundConnector(new NetworkBinding(settings), settings);
    }

    private static async Task<string> ReadAsync(Socket socket, int count, CancellationToken token)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var received = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, token);
            if (received <= 0) break;
            read += received;
        }

        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    private sealed class Listener : IDisposable
    {
        private readonly TcpListener _listener;

        public Listener()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Task<TcpClient> AcceptAsync(CancellationToken token) => _listener.AcceptTcpClientAsync(token).AsTask();

        public void Dispose() => _listener.Stop();
    }
}
