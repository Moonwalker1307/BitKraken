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
    public static TorrentPreferences Preferences { get; private set; } = null!;
    public static NetworkBinding Network { get; private set; } = null!;
    public static TorrentService Torrents { get; private set; } = null!;

    /// <summary>The running instance, for the window to ask about tray behaviour and to exit through.</summary>
    public static new App? Current => Application.Current as App;

    /// <summary>Torrents/magnets handed to us before the engine was ready to take them.</summary>
    private readonly List<string> _pendingSources = [];

    private MainWindowViewModel? _viewModel;
    private Window? _mainWindow;
    private TrayIconHost? _tray;
    private WatchFolderService? _watchFolder;
    private bool _ready;
    private bool _exiting;

    public override void Initialize()
    {
        // macOS titles the application menu - and its About / Hide / Quit items - after this. Without it
        // Avalonia falls back to "Avalonia Application", which is what the menu bar then shows.
        Name = AppInfo.Name;

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Must stay synchronous: the lifetime shows MainWindow as soon as this method returns.
        Settings = new SettingsService();
        Settings.Load();

        Preferences = new TorrentPreferences();
        Preferences.Load();

        Network = new NetworkBinding(Settings);
        Torrents = new TorrentService(Settings, Preferences, Network);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel(Torrents, Settings, Network);
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
            desktop.Exit += (_, _) => ShutdownServices();

            // Windows and Linux only learn that BitKraken handles magnet: links if we tell them.
            _ = ShellIntegration.ApplyAsync(Settings.Current.HandleMagnetLinks);
            Settings.Changed += (_, _) => _ = ShellIntegration.ApplyAsync(Settings.Current.HandleMagnetLinks);

            // The tray is the one thing that has to exist before the window can be hidden into it.
            _tray = new TrayIconHost(this, Settings, new TrayActions(
                Show: BringToFront,
                StartAll: () => vm.StartAllCommand.Execute(null),
                PauseAll: () => vm.PauseAllCommand.Execute(null),
                Quit: RequestExit));
            _tray.Apply();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.TrayTooltip)) _tray?.SetTooltip(vm.TrayTooltip);
            };

            // Kick off the engine after the window is shown so the UI appears instantly.
            window.Opened += async (_, _) =>
            {
                await vm.InitializeAsync();
                _ready = true;
                await FlushPendingSourcesAsync();

                // Started last: a folder full of torrents must not race the engine coming up.
                _watchFolder = new WatchFolderService(Settings, vm.AddFromWatchFolderAsync);
                _watchFolder.Error += (_, message) => Dispatcher.UIThread.Post(() => vm.ShowToast("Watch folder", message, isError: true));
                _watchFolder.Apply();
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

                // Clicking the dock icon of an app whose window is hidden in the tray. Without this the
                // window would have no way back on macOS, where the menu bar item is the only other door.
                case { Kind: ActivationKind.Reopen }:
                    Dispatcher.UIThread.Post(BringToFront);
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

    /// <summary>
    /// Whether closing the window should hide it instead of quitting. Only ever true when there is a
    /// tray icon to hide into, so a desktop without one can never strand a running BitKraken.
    /// </summary>
    public bool ShouldCloseToTray => !_exiting && Settings.Current.CloseToTray && _tray?.IsActive == true;

    /// <summary>Whether minimizing the window should hide it to the tray.</summary>
    public bool ShouldMinimizeToTray => !_exiting && Settings.Current.MinimizeToTray && _tray?.IsActive == true;

    /// <summary>Quits for real, from the tray menu: the window closes and takes the engine down with it.</summary>
    public void RequestExit()
    {
        _exiting = true;
        _tray?.Dispose();
        _tray = null;

        if (_mainWindow is null) return;

        // Closing runs the same shutdown the window's own close button does, and with the last window
        // gone the lifetime exits. A hidden window closes just as well, and without showing itself first.
        _mainWindow.Close();
    }

    /// <summary>
    /// Releases what the app owns outside the engine, as the lifetime exits.
    /// </summary>
    /// <remarks>
    /// Nothing in here may throw. Avalonia raises Exit with no catch around it, on the main thread's
    /// way out of Main, so an exception escaping this is not a logged error and not a dialog - it is
    /// an abort, with no window left to report it in and a crash log instead of a clean quit.
    /// </remarks>
    private void ShutdownServices()
    {
        try
        {
            SingleInstance.Stop();
        }
        catch (Exception)
        {
            // Nothing left to clean up for.
        }

        try
        {
            _tray?.Dispose();
            _tray = null;

            _watchFolder?.Dispose();
            _watchFolder = null;
        }
        catch (Exception)
        {
            // As above: on the way out, a failed teardown is not worth a crash report.
        }
    }

    /// <summary>Turns what the shell handed us into a magnet link or a torrent path, or null if it is neither.</summary>
    internal static string? NormalizeSource(string source)
    {
        if (source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return source;

        // A browser or file manager can pass a .torrent as a file:// URL rather than a plain path.
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile) source = uri.LocalPath;

        return File.Exists(source) ? source : null;
    }
}
