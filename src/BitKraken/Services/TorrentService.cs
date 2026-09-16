using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using BitKraken.Models;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Peer;
using MonoTorrent.Connections.Tracker;
using MonoTorrent.Trackers;

namespace BitKraken.Services;

/// <summary>
/// Thin wrapper around MonoTorrent's <see cref="ClientEngine"/>. Owns engine lifetime, persistence
/// (engine state + fast-resume + which torrents the user paused) and settings application.
/// </summary>
public sealed class TorrentService : IAsyncDisposable
{
    private static readonly string PausedStatePath = Path.Combine(SettingsService.AppDataDirectory, "paused.json");

    /// <summary>
    /// Well-known open trackers appended to public torrents when <see cref="AppSettings.AddFallbackTrackers"/>
    /// is on. Each goes into its own tier so a slow one never holds up the others.
    /// </summary>
    private static readonly string[] FallbackTrackers =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.demonii.com:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://explodie.org:6969/announce",
        "udp://tracker.openbittorrent.com:6969/announce",
    ];

    private readonly SettingsService _settings;
    private readonly NetworkBinding _binding;
    private readonly Factories _factories;
    private readonly TorrentActivation _activation = new();
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _networkLock = new(1, 1);
    private ClientEngine? _engine;
    private bool _shuttingDown;

    // The route in force the last time settings were applied, so we can tell when it moved.
    private (ProxyConfiguration Proxy, string Interface) _appliedRoute;

    public TorrentService(SettingsService settings, NetworkBinding binding)
    {
        _settings = settings;
        _binding = binding;

        // Every socket the engine opens goes through these, so the binding and the proxy cover peers and
        // trackers alike. They read the settings per connection, which is what lets them change at runtime.
        var outbound = new OutboundConnector(binding, settings);
        var connector = new BoundSocketConnector(outbound);
        var httpClients = new BoundHttpClientFactory(outbound);
        _factories = Factories.Default
            .WithPeerConnectionCreator("ipv4", uri => new SocketPeerConnection(uri, connector))
            .WithPeerConnectionCreator("ipv6", uri => new SocketPeerConnection(uri, connector))
            .WithSocketConnectorCreator(() => connector)
            .WithHttpClientCreator(httpClients.Create)
            .WithTrackerCreator("udp", uri => new Tracker(
                BlockedWhileProxied(new UdpTrackerConnection(uri, AddressFamily.InterNetwork)),
                BlockedWhileProxied(new UdpTrackerConnection(uri, AddressFamily.InterNetworkV6))));

        _appliedRoute = CurrentRoute();
        _settings.Changed += async (_, _) => await ApplySettingsAsync();
        _binding.Changed += (_, state) => _ = OnBindingChangedAsync(state);
    }

    public ClientEngine Engine => _engine ?? throw new InvalidOperationException("Engine not initialised.");
    public bool IsInitialized => _engine is not null;

    public event EventHandler<TorrentManager>? TorrentAdded;
    public event EventHandler<TorrentManager>? TorrentRemoved;
    public event EventHandler<string>? EngineError;

    /// <summary>Raised when the kill switch trips (true) or lifts (false).</summary>
    public event EventHandler<bool>? NetworkSuspendedChanged;

    /// <summary>True while everything is held because the bound interface is gone.</summary>
    public bool IsNetworkSuspended => _activation.IsSuspended;

    /// <summary>Creates (or restores) the engine and starts all torrents that weren't paused by the user.</summary>
    public async Task InitializeAsync()
    {
        if (_engine is not null) return;

        LoadPausedSet();

        // A tunnel that is already down at startup means we never start anything in the first place.
        if (!_binding.Current.IsAvailable) _activation.Suspend();

        ClientEngine? engine = null;
        if (File.Exists(SettingsService.EngineStatePath))
        {
            try
            {
                engine = await ClientEngine.RestoreStateAsync(SettingsService.EngineStatePath, _factories);
                await engine.UpdateSettingsAsync(BuildEngineSettings(_settings.Current));
            }
            catch (Exception ex)
            {
                EngineError?.Invoke(this, $"Could not restore previous session: {ex.Message}");
                engine?.Dispose();
                engine = null;
            }
        }

        engine ??= new ClientEngine(BuildEngineSettings(_settings.Current), _factories);
        _appliedRoute = CurrentRoute();
        engine.CriticalException += (_, e) => EngineError?.Invoke(this, e.Exception.Message);
        _engine = engine;

        foreach (var manager in engine.Torrents.ToList())
        {
            HookManager(manager);
            TorrentAdded?.Invoke(this, manager);

            // Cheap (no network I/O) and idempotent, so it's safe to redo on every restore.
            await AddFallbackTrackersAsync(manager);

            if (_activation.RequestRestore(Key(manager)))
                _ = SafeStartAsync(manager);
        }
    }

    public async Task<TorrentManager> AddTorrentFileAsync(string path, string? saveDirectory = null, bool? autoStart = null)
    {
        var torrent = await Torrent.LoadAsync(path);
        if (Engine.Contains(torrent.InfoHashes))
            throw new InvalidOperationException($"\"{torrent.Name}\" is already in your list.");

        var manager = await Engine.AddAsync(torrent, saveDirectory ?? _settings.Current.DownloadDirectory, BuildTorrentSettings());
        return await FinishAddAsync(manager, autoStart);
    }

    public async Task<TorrentManager> AddMagnetAsync(string magnetUri, string? saveDirectory = null, bool? autoStart = null)
    {
        if (!MagnetLink.TryParse(magnetUri.Trim(), out var magnet))
            throw new InvalidOperationException("That doesn't look like a valid magnet link.");

        if (Engine.Contains(magnet.InfoHashes))
            throw new InvalidOperationException($"\"{magnet.Name ?? magnet.InfoHashes.V1OrV2.ToHex()}\" is already in your list.");

        var manager = await Engine.AddAsync(magnet, saveDirectory ?? _settings.Current.DownloadDirectory, BuildTorrentSettings());
        return await FinishAddAsync(manager, autoStart);
    }

    private async Task<TorrentManager> FinishAddAsync(TorrentManager manager, bool? autoStart)
    {
        HookManager(manager);
        TorrentAdded?.Invoke(this, manager);

        await AddFallbackTrackersAsync(manager);

        var start = autoStart ?? _settings.Current.StartTorrentsAutomatically;
        if (start)
        {
            if (_activation.RequestStart(Key(manager)))
                await SafeStartAsync(manager);
        }
        else
        {
            _activation.Pause(Key(manager));
        }

        SavePausedSet();
        _ = SaveStateAsync();
        return manager;
    }

    public async Task StartAsync(TorrentManager manager)
    {
        var start = _activation.RequestStart(Key(manager));
        SavePausedSet();

        // Suspended: the torrent is now queued rather than paused, and starts itself when the
        // interface is back. Starting it now would only spray failed connections.
        if (start) await SafeStartAsync(manager);
    }

    public async Task PauseAsync(TorrentManager manager)
    {
        _activation.Pause(Key(manager));
        SavePausedSet();

        // MonoTorrent's Pause only works from an active state; Stop is the more robust "hold" for the UI.
        if (manager.State is TorrentState.Downloading or TorrentState.Seeding or TorrentState.Metadata or TorrentState.Hashing or TorrentState.Starting)
            await manager.StopAsync();
    }

    public async Task RemoveAsync(TorrentManager manager, bool deleteData)
    {
        _activation.Forget(Key(manager));
        SavePausedSet();

        if (manager.State != TorrentState.Stopped)
            await manager.StopAsync(TimeSpan.FromSeconds(5));

        var mode = deleteData ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly;
        await Engine.RemoveAsync(manager, mode);
        TorrentRemoved?.Invoke(this, manager);
        _ = SaveStateAsync();
    }

    public async Task RecheckAsync(TorrentManager manager)
    {
        if (manager.State != TorrentState.Stopped)
            await manager.StopAsync();
        await manager.HashCheckAsync(autoStart: _activation.RequestRestore(Key(manager)));
    }

    public Task StartAllAsync() => Task.WhenAll(Engine.Torrents.Select(StartAsync));
    public Task PauseAllAsync() => Task.WhenAll(Engine.Torrents.Select(PauseAsync));

    public async Task SetFilePriorityAsync(TorrentManager manager, ITorrentManagerFile file, Priority priority)
        => await manager.SetFilePriorityAsync(file, priority);

    public async Task SaveStateAsync()
    {
        if (_engine is null) return;
        if (!await _stateLock.WaitAsync(0)) return;
        try
        {
            await _engine.SaveStateAsync(SettingsService.EngineStatePath);
        }
        catch (Exception ex)
        {
            EngineError?.Invoke(this, $"Failed to save session: {ex.Message}");
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task ShutdownAsync()
    {
        if (_engine is null || _shuttingDown) return;
        _shuttingDown = true;

        try
        {
            await SaveStateAsync();
            await _engine.StopAllAsync(TimeSpan.FromSeconds(5));
            // Fast-resume data is written during Stop; persist again so it lands in the state file.
            await _engine.SaveStateAsync(SettingsService.EngineStatePath);
        }
        catch
        {
            // Best-effort: we're on the way out.
        }
        finally
        {
            _engine.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync();

    /// <summary>
    /// The kill switch. The bound interface going away stops every torrent that was running and, when
    /// it returns, starts exactly those again - not the ones the user paused themselves.
    /// </summary>
    private async Task OnBindingChangedAsync(NetworkBindingState state)
    {
        if (_shuttingDown) return;

        await _networkLock.WaitAsync();
        try
        {
            if (_shuttingDown) return;

            if (!state.IsAvailable)
            {
                await SuspendForNetworkAsync();
                // Re-applying settings now drops the listeners, since there is no address to listen on.
                await ApplySettingsAsync();
            }
            else
            {
                // Rebind to the new address first, so the torrents we start are already on the tunnel.
                await ApplySettingsAsync();
                await ResumeAfterNetworkAsync();
            }
        }
        catch (Exception ex)
        {
            EngineError?.Invoke(this, $"Network change could not be applied: {ex.Message}");
        }
        finally
        {
            _networkLock.Release();
        }
    }

    private async Task SuspendForNetworkAsync()
    {
        if (!_activation.Suspend()) return;

        NetworkSuspendedChanged?.Invoke(this, true);
        if (_engine is null) return;

        foreach (var manager in _engine.Torrents.ToList())
        {
            var key = Key(manager);
            if (_activation.IsPausedByUser(key)) continue;

            _activation.Defer(key);
            if (manager.State is TorrentState.Stopped or TorrentState.Stopping) continue;

            try
            {
                await manager.StopAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Already on the way down, or the engine is shutting down: nothing to recover from.
            }
        }
    }

    private async Task ResumeAfterNetworkAsync()
    {
        if (!_activation.IsSuspended) return;

        var owed = _activation.Resume();
        NetworkSuspendedChanged?.Invoke(this, false);
        if (_engine is null) return;

        var wanted = owed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var manager in _engine.Torrents.ToList())
        {
            if (wanted.Contains(Key(manager)))
                await SafeStartAsync(manager);
        }
    }

    private async Task ApplySettingsAsync()
    {
        if (_engine is null) return;
        try
        {
            await _engine.UpdateSettingsAsync(BuildEngineSettings(_settings.Current));
            var torrentSettings = BuildTorrentSettings();
            foreach (var manager in _engine.Torrents)
                await manager.UpdateSettingsAsync(torrentSettings);

            await RestartIfRouteChangedAsync();
        }
        catch (Exception ex)
        {
            EngineError?.Invoke(this, $"Could not apply settings: {ex.Message}");
        }
    }

    private (ProxyConfiguration Proxy, string Interface) CurrentRoute()
        => (ProxyConfiguration.From(_settings.Current), _binding.Current.InterfaceName);

    /// <summary>
    /// Turning a proxy on doesn't do much for connections that are already open - they keep running
    /// along the old path, from the address the proxy was meant to replace. So when the route changes,
    /// the torrents are cycled and every connection is made again the new way.
    /// </summary>
    private async Task RestartIfRouteChangedAsync()
    {
        var route = CurrentRoute();
        if (route == _appliedRoute) return;

        _appliedRoute = route;
        if (_engine is null || _activation.IsSuspended) return;

        foreach (var manager in _engine.Torrents.ToList())
        {
            if (_activation.IsPausedByUser(Key(manager))) continue;
            if (manager.State is TorrentState.Stopped or TorrentState.Stopping) continue;

            try
            {
                await manager.StopAsync(TimeSpan.FromSeconds(5));
                await manager.StartAsync();
            }
            catch (Exception ex)
            {
                EngineError?.Invoke(this, $"Could not restart \"{manager.Name}\" on the new route: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Appends <see cref="FallbackTrackers"/> to a public torrent. More trackers queried in parallel means
    /// a better chance one answers quickly, which is usually what gates time-to-first-peer on a magnet.
    /// Private torrents are left untouched - MonoTorrent throws for those, and announcing a private
    /// torrent to public trackers is exactly what gets people banned from private sites.
    /// </summary>
    private async Task AddFallbackTrackersAsync(TorrentManager manager)
    {
        if (!_settings.Current.AddFallbackTrackers) return;

        var trackerManager = manager.TrackerManager;
        if (trackerManager.Private || manager.Torrent?.IsPrivate == true) return;

        var existing = trackerManager.Tiers
            .SelectMany(tier => tier.Trackers)
            .Select(tracker => tracker.Uri.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = new List<Uri>();
        foreach (var url in FallbackTrackers)
        {
            if (existing.Contains(url)) continue;
            try
            {
                var uri = new Uri(url);
                await trackerManager.AddTrackerAsync(uri);
                added.Add(uri);
            }
            catch
            {
                // A tracker we couldn't add is not worth failing the whole add over.
            }
        }

        // A magnet has no metadata yet, so the private flag is still unknown and reads as false. Add the
        // trackers now (that's where the speed-up is) but take them back out if metadata says private.
        if (manager.Torrent is null && added.Count > 0)
            _ = RemoveTrackersIfPrivateAsync(manager, added);
    }

    /// <summary>Strips the trackers added by <see cref="AddFallbackTrackersAsync"/> if metadata reveals a private torrent.</summary>
    private static async Task RemoveTrackersIfPrivateAsync(TorrentManager manager, List<Uri> added)
    {
        try
        {
            await manager.WaitForMetadataAsync();
            if (manager.Torrent?.IsPrivate != true) return;

            var trackerManager = manager.TrackerManager;
            var stale = trackerManager.Tiers
                .SelectMany(tier => tier.Trackers)
                .Where(tracker => added.Contains(tracker.Uri))
                .ToList();

            foreach (var tracker in stale)
                await trackerManager.RemoveTrackerAsync(tracker);
        }
        catch
        {
            // Torrent removed before metadata arrived, or the manager won't allow removal - nothing to do.
        }
    }

    private void HookManager(TorrentManager manager)
    {
        manager.TorrentStateChanged += (_, e) =>
        {
            // Persist when a torrent finishes so a crash doesn't lose the "complete" fast-resume.
            if (e.NewState == TorrentState.Seeding && e.OldState == TorrentState.Downloading)
                _ = SaveStateAsync();
        };
    }

    private async Task SafeStartAsync(TorrentManager manager)
    {
        try
        {
            if (manager.State is TorrentState.Stopped or TorrentState.Paused or TorrentState.Error)
                await manager.StartAsync();
        }
        catch (Exception ex)
        {
            EngineError?.Invoke(this, $"Could not start \"{manager.Name}\": {ex.Message}");
        }
    }

    /// <summary>Wraps a UDP tracker so it stops announcing the moment a proxy is configured.</summary>
    private ITrackerConnection BlockedWhileProxied(ITrackerConnection connection)
        => new ProxyBlockedTrackerConnection(connection, () => ProxyConfiguration.From(_settings.Current).IsEnabled);

    private EngineSettings BuildEngineSettings(AppSettings s)
    {
        var binding = _binding.Current;
        var proxy = ProxyConfiguration.From(s);
        var listenV4 = binding.ListenAddress(AddressFamily.InterNetwork);
        var listenV6 = binding.ListenAddress(AddressFamily.InterNetworkV6);

        // A family the bound interface doesn't have gets no listener at all - an "any" listener would
        // accept peers on every other interface, which is the leak binding is meant to close.
        var listenEndPoints = new Dictionary<string, IPEndPoint>();
        if (listenV4 is not null) listenEndPoints["ipv4"] = new IPEndPoint(listenV4, s.ListenPort);
        if (listenV6 is not null) listenEndPoints["ipv6"] = new IPEndPoint(listenV6, s.ListenPort);

        // A proxy carries outgoing connections only. Accepting incoming ones would let a peer reach the
        // address the proxy exists to hide, so we stop listening entirely while one is set.
        if (proxy.IsEnabled) listenEndPoints.Clear();

        var dhtAddress = listenV4 ?? listenV6;

        var builder = new EngineSettingsBuilder
        {
            // Asking the router to forward a port to a VPN address is meaningless, and the request
            // itself tells the LAN what we're doing, so it goes off whenever we're bound.
            AllowPortForwarding = s.EnablePortForwarding && !binding.IsBound && !proxy.IsEnabled,

            // Local peer discovery is a multicast shout on the LAN, and no proxy can carry it.
            AllowLocalPeerDiscovery = s.EnableLocalPeerDiscovery && !proxy.IsEnabled,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            CacheDirectory = SettingsService.CacheDirectory,
            MaximumConnections = Math.Max(10, s.MaxConnections),
            MaximumDownloadRate = Math.Max(0, s.MaxDownloadRateKiB) * 1024,
            MaximumUploadRate = Math.Max(0, s.MaxUploadRateKiB) * 1024,
            ListenEndPoints = listenEndPoints,
            // DHT is UDP, so it goes the same way as the UDP trackers when a proxy is set: nowhere.
            DhtEndPoint = s.EnableDht && !proxy.IsEnabled && dhtAddress is not null
                ? new IPEndPoint(dhtAddress, s.ListenPort)
                : null,
        };

        if (s.RequireEncryption)
            builder.AllowedEncryption = [EncryptionType.RC4Full, EncryptionType.RC4Header];

        return builder.ToSettings();
    }

    private TorrentSettings BuildTorrentSettings() => new TorrentSettingsBuilder
    {
        AllowDht = _settings.Current.EnableDht,
        AllowPeerExchange = _settings.Current.EnablePex,
    }.ToSettings();

    private static string Key(TorrentManager manager) => manager.InfoHashes.V1OrV2.ToHex();

    private void LoadPausedSet()
    {
        try
        {
            if (!File.Exists(PausedStatePath)) return;
            var items = JsonSerializer.Deserialize<string[]>(File.ReadAllText(PausedStatePath));
            if (items is null) return;
            _activation.LoadPaused(items);
        }
        catch
        {
            // Ignore - worst case everything auto-starts.
        }
    }

    private void SavePausedSet()
    {
        try
        {
            File.WriteAllText(PausedStatePath, JsonSerializer.Serialize(_activation.PausedByUser.ToArray()));
        }
        catch
        {
            // Non-fatal.
        }
    }
}
