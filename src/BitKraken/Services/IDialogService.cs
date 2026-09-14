using BitKraken.ViewModels;

namespace BitKraken.Services;

public enum RemoveChoice
{
    Cancel,
    RemoveKeepData,
    RemoveDeleteData,
}

/// <summary>What the "Add torrent" dialog hands back once confirmed.</summary>
public sealed record AddTorrentRequest(string Source, bool IsMagnet, string SaveDirectory, bool StartImmediately);

/// <summary>View-side services the main view-model needs (file pickers, dialogs, shell integration).</summary>
public interface IDialogService
{
    Task<IReadOnlyList<string>> PickTorrentFilesAsync();
    Task<string?> PickFolderAsync(string? initialDirectory);
    Task<AddTorrentRequest?> ShowAddTorrentAsync(AddTorrentViewModel viewModel);
    Task<bool> ShowSettingsAsync(SettingsViewModel viewModel);
    Task<RemoveChoice> ConfirmRemoveAsync(IReadOnlyList<string> names);
    Task<string?> GetClipboardTextAsync();
    Task SetClipboardTextAsync(string text);
    void OpenInFileManager(string path);
}
