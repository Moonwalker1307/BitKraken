using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BitKraken.Services;

/// <summary>A network interface as the settings dialog and <see cref="NetworkBinding"/> see it.</summary>
/// <param name="Name">The name the OS knows it by ("wg0", "utun4", "Wi-Fi"). This is what we persist.</param>
/// <param name="Description">A longer, friendlier name where the platform has one.</param>
public sealed record NetworkInterfaceInfo(string Name, string Description, bool IsUp, IReadOnlyList<IPAddress> Addresses);

/// <summary>
/// The addresses BitKraken is allowed to use right now. Unbound (an empty <see cref="InterfaceName"/>)
/// means "whatever the routing table picks", which is what the engine did before binding existed.
/// </summary>
public sealed record NetworkBindingState(string InterfaceName, IPAddress? IPv4, IPAddress? IPv6)
{
    public static readonly NetworkBindingState Unbound = new("", null, null);

    /// <summary>True when the user pinned BitKraken to one interface.</summary>
    public bool IsBound => InterfaceName.Length > 0;

    /// <summary>True unless we're bound to an interface that is currently gone (or has no usable address).</summary>
    public bool IsAvailable => !IsBound || IPv4 is not null || IPv6 is not null;

    public IPAddress? Address(AddressFamily family)
        => family == AddressFamily.InterNetworkV6 ? IPv6 : IPv4;

    /// <summary>The address to bind a listener to, or null when this family must not be listened on at all.</summary>
    public IPAddress? ListenAddress(AddressFamily family)
        => IsBound ? Address(family)
            : family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;

    /// <summary>The source address outgoing sockets must leave from, or null to let the OS choose.</summary>
    public IPAddress? SourceAddress(AddressFamily family) => IsBound ? Address(family) : null;

    /// <summary>False when a connection of this family would have to leave the bound interface.</summary>
    public bool Allows(AddressFamily family) => !IsBound || Address(family) is not null;

    /// <summary>e.g. "wg0 · 10.2.0.2", for the status bar.</summary>
    public string Describe()
    {
        if (!IsBound) return "";
        var address = IPv4 ?? IPv6;
        return address is null ? $"{InterfaceName} (offline)" : $"{InterfaceName} · {address}";
    }
}

/// <summary>
/// Resolves "bind BitKraken to this interface" into concrete addresses, and keeps that answer current
/// as interfaces come and go - which is exactly what happens when a VPN tunnel connects or drops.
/// </summary>
public sealed class NetworkBinding : IDisposable
{
    /// <summary>
    /// How often the interface list is re-read. NetworkChange doesn't fire reliably for tunnel
    /// interfaces on every platform, and a kill switch that depends on an event it might not get is
    /// not a kill switch.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly SettingsService _settings;
    private readonly System.Threading.Timer _poll;
    private readonly object _gate = new();
    private volatile NetworkBindingState _current;
    private bool _subscribed;

    public NetworkBinding(SettingsService settings)
    {
        _settings = settings;
        _current = Resolve(settings.Current.NetworkInterface);
        _settings.Changed += OnSettingsChanged;
        _poll = new System.Threading.Timer(_ => Refresh(), null, PollInterval, PollInterval);

        try
        {
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
            _subscribed = true;
        }
        catch
        {
            // Not wired up on every platform; Refresh() is still called on a timer, so we just lose the
            // "react instantly" part, not the behaviour.
        }
    }

    /// <summary>The addresses in force right now. Cheap - read it on every connection.</summary>
    public NetworkBindingState Current => _current;

    /// <summary>Raised when the bound interface appears, disappears or changes address.</summary>
    public event EventHandler<NetworkBindingState>? Changed;

    /// <summary>Re-reads the interface list and publishes the result if it moved.</summary>
    public NetworkBindingState Refresh()
    {
        NetworkBindingState next;
        try
        {
            next = Resolve(_settings.Current.NetworkInterface);
        }
        catch
        {
            // Never let a hiccup while enumerating take the polling timer (or the app) down.
            return _current;
        }

        // The timer and the OS events both land here, so only one of them gets to announce a change.
        lock (_gate)
        {
            if (next == _current) return next;
            _current = next;
        }

        Changed?.Invoke(this, next);
        return next;
    }

    public void Dispose()
    {
        _poll.Dispose();
        _settings.Changed -= OnSettingsChanged;
        if (!_subscribed) return;

        _subscribed = false;
        try
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        }
        catch
        {
            // Nothing useful to do while unsubscribing.
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Refresh();
    private void OnNetworkChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>Every interface worth binding to, loopback excluded.</summary>
    public static IReadOnlyList<NetworkInterfaceInfo> Enumerate()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(nic => new NetworkInterfaceInfo(
                    nic.Name,
                    nic.Description,
                    nic.OperationalStatus == OperationalStatus.Up,
                    ReadAddresses(nic)))
                .ToList();
        }
        catch
        {
            // A locked-down or exotic platform that won't enumerate: behave as if nothing is bindable.
            return [];
        }
    }

    public static NetworkBindingState Resolve(string? interfaceName)
    {
        // Nothing bound means nothing to look up - worth short-circuiting, since we poll.
        if (string.IsNullOrWhiteSpace(interfaceName)) return NetworkBindingState.Unbound;

        return Select(Enumerate(), interfaceName);
    }

    /// <summary>
    /// Picks the addresses for <paramref name="interfaceName"/>. A name we can't find right now resolves to
    /// a bound-but-address-less state rather than to "any": falling back to the default route is precisely
    /// the leak the user turned this on to avoid.
    /// </summary>
    public static NetworkBindingState Select(IReadOnlyList<NetworkInterfaceInfo> interfaces, string? interfaceName)
    {
        var name = interfaceName?.Trim() ?? "";
        if (name.Length == 0) return NetworkBindingState.Unbound;

        var match = interfaces.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? interfaces.FirstOrDefault(i => string.Equals(i.Description, name, StringComparison.OrdinalIgnoreCase));

        if (match is null || !match.IsUp) return new NetworkBindingState(name, null, null);

        return new NetworkBindingState(name, PickIPv4(match.Addresses), PickIPv6(match.Addresses));
    }

    /// <summary>A link-local IPv4 (169.254.x.x) means DHCP failed, so it is never a usable source address.</summary>
    private static IPAddress? PickIPv4(IReadOnlyList<IPAddress> addresses)
        => addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IsIPv4LinkLocal(a));

    /// <summary>Only globally routable IPv6: fe80:: needs a scope id, and the rest can't reach a peer.</summary>
    private static IPAddress? PickIPv6(IReadOnlyList<IPAddress> addresses)
        => addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6
                                         && !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal && !a.IsIPv6Teredo);

    private static bool IsIPv4LinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    private static IReadOnlyList<IPAddress> ReadAddresses(System.Net.NetworkInformation.NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().UnicastAddresses.Select(a => a.Address).ToList();
        }
        catch
        {
            // Some virtual adapters throw while being torn down.
            return [];
        }
    }
}
