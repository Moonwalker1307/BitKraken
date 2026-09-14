using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Trackers;

namespace BitKraken.ViewModels;

/// <summary>A single file inside a torrent, with a priority selector.</summary>
public sealed partial class FileItemViewModel : ObservableObject
{
    private readonly TorrentManager _manager;
    public ITorrentManagerFile File { get; }

    public FileItemViewModel(TorrentManager manager, ITorrentManagerFile file)
    {
        _manager = manager;
        File = file;
        Refresh();
    }

    public string Path => File.Path;
    public string Name => System.IO.Path.GetFileName(File.Path);
    public string SizeText => Format.Bytes(File.Length);
    public string Directory => System.IO.Path.GetDirectoryName(File.Path) is { Length: > 0 } d ? d : "";

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "0%";
    [ObservableProperty] private bool _isComplete;
    private Priority _priority;
    private bool _isWanted;

    public Priority Priority
    {
        get => _priority;
        set
        {
            if (!SetProperty(ref _priority, value)) return;
            if (value != File.Priority) _ = _manager.SetFilePriorityAsync(File, value);
            SetProperty(ref _isWanted, value != Priority.DoNotDownload, nameof(IsWanted));
        }
    }

    public bool IsWanted
    {
        get => _isWanted;
        set
        {
            if (!SetProperty(ref _isWanted, value)) return;
            var target = value ? (File.Priority == Priority.DoNotDownload ? Priority.Normal : File.Priority) : Priority.DoNotDownload;
            if (target != Priority) Priority = target;
        }
    }

    public static IReadOnlyList<Priority> Priorities { get; } =
        [Priority.DoNotDownload, Priority.Low, Priority.Normal, Priority.High, Priority.Highest];

    public void Refresh()
    {
        var percent = File.BitField.PercentComplete;
        Progress = percent;
        ProgressText = Format.Percent(percent);
        IsComplete = percent >= 100;
        SetProperty(ref _priority, File.Priority, nameof(Priority));
        SetProperty(ref _isWanted, File.Priority != Priority.DoNotDownload, nameof(IsWanted));
    }

}

/// <summary>A connected peer.</summary>
public sealed partial class PeerItemViewModel : ObservableObject
{
    public string Key { get; }

    public PeerItemViewModel(PeerId peer)
    {
        Key = peer.Uri.ToString();
        Address = $"{peer.Uri.Host}:{peer.Uri.Port}";
        Update(peer);
    }

    public string Address { get; }

    [ObservableProperty] private string _client = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _downloadRate = "";
    [ObservableProperty] private string _uploadRate = "";
    [ObservableProperty] private string _flags = "";
    [ObservableProperty] private bool _isSeeder;
    [ObservableProperty] private bool _isEncrypted;

    public void Update(PeerId peer)
    {
        Client = peer.ClientApp.Client == ClientApp.Unknown ? peer.ClientApp.ShortId : peer.ClientApp.Client.ToString();
        var pct = peer.BitField.PercentComplete;
        Progress = pct;
        ProgressText = Format.Percent(pct);
        DownloadRate = Format.Speed(peer.Monitor.DownloadRate);
        UploadRate = Format.Speed(peer.Monitor.UploadRate);
        IsSeeder = peer.IsSeeder;
        IsEncrypted = peer.EncryptionType != MonoTorrent.Connections.EncryptionType.PlainText;

        var flags = new List<string>();
        if (peer.ConnectionDirection == Direction.Incoming) flags.Add("I");
        if (peer.AmInterested) flags.Add("d");
        if (peer.IsInterested) flags.Add("u");
        if (peer.IsChoking) flags.Add("C");
        if (IsEncrypted) flags.Add("E");
        if (peer.SupportsFastPeer) flags.Add("F");
        Flags = string.Join(" ", flags);
    }
}

/// <summary>A tracker in one of the torrent's tiers.</summary>
public sealed partial class TrackerItemViewModel : ObservableObject
{
    public string Key { get; }

    public TrackerItemViewModel(ITracker tracker)
    {
        Key = tracker.Uri.ToString();
        Url = tracker.Uri.ToString();
        Update(tracker);
    }

    public string Url { get; }

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private IBrush _statusBrush = Brushes.Gray;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _nextAnnounce = "";

    public void Update(ITracker tracker)
    {
        Status = tracker.Status switch
        {
            TrackerState.Ok => "Working",
            TrackerState.Connecting => "Connecting",
            TrackerState.Offline => "Offline",
            TrackerState.InvalidResponse => "Invalid response",
            _ => "Not contacted",
        };
        StatusBrush = tracker.Status switch
        {
            TrackerState.Ok => new SolidColorBrush(Color.Parse("#34D399")),
            TrackerState.Connecting => new SolidColorBrush(Color.Parse("#FBBF24")),
            TrackerState.Offline or TrackerState.InvalidResponse => new SolidColorBrush(Color.Parse("#F87171")),
            _ => new SolidColorBrush(Color.Parse("#7C7199")),
        };
        Message = tracker.FailureMessage is { Length: > 0 } f ? f : tracker.WarningMessage ?? "";
        var remaining = tracker.UpdateInterval - tracker.TimeSinceLastAnnounce;
        NextAnnounce = tracker.Status == TrackerState.Ok && remaining > TimeSpan.Zero ? Format.Eta(remaining) : "—";
    }
}

/// <summary>Transient notification shown in the bottom-right corner.</summary>
public sealed partial class ToastViewModel : ObservableObject
{
    public ToastViewModel(string title, string? message, bool isError)
    {
        Title = title;
        Message = message ?? "";
        IsError = isError;
    }

    public string Title { get; }
    public string Message { get; }
    public bool IsError { get; }
    public bool HasMessage => Message.Length > 0;

    public event Action<ToastViewModel>? Dismissed;
    public event Action<ToastViewModel>? Activated;

    [RelayCommand]
    private void Dismiss() => Dismissed?.Invoke(this);

    [RelayCommand]
    private void Activate() => Activated?.Invoke(this);
}
