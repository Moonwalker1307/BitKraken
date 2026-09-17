using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using BitKraken.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonoTorrent;
using MonoTorrent.Client;

namespace BitKraken.ViewModels;

/// <summary>Observable projection of a <see cref="TorrentManager"/> for the torrent list and details panel.</summary>
public sealed partial class TorrentItemViewModel : ViewModelBase
{
    private const int HistoryLength = 90;

    /// <summary>Upper bound on a manual re-announce so one dead tracker can't hang the command.</summary>
    private static readonly TimeSpan ReannounceTimeout = TimeSpan.FromSeconds(20);

    private static readonly IBrush DownloadingBrush = new SolidColorBrush(Color.Parse("#A78BFA"));
    private static readonly IBrush SeedingBrush = new SolidColorBrush(Color.Parse("#22D3EE"));
    private static readonly IBrush PausedBrush = new SolidColorBrush(Color.Parse("#7C7199"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#F87171"));
    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#FBBF24"));
    private static readonly IBrush CompleteBrush = new SolidColorBrush(Color.Parse("#34D399"));

    private static IBrush? _seedGradient;
    private static IBrush? _accentGradient;

    private readonly TorrentService _service;
    private readonly Queue<double> _downloadHistory = new();
    private readonly Queue<double> _uploadHistory = new();
    private readonly Dictionary<string, PeerItemViewModel> _peerLookup = new();
    private readonly Dictionary<string, TrackerItemViewModel> _trackerLookup = new();
    private bool _filesLoaded;

    public TorrentManager Manager { get; }
    public DateTime AddedAt { get; }

    public event EventHandler? StateChanged;

    public TorrentItemViewModel(TorrentService service, TorrentManager manager)
    {
        _service = service;
        Manager = manager;
        AddedAt = manager.StartTime == default ? DateTime.Now : manager.StartTime;
        InfoHash = manager.InfoHashes.V1OrV2.ToHex().ToLowerInvariant();
        MagnetUri = manager.MagnetLink.ToV1String();

        manager.TorrentStateChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            Refresh();
            StateChanged?.Invoke(this, EventArgs.Empty);
        });

        for (var i = 0; i < HistoryLength; i++)
        {
            _downloadHistory.Enqueue(0);
            _uploadHistory.Enqueue(0);
        }

        Refresh();
    }

