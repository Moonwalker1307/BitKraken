using System.Net;
using System.Text;

namespace BitKraken.Services;

/// <summary>
/// The wire format of SOCKS5 (RFC 1928) and its username/password auth (RFC 1929), as pure bytes in and
/// out so the protocol can be tested without a socket. <see cref="ProxyClient"/> does the talking.
/// </summary>
internal static class Socks5
{
    public const byte Version = 0x05;
    public const byte MethodNone = 0x00;
    public const byte MethodUsernamePassword = 0x02;
    public const byte MethodUnacceptable = 0xFF;

    private const byte AddressIPv4 = 0x01;
    private const byte AddressDomain = 0x03;
    private const byte AddressIPv6 = 0x04;

    /// <summary>The opening "here are the auth methods I support".</summary>
    public static byte[] Greeting(bool withCredentials)
        => withCredentials
            ? [Version, 2, MethodNone, MethodUsernamePassword]
            : [Version, 1, MethodNone];

    /// <summary>The method the proxy chose, having checked it is one we actually offered.</summary>
    public static byte SelectedMethod(ReadOnlySpan<byte> reply, bool hasCredentials)
    {
        if (reply.Length < 2 || reply[0] != Version)
            throw new ProxyException("The proxy did not answer as a SOCKS5 proxy.");

        var method = reply[1];
        if (method == MethodUnacceptable)
            throw new ProxyException(hasCredentials
                ? "The proxy rejected the username and password."
                : "The proxy requires authentication. Add a username and password in Settings.");

        if (method != MethodNone && method != MethodUsernamePassword)
            throw new ProxyException($"The proxy asked for an authentication method BitKraken doesn't support (0x{method:X2}).");

        if (method == MethodUsernamePassword && !hasCredentials)
            throw new ProxyException("The proxy requires authentication. Add a username and password in Settings.");

        return method;
    }

    public static byte[] Credentials(string username, string password)
    {
        var user = Encoding.UTF8.GetBytes(username);
        var secret = Encoding.UTF8.GetBytes(password);
        if (user.Length > 255 || secret.Length > 255)
            throw new ProxyException("The proxy username and password must each be 255 bytes or fewer.");

        var message = new byte[3 + user.Length + secret.Length];
        message[0] = 0x01;                       // the auth sub-negotiation has its own version
        message[1] = (byte)user.Length;
        user.CopyTo(message, 2);
        message[2 + user.Length] = (byte)secret.Length;
        secret.CopyTo(message, 3 + user.Length);
        return message;
    }

    public static void EnsureAuthSucceeded(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 2 || reply[0] != 0x01)
            throw new ProxyException("The proxy sent a malformed authentication reply.");

        if (reply[1] != 0x00)
            throw new ProxyException("The proxy rejected the username and password.");
    }

    /// <summary>
    /// "Connect me to this host". A hostname is sent as a hostname, so the proxy resolves it - otherwise
    /// every tracker lookup would still be a DNS query leaving from this machine.
    /// </summary>
    public static byte[] ConnectRequest(string host, int port)
    {
        if (port is < 1 or > 65535)
            throw new ProxyException($"{port} is not a valid port.");

        var request = new List<byte> { Version, 0x01, 0x00 };

        if (IPAddress.TryParse(host, out var address))
        {
            request.Add(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? AddressIPv6 : AddressIPv4);
            request.AddRange(address.GetAddressBytes());
        }
        else
        {
            var name = Encoding.ASCII.GetBytes(host);
            if (name.Length is 0 or > 255)
                throw new ProxyException($"\"{host}\" is not a hostname SOCKS5 can carry.");

            request.Add(AddressDomain);
            request.Add((byte)name.Length);
            request.AddRange(name);
        }

        request.Add((byte)(port >> 8));
        request.Add((byte)(port & 0xFF));
        return [.. request];
    }

    /// <summary>Checks the first four bytes of the reply; the bound address after them is of no use to us.</summary>
    public static void EnsureConnectSucceeded(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 4 || reply[0] != Version)
            throw new ProxyException("The proxy sent a malformed reply.");

        if (reply[1] != 0x00)
            throw new ProxyException(FailureReason(reply[1]));
    }

    /// <summary>How many bytes of address (plus its two port bytes) still follow the reply header.</summary>
    public static int RemainingAddressLength(byte addressType, byte firstByte) => addressType switch
    {
        AddressIPv4 => 4 + 2,
        AddressIPv6 => 16 + 2,
        AddressDomain => firstByte + 2,
        _ => throw new ProxyException($"The proxy replied with an address type BitKraken doesn't understand (0x{addressType:X2})."),
    };

    public static bool IsDomainAddress(byte addressType) => addressType == AddressDomain;

    private static string FailureReason(byte code) => code switch
    {
        0x01 => "The proxy failed: general SOCKS server failure.",
        0x02 => "The proxy refused the connection: not allowed by ruleset.",
        0x03 => "The proxy could not reach the network.",
        0x04 => "The proxy could not reach the host.",
        0x05 => "The proxy's connection was refused by the host.",
        0x06 => "The proxy's connection timed out (TTL expired).",
        0x07 => "The proxy does not support this command.",
        0x08 => "The proxy does not support this address type.",
        _ => $"The proxy refused the connection (code 0x{code:X2}).",
    };
}

/// <summary>Something the proxy said, or failed to say. Surfaced to the user as a torrent/tracker error.</summary>
public sealed class ProxyException : Exception
{
    public ProxyException(string message) : base(message)
    {
    }
}
