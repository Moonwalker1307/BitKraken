using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BitKraken.Services;
using BitKraken.ViewModels;
using BitKraken.Views;

namespace BitKraken;

public partial class App : Application
{
    public static SettingsService Settings { get; private set; } = null!;
    public static TorrentService Torrents { get; private set; } = null!;

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
            desktop.MainWindow = new MainWindow { DataContext = vm };

            // Kick off the engine after the window is shown so the UI appears instantly.
            desktop.MainWindow.Opened += async (_, _) =>
            {
                await vm.InitializeAsync();

                // Support "BitKraken file.torrent" / "BitKraken magnet:?..." from the shell or file associations.
                var sources = (desktop.Args ?? []).Where(a => a.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) || File.Exists(a)).ToList();
                if (sources.Count > 0) await vm.AddPathsAsync(sources);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
