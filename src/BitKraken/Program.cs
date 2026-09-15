using Avalonia;
using BitKraken.Services;

namespace BitKraken;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Opening a magnet link (or a .torrent) starts a fresh process on Windows and Linux. If BitKraken
        // is already running, give it the link and let that window come forward instead of starting a
        // second engine on the same port.
        if (SingleInstance.TryHandOff(args)) return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
