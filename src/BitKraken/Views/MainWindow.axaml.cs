using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using BitKraken.Services;
using BitKraken.ViewModels;

namespace BitKraken.Views;

public partial class MainWindow : Window, IDialogService
{
    public MainWindow()
    {
        InitializeComponent();
        ConfigureChrome();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm) vm.Dialogs = this;
        };

        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaximizeButton.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        CloseButton.Click += (_, _) => Close();

        ToastHost.AddHandler(PointerPressedEvent, OnToastPressed, RoutingStrategies.Bubble);

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        KeyDown += OnKeyDown;
        Closing += OnClosing;

        Diagnostics.DebugHooks.Attach(this);
    }

    private bool _shutdownComplete;

    /// <summary>Intercepts the first close to flush fast-resume data and stop the engine cleanly, then closes for real.</summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;

        try
        {
            await App.Torrents.ShutdownAsync();
        }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
    }

    private void ConfigureChrome()
    {
        if (OperatingSystem.IsMacOS())
        {
            // Keep the native traffic lights (they overlay the client area) and clear space for them.
            WindowDecorations = WindowDecorations.Full;
            CaptionButtons.IsVisible = false;
            TitleLeft.Margin = new Avalonia.Thickness(84, 0, 0, 0);
        }
        else
        {
            // Border-only keeps native resize edges while we draw our own title bar and caption buttons.
            WindowDecorations = WindowDecorations.BorderOnly;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        if (ctrl && e.Key == Key.O) { vm.AddTorrentFileCommand.Execute(null); e.Handled = true; }
        else if (ctrl && e.Key == Key.M) { vm.AddMagnetCommand.Execute(null); e.Handled = true; }
        else if (ctrl && e.Key == Key.OemComma) { vm.OpenSettingsCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Delete && vm.HasSelection && !(FocusManager?.GetFocusedElement() is TextBox)) { vm.RemoveSelectedCommand.Execute(null); e.Handled = true; }
    }

    private void OnToastPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source || source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (source is StyledElement { DataContext: ToastViewModel toast })
            toast.ActivateCommand.Execute(null);
    }

    // ----- Drag & drop ---------------------------------------------------------------------------------------------

    private static bool HasTorrentPayload(DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is { Length: > 0 } files)
            return files.Any(f => f.Name.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase));
        var text = e.DataTransfer.TryGetText();
        return text is not null && text.TrimStart().StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        var ok = HasTorrentPayload(e);
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.IsVisible = ok;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasTorrentPayload(e) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => DropOverlay.IsVisible = false;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        if (DataContext is not MainWindowViewModel vm) return;

        var sources = new List<string>();
        if (e.DataTransfer.TryGetFiles() is { } files)
            sources.AddRange(files.Select(f => f.TryGetLocalPath()).Where(p => p is not null && p.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))!);

        var text = e.DataTransfer.TryGetText()?.Trim();
        if (text is not null && text.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            sources.Add(text);

        if (sources.Count > 0) await vm.AddPathsAsync(sources);
    }

    // ----- IDialogService ------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<string>> PickTorrentFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open torrent files",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Torrent files") { Patterns = ["*.torrent"], MimeTypes = ["application/x-bittorrent"], AppleUniformTypeIdentifiers = ["org.bittorrent.torrent"] },
                FilePickerFileTypes.All,
            ],
        });

        return files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Cast<string>().ToList();
    }

    public async Task<string?> PickFolderAsync(string? initialDirectory)
    {
        IStorageFolder? start = null;
        if (initialDirectory is not null && Directory.Exists(initialDirectory))
            start = await StorageProvider.TryGetFolderFromPathAsync(new Uri(initialDirectory));

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose download folder",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<AddTorrentRequest?> ShowAddTorrentAsync(AddTorrentViewModel viewModel)
    {
        var dialog = new AddTorrentDialog { DataContext = viewModel };
        return await dialog.ShowDialog<AddTorrentRequest?>(this);
    }

    public async Task<bool> ShowSettingsAsync(SettingsViewModel viewModel)
    {
        var dialog = new SettingsDialog { DataContext = viewModel };
        return await dialog.ShowDialog<bool>(this);
    }

    public async Task<RemoveChoice> ConfirmRemoveAsync(IReadOnlyList<string> names)
    {
        var dialog = new RemoveDialog(names);
        return await dialog.ShowDialog<RemoveChoice>(this);
    }

    public async Task<string?> GetClipboardTextAsync()
    {
        if (Clipboard is null) return null;
        return await Clipboard.TryGetTextAsync();
    }

    public async Task SetClipboardTextAsync(string text)
    {
        if (Clipboard is null) return;
        await Clipboard.SetTextAsync(text);
    }

    public void OpenInFileManager(string path)
    {
        try
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return;

            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", [path]);
            else
                Process.Start("xdg-open", [path]);
        }
        catch
        {
            // Not fatal if the shell isn't cooperating.
        }
    }
}
