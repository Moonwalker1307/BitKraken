using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BitKraken.ViewModels;
using BitKraken.Views;

namespace BitKraken.Diagnostics;

/// <summary>
/// Developer aids driven by environment variables (handy for headless UI checks):
///   BITKRAKEN_SCREENSHOT=/path/out.png           render the main window to a PNG ~4s after it opens
///   BITKRAKEN_DEBUG_DIALOG=add|settings|remove   also open that dialog and render it to /path/out-dialog.png
/// </summary>
internal static class DebugHooks
{
    public static void Attach(MainWindow window)
    {
        var shotPath = Environment.GetEnvironmentVariable("BITKRAKEN_SCREENSHOT");
        if (string.IsNullOrEmpty(shotPath)) return;

        window.Opened += (_, _) =>
        {
            // Optional: BITKRAKEN_DEBUG_SELECT=1 selects the first torrent, BITKRAKEN_DEBUG_TAB=n picks a details tab.
            DispatcherTimer.RunOnce(() =>
            {
                if (window.DataContext is MainWindowViewModel vm && Environment.GetEnvironmentVariable("BITKRAKEN_DEBUG_SELECT") == "1")
                    vm.SelectedTorrent = vm.FilteredTorrents.FirstOrDefault();
                if (int.TryParse(Environment.GetEnvironmentVariable("BITKRAKEN_DEBUG_TAB"), out var tab) && window.FindControl<TabControl>("DetailsTabs") is { } tabs)
                    tabs.SelectedIndex = tab;
            }, TimeSpan.FromSeconds(2));
            var delay = double.TryParse(Environment.GetEnvironmentVariable("BITKRAKEN_SCREENSHOT_DELAY"), out var d) ? d : 6;
            DispatcherTimer.RunOnce(() => Save(window, shotPath), TimeSpan.FromSeconds(delay));

            // BITKRAKEN_DEBUG_CLOSE_AFTER=n closes the window after n seconds (exercises the shutdown path).
            if (double.TryParse(Environment.GetEnvironmentVariable("BITKRAKEN_DEBUG_CLOSE_AFTER"), out var closeAfter))
                DispatcherTimer.RunOnce(window.Close, TimeSpan.FromSeconds(closeAfter));

            var dialog = Environment.GetEnvironmentVariable("BITKRAKEN_DEBUG_DIALOG");
            if (string.IsNullOrEmpty(dialog)) return;

            DispatcherTimer.RunOnce(async () =>
            {
                if (window.DataContext is not MainWindowViewModel vm) return;
                var dialogPath = Path.ChangeExtension(shotPath, null) + "-dialog.png";

                DispatcherTimer.RunOnce(() =>
                {
                    if (window.OwnedWindows.Count == 0) return;
                    var opened = window.OwnedWindows[0];
                    Save(opened, dialogPath);
                    opened.Close();
                }, TimeSpan.FromSeconds(2));

                switch (dialog)
                {
                    case "add":
                        await vm.OpenAddDialogAsync("magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c&dn=Example+Torrent");
                        break;
                    case "settings":
                        vm.OpenSettingsCommand.Execute(null);
                        break;
                    case "remove":
                        vm.RemoveSelectedCommand.Execute(null);
                        break;
                }
            }, TimeSpan.FromSeconds(5));
        };
    }

    private static void Save(Window window, string path)
    {
        try
        {
            var scale = window.RenderScaling;
            var size = new PixelSize((int)(window.Bounds.Width * scale), (int)(window.Bounds.Height * scale));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            bitmap.Render(window);
            bitmap.Save(path, new PngBitmapEncoderOptions());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Screenshot failed: {ex}");
        }
    }
}
