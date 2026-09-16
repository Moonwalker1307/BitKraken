using System.Net.Sockets;
using System.Text;
using BitKraken.Models;

namespace BitKraken.Services;

/// <summary>Talks a freshly connected socket through a proxy until it is a tunnel to the real target.</summary>
internal static class ProxyClient
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    private const int MaxHeaderBytes = 8 * 1024;

    public static async Task HandshakeAsync(Socket socket, ProxyConfiguration proxy, string host, int port, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(HandshakeTimeout);

        try
        {
            if (proxy.Mode == ProxyMode.Socks5)
                await Socks5HandshakeAsync(socket, proxy, host, port, timeout.Token).ConfigureAwait(false);
            else
                await HttpConnectHandshakeAsync(socket, proxy, host, port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new ProxyException("The proxy did not answer in time.");
        }
    }

    private static async Task Socks5HandshakeAsync(Socket socket, ProxyConfiguration proxy, string host, int port, CancellationToken token)
    {
        await SendAsync(socket, Socks5.Greeting(proxy.HasCredentials), token).ConfigureAwait(false);

        var greeting = await ReceiveExactlyAsync(socket, 2, token).ConfigureAwait(false);
        if (Socks5.SelectedMethod(greeting, proxy.HasCredentials) == Socks5.MethodUsernamePassword)
        {
            await SendAsync(socket, Socks5.Credentials(proxy.Username, proxy.Password), token).ConfigureAwait(false);
            Socks5.EnsureAuthSucceeded(await ReceiveExactlyAsync(socket, 2, token).ConfigureAwait(false));
        }

        await SendAsync(socket, Socks5.ConnectRequest(host, port), token).ConfigureAwait(false);

        var reply = await ReceiveExactlyAsync(socket, 4, token).ConfigureAwait(false);
        Socks5.EnsureConnectSucceeded(reply);

        // The bound address the proxy reports is of no use to us, but it has to come off the socket
        // before the tunnel carries anything else.
        byte domainLength = 0;
        if (Socks5.IsDomainAddress(reply[3]))
            domainLength = (await ReceiveExactlyAsync(socket, 1, token).ConfigureAwait(false))[0];

        var remaining = Socks5.RemainingAddressLength(reply[3], domainLength);
        if (remaining > 0) await ReceiveExactlyAsync(socket, remaining, token).ConfigureAwait(false);
    }

    private static async Task HttpConnectHandshakeAsync(Socket socket, ProxyConfiguration proxy, string host, int port, CancellationToken token)
    {
        await SendAsync(socket, HttpConnect.Request(host, port, proxy.Username, proxy.Password), token).ConfigureAwait(false);
        HttpConnect.EnsureConnected(await ReceiveHeadersAsync(socket, token).ConfigureAwait(false));
    }

    private static async Task SendAsync(Socket socket, byte[] payload, CancellationToken token)
    {
        var sent = 0;
        while (sent < payload.Length)
        {
            var count = await socket.SendAsync(payload.AsMemory(sent), SocketFlags.None, token).ConfigureAwait(false);
            if (count <= 0) throw new ProxyException("The proxy closed the connection.");
            sent += count;
        }
    }

    private static async Task<byte[]> ReceiveExactlyAsync(Socket socket, int count, CancellationToken token)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var received = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, token).ConfigureAwait(false);
            if (received <= 0) throw new ProxyException("The proxy closed the connection.");
            read += received;
        }

        return buffer;
    }

    /// <summary>
    /// Reads the CONNECT response one byte at a time. Anything read past the blank line would be the
    /// tunnelled traffic itself, and there is nowhere to put it back.
    /// </summary>
    private static async Task<string> ReceiveHeadersAsync(Socket socket, CancellationToken token)
    {
        var response = new StringBuilder();
        var buffer = new byte[1];

        while (response.Length < MaxHeaderBytes)
        {
            var received = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, token).ConfigureAwait(false);
            if (received <= 0) throw new ProxyException("The proxy closed the connection.");

            response.Append((char)buffer[0]);
            if (response.Length >= 4
                && response[^4] == '\r' && response[^3] == '\n'
                && response[^2] == '\r' && response[^1] == '\n')
                return response.ToString();
        }

        throw new ProxyException("The proxy sent a response BitKraken couldn't read.");
    }
}
