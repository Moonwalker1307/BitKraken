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
        _maxDownloadRateKiB = settings.MaxDownloadRateKiB;
        _maxUploadRateKiB = settings.MaxUploadRateKiB;
        _maxConnections = settings.MaxConnections;
        _enableDht = settings.EnableDht;
        _enablePex = settings.EnablePex;
        _enableLocalPeerDiscovery = settings.EnableLocalPeerDiscovery;
        _enablePortForwarding = settings.EnablePortForwarding;
        _requireEncryption = settings.RequireEncryption;
        _startTorrentsAutomatically = settings.StartTorrentsAutomatically;
        _autoStartMagnetFromClipboard = settings.AutoStartMagnetFromClipboard;
        _handleMagnetLinks = settings.HandleMagnetLinks;
        _animatedBackground = settings.AnimatedBackground;
    }

    [ObservableProperty] private string _downloadDirectory;
    [ObservableProperty] private int _listenPort;
    [ObservableProperty] private int _maxDownloadRateKiB;
    [ObservableProperty] private int _maxUploadRateKiB;
    [ObservableProperty] private int _maxConnections;
    [ObservableProperty] private bool _enableDht;
    [ObservableProperty] private bool _enablePex;
    [ObservableProperty] private bool _enableLocalPeerDiscovery;
    [ObservableProperty] private bool _enablePortForwarding;
    [ObservableProperty] private bool _requireEncryption;
    [ObservableProperty] private bool _startTorrentsAutomatically;
    [ObservableProperty] private bool _autoStartMagnetFromClipboard;
    [ObservableProperty] private bool _handleMagnetLinks;
    [ObservableProperty] private bool _animatedBackground;

    public string CacheDirectory => SettingsService.AppDataDirectory;

    public event Action<bool>? Completed;

    public AppSettings ToSettings() => new()
    {
        DownloadDirectory = DownloadDirectory.Trim(),
        ListenPort = Math.Clamp(ListenPort, 1024, 65535),
        MaxDownloadRateKiB = Math.Max(0, MaxDownloadRateKiB),
        MaxUploadRateKiB = Math.Max(0, MaxUploadRateKiB),
        MaxConnections = Math.Clamp(MaxConnections, 10, 2000),
        EnableDht = EnableDht,
        EnablePex = EnablePex,
        EnableLocalPeerDiscovery = EnableLocalPeerDiscovery,
        EnablePortForwarding = EnablePortForwarding,
        RequireEncryption = RequireEncryption,
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
