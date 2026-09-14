using BitKraken.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonoTorrent;

namespace BitKraken.ViewModels;

/// <summary>Backs the "Add torrent" dialog: accepts a magnet link or a .torrent path and previews it.</summary>
public sealed partial class AddTorrentViewModel : ViewModelBase
{
    private readonly IDialogService _dialogs;

    public AddTorrentViewModel(IDialogService dialogs, string defaultSaveDirectory, bool startImmediately)
    {
        _dialogs = dialogs;
        _saveDirectory = defaultSaveDirectory;
        _startImmediately = startImmediately;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private string _source = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private string _saveDirectory;

    [ObservableProperty] private bool _startImmediately;

    [ObservableProperty] private bool _isMagnet;
    [ObservableProperty] private bool _isFile;
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private string _previewName = "";
    [ObservableProperty] private string _previewSize = "";
    [ObservableProperty] private string _previewFiles = "";
    [ObservableProperty] private string _previewHash = "";
    [ObservableProperty] private string _validationMessage = "";

    public bool CanAdd => (IsMagnet || IsFile) && !string.IsNullOrWhiteSpace(SaveDirectory);

    public event Action<AddTorrentRequest?>? Completed;

    partial void OnSourceChanged(string value) => _ = AnalyzeAsync(value);

    private async Task AnalyzeAsync(string value)
    {
        value = value.Trim();
        IsMagnet = false;
        IsFile = false;
        HasPreview = false;
        ValidationMessage = "";

        if (value.Length == 0) { OnPropertyChanged(nameof(CanAdd)); return; }

        if (value.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            if (MagnetLink.TryParse(value, out var magnet))
            {
                IsMagnet = true;
                HasPreview = true;
                PreviewName = magnet.Name ?? "(name will appear once metadata arrives)";
                PreviewSize = magnet.Size is { } s ? Format.Bytes(s) : "unknown size";
                PreviewFiles = magnet.AnnounceUrls.Count > 0 ? $"{magnet.AnnounceUrls.Count} tracker(s)" : "DHT only";
                PreviewHash = magnet.InfoHashes.V1OrV2.ToHex().ToLowerInvariant();
            }
            else
            {
                ValidationMessage = "That magnet link couldn't be parsed.";
            }
        }
        else if (File.Exists(value))
        {
            try
            {
                var torrent = await Torrent.LoadAsync(value);
                IsFile = true;
                HasPreview = true;
                PreviewName = torrent.Name;
                PreviewSize = Format.Bytes(torrent.Size);
                PreviewFiles = torrent.Files.Count == 1 ? "1 file" : $"{torrent.Files.Count} files";
                PreviewHash = torrent.InfoHashes.V1OrV2.ToHex().ToLowerInvariant();
            }
            catch (Exception ex)
            {
                ValidationMessage = $"Not a valid .torrent file: {ex.Message}";
            }
        }
        else
        {
            ValidationMessage = "Paste a magnet link or the path to a .torrent file.";
        }

        OnPropertyChanged(nameof(CanAdd));
    }

    [RelayCommand]
    private async Task BrowseFile()
    {
        var files = await _dialogs.PickTorrentFilesAsync();
        if (files.Count > 0) Source = files[0];
    }

    [RelayCommand]
    private async Task BrowseFolder()
    {
        var folder = await _dialogs.PickFolderAsync(SaveDirectory);
        if (folder is not null) SaveDirectory = folder;
    }

    [RelayCommand]
    private async Task PasteFromClipboard()
    {
        var text = await _dialogs.GetClipboardTextAsync();
        if (!string.IsNullOrWhiteSpace(text)) Source = text.Trim();
    }

    [RelayCommand]
    private void Confirm()
    {
        if (!CanAdd) return;
        Completed?.Invoke(new AddTorrentRequest(Source.Trim(), IsMagnet, SaveDirectory.Trim(), StartImmediately));
    }

    [RelayCommand]
    private void Cancel() => Completed?.Invoke(null);
}
