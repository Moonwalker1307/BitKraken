using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace BitKraken.Services;

/// <summary>What the tray menu can ask for. Supplied by <see cref="App"/>, which owns the window and engine.</summary>
/// <param name="Show">Bring the main window back to the front.</param>
/// <param name="StartAll">Start every torrent.</param>
/// <param name="PauseAll">Pause every torrent.</param>
/// <param name="Quit">Shut the engine down and exit for real.</param>
public sealed record TrayActions(Action Show, Action StartAll, Action PauseAll, Action Quit);

/// <summary>
/// BitKraken's presence in the system tray / menu bar: an icon that reopens the window, a menu for the
/// things worth doing without one, and a tooltip carrying the current transfer rates.
/// </summary>
/// <remarks>
/// Whether a tray exists at all is not something the platform will tell us - a bare window manager
/// accepts the icon and shows nothing. That is why closing to the tray is off unless the user turns it
/// on: the setting is the only reliable evidence that there is somewhere for the window to go.
/// </remarks>
public sealed class TrayIconHost : IDisposable
{
    private readonly Application _application;
    private readonly SettingsService _settings;
    private readonly TrayActions _actions;
    private TrayIcon? _icon;
    private string _tooltip = AppInfo.Name;

    public TrayIconHost(Application application, SettingsService settings, TrayActions actions)
    {
        _application = application;
        _settings = settings;
        _actions = actions;
        // A tray icon is a UI object, and a settings save does not have to come from the UI thread.
        _settings.Changed += (_, _) => Dispatcher.UIThread.Post(Apply);
    }

    /// <summary>True while the icon is up, which is the only case where hiding the window is safe.</summary>
    public bool IsActive => _icon is not null;

    /// <summary>Puts the icon up or takes it down to match the current settings.</summary>
    public void Apply()
    {
        if (_settings.Current.ShowTrayIcon)
            Create();
        else
            Remove();
    }

    /// <summary>Updates the text shown when hovering the icon. Cheap enough to call every second.</summary>
    public void SetTooltip(string text)
    {
        _tooltip = string.IsNullOrWhiteSpace(text) ? AppInfo.Name : text;
        if (_icon is null) return;

        try
        {
            _icon.ToolTipText = _tooltip;
        }
        catch (Exception)
        {
            // Called once a second off the UI tick. Avalonia disposes tray icons itself when the
            // dispatcher shuts down, without telling us, so a tick landing in that window would
            // otherwise reach a disposed icon - and a tooltip is never worth a crash.
        }
    }

    /// <remarks>
    /// Guarded for the same reason <see cref="Remove"/> is: this runs from a dispatcher post when
    /// settings are saved, where an exception has no caller to catch it and takes the process with it.
    /// A desktop that will not give us a tray icon should cost the icon, not the app.
    /// </remarks>
    private void Create()
    {
        if (_icon is not null) return;

        var menu = new NativeMenu();
        menu.Add(Item($"Show {AppInfo.Name}", _actions.Show));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("Start all", _actions.StartAll));
        menu.Add(Item("Pause all", _actions.PauseAll));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("Quit", _actions.Quit));

        try
        {
            var icon = new TrayIcon
            {
                Icon = LoadIcon(),
                ToolTipText = _tooltip,
                Menu = menu,
                IsVisible = true,
            };

            // On Windows and most Linux desktops the icon itself is clickable; on macOS the click opens
            // the menu instead, which is why "Show" is the first item in it.
            icon.Clicked += (_, _) => _actions.Show();

            TrayIcon.SetIcons(_application, [icon]);

            // Only recorded once it is really up, so IsActive cannot claim a tray that is not there
            // and talk the window into hiding itself into it.
            _icon = icon;
        }
        catch (Exception)
        {
            _icon = null;
        }
    }

    private void Remove()
    {
        if (_icon is null) return;

        // Cleared first, so switching the tray off and then exiting does not try to remove it twice.
        _icon = null;

        try
        {
            // Clearing the collection is the whole teardown: Avalonia's handler for the Icons property
            // disposes every icon that was in it, and disposing a tray icon is what takes it out of the
            // tray. Nothing may touch the icon afterwards - Dispose does not null the platform handle it
            // just released, so a later IsVisible or Dispose call reaches straight into freed memory.
            TrayIcon.SetIcons(_application, []);
        }
        catch (Exception)
        {
            // This runs on the way out, where there is nothing left to recover for, and a tray that
            // will not let go of its icon is not a reason to take the process down with it.
        }
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    private static WindowIcon? LoadIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://BitKraken/Assets/bitkraken.png"));
            return new WindowIcon(stream);
        }
        catch
        {
            // An icon-less tray entry is still better than no tray entry.
            return null;
        }
    }

    public void Dispose() => Remove();
}
