using BitKraken.Models;
using BitKraken.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BitKraken.ViewModels;

/// <summary>Editable copy of <see cref="AppSettings"/> for the settings dialog.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IDialogService _dialogs;

    public SettingsViewModel(IDialogService dialogs, AppSettings settings)
    {
        _dialogs = dialogs;
        _downloadDirectory = settings.DownloadDirectory;
        _listenPort = settings.ListenPort;
        var boundInterface = settings.NetworkInterface?.Trim() ?? "";
        NetworkInterfaces = NetworkInterfaceChoice.Build(boundInterface);
        _selectedNetworkInterface = NetworkInterfaces.First(c => c.Name == boundInterface);
        ProxyModes = ProxyModeChoice.All;
        _selectedProxyMode = ProxyModes.First(c => c.Mode == settings.ProxyMode);
        _proxyHost = settings.ProxyHost ?? "";
        _proxyPort = settings.ProxyPort;
        _proxyUsername = settings.ProxyUsername ?? "";
        _proxyPassword = settings.ProxyPassword ?? "";
        _maxDownloadRateKiB = settings.MaxDownloadRateKiB;
        _maxUploadRateKiB = settings.MaxUploadRateKiB;
        _maxConnections = settings.MaxConnections;
        _enableDht = settings.EnableDht;
        _enablePex = settings.EnablePex;
        _enableLocalPeerDiscovery = settings.EnableLocalPeerDiscovery;
        _enablePortForwarding = settings.EnablePortForwarding;
        _requireEncryption = settings.RequireEncryption;
        _addFallbackTrackers = settings.AddFallbackTrackers;
        _startTorrentsAutomatically = settings.StartTorrentsAutomatically;
        _autoStartMagnetFromClipboard = settings.AutoStartMagnetFromClipboard;
        _handleMagnetLinks = settings.HandleMagnetLinks;
        _animatedBackground = settings.AnimatedBackground;
    }

    [ObservableProperty] private string _downloadDirectory;
    [ObservableProperty] private int _listenPort;
    [ObservableProperty] private NetworkInterfaceChoice _selectedNetworkInterface;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProxyEnabled))]
    private ProxyModeChoice _selectedProxyMode;

    [ObservableProperty] private string _proxyHost;
    [ObservableProperty] private int _proxyPort;
    [ObservableProperty] private string _proxyUsername;
    [ObservableProperty] private string _proxyPassword;
    [ObservableProperty] private int _maxDownloadRateKiB;
    [ObservableProperty] private int _maxUploadRateKiB;
    [ObservableProperty] private int _maxConnections;
    [ObservableProperty] private bool _enableDht;
    [ObservableProperty] private bool _enablePex;
    [ObservableProperty] private bool _enableLocalPeerDiscovery;
    [ObservableProperty] private bool _enablePortForwarding;
    [ObservableProperty] private bool _requireEncryption;
    [ObservableProperty] private bool _addFallbackTrackers;
    [ObservableProperty] private bool _startTorrentsAutomatically;
    [ObservableProperty] private bool _autoStartMagnetFromClipboard;
    [ObservableProperty] private bool _handleMagnetLinks;
    [ObservableProperty] private bool _animatedBackground;

    /// <summary>"Any", plus every interface the machine currently has.</summary>
    public IReadOnlyList<NetworkInterfaceChoice> NetworkInterfaces { get; }

    public IReadOnlyList<ProxyModeChoice> ProxyModes { get; }

    /// <summary>Greys out the proxy address fields while no proxy is selected.</summary>
    public bool IsProxyEnabled => SelectedProxyMode.Mode != ProxyMode.None;

    public string CacheDirectory => SettingsService.AppDataDirectory;

    public event Action<bool>? Completed;

    public AppSettings ToSettings() => new()
    {
        DownloadDirectory = DownloadDirectory.Trim(),
        ListenPort = Math.Clamp(ListenPort, 1024, 65535),
        NetworkInterface = SelectedNetworkInterface.Name,
        ProxyMode = SelectedProxyMode.Mode,
        ProxyHost = ProxyHost.Trim(),
        ProxyPort = Math.Clamp(ProxyPort, 1, 65535),
        ProxyUsername = ProxyUsername.Trim(),
        ProxyPassword = ProxyPassword,
        MaxDownloadRateKiB = Math.Max(0, MaxDownloadRateKiB),
        MaxUploadRateKiB = Math.Max(0, MaxUploadRateKiB),
        MaxConnections = Math.Clamp(MaxConnections, 10, 2000),
        EnableDht = EnableDht,
        EnablePex = EnablePex,
        EnableLocalPeerDiscovery = EnableLocalPeerDiscovery,
        EnablePortForwarding = EnablePortForwarding,
        RequireEncryption = RequireEncryption,
        AddFallbackTrackers = AddFallbackTrackers,
        StartTorrentsAutomatically = StartTorrentsAutomatically,
        AutoStartMagnetFromClipboard = AutoStartMagnetFromClipboard,
        HandleMagnetLinks = HandleMagnetLinks,
        AnimatedBackground = AnimatedBackground,
    };

    [RelayCommand]
    private async Task BrowseFolder()
    {
        var folder = await _dialogs.PickFolderAsync(DownloadDirectory);
        if (folder is not null) DownloadDirectory = folder;
    }

    [RelayCommand]
    private void OpenCacheFolder() => _dialogs.OpenInFileManager(SettingsService.AppDataDirectory);

    [RelayCommand]
    private void Save() => Completed?.Invoke(true);

    [RelayCommand]
    private void Cancel() => Completed?.Invoke(false);
}

/// <summary>One entry in the "bind to interface" list.</summary>
public sealed class NetworkInterfaceChoice
{
    private NetworkInterfaceChoice(string name, string display)
    {
        Name = name;
        Display = display;
    }

    /// <summary>The name we persist. Empty for "Any".</summary>
    public string Name { get; }

    public string Display { get; }

    public static IReadOnlyList<NetworkInterfaceChoice> Build(string? selectedName)
    {
        var choices = new List<NetworkInterfaceChoice> { new("", "Any (default route)") };

        foreach (var nic in NetworkBinding.Enumerate().OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            var address = nic.Addresses.FirstOrDefault();
            var detail = !nic.IsUp ? "down" : address?.ToString() ?? "no address";
            choices.Add(new NetworkInterfaceChoice(nic.Name, $"{nic.Name} · {detail}"));
        }

        // A tunnel that is down right now still has to stay selected, or opening Settings while the VPN
        // is disconnected would silently unbind and send the next torrent out of the default route.
        var selected = selectedName?.Trim() ?? "";
        if (selected.Length > 0 && !choices.Any(c => c.Name == selected))
            choices.Add(new NetworkInterfaceChoice(selected, $"{selected} · not present"));

        return choices;
    }
}

/// <summary>One entry in the proxy type list.</summary>
public sealed class ProxyModeChoice
{
    private ProxyModeChoice(ProxyMode mode, string display)
    {
        Mode = mode;
        Display = display;
    }

    public ProxyMode Mode { get; }

    public string Display { get; }

    public static IReadOnlyList<ProxyModeChoice> All { get; } =
    [
        new(ProxyMode.None, "No proxy"),
        new(ProxyMode.Socks5, "SOCKS5"),
        new(ProxyMode.Http, "HTTP (CONNECT)"),
    ];
}
