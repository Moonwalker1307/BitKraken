using System.Net;
using System.Text.Json;
using BitKraken.Models;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;

namespace BitKraken.Services;

/// <summary>
/// Thin wrapper around MonoTorrent's <see cref="ClientEngine"/>. Owns engine lifetime, persistence
/// (engine state + fast-resume + which torrents the user paused) and settings application.
/// </summary>
public sealed class TorrentService : IAsyncDisposable
{
    private static readonly string PausedStatePath = Path.Combine(SettingsService.AppDataDirectory, "paused.json");

    private readonly SettingsService _settings;
    private readonly HashSet<string> _pausedByUser = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private ClientEngine? _engine;
    private bool _shuttingDown;

    public TorrentService(SettingsService settings)
    {
        _settings = settings;
        _settings.Changed += async (_, _) => await ApplySettingsAsync();
    }

    public ClientEngine Engine => _engine ?? throw new InvalidOperationException("Engine not initialised.");
    public bool IsInitialized => _engine is not null;

    public event EventHandler<TorrentManager>? TorrentAdded;
    public event EventHandler<TorrentManager>? TorrentRemoved;
    public event EventHandler<string>? EngineError;

    /// <summary>Creates (or restores) the engine and starts all torrents that weren't paused by the user.</summary>
    public async Task InitializeAsync()
    {
        if (_engine is not null) return;

        LoadPausedSet();

        ClientEngine? engine = null;
        if (File.Exists(SettingsService.EngineStatePath))
        {
            try
            {
                engine = await ClientEngine.RestoreStateAsync(SettingsService.EngineStatePath);
                await engine.UpdateSettingsAsync(BuildEngineSettings(_settings.Current));
            }
            catch (Exception ex)
            {
                EngineError?.Invoke(this, $"Could not restore previous session: {ex.Message}");
                engine?.Dispose();
                engine = null;
            }
        }

        engine ??= new ClientEngine(BuildEngineSettings(_settings.Current));
        engine.CriticalException += (_, e) => EngineError?.Invoke(this, e.Exception.Message);
        _engine = engine;

        foreach (var manager in engine.Torrents.ToList())
        {
            HookManager(manager);
            TorrentAdded?.Invoke(this, manager);

            if (!_pausedByUser.Contains(Key(manager)))
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

        var start = autoStart ?? _settings.Current.StartTorrentsAutomatically;
        if (start)
        {
            _pausedByUser.Remove(Key(manager));
            await SafeStartAsync(manager);
        }
        else
        {
            _pausedByUser.Add(Key(manager));
        }

        SavePausedSet();
        _ = SaveStateAsync();
        return manager;
    }

    public async Task StartAsync(TorrentManager manager)
    {
        _pausedByUser.Remove(Key(manager));
        SavePausedSet();
        await SafeStartAsync(manager);
    }

    public async Task PauseAsync(TorrentManager manager)
    {
        _pausedByUser.Add(Key(manager));
        SavePausedSet();

        // MonoTorrent's Pause only works from an active state; Stop is the more robust "hold" for the UI.
        if (manager.State is TorrentState.Downloading or TorrentState.Seeding or TorrentState.Metadata or TorrentState.Hashing or TorrentState.Starting)
            await manager.StopAsync();
    }

    public async Task RemoveAsync(TorrentManager manager, bool deleteData)
    {
        _pausedByUser.Remove(Key(manager));
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
        await manager.HashCheckAsync(autoStart: !_pausedByUser.Contains(Key(manager)));
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

    private async Task ApplySettingsAsync()
    {
        if (_engine is null) return;
        try
        {
            await _engine.UpdateSettingsAsync(BuildEngineSettings(_settings.Current));
            var torrentSettings = BuildTorrentSettings();
            foreach (var manager in _engine.Torrents)
                await manager.UpdateSettingsAsync(torrentSettings);
        }
        catch (Exception ex)
        {
            EngineError?.Invoke(this, $"Could not apply settings: {ex.Message}");
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

    private static EngineSettings BuildEngineSettings(AppSettings s)
    {
        var builder = new EngineSettingsBuilder
        {
            AllowPortForwarding = s.EnablePortForwarding,
            AllowLocalPeerDiscovery = s.EnableLocalPeerDiscovery,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            CacheDirectory = SettingsService.CacheDirectory,
            MaximumConnections = Math.Max(10, s.MaxConnections),
            MaximumDownloadRate = Math.Max(0, s.MaxDownloadRateKiB) * 1024,
            MaximumUploadRate = Math.Max(0, s.MaxUploadRateKiB) * 1024,
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                ["ipv4"] = new(IPAddress.Any, s.ListenPort),
                ["ipv6"] = new(IPAddress.IPv6Any, s.ListenPort),
            },
            DhtEndPoint = s.EnableDht ? new IPEndPoint(IPAddress.Any, s.ListenPort) : null,
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
            foreach (var item in items) _pausedByUser.Add(item);
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
            File.WriteAllText(PausedStatePath, JsonSerializer.Serialize(_pausedByUser.ToArray()));
        }
        catch
        {
            // Non-fatal.
        }
    }
}
