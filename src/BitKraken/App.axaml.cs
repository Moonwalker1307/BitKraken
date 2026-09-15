using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BitKraken.Services;
using BitKraken.ViewModels;
using BitKraken.Views;

namespace BitKraken;

public partial class App : Application
{
    public static SettingsService Settings { get; private set; } = null!;
    public static TorrentService Torrents { get; private set; } = null!;

    /// <summary>Torrents/magnets handed to us before the engine was ready to take them.</summary>
    private readonly List<string> _pendingSources = [];

    private MainWindowViewModel? _viewModel;
    private Window? _mainWindow;
    private bool _ready;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Must stay synchronous: the lifetime shows MainWindow as soon as this method returns.
        Settings = new SettingsService();
        Settings.Load();

        Torrents = new TorrentService(Settings);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel(Torrents, Settings);
            var window = new MainWindow { DataContext = vm };
            _viewModel = vm;
            _mainWindow = window;
            desktop.MainWindow = window;

            // A magnet link can reach us three ways, and all of them can happen before the engine is up,
            // so everything funnels through the same queue and is flushed once it is.
            //  1. on our command line ("BitKraken magnet:?..." - a browser on Windows/Linux, or the shell)
            QueueSources(desktop.Args ?? []);
            //  2. as a URL event: how macOS delivers magnet: links to the app registered in Info.plist,
            //     whether it was already running or was just launched by the click. These never appear in Args.
            SubscribeToUrlActivation();
            //  3. from a second launch that handed its arguments over and quit (see SingleInstance).
            SingleInstance.Listen(sources => QueueSources(sources));
            desktop.Exit += (_, _) => SingleInstance.Stop();

            // Windows and Linux only learn that BitKraken handles magnet: links if we tell them.
            _ = ShellIntegration.ApplyAsync(Settings.Current.HandleMagnetLinks);
            Settings.Changed += (_, _) => _ = ShellIntegration.ApplyAsync(Settings.Current.HandleMagnetLinks);

            // Kick off the engine after the window is shown so the UI appears instantly.
            window.Opened += async (_, _) =>
            {
                await vm.InitializeAsync();
                _ready = true;
                await FlushPendingSourcesAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Listens for the URL / file activations the OS sends to an app registered for a scheme.</summary>
    private void SubscribeToUrlActivation()
    {
        var activatable = (ApplicationLifetime as IActivatableLifetime) ?? this.TryGetFeature<IActivatableLifetime>();
        if (activatable is null) return;

        activatable.Activated += (_, e) =>
        {
            switch (e)
            {
                case ProtocolActivatedEventArgs protocol:
                    QueueSources([protocol.Uri.OriginalString]);
                    break;

                case FileActivatedEventArgs files:
                    QueueSources(files.Files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Cast<string>().ToList());
                    break;
            }
        };
    }

    /// <summary>Adds every torrent/magnet in <paramref name="sources"/>, queueing them if the engine isn't up yet.</summary>
    private void QueueSources(IEnumerable<string> sources)
    {
        var accepted = sources.Select(NormalizeSource).Where(s => s is not null).Cast<string>().ToList();
        if (accepted.Count == 0)
        {
            // A second launch with nothing to add still means "the user asked for BitKraken".
            Dispatcher.UIThread.Post(BringToFront);
            return;
        }

        Dispatcher.UIThread.Post(() => _ = DeliverAsync(accepted));
    }

    private async Task DeliverAsync(List<string> sources)
    {
        BringToFront();

        if (!_ready || _viewModel is null)
        {
            _pendingSources.AddRange(sources);
            return;
        }

        await _viewModel.AddPathsAsync(sources);
    }

    private async Task FlushPendingSourcesAsync()
    {
        if (_pendingSources.Count == 0 || _viewModel is null) return;

        var sources = _pendingSources.ToList();
        _pendingSources.Clear();
        await _viewModel.AddPathsAsync(sources);
    }

    private void BringToFront()
    {
        if (!_ready || _mainWindow is null) return;

        if (!_mainWindow.IsVisible) _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    /// <summary>Turns what the shell handed us into a magnet link or a torrent path, or null if it is neither.</summary>
    private static string? NormalizeSource(string source)
    {
        if (source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return source;

        // A browser or file manager can pass a .torrent as a file:// URL rather than a plain path.
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile) source = uri.LocalPath;

        return File.Exists(source) ? source : null;
    }
}
