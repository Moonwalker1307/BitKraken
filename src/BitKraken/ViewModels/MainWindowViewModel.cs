using System.Collections.ObjectModel;
using Avalonia.Controls.Selection;
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
        _service.TorrentCompleted += (_, m) => Dispatcher.UIThread.Post(() => OnTorrentCompleted(m));
        _service.TorrentFailed += (_, m) => Dispatcher.UIThread.Post(() => OnTorrentFailed(m));
        _service.SeedLimitReached += (_, m) => Dispatcher.UIThread.Post(() => OnSeedLimitReached(m));
        _settings.Changed += (_, _) => Dispatcher.UIThread.Post(ApplyUiSettings);

        for (var i = 0; i < HistoryLength; i++)
        {
            _downloadHistory.Enqueue(0);
            _uploadHistory.Enqueue(0);
        }

        Selection.SelectionChanged += (_, _) => OnSelectionChanged();

        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (_, _) => await TickAsync());
        ApplyUiSettings();
    }

    public ObservableCollection<TorrentItemViewModel> Torrents { get; } = [];
    public ObservableCollection<TorrentItemViewModel> FilteredTorrents { get; } = [];
    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    /// <summary>
    /// What is selected in the torrent list. The list binds to this model rather than to a single
    /// <c>SelectedItem</c>, so Shift and Ctrl/⌘ can pick several torrents and the actions take them all.
    /// Its source is <see cref="FilteredTorrents"/>, which the list hands it.
    /// </summary>
    public SelectionModel<TorrentItemViewModel> Selection { get; } = new() { SingleSelect = false };

    /// <summary>Every selected torrent, in list order. What Start, Pause, Remove and the rest act on.</summary>
    public IReadOnlyList<TorrentItemViewModel> SelectedTorrents { get; private set; } = [];

    /// <summary>
    /// The one torrent the details panel shows: the first selected in the list. Things that only make
    /// sense for one torrent at a time, like opening its folder, use this too.
    /// </summary>
    public TorrentItemViewModel? SelectedTorrent { get; private set; }

    public bool HasMultipleSelected => SelectedTorrents.Count > 1;
    public string SelectionText => $"{SelectedTorrents.Count} selected";

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

    /// <summary>The queue filter only appears once something is actually waiting in it.</summary>
    public bool HasQueued => QueuedCount > 0;

    [ObservableProperty] private string _totalDownloadRateText = "0 B/s";
    [ObservableProperty] private string _totalUploadRateText = "0 B/s";
    [ObservableProperty] private string _dhtStatusText = "DHT: off";
    [ObservableProperty] private string _networkStatusText = "";
    [ObservableProperty] private bool _isNetworkBlocked;
    [ObservableProperty] private string _proxyStatusText = "";
    [ObservableProperty] private bool _isProxied;
    [ObservableProperty] private string _listenPortText = "";
    [ObservableProperty] private string _connectionsText = "0 peers";
    [ObservableProperty] private double[] _downloadSamples = [];
    [ObservableProperty] private double[] _uploadSamples = [];
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isFilteredEmpty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQueued))]
    private int _queuedCount;

    /// <summary>What the tray icon says on hover. Watched by <see cref="App"/>.</summary>
    [ObservableProperty] private string _trayTooltip = AppInfo.Name;

    public bool HasSelection => SelectedTorrent is not null;

    public bool IsFilterAll { get => Filter == TorrentFilter.All; set { if (value) Filter = TorrentFilter.All; } }
    public bool IsFilterDownloading { get => Filter == TorrentFilter.Downloading; set { if (value) Filter = TorrentFilter.Downloading; } }
    public bool IsFilterSeeding { get => Filter == TorrentFilter.Seeding; set { if (value) Filter = TorrentFilter.Seeding; } }
    public bool IsFilterQueued { get => Filter == TorrentFilter.Queued; set { if (value) Filter = TorrentFilter.Queued; } }
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
        OnPropertyChanged(nameof(IsFilterQueued));
        OnPropertyChanged(nameof(IsFilterCompleted));
        OnPropertyChanged(nameof(IsFilterPaused));
        OnPropertyChanged(nameof(IsFilterActive));
        OnPropertyChanged(nameof(IsFilterError));
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>Selects just this torrent, as a plain click would.</summary>
    public void Select(TorrentItemViewModel? torrent)
    {
        var index = torrent is null ? -1 : FilteredTorrents.IndexOf(torrent);
        if (index < 0) Selection.Clear();
        else Selection.SelectedIndex = index;
        OnSelectionChanged();
    }

    /// <summary>
    /// Re-reads the selection. Called when the list's selection changes, and after the filter has edited
    /// the list, since a selected torrent that drops out of the filter drops out of the selection with it.
    /// </summary>
    private void OnSelectionChanged()
    {
        var selected = Selection.SelectedIndexes
            .Where(i => i >= 0 && i < FilteredTorrents.Count)
            .Order()
            .Select(i => FilteredTorrents[i])
            .ToList();
        if (selected.SequenceEqual(SelectedTorrents)) return;

        var previous = SelectedTorrent;
        SelectedTorrents = selected;
        SelectedTorrent = selected.FirstOrDefault();

        OnPropertyChanged(nameof(SelectedTorrents));
        OnPropertyChanged(nameof(HasMultipleSelected));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(HasSelection));
        if (!ReferenceEquals(previous, SelectedTorrent))
        {
            OnPropertyChanged(nameof(SelectedTorrent));
            if (SelectedTorrent is { } shown) _ = shown.RefreshDetailsAsync();
        }

        StartSelectedCommand.NotifyCanExecuteChanged();
        PauseSelectedCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        RecheckSelectedCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
        CopyMagnetCommand.NotifyCanExecuteChanged();
        MoveToTopOfQueueCommand.NotifyCanExecuteChanged();
        MoveToBottomOfQueueCommand.NotifyCanExecuteChanged();
    }

    private void ApplyUiSettings()
    {
        AnimatedBackground = _settings.Current.AnimatedBackground;

        var binding = _binding.Current;
        var proxy = ProxyConfiguration.From(_settings.Current);

        IsProxied = proxy.IsEnabled;
        ProxyStatusText = proxy.Describe();

        // No listener while proxying, so quoting a port would be a lie.
        ListenPortText = proxy.IsEnabled ? "Proxied"
            : binding.IsBound ? $"Port {_settings.Current.ListenPort} · {binding.Describe()}"
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

    /// <summary>
    /// A torrent finished. Notifications are the desktop's business and toasts are ours; both go out,
    /// because the window is often not the thing the user is looking at when this happens.
    /// </summary>
    private void OnTorrentCompleted(TorrentManager manager)
    {
        var name = NameOf(manager);
        ShowToast("Finished", name, isError: false);

        if (_settings.Current.NotifyOnComplete)
            DesktopNotifier.Notify("Download finished", name);
    }

    private void OnTorrentFailed(TorrentManager manager)
    {
        var name = NameOf(manager);
        var reason = manager.Error?.Exception?.Message ?? manager.Error?.Reason.ToString() ?? "Unknown error";
        ShowToast("Torrent failed", $"{name} - {reason}", isError: true);

        if (_settings.Current.NotifyOnError)
            DesktopNotifier.Notify("Torrent failed", $"{name}\n{reason}");
    }

    private void OnSeedLimitReached(TorrentManager manager)
    {
        var name = NameOf(manager);
        ShowToast("Seeding complete", $"{name} reached its seeding limit and has been stopped.", isError: false);
    }

    private static string NameOf(TorrentManager manager) =>
        string.IsNullOrWhiteSpace(manager.Name) ? manager.InfoHashes.V1OrV2.ToHex() : manager.Name;

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

        // Edit rather than refill, so the torrents that stay keep their place and their selection.
        FilteredTorrents.SyncTo(visible);
        OnSelectionChanged();

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
        QueuedCount = Torrents.Count(t => t.IsQueued);
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

        TrayTooltip = Torrents.Count == 0
            ? AppInfo.Name
            : $"{AppInfo.Name} - ↓ {TotalDownloadRateText}  ↑ {TotalUploadRateText}\n{DownloadingCount} downloading, {SeedingCount} seeding" +
              (QueuedCount > 0 ? $", {QueuedCount} queued" : "");

        DhtStatusText = IsProxied ? "DHT: off (proxy)"
            : !_settings.Current.EnableDht ? "DHT: off"
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
            Select(vm);
        }
    }

    private List<TorrentManager> SelectedManagers() => SelectedTorrents.Select(t => t.Manager).ToList();

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task StartSelected() => HasSelection ? _service.StartAsync(SelectedManagers()) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task PauseSelected() => HasSelection ? _service.PauseAsync(SelectedManagers()) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RecheckSelected()
    {
        foreach (var t in SelectedTorrents.ToList()) await _service.RecheckAsync(t.Manager);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveSelected()
    {
        // Copied first: each removal edits the list, and with it the selection.
        var torrents = SelectedTorrents.ToList();
        if (torrents.Count == 0 || Dialogs is null) return;

        var choice = await Dialogs.ConfirmRemoveAsync(torrents.Select(t => t.Name).ToList());
        if (choice == RemoveChoice.Cancel) return;

        var removed = 0;
        foreach (var t in torrents)
        {
            try
            {
                await _service.RemoveAsync(t.Manager, deleteData: choice == RemoveChoice.RemoveDeleteData);
                removed++;
            }
            catch (Exception ex)
            {
                ShowToast("Couldn't remove torrent", $"{t.Name} - {ex.Message}", isError: true);
            }
        }

        if (removed > 0)
            ShowToast("Removed", torrents.Count == 1 ? torrents[0].Name : $"{removed} torrents", isError: false);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenFolder()
    {
        if (SelectedTorrent is not { } t || Dialogs is null) return;
        var path = t.Manager.ContainingDirectory is { Length: > 0 } dir && Directory.Exists(dir) ? dir : t.SavePath;
        Dialogs.OpenInFileManager(path);
    }

    /// <summary>With several selected, copies one magnet link per line - the form most clients accept pasted.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopyMagnet()
    {
        var torrents = SelectedTorrents;
        if (torrents.Count == 0 || Dialogs is null) return;

        var text = string.Join(Environment.NewLine, torrents.Select(t => t.MagnetUri));
        await Dialogs.SetClipboardTextAsync(text);
        _lastClipboardMagnet = text; // don't offer to re-add what we just copied
        ShowToast("Copied", torrents.Count == 1 ? "Magnet link copied to clipboard." : $"{torrents.Count} magnet links copied to clipboard.", isError: false);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task MoveToTopOfQueue() =>
        HasSelection ? _service.MoveToTopOfQueueAsync(SelectedManagers()) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task MoveToBottomOfQueue() =>
        HasSelection ? _service.MoveToBottomOfQueueAsync(SelectedManagers()) : Task.CompletedTask;

    /// <summary>
    /// Adds one torrent the watch folder found. The bool is what tells the watcher it may clear the file
    /// away, so a torrent we already have counts as handled - otherwise the same file would be offered,
    /// and refused, on every sweep for the rest of the session.
    /// </summary>
    public async Task<bool> AddFromWatchFolderAsync(string path)
    {
        if (!_service.IsInitialized) return false;

        try
        {
            var manager = await _service.AddTorrentFileAsync(path);
            ShowToast("Added from watch folder", manager.Name, isError: false);
            return true;
        }
        catch (InvalidOperationException)
        {
            // Already in the list, or not a torrent we can make sense of. Either way, done with it.
            return true;
        }
        catch (Exception ex)
        {
            ShowToast("Watch folder", $"{Path.GetFileName(path)}: {ex.Message}", isError: true);
            return false;
        }
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