    public string InfoHash { get; }
    public string MagnetUri { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private TorrentState _state;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private IBrush _statusBrush = PausedBrush;
    [ObservableProperty] private IBrush _progressBrush = DownloadingBrush;
    [ObservableProperty] private Color _glowColor = Color.Parse("#8B5CF6");
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "0%";
    [ObservableProperty] private long _size;
    [ObservableProperty] private string _sizeText = "—";
    [ObservableProperty] private string _downloadedText = "0 B";
    [ObservableProperty] private string _uploadedText = "0 B";
    [ObservableProperty] private long _downloadRate;
    [ObservableProperty] private long _uploadRate;
    [ObservableProperty] private string _downloadRateText = "0 B/s";
    [ObservableProperty] private string _uploadRateText = "0 B/s";
    [ObservableProperty] private string _etaText = "∞";
    [ObservableProperty] private string _ratioText = "0.00";
    [ObservableProperty] private int _seeds;
    [ObservableProperty] private int _leechers;
    [ObservableProperty] private int _connectedPeers;
    [ObservableProperty] private string _peersText = "0 / 0";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunningOrWaiting))]
    private bool _isActive;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isSeeding;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isComplete;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _hasMetadata;
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private string _savePath = "";
    [ObservableProperty] private string _pieceInfo = "";
    [ObservableProperty] private bool[]? _pieces;
    [ObservableProperty] private double[] _downloadSamples = [];
    [ObservableProperty] private double[] _uploadSamples = [];
    [ObservableProperty] private string _createdBy = "";
    [ObservableProperty] private string _comment = "";
    [ObservableProperty] private bool _isPrivate;
    [ObservableProperty] private int _fileCount;

    /// <summary>True while the queue is holding this torrent back rather than the user.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunningOrWaiting))]
    private bool _isQueued;

    /// <summary>True once this torrent has seeded everything its limits asked for.</summary>
    [ObservableProperty] private bool _isSeedLimitReached;

    [ObservableProperty] private string _seedTimeText = "—";
    [ObservableProperty] private string _seedLimitText = "";

    public ObservableCollection<FileItemViewModel> Files { get; } = [];
    public ObservableCollection<PeerItemViewModel> Peers { get; } = [];
    public ObservableCollection<TrackerItemViewModel> Trackers { get; } = [];

    public string AddedAtText => AddedAt.ToString("g");

    /// <summary>Cheap per-second refresh used for every torrent in the list.</summary>
    public void Refresh()
    {
        var m = Manager;
        var torrent = m.Torrent;

        Name = string.IsNullOrWhiteSpace(m.Name) ? (m.MagnetLink.Name ?? InfoHash) : m.Name;
        State = m.State;
        HasMetadata = m.HasMetadata;
        SavePath = m.SavePath;

        Size = torrent?.Size ?? m.MagnetLink.Size ?? 0;
        SizeText = Size > 0 ? Format.Bytes(Size) : "—";

        Progress = m.HasMetadata ? m.Progress : 0;
        ProgressText = m.HasMetadata ? Format.Percent(Progress) : "…";

        // The engine's counters start again from zero every session; these are the running totals
        // BitKraken keeps, which is also what the seeding limit is measured against.
        var totals = _service.TotalsFor(m);
        DownloadedText = Format.Bytes(totals.Downloaded);
        UploadedText = Format.Bytes(totals.Uploaded);
        RatioText = Format.Ratio(ShareRatio.Of(totals.Uploaded, totals.Downloaded, (long)(Size * Progress / 100)));
        SeedTimeText = totals.Seeded > TimeSpan.Zero ? Format.Duration(totals.Seeded) : "—";

        DownloadRate = m.Monitor.DownloadRate;
        UploadRate = m.Monitor.UploadRate;
        DownloadRateText = Format.Speed(DownloadRate);
        UploadRateText = Format.Speed(UploadRate);

        Seeds = m.Peers.Seeds;
        Leechers = m.Peers.Leechs;
        ConnectedPeers = m.OpenConnections;
        PeersText = $"{Seeds} / {Leechers}";

        IsComplete = m.Complete;
        IsDownloading = m.State == TorrentState.Downloading;
        IsSeeding = m.State == TorrentState.Seeding;
        IsPaused = m.State is TorrentState.Paused or TorrentState.Stopped;
        HasError = m.State == TorrentState.Error;
        IsActive = m.State is TorrentState.Downloading or TorrentState.Seeding or TorrentState.Metadata or TorrentState.Hashing or TorrentState.Starting;
        ErrorText = m.Error?.Exception?.Message ?? m.Error?.Reason.ToString() ?? "";

        var remaining = Size - (long)(Size * Progress / 100);
        EtaText = IsDownloading && DownloadRate > 0 ? Format.Eta(TimeSpan.FromSeconds(remaining / (double)DownloadRate))
            : IsComplete ? "Done" : "∞";

        var overrides = _service.OverridesFor(m);
        var queuePosition = _service.QueuePositionOf(m);
        IsQueued = queuePosition is not null;
        IsSeedLimitReached = overrides.SeedLimitReached;
        SeedLimitText = _service.SeedLimitTextFor(m);

        // The kill switch is holding everything it did not find paused.
        var held = _service.IsNetworkSuspended && !overrides.PausedByUser && !overrides.SeedLimitReached;

        (StatusText, StatusBrush) = m.State switch
        {
            TorrentState.Downloading => ("Downloading", DownloadingBrush),
            TorrentState.Seeding => ("Seeding", SeedingBrush),
            TorrentState.Paused => ("Paused", PausedBrush),
            TorrentState.Stopped when IsSeedLimitReached => ("Seeding complete", CompleteBrush),
            TorrentState.Stopped when queuePosition is { } place => ($"Queued #{place}", PendingBrush),
            TorrentState.Stopped when held => ("Held - network down", PendingBrush),
            TorrentState.Stopped when overrides.PausedByUser => ("Paused", PausedBrush),
            TorrentState.Stopped => (IsComplete ? "Finished" : "Stopped", IsComplete ? CompleteBrush : PausedBrush),
            TorrentState.Hashing => ("Checking", PendingBrush),
            TorrentState.HashingPaused => ("Check paused", PausedBrush),
            TorrentState.Metadata => ("Fetching metadata", PendingBrush),
            TorrentState.FetchingHashes => ("Fetching hashes", PendingBrush),
            TorrentState.Starting => ("Starting", PendingBrush),
            TorrentState.Stopping => ("Stopping", PausedBrush),
            TorrentState.Error => ("Error", ErrorBrush),
            _ => (m.State.ToString(), PausedBrush),
        };

        if (IsSeeding || (IsComplete && !IsDownloading))
        {
            ProgressBrush = _seedGradient ??= LookupBrush("Kraken.SeedGradient") ?? SeedingBrush;
            GlowColor = Color.Parse("#22D3EE");
        }
        else
        {
            ProgressBrush = _accentGradient ??= LookupBrush("Kraken.AccentGradient") ?? DownloadingBrush;
            GlowColor = Color.Parse("#8B5CF6");
        }

        PushHistory(DownloadRate, UploadRate);
    }

    private static IBrush? LookupBrush(string key) =>
        Avalonia.Application.Current?.TryGetResource(key, null, out var res) == true ? res as IBrush : null;

    private void PushHistory(long down, long up)
    {
        _downloadHistory.Enqueue(down);
        _uploadHistory.Enqueue(up);
        while (_downloadHistory.Count > HistoryLength) _downloadHistory.Dequeue();
        while (_uploadHistory.Count > HistoryLength) _uploadHistory.Dequeue();
    }

    /// <summary>Heavier refresh of the details panel — only run for the selected torrent.</summary>
    public async Task RefreshDetailsAsync()
    {
        var m = Manager;

        DownloadSamples = _downloadHistory.ToArray();
        UploadSamples = _uploadHistory.ToArray();

        if (m.HasMetadata)
        {
            var bitfield = m.Bitfield;
            var pieces = new bool[bitfield.Length];
            for (var i = 0; i < pieces.Length; i++) pieces[i] = bitfield[i];
            Pieces = pieces;
            PieceInfo = $"{bitfield.TrueCount:N0} / {bitfield.Length:N0} pieces · {Format.Bytes(m.Torrent?.PieceLength ?? 0)} each";

            if (m.Torrent is { } t)
            {
                CreatedBy = t.CreatedBy ?? "";
                Comment = t.Comment ?? "";
                IsPrivate = t.IsPrivate;
            }

            if (!_filesLoaded)
            {
                _filesLoaded = true;
                Files.Clear();
                foreach (var file in m.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
                    Files.Add(new FileItemViewModel(m, file));
                FileCount = Files.Count;
            }
            else
            {
                foreach (var f in Files) f.Refresh();
            }
        }
        else
        {
            Pieces = null;
            PieceInfo = "Waiting for metadata…";
        }

        try
        {
            var peers = await m.GetPeersAsync();
            SyncPeers(peers);
        }
        catch
        {
            // Engine may be mid-transition; try again next tick.
        }

        SyncTrackers();
    }

    private void SyncPeers(IReadOnlyList<PeerId> peers)
    {
        var seen = new HashSet<string>();
        foreach (var peer in peers)
        {
            var key = peer.Uri.ToString();
            seen.Add(key);
            if (_peerLookup.TryGetValue(key, out var vm))
            {
                vm.Update(peer);
            }
            else
            {
                vm = new PeerItemViewModel(peer);
                _peerLookup[key] = vm;
                Peers.Add(vm);
            }
        }

        for (var i = Peers.Count - 1; i >= 0; i--)
        {
            if (seen.Contains(Peers[i].Key)) continue;
            _peerLookup.Remove(Peers[i].Key);
            Peers.RemoveAt(i);
        }
    }

    private void SyncTrackers()
    {
        var seen = new HashSet<string>();
        foreach (var tier in Manager.TrackerManager.Tiers)
        {
            foreach (var tracker in tier.Trackers)
            {
                var key = tracker.Uri.ToString();
                seen.Add(key);
                if (_trackerLookup.TryGetValue(key, out var vm))
                {
                    vm.Update(tracker);
                }
                else
                {
                    vm = new TrackerItemViewModel(tracker);
                    _trackerLookup[key] = vm;
                    Trackers.Add(vm);
                }
            }
        }

        for (var i = Trackers.Count - 1; i >= 0; i--)
        {
            if (seen.Contains(Trackers[i].Key)) continue;
            _trackerLookup.Remove(Trackers[i].Key);
            Trackers.RemoveAt(i);
        }
    }

    // ----- Per-torrent options (the Options tab) -------------------------------------------------------------------

    /// <summary>
    /// Whether this torrent picks pieces in order. Reads the setting in force - the torrent's own choice
    /// if it has one, the global default otherwise - and writing pins it to this torrent.
    /// </summary>
    public bool SequentialDownload
    {
        get => _service.IsSequential(Manager);
        set
        {
            if (value == SequentialDownload) return;
            _ = _service.SetSequentialAsync(Manager, value);
            OnPropertyChanged();
        }
    }

    /// <summary>Download cap for this torrent alone, in KiB/s. Null follows the global limit.</summary>
    public decimal? DownloadLimitKiB
    {
        get => _service.OverridesFor(Manager).MaxDownloadRateKiB;
        set => SetRateLimits(value, UploadLimitKiB);
    }

    /// <summary>Upload cap for this torrent alone, in KiB/s. Null follows the global limit.</summary>
    public decimal? UploadLimitKiB
    {
        get => _service.OverridesFor(Manager).MaxUploadRateKiB;
        set => SetRateLimits(DownloadLimitKiB, value);
    }

    /// <summary>Ratio this torrent stops seeding at. Null follows the global limit.</summary>
    public decimal? SeedRatioOverride
    {
        get => (decimal?)_service.OverridesFor(Manager).SeedRatioLimit;
        set => SetSeedLimit(value, SeedMinutesOverride);
    }

    /// <summary>Minutes this torrent seeds for. Null follows the global limit.</summary>
    public decimal? SeedMinutesOverride
    {
        get => _service.OverridesFor(Manager).SeedTimeLimitMinutes;
        set => SetSeedLimit(SeedRatioOverride, value);
    }

    private void SetRateLimits(decimal? download, decimal? upload)
    {
        _ = _service.SetRateLimitsAsync(Manager, (int?)download, (int?)upload);
        OnPropertyChanged(nameof(DownloadLimitKiB));
        OnPropertyChanged(nameof(UploadLimitKiB));
    }

    private void SetSeedLimit(decimal? ratio, decimal? minutes)
    {
        _ = _service.SetSeedLimitAsync(Manager, (double?)ratio, (int?)minutes);
        OnPropertyChanged(nameof(SeedRatioOverride));
        OnPropertyChanged(nameof(SeedMinutesOverride));
    }

    [RelayCommand]
    private Task MoveToTopOfQueue() => _service.MoveToTopOfQueueAsync(Manager);

    [RelayCommand]
    private Task MoveToBottomOfQueue() => _service.MoveToBottomOfQueueAsync(Manager);

    [RelayCommand]
    private Task Start() => _service.StartAsync(Manager);

    [RelayCommand]
    private Task Pause() => _service.PauseAsync(Manager);

    /// <summary>
    /// True when this torrent is running or on its way to running. It drives the one button on the
    /// card, and a queued torrent belongs on this side of it: it has already been started, so the
    /// useful thing to offer is pausing it, not starting it a second time.
    /// </summary>
    public bool IsRunningOrWaiting => IsActive || IsQueued;

    [RelayCommand]
    private Task TogglePause() => IsRunningOrWaiting ? Pause() : Start();

    [RelayCommand]
    private Task Recheck() => _service.RecheckAsync(Manager);

    [RelayCommand]
    private async Task Reannounce()
    {
        // The parameterless overload honours the tracker's MinUpdateInterval, so it silently does
        // nothing when pressed shortly after the last announce. Passing an explicit event forces it.
        using var cts = new CancellationTokenSource(ReannounceTimeout);
        try { await Manager.TrackerManager.AnnounceAsync(TorrentEvent.Started, cts.Token); }
        catch { /* tracker errors surface in the tracker tab */ }
    }

    public bool MatchesFilter(TorrentFilter filter) => filter switch
    {
        TorrentFilter.All => true,
        TorrentFilter.Downloading => State is TorrentState.Downloading or TorrentState.Metadata or TorrentState.Hashing or TorrentState.Starting or TorrentState.FetchingHashes,
        TorrentFilter.Seeding => State == TorrentState.Seeding,
        TorrentFilter.Completed => IsComplete,
        TorrentFilter.Queued => IsQueued,

        // Queued torrents are stopped, but nobody paused them - they are waiting their turn, and
        // counting them under Paused would have the sidebar blaming the user for the queue.
        TorrentFilter.Paused => !IsQueued && State is TorrentState.Paused or TorrentState.Stopped or TorrentState.HashingPaused,
        TorrentFilter.Active => DownloadRate > 0 || UploadRate > 0,
        TorrentFilter.Error => State == TorrentState.Error,
        _ => true,
    };
}

public enum TorrentFilter
{
    All,
    Downloading,
    Seeding,
    Queued,
    Completed,
    Paused,
    Active,
    Error,
}
