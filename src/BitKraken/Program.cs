using Avalonia;
using BitKraken.Services;

namespace BitKraken;

internal static class Program
{
    /// <summary>
    /// Posts one notification and exits, reporting whether the in-process route worked. It is here
    /// rather than in a test because the answer depends on being inside BitKraken.app - which is the
    /// whole point of that route - and a test run out of a build directory is not.
    /// scripts/verify-macos-app.sh runs it against the bundle CI has just built.
    /// </summary>
    private const string NotifyTestFlag = "--notify-test";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == NotifyTestFlag)
        {
            var posted = MacNotifications.TryPost(
                args.Length > 1 ? args[1] : AppInfo.Name,
                args.Length > 2 ? args[2] : "");

            Console.WriteLine(posted
                ? "posted in-process, as this bundle"
                : "the in-process route is not available here");

            Environment.Exit(posted ? 0 : 1);
            return;
        }

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
