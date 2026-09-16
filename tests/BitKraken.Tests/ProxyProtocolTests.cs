using System.Text;
using BitKraken.Services;
using Xunit;

namespace BitKraken.Tests;

public class Socks5Tests
{
    [Fact]
    public void The_greeting_offers_authentication_only_when_there_are_credentials()
    {
        Assert.Equal(new byte[] { 0x05, 1, 0x00 }, Socks5.Greeting(withCredentials: false));
        Assert.Equal(new byte[] { 0x05, 2, 0x00, 0x02 }, Socks5.Greeting(withCredentials: true));
    }

    [Fact]
    public void A_proxy_that_asks_for_a_password_we_do_not_have_says_so_plainly()
    {
        var error = Assert.Throws<ProxyException>(
            () => { Socks5.SelectedMethod([0x05, 0x02], hasCredentials: false); });

        Assert.Contains("requires authentication", error.Message);
    }

    [Fact]
    public void An_unusable_reply_to_the_greeting_is_rejected()
    {
        // Wrong version, no acceptable method, and a method we never offered.
        Assert.Throws<ProxyException>(() => { Socks5.SelectedMethod([0x04, 0x00], hasCredentials: false); });
        Assert.Throws<ProxyException>(() => { Socks5.SelectedMethod([0x05, 0xFF], hasCredentials: true); });
        Assert.Throws<ProxyException>(() => { Socks5.SelectedMethod([0x05, 0x01], hasCredentials: true); });

        Assert.Equal(Socks5.MethodNone, Socks5.SelectedMethod([0x05, 0x00], hasCredentials: true));
    }

    [Fact]
    public void Credentials_are_length_prefixed_per_RFC_1929()
    {
        Assert.Equal(
            new byte[] { 0x01, 2, (byte)'m', (byte)'e', 3, (byte)'p', (byte)'w', (byte)'d' },
            Socks5.Credentials("me", "pwd"));
    }

    [Fact]
    public void A_rejected_password_is_reported_rather_than_ignored()
    {
        Socks5.EnsureAuthSucceeded([0x01, 0x00]);
        Assert.Throws<ProxyException>(() => Socks5.EnsureAuthSucceeded([0x01, 0x01]));
        Assert.Throws<ProxyException>(() => Socks5.EnsureAuthSucceeded([0x01]));
    }

    [Fact]
    public void A_hostname_target_is_sent_as_a_hostname_so_the_proxy_resolves_it()
    {
        var request = Socks5.ConnectRequest("tracker.example.org", 6969);

        Assert.Equal(new byte[] { 0x05, 0x01, 0x00, 0x03, 19 }, request[..5]);
        Assert.Equal("tracker.example.org", Encoding.ASCII.GetString(request[5..^2]));
        Assert.Equal(6969, (request[^2] << 8) | request[^1]);
    }

    [Fact]
    public void An_address_target_is_sent_as_an_address()
    {
        Assert.Equal(
            new byte[] { 0x05, 0x01, 0x00, 0x01, 203, 0, 113, 9, 0x1A, 0xE1 },
            Socks5.ConnectRequest("203.0.113.9", 6881));

        var v6 = Socks5.ConnectRequest("2001:db8::2", 6881);
        Assert.Equal(0x04, v6[3]);
        Assert.Equal(4 + 16 + 2, v6.Length);
    }

    [Fact]
    public void A_refused_connection_names_the_reason()
    {
        Socks5.EnsureConnectSucceeded([0x05, 0x00, 0x00, 0x01]);

        var error = Assert.Throws<ProxyException>(() => Socks5.EnsureConnectSucceeded([0x05, 0x02, 0x00, 0x01]));
        Assert.Contains("not allowed by ruleset", error.Message);

        Assert.Throws<ProxyException>(() => Socks5.EnsureConnectSucceeded([0x05, 0x00]));
    }

    [Fact]
    public void The_bound_address_length_follows_the_address_type()
    {
        Assert.Equal(6, Socks5.RemainingAddressLength(0x01, 0));     // IPv4 + port
        Assert.Equal(18, Socks5.RemainingAddressLength(0x04, 0));    // IPv6 + port
        Assert.Equal(9, Socks5.RemainingAddressLength(0x03, 7));     // 7-byte name + port
        Assert.Throws<ProxyException>(() => { Socks5.RemainingAddressLength(0x09, 0); });
    }
}

public class HttpConnectTests
{
    [Fact]
    public void The_request_targets_the_host_by_name()
    {
        var request = Encoding.ASCII.GetString(HttpConnect.Request("tracker.example.org", 443, "", ""));

        Assert.StartsWith("CONNECT tracker.example.org:443 HTTP/1.1\r\n", request);
        Assert.Contains("Host: tracker.example.org:443\r\n", request);
        Assert.DoesNotContain("Proxy-Authorization", request);
        Assert.EndsWith("\r\n\r\n", request);
    }

    [Fact]
    public void Credentials_go_out_as_basic_auth()
    {
        var request = Encoding.ASCII.GetString(HttpConnect.Request("example.org", 80, "me", "pwd"));

        Assert.Contains($"Proxy-Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("me:pwd"))}\r\n", request);
    }

    [Fact]
    public void An_IPv6_literal_is_bracketed()
    {
        var request = Encoding.ASCII.GetString(HttpConnect.Request("2001:db8::2", 6881, "", ""));

        Assert.StartsWith("CONNECT [2001:db8::2]:6881 HTTP/1.1\r\n", request);
    }

    [Fact]
    public void Any_2xx_is_a_tunnel_and_everything_else_is_an_error()
    {
        HttpConnect.EnsureConnected("HTTP/1.1 200 Connection established\r\n\r\n");

        var auth = Assert.Throws<ProxyException>(() => HttpConnect.EnsureConnected("HTTP/1.1 407 Proxy Authentication Required\r\n\r\n"));
        Assert.Contains("requires authentication", auth.Message);

        var refused = Assert.Throws<ProxyException>(() => HttpConnect.EnsureConnected("HTTP/1.1 502 Bad Gateway\r\n\r\n"));
        Assert.Contains("502", refused.Message);

        Assert.Throws<ProxyException>(() => HttpConnect.EnsureConnected("not http at all\r\n\r\n"));
    }
}
