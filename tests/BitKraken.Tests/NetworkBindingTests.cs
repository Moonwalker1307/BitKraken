using System.Net;
using System.Net.Sockets;
using BitKraken.Services;
using Xunit;

namespace BitKraken.Tests;

public class NetworkBindingTests
{
    private static NetworkInterfaceInfo Nic(string name, bool isUp, params string[] addresses)
        => new(name, $"{name} adapter", isUp, addresses.Select(IPAddress.Parse).ToList());

    [Fact]
    public void No_interface_selected_listens_on_any_address()
    {
        var state = NetworkBinding.Select([Nic("wg0", true, "10.2.0.2")], "");

        Assert.False(state.IsBound);
        Assert.True(state.IsAvailable);
        Assert.Equal(IPAddress.Any, state.ListenAddress(AddressFamily.InterNetwork));
        Assert.Equal(IPAddress.IPv6Any, state.ListenAddress(AddressFamily.InterNetworkV6));

        // Nothing bound means the routing table picks the source address, as it always did.
        Assert.Null(state.SourceAddress(AddressFamily.InterNetwork));
        Assert.True(state.Allows(AddressFamily.InterNetwork));
        Assert.True(state.Allows(AddressFamily.InterNetworkV6));
    }

    [Fact]
    public void A_bound_interface_supplies_both_the_listen_and_the_source_address()
    {
        var interfaces = new[] { Nic("eth0", true, "192.168.1.20"), Nic("wg0", true, "10.2.0.2", "fd00::2") };

        var state = NetworkBinding.Select(interfaces, "wg0");

        Assert.True(state.IsBound);
        Assert.True(state.IsAvailable);
        Assert.Equal(IPAddress.Parse("10.2.0.2"), state.ListenAddress(AddressFamily.InterNetwork));
        Assert.Equal(IPAddress.Parse("10.2.0.2"), state.SourceAddress(AddressFamily.InterNetwork));
        Assert.Equal(IPAddress.Parse("fd00::2"), state.SourceAddress(AddressFamily.InterNetworkV6));
    }

    [Fact]
    public void The_interface_name_is_matched_regardless_of_case()
    {
        var state = NetworkBinding.Select([Nic("Wi-Fi", true, "192.168.1.20")], "wi-fi");

        Assert.Equal(IPAddress.Parse("192.168.1.20"), state.SourceAddress(AddressFamily.InterNetwork));
    }

    [Fact]
    public void A_bound_interface_that_is_gone_offers_no_address_at_all()
    {
        // This is the VPN-dropped case: never fall back to "any", which is the leak the user
        // turned binding on to prevent.
        var state = NetworkBinding.Select([Nic("eth0", true, "192.168.1.20")], "wg0");

        Assert.True(state.IsBound);
        Assert.False(state.IsAvailable);
        Assert.Null(state.ListenAddress(AddressFamily.InterNetwork));
        Assert.Null(state.ListenAddress(AddressFamily.InterNetworkV6));
        Assert.False(state.Allows(AddressFamily.InterNetwork));
        Assert.False(state.Allows(AddressFamily.InterNetworkV6));
    }

    [Fact]
    public void An_interface_that_is_down_counts_as_gone()
    {
        var state = NetworkBinding.Select([Nic("wg0", false, "10.2.0.2")], "wg0");

        Assert.False(state.IsAvailable);
        Assert.Null(state.ListenAddress(AddressFamily.InterNetwork));
    }

    [Fact]
    public void An_interface_with_only_IPv4_refuses_IPv6_connections()
    {
        var state = NetworkBinding.Select([Nic("wg0", true, "10.2.0.2")], "wg0");

        Assert.True(state.Allows(AddressFamily.InterNetwork));
        Assert.False(state.Allows(AddressFamily.InterNetworkV6));
        Assert.Null(state.ListenAddress(AddressFamily.InterNetworkV6));
    }

    [Fact]
    public void Addresses_that_cannot_reach_a_peer_are_skipped()
    {
        // 169.254/16 means DHCP failed, and fe80:: needs a scope id no peer will ever have.
        var state = NetworkBinding.Select([Nic("wg0", true, "169.254.7.7", "10.2.0.2", "fe80::1", "2001:db8::2")], "wg0");

        Assert.Equal(IPAddress.Parse("10.2.0.2"), state.IPv4);
        Assert.Equal(IPAddress.Parse("2001:db8::2"), state.IPv6);
    }

    [Fact]
    public void An_interface_with_no_usable_address_reads_as_unavailable()
    {
        var state = NetworkBinding.Select([Nic("wg0", true, "169.254.7.7")], "wg0");

        Assert.False(state.IsAvailable);
    }

    [Fact]
    public void The_status_bar_description_names_the_interface_and_its_address()
    {
        Assert.Equal("", NetworkBinding.Select([], "").Describe());
        Assert.Equal("wg0 · 10.2.0.2", NetworkBinding.Select([Nic("wg0", true, "10.2.0.2")], "wg0").Describe());
        Assert.Equal("wg0 (offline)", NetworkBinding.Select([], "wg0").Describe());
    }
}
