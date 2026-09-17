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
        if (_icon is not null) _icon.ToolTipText = _tooltip;
    }

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

        _icon = new TrayIcon
        {
            Icon = LoadIcon(),
            ToolTipText = _tooltip,
            Menu = menu,
            IsVisible = true,
        };

        // On Windows and most Linux desktops the icon itself is clickable; on macOS the click opens
        // the menu instead, which is why "Show" is the first item in it.
        _icon.Clicked += (_, _) => _actions.Show();

        TrayIcon.SetIcons(_application, [_icon]);
    }

    private void Remove()
    {
        if (_icon is null) return;

        // Clearing the collection first stops Avalonia holding on to a disposed icon.
        TrayIcon.SetIcons(_application, []);
        _icon.IsVisible = false;
        _icon.Dispose();
        _icon = null;
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
