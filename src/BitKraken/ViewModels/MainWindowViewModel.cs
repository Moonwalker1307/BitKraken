using System.Collections.ObjectModel;
using Avalonia.Threading;
using BitKraken.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonoTorrent.Client;
using MonoTorrent.Dht;

namespace BitKraken.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private const int HistoryLength = 120;

    private readonly TorrentService _service;
    private readonly SettingsService _settings;
    private readonly NetworkBinding _binding;
    private readonly DispatcherTimer _tick;
    private readonly Queue<double> _downloadHistory = new();
    private readonly Queue<double> _uploadHistory = new();
    private readonly Dictionary<TorrentManager, TorrentItemViewModel> _lookup = new();
    private DateTime _lastStateSave = DateTime.UtcNow;
    private string? _lastClipboardMagnet;

    public IDialogService? Dialogs { get; set; }

    public MainWindowViewModel(TorrentService service, SettingsService settings, NetworkBinding binding)
    {
        _service = service;
        _settings = settings;
        _binding = binding;
        _binding.Changed += (_, _) => Dispatcher.UIThread.Post(ApplyUiSettings);

        _service.TorrentAdded += (_, m) => Dispatcher.UIThread.Post(() => OnTorrentAdded(m));
        _service.TorrentRemoved += (_, m) => Dispatcher.UIThread.Post(() => OnTorrentRemoved(m));
        _service.EngineError += (_, msg) => Dispatcher.UIThread.Post(() => ShowToast("Engine", msg, isError: true));
        _service.NetworkSuspendedChanged += (_, suspended) => Dispatcher.UIThread.Post(() => OnNetworkSuspendedChanged(suspended));
        _settings.Changed += (_, _) => Dispatcher.UIThread.Post(ApplyUiSettings);

        for (var i = 0; i < HistoryLength; i++)
        {
            _downloadHistory.Enqueue(0);
            _uploadHistory.Enqueue(0);
        }

        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (_, _) => await TickAsync());
        ApplyUiSettings();
    }

    public ObservableCollection<TorrentItemViewModel> Torrents { get; } = [];
    public ObservableCollection<TorrentItemViewModel> FilteredTorrents { get; } = [];
    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(StartSelectedCommand), nameof(PauseSelectedCommand), nameof(RemoveSelectedCommand),
        nameof(RecheckSelectedCommand), nameof(OpenFolderCommand), nameof(CopyMagnetCommand))]
    private TorrentItemViewModel? _selectedTorrent;

    [ObservableProperty] private TorrentFilter _filter = TorrentFilter.All;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isDetailsOpen = true;
    [ObservableProperty] private bool _isEngineReady;
    [ObservableProperty] private bool _animatedBackground = true;

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _downloadingCount;
    [ObservableProperty] private int _seedingCount;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private int _pausedCount;
    [ObservableProperty] private int _activeCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrors))]
    private int _errorCount;

    public bool HasErrors => ErrorCount > 0;

    [ObservableProperty] private string _totalDownloadRateText = "0 B/s";
    [ObservableProperty] private string _totalUploadRateText = "0 B/s";
    [ObservableProperty] private string _dhtStatusText = "DHT: off";
    [ObservableProperty] private string _networkStatusText = "";
    [ObservableProperty] private bool _isNetworkBlocked;
    [ObservableProperty] private string _listenPortText = "";
    [ObservableProperty] private string _connectionsText = "0 peers";
    [ObservableProperty] private double[] _downloadSamples = [];
    [ObservableProperty] private double[] _uploadSamples = [];
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isFilteredEmpty;

    public bool HasSelection => SelectedTorrent is not null;

    public bool IsFilterAll { get => Filter == TorrentFilter.All; set { if (value) Filter = TorrentFilter.All; } }
    public bool IsFilterDownloading { get => Filter == TorrentFilter.Downloading; set { if (value) Filter = TorrentFilter.Downloading; } }
    public bool IsFilterSeeding { get => Filter == TorrentFilter.Seeding; set { if (value) Filter = TorrentFilter.Seeding; } }
    public bool IsFilterCompleted { get => Filter == TorrentFilter.Completed; set { if (value) Filter = TorrentFilter.Completed; } }
    public bool IsFilterPaused { get => Filter == TorrentFilter.Paused; set { if (value) Filter = TorrentFilter.Paused; } }
    public bool IsFilterActive { get => Filter == TorrentFilter.Active; set { if (value) Filter = TorrentFilter.Active; } }
    public bool IsFilterError { get => Filter == TorrentFilter.Error; set { if (value) Filter = TorrentFilter.Error; } }

    public async Task InitializeAsync()
    {
        try
        {
            await _service.InitializeAsync();
            IsEngineReady = true;
            ApplyUiSettings();

            // The tunnel can already be down before we ever start, so take the state the engine came up in.
            if (_service.IsNetworkSuspended) OnNetworkSuspendedChanged(true);
            _tick.Start();
        }
        catch (Exception ex)
        {
            ShowToast("Engine failed to start", ex.Message, isError: true);
        }
    }

    partial void OnFilterChanged(TorrentFilter value)
    {
        OnPropertyChanged(nameof(IsFilterAll));
        OnPropertyChanged(nameof(IsFilterDownloading));
        OnPropertyChanged(nameof(IsFilterSeeding));
        OnPropertyChanged(nameof(IsFilterCompleted));
        OnPropertyChanged(nameof(IsFilterPaused));
        OnPropertyChanged(nameof(IsFilterActive));
        OnPropertyChanged(nameof(IsFilterError));
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedTorrentChanged(TorrentItemViewModel? value)
    {
        if (value is not null) _ = value.RefreshDetailsAsync();
    }

    private void ApplyUiSettings()
    {
        AnimatedBackground = _settings.Current.AnimatedBackground;

        var binding = _binding.Current;
        ListenPortText = binding.IsBound
            ? $"Port {_settings.Current.ListenPort} · {binding.Describe()}"
            : $"Port {_settings.Current.ListenPort}";

        if (IsNetworkBlocked)
            NetworkStatusText = $"{binding.InterfaceName} is down - torrents held";
    }

    /// <summary>The kill switch tripped or lifted: say so in the status bar, and once as a toast.</summary>
    private void OnNetworkSuspendedChanged(bool suspended)
    {
        IsNetworkBlocked = suspended;
        var name = _binding.Current.InterfaceName;

        if (suspended)
        {
            NetworkStatusText = $"{name} is down - torrents held";
            ShowToast("Network gone", $"{name} is down. Torrents are held until it is back.", isError: true);
        }
        else
        {
            NetworkStatusText = "";
            ShowToast("Network back", $"{name} is up again. Torrents are resuming.", isError: false);
        }
    }

    private void OnTorrentAdded(TorrentManager manager)
    {
        if (_lookup.ContainsKey(manager)) return;
        var vm = new TorrentItemViewModel(_service, manager);
        vm.StateChanged += (_, _) => ApplyFilter();
        _lookup[manager] = vm;
        Torrents.Add(vm);
        ApplyFilter();
        UpdateCounts();
    }

    private void OnTorrentRemoved(TorrentManager manager)
    {
        if (!_lookup.Remove(manager, out var vm)) return;
        Torrents.Remove(vm);
        if (SelectedTorrent == vm) SelectedTorrent = null;
        ApplyFilter();
        UpdateCounts();
    }

    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        var visible = Torrents
            .Where(t => t.MatchesFilter(Filter))
            .Where(t => search.Length == 0 || t.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.AddedAt)
            .ToList();

        // Only touch the collection when membership/order actually changed to avoid flicker.
        if (visible.Count == FilteredTorrents.Count && visible.Zip(FilteredTorrents).All(p => ReferenceEquals(p.First, p.Second)))
            return;

        var selected = SelectedTorrent;
        FilteredTorrents.Clear();
        foreach (var t in visible) FilteredTorrents.Add(t);
        if (selected is not null && visible.Contains(selected)) SelectedTorrent = selected;

        IsEmpty = Torrents.Count == 0;
        IsFilteredEmpty = !IsEmpty && FilteredTorrents.Count == 0;
    }

    private void UpdateCounts()
    {
        TotalCount = Torrents.Count;
        DownloadingCount = Torrents.Count(t => t.MatchesFilter(TorrentFilter.Downloading));
        SeedingCount = Torrents.Count(t => t.MatchesFilter(TorrentFilter.Seeding));
        CompletedCount = Torrents.Count(t => t.MatchesFilter(TorrentFilter.Completed));
        PausedCount = Torrents.Count(t => t.MatchesFilter(TorrentFilter.Paused));
        ActiveCount = Torrents.Count(t => t.MatchesFilter(TorrentFilter.Active));
        ErrorCount = Torrents.Count(t => t.MatchesFilter(TorrentFilter.Error));
        IsEmpty = Torrents.Count == 0;
        IsFilteredEmpty = !IsEmpty && FilteredTorrents.Count == 0;
    }

    private async Task TickAsync()
    {
        if (!_service.IsInitialized) return;

        foreach (var t in Torrents) t.Refresh();
        ApplyFilter();
        UpdateCounts();

        var engine = _service.Engine;
        var down = engine.TotalDownloadRate;
        var up = engine.TotalUploadRate;
        TotalDownloadRateText = Format.Speed(down);
        TotalUploadRateText = Format.Speed(up);
        ConnectionsText = $"{engine.ConnectionManager.OpenConnections} peers";

        _downloadHistory.Enqueue(down);
        _uploadHistory.Enqueue(up);
        while (_downloadHistory.Count > HistoryLength) _downloadHistory.Dequeue();
        while (_uploadHistory.Count > HistoryLength) _uploadHistory.Dequeue();
        DownloadSamples = _downloadHistory.ToArray();
        UploadSamples = _uploadHistory.ToArray();

        DhtStatusText = !_settings.Current.EnableDht ? "DHT: off"
            : engine.Dht.State switch
            {
                DhtState.Ready => $"DHT: {engine.Dht.NodeCount:N0} nodes",
                DhtState.Initialising => "DHT: bootstrapping",
                _ => "DHT: waiting",
            };

        if (SelectedTorrent is { } selected && IsDetailsOpen)
            await selected.RefreshDetailsAsync();

        if (_settings.Current.AutoStartMagnetFromClipboard)
            await CheckClipboardForMagnetAsync();

        if ((DateTime.UtcNow - _lastStateSave).TotalSeconds > 60)
        {
            _lastStateSave = DateTime.UtcNow;
            _ = _service.SaveStateAsync();
        }
    }

    private async Task CheckClipboardForMagnetAsync()
    {
        if (Dialogs is null) return;
        try
        {
            var text = (await Dialogs.GetClipboardTextAsync())?.Trim();
            if (string.IsNullOrEmpty(text) || !text.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return;
            if (text == _lastClipboardMagnet) return;
            _lastClipboardMagnet = text;

            if (!MonoTorrent.MagnetLink.TryParse(text, out var magnet) || _service.Engine.Contains(magnet.InfoHashes)) return;

            var toast = new ToastViewModel("Magnet link detected", "Click to add it from your clipboard.", isError: false);
            toast.Dismissed += t => Toasts.Remove(t);
            toast.Activated += async _ =>
            {
                Toasts.Remove(toast);
                await OpenAddDialogAsync(text);
            };
            Toasts.Add(toast);
            ScheduleToastDismiss(toast, TimeSpan.FromSeconds(8));
        }
        catch
        {
            // Clipboard can be unavailable (e.g. on Wayland without focus). Not important.
        }
    }

    // ----- Commands -----------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task AddTorrentFile()
    {
        if (Dialogs is null) return;
        var files = await Dialogs.PickTorrentFilesAsync();
        if (files.Count == 0) return;

        if (files.Count == 1)
        {
            await OpenAddDialogAsync(files[0]);
            return;
        }

        await AddPathsAsync(files);
    }

    [RelayCommand]
    private async Task AddMagnet()
    {
        var clip = Dialogs is null ? null : (await SafeClipboardAsync())?.Trim();
        var prefill = clip is not null && clip.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? clip : "";
        await OpenAddDialogAsync(prefill);
    }

    /// <summary>Used for drag-and-drop and command-line arguments: adds each .torrent / magnet without prompting.</summary>
    public async Task AddPathsAsync(IEnumerable<string> sources)
    {
        foreach (var source in sources)
        {
            try
            {
                var isMagnet = source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);
                var manager = isMagnet
                    ? await _service.AddMagnetAsync(source)
                    : await _service.AddTorrentFileAsync(source);
                ShowToast("Added", manager.Name, isError: false);
                SelectTorrent(manager);
            }
            catch (Exception ex)
            {
                ShowToast("Couldn't add torrent", ex.Message, isError: true);
            }
        }
    }

    public async Task OpenAddDialogAsync(string prefill)
    {
        if (Dialogs is null) return;

        var vm = new AddTorrentViewModel(Dialogs, _settings.Current.DownloadDirectory, _settings.Current.StartTorrentsAutomatically)
        {
            Source = prefill,
        };

        var request = await Dialogs.ShowAddTorrentAsync(vm);
        if (request is null) return;

        try
        {
            var manager = request.IsMagnet
                ? await _service.AddMagnetAsync(request.Source, request.SaveDirectory, request.StartImmediately)
                : await _service.AddTorrentFileAsync(request.Source, request.SaveDirectory, request.StartImmediately);
            ShowToast("Added", manager.Name, isError: false);
            SelectTorrent(manager);
        }
        catch (Exception ex)
        {
            ShowToast("Couldn't add torrent", ex.Message, isError: true);
        }
    }

    private void SelectTorrent(TorrentManager manager)
    {
        if (_lookup.TryGetValue(manager, out var vm))
        {
            if (!vm.MatchesFilter(Filter)) Filter = TorrentFilter.All;
            SelectedTorrent = vm;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task StartSelected() => SelectedTorrent is { } t ? _service.StartAsync(t.Manager) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task PauseSelected() => SelectedTorrent is { } t ? _service.PauseAsync(t.Manager) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task RecheckSelected() => SelectedTorrent is { } t ? _service.RecheckAsync(t.Manager) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveSelected()
    {
        if (SelectedTorrent is not { } t || Dialogs is null) return;

        var choice = await Dialogs.ConfirmRemoveAsync([t.Name]);
        if (choice == RemoveChoice.Cancel) return;

        try
        {
            await _service.RemoveAsync(t.Manager, deleteData: choice == RemoveChoice.RemoveDeleteData);
            ShowToast("Removed", t.Name, isError: false);
        }
        catch (Exception ex)
        {
            ShowToast("Couldn't remove torrent", ex.Message, isError: true);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenFolder()
    {
        if (SelectedTorrent is not { } t || Dialogs is null) return;
        var path = t.Manager.ContainingDirectory is { Length: > 0 } dir && Directory.Exists(dir) ? dir : t.SavePath;
        Dialogs.OpenInFileManager(path);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopyMagnet()
    {
        if (SelectedTorrent is not { } t || Dialogs is null) return;
        await Dialogs.SetClipboardTextAsync(t.MagnetUri);
        _lastClipboardMagnet = t.MagnetUri; // don't offer to re-add what we just copied
        ShowToast("Copied", "Magnet link copied to clipboard.", isError: false);
    }

    [RelayCommand]
    private Task StartAll() => _service.IsInitialized ? _service.StartAllAsync() : Task.CompletedTask;

    [RelayCommand]
    private Task PauseAll() => _service.IsInitialized ? _service.PauseAllAsync() : Task.CompletedTask;

    [RelayCommand]
    private void ToggleDetails() => IsDetailsOpen = !IsDetailsOpen;

    [RelayCommand]
    private async Task OpenSettings()
    {
        if (Dialogs is null) return;
        var vm = new SettingsViewModel(Dialogs, _settings.Current);
        if (!await Dialogs.ShowSettingsAsync(vm)) return;

        try
        {
            await _settings.SaveAsync(vm.ToSettings());
            ShowToast("Settings saved", null, isError: false);
        }
        catch (Exception ex)
        {
            ShowToast("Couldn't save settings", ex.Message, isError: true);
        }
    }

    // ----- Toasts -------------------------------------------------------------------------------------------------

    public void ShowToast(string title, string? message, bool isError)
    {
        var toast = new ToastViewModel(title, message, isError);
        toast.Dismissed += t => Toasts.Remove(t);
        Toasts.Add(toast);
        while (Toasts.Count > 4) Toasts.RemoveAt(0);
        ScheduleToastDismiss(toast, TimeSpan.FromSeconds(isError ? 7 : 4));
    }

    private void ScheduleToastDismiss(ToastViewModel toast, TimeSpan after)
    {
        DispatcherTimer.RunOnce(() => Toasts.Remove(toast), after);
    }

    private async Task<string?> SafeClipboardAsync()
    {
        try { return Dialogs is null ? null : await Dialogs.GetClipboardTextAsync(); }
        catch { return null; }
    }
}
