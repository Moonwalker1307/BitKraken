using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using BitKraken.Models;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Peer;
using MonoTorrent.Connections.Tracker;
using MonoTorrent.PiecePicking;
using MonoTorrent.Trackers;

namespace BitKraken.Services;

/// <summary>Everything one torrent has shifted, added up across every session it has run in.</summary>
public readonly record struct TorrentTotals(long Uploaded, long Downloaded, TimeSpan Seeded);

/// <summary>
/// Thin wrapper around MonoTorrent's <see cref="ClientEngine"/>. Owns engine lifetime, persistence
/// (engine state + fast-resume + per-torrent preferences) and settings application.
/// </summary>
/// <remarks>
/// What is actually running is not decided at the call sites. Every reason a torrent might be stopped -
/// the user paused it, the bound interface went away, the queue is full, it has seeded its fill - feeds
/// into <see cref="ReconcileAsync"/>, which works out the set that should be running and moves the
/// engine to it. Callers change the inputs and ask for a reconcile; only the reconcile starts or stops.
/// </remarks>
public sealed class TorrentService : IAsyncDisposable
{
    /// <summary>How often the running set is re-derived, and the window transfer totals are sampled over.</summary>
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(2);

    /// <summary>Per-torrent preferences are written at most this often while only the totals are moving.</summary>
    private static readonly TimeSpan PreferenceSaveInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long shutdown waits for a reconcile in flight before giving up on a last sample.</summary>
    private static readonly TimeSpan ReconcileSettleTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long stopping a torrent waits for the tracker to acknowledge the final "stopped" announce.
    /// None, because there is nothing to wait for: MonoTorrent has already sent the announce by the time
    /// this timeout applies, and lets it finish on its own afterwards - removing a torrent and disposing
    /// it do not cancel it. The stop itself still completes properly, closing peers and flushing files.
    /// Waiting for a reply we do not read is what made removing a torrent take five seconds whenever one
    /// of its trackers was slow or dead. Shutdown is the one place that does wait - see ShutdownAsync.
    /// </summary>
    private static readonly TimeSpan StopAnnounceWait = TimeSpan.Zero;

    /// <summary>
    /// How long to wait for a stop that was already in flight before giving up on it. Every stop this
    /// app starts returns in milliseconds - none of them wait on a tracker - so this only has to cover
    /// a machine under load, never a dead announce.
    /// </summary>
    private static readonly TimeSpan StopSettleTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many times removal will stop a torrent and offer it to the engine. More than one because a
    /// reconcile already in flight can start it back up in between; only a few because each go is a full
    /// stop, and something restarting it that persistently is a bug rather than a race worth riding out.
    /// </summary>
    private const int RemoveAttempts = 3;

    /// <summary>
    /// Picks pieces in order instead of rarest-first. Both of MonoTorrent's usual heuristics have to go:
    /// rarest-first reorders the whole torrent, and the randomiser shuffles whatever is left. File
    /// priorities are still honoured, so a file set to Highest is the one that fills up first.
    /// </summary>
    private static readonly PieceRequesterSettings LinearPicking =
        new(allowPrioritisation: true, allowRandomised: false, allowRarestFirst: false);

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
    private readonly TorrentPreferences _preferences;
    private readonly NetworkBinding _binding;
    private readonly Factories _factories;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _networkLock = new(1, 1);
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);

    /// <summary>
    /// The picker each torrent was last given, so we only cycle one when the mode really changed.
    /// Concurrent because a reconcile pass now applies its decisions to several torrents at once, and
    /// the UI thread reads it when the user toggles sequential mode.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _appliedSequential = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Last sample of each torrent's session counters, for turning them into running totals.</summary>
    private readonly Dictionary<string, (long Received, long Sent)> _lastCounters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Torrents the user restarted past their seeding limit. Session-only, and deliberately so. A set,
    /// spelled as a dictionary because the UI thread adds to it while the reconcile timer reads it.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _seedLimitWaived = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Torrents on their way out of the engine. Removing one means stopping it and then handing it over,
    /// and the engine will only unregister a torrent that is Stopped - but stopping it raises a state
    /// change, every state change asks for a reconcile, and a reconcile starts whatever is owed a slot.
    /// So removal stopped a torrent and the queue started it again, every time, before the engine was
    /// ever asked. This is how the pass that decides what runs is told that this one is leaving.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _removing = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _reconcileTimer;
    private ClientEngine? _engine;
    private bool _shuttingDown;
    private bool _networkSuspended;
    private int _reconcilePending;
    private DateTime _lastSample;
    private DateTime _lastPreferenceSave = DateTime.UtcNow;
    private IReadOnlyDictionary<string, int> _waiting = QueuePlan.Empty.Waiting;

    // The route in force the last time settings were applied, so we can tell when it moved.
    private (ProxyConfiguration Proxy, string Interface) _appliedRoute;

    public TorrentService(SettingsService settings, TorrentPreferences preferences, NetworkBinding binding)
    {
        _settings = settings;
        _preferences = preferences;
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

    /// <summary>Raised when a torrent finishes downloading and starts seeding.</summary>
    public event EventHandler<TorrentManager>? TorrentCompleted;

    /// <summary>Raised when a torrent drops into the error state.</summary>
    public event EventHandler<TorrentManager>? TorrentFailed;

    /// <summary>Raised when a torrent has seeded as much as its limits allow and has been stopped.</summary>
    public event EventHandler<TorrentManager>? SeedLimitReached;

    /// <summary>Raised when the kill switch trips (true) or lifts (false).</summary>
    public event EventHandler<bool>? NetworkSuspendedChanged;

    /// <summary>True while everything is held because the bound interface is gone.</summary>
    public bool IsNetworkSuspended => _networkSuspended;

    /// <summary>Creates (or restores) the engine and starts whatever the queue allows.</summary>
    public async Task InitializeAsync()
    {
        if (_engine is not null) return;

        // A tunnel that is already down at startup means we never start anything in the first place.
        _networkSuspended = !_binding.Current.IsAvailable;

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
            Track(manager);
            TorrentAdded?.Invoke(this, manager);

            // Cheap (no network I/O) and idempotent, so it's safe to redo on every restore.
            await AddFallbackTrackersAsync(manager);
            await manager.UpdateSettingsAsync(BuildTorrentSettings(Key(manager)));
        }

        _lastSample = DateTime.UtcNow;
        _reconcileTimer = new Timer(_ => RequestReconcile(), null, ReconcileInterval, ReconcileInterval);
        await ReconcileAsync();
    }

    public async Task<TorrentManager> AddTorrentFileAsync(string path, string? saveDirectory = null, bool? autoStart = null)
    {
        var torrent = await Torrent.LoadAsync(path);
        if (Engine.Contains(torrent.InfoHashes))
            throw new InvalidOperationException($"\"{torrent.Name}\" is already in your list.");

        var manager = await Engine.AddAsync(
            torrent,
            saveDirectory ?? _settings.Current.DownloadDirectory,
            BuildTorrentSettings(torrent.InfoHashes.V1OrV2.ToHex()));

        return await FinishAddAsync(manager, autoStart);
    }

    public async Task<TorrentManager> AddMagnetAsync(string magnetUri, string? saveDirectory = null, bool? autoStart = null)
    {
        if (!MagnetLink.TryParse(magnetUri.Trim(), out var magnet))
            throw new InvalidOperationException("That doesn't look like a valid magnet link.");

        if (Engine.Contains(magnet.InfoHashes))
            throw new InvalidOperationException($"\"{magnet.Name ?? magnet.InfoHashes.V1OrV2.ToHex()}\" is already in your list.");

        var manager = await Engine.AddAsync(
            magnet,
            saveDirectory ?? _settings.Current.DownloadDirectory,
            BuildTorrentSettings(magnet.InfoHashes.V1OrV2.ToHex()));

        return await FinishAddAsync(manager, autoStart);
    }

    private async Task<TorrentManager> FinishAddAsync(TorrentManager manager, bool? autoStart)
    {
        var key = Key(manager);
        Track(manager);
        TorrentAdded?.Invoke(this, manager);

        await AddFallbackTrackersAsync(manager);

        // A torrent added without auto-start is paused, not queued: it waits for the user, not a slot.
        var start = autoStart ?? _settings.Current.StartTorrentsAutomatically;
        _preferences.Update(key, e => e.PausedByUser = !start);
        _preferences.Save();

        await ReconcileAsync();
        _ = SaveStateAsync();
        return manager;
    }

    /// <summary>The user asked for this torrent. Whether it runs now is still up to the queue.</summary>
    public async Task StartAsync(TorrentManager manager)
    {
        var key = Key(manager);
        _preferences.Update(key, e =>
        {
            e.PausedByUser = false;
            e.SeedLimitReached = false;
        });

        // Starting a torrent by hand overrides its seeding limit for the rest of the session. Re-arming
        // it here would only stop the torrent again a second later, which is not what the click meant.
        if (SeedLimitFor(key).IsReached(RatioOf(manager), SeededFor(key)))
            _seedLimitWaived[key] = true;

        _preferences.Save();

        // An errored torrent has to be put back to Stopped before anything can start it again.
        if (manager.State == TorrentState.Error) await SafeStopAsync(manager);

        await ReconcileAsync();
    }

    public async Task PauseAsync(TorrentManager manager)
    {
        _preferences.Update(Key(manager), e => e.PausedByUser = true);
        _preferences.Save();
        await ReconcileAsync();
    }

    public async Task RemoveAsync(TorrentManager manager, bool deleteData)
    {
        var key = Key(manager);
        var name = manager.Name;

        // Said before anything is stopped. Stopping raises a state change, a state change asks for a
        // reconcile, and a reconcile starts whatever wants to run - so without this the queue puts the
        // torrent straight back up, and the engine then refuses to unregister a torrent that is running.
        _removing[key] = true;

        try
        {
            var mode = deleteData ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly;

            // A pass that was already deciding when we said it can still start the torrent, and the
            // engine re-checks the state on its own loop after we have checked ours. Neither window is
            // closed by the flag alone, and both are answered by going round again rather than failing.
            for (var attempt = 1; ; attempt++)
            {
                // The engine refuses to unregister a torrent that is not stopped, and says so in words
                // that mean nothing to anyone reading a toast, so make sure of it before handing it over.
                if (!await StopCompletelyAsync(manager))
                    throw new InvalidOperationException($"\"{name}\" will not stop. Try removing it again in a moment.");

                try
                {
                    await Engine.RemoveAsync(manager, mode);
                    break;
                }
                catch (TorrentException) when (attempt < RemoveAttempts)
                {
                    // Started again between our stop and the engine's check. Stop it and re-ask.
                }
            }

            // Forgotten only once the engine has really let go: undoing this first would lose the
            // torrent's queue position, its paused state and its totals while leaving it in the list.
            Forget(key);
            _preferences.Save();
        }
        finally
        {
            // A torrent still in the list because removal failed has to be allowed to run again.
            _removing.TryRemove(key, out _);
        }

        TorrentRemoved?.Invoke(this, manager);
        _ = SaveStateAsync();

        // The freed slot is the queue's problem, not this caller's: whoever asked for the removal should
        // not be kept waiting while the next torrent in line starts up.
        RequestReconcile();
    }

    public async Task RecheckAsync(TorrentManager manager)
    {
        if (manager.State != TorrentState.Stopped)
            await SafeStopAsync(manager);

        // The reconcile starts it again afterwards if it is still owed a slot, so the check itself
        // never jumps the queue.
        await manager.HashCheckAsync(autoStart: false);
        await ReconcileAsync();
    }

    public Task StartAllAsync() => SetPausedForAllAsync(paused: false);
    public Task PauseAllAsync() => SetPausedForAllAsync(paused: true);

    private async Task SetPausedForAllAsync(bool paused)
    {
        foreach (var manager in Engine.Torrents.ToList())
        {
            var key = Key(manager);
            _preferences.Update(key, e =>
            {
                e.PausedByUser = paused;
                if (!paused) e.SeedLimitReached = false;
            });

            if (!paused && SeedLimitFor(key).IsReached(RatioOf(manager), SeededFor(key)))
                _seedLimitWaived[key] = true;
        }

        _preferences.Save();
        await ReconcileAsync();
    }

    public async Task SetFilePriorityAsync(TorrentManager manager, ITorrentManagerFile file, Priority priority)
        => await manager.SetFilePriorityAsync(file, priority);

    // ----- Per-torrent settings ------------------------------------------------------------------------------------

    /// <summary>What BitKraken remembers about this torrent. Read-only as far as callers are concerned.</summary>
    public TorrentOverrides OverridesFor(TorrentManager manager) => _preferences.Get(Key(manager));

    /// <summary>Everything this torrent has shifted, across every session.</summary>
    public TorrentTotals TotalsFor(TorrentManager manager)
    {
        var overrides = _preferences.Get(Key(manager));
        return new TorrentTotals(overrides.TotalUploaded, overrides.TotalDownloaded, TimeSpan.FromSeconds(overrides.SecondsSeeded));
    }

    /// <summary>The seeding limit in force for this torrent, as the UI shows it. Empty when it has none.</summary>
    public string SeedLimitTextFor(TorrentManager manager) => SeedLimitFor(Key(manager)).Describe();

    /// <summary>Where this torrent is waiting, or null if it is not being held back by the queue.</summary>
    public int? QueuePositionOf(TorrentManager manager) =>
        _waiting.TryGetValue(Key(manager), out var place) ? place : null;

    /// <summary>True when this torrent asks its peers for pieces in order rather than rarest-first.</summary>
    public bool IsSequential(TorrentManager manager) => SequentialFor(Key(manager));

    /// <summary>
    /// Turns piece-order downloading on or off. MonoTorrent only swaps a picker on a stopped torrent,
    /// so a running one is stopped first and handed back to the queue, which starts it again.
    /// </summary>
    public async Task SetSequentialAsync(TorrentManager manager, bool? sequential)
    {
        var key = Key(manager);
        _preferences.Update(key, e => e.Sequential = sequential);
        _preferences.Save();

        if (SequentialFor(key) == _appliedSequential.GetValueOrDefault(key))
        {
            await ReconcileAsync();
            return;
        }

        await StopCompletelyAsync(manager);
        await ApplyPickerAsync(manager);
        await ReconcileAsync();
    }

    /// <summary>Caps this torrent alone, in KiB/s. Null lifts the per-torrent cap and leaves only the global one.</summary>
    public async Task SetRateLimitsAsync(TorrentManager manager, int? downloadKiB, int? uploadKiB)
    {
        var key = Key(manager);
        _preferences.Update(key, e =>
        {
            e.MaxDownloadRateKiB = downloadKiB is null ? null : Math.Max(0, downloadKiB.Value);
            e.MaxUploadRateKiB = uploadKiB is null ? null : Math.Max(0, uploadKiB.Value);
        });
        _preferences.Save();

        await manager.UpdateSettingsAsync(BuildTorrentSettings(key));
    }

    /// <summary>Overrides the global seeding limits for this torrent. Null on both follows the globals again.</summary>
    public async Task SetSeedLimitAsync(TorrentManager manager, double? ratio, int? minutes)
    {
        var key = Key(manager);
        _preferences.Update(key, e =>
        {
            e.SeedRatioLimit = ratio is null ? null : Math.Max(0, ratio.Value);
            e.SeedTimeLimitMinutes = minutes is null ? null : Math.Max(0, minutes.Value);

            // A limit the user just widened should let the torrent seed again.
            e.SeedLimitReached = false;
        });
        _seedLimitWaived.TryRemove(key, out _);
        _preferences.Save();

        await ReconcileAsync();
    }

    /// <summary>Puts this torrent ahead of everything else waiting for a slot.</summary>
    public async Task MoveToTopOfQueueAsync(TorrentManager manager)
    {
        _preferences.MoveToTop(Key(manager));
        _preferences.Save();
        await ReconcileAsync();
    }

    /// <summary>Puts this torrent behind everything else waiting for a slot.</summary>
    public async Task MoveToBottomOfQueueAsync(TorrentManager manager)
    {
        _preferences.MoveToBottom(Key(manager));
        _preferences.Save();
        await ReconcileAsync();
    }

    // ----- Reconciliation ------------------------------------------------------------------------------------------

    /// <summary>Asks for a reconcile without waiting for it. Safe to call from anywhere, including timers.</summary>
    private void RequestReconcile() => _ = ReconcileAsync();

    /// <summary>
    /// Works out which torrents should be running and moves the engine to that set. Only one runs at a
    /// time; anything asked for while one is in flight is folded into a single follow-up pass.
    /// </summary>
    private async Task ReconcileAsync()
    {
        Interlocked.Exchange(ref _reconcilePending, 1);

        // Someone else holds the lock; they will see the flag we just set and run again for us.
        if (!await _reconcileLock.WaitAsync(0)) return;

        try
        {
            while (Interlocked.Exchange(ref _reconcilePending, 0) == 1)
                await ReconcileOnceAsync();
        }
        catch (Exception ex)
        {
            EngineError?.Invoke(this, $"Could not apply the queue: {ex.Message}");
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private async Task ReconcileOnceAsync()
    {
        if (_engine is null || _shuttingDown) return;

        var settings = _settings.Current;
        var torrents = _engine.Torrents.ToList();

        SampleTotals(torrents);
        await EnforceSeedLimitsAsync(torrents, settings);

        var suspended = _networkSuspended;
        var plan = suspended
            ? QueuePlan.Empty
            : TorrentQueue.Plan(
                // An errored torrent is left out rather than queued: nothing here will start it, and a
                // slot handed to something that never runs is a slot no other torrent can have.
                torrents.Where(m => m.State != TorrentState.Error && WantsToRun(Key(m)))
                        .Select(m => new QueueCandidate(Key(m), m.Complete, _preferences.Get(Key(m)).QueueOrder)),
                settings.MaxActiveDownloads,
                settings.MaxActiveSeeds);

        _waiting = plan.Waiting;

        List<Task>? actions = null;
        foreach (var manager in torrents)
        {
            if (_shuttingDown) return;

            if (plan.Running.Contains(Key(manager)))
            {
                // Error is deliberately not in here: restarting a failing torrent every two seconds
                // would spin forever. Pressing Start clears the error and brings it back.
                if (manager.State is TorrentState.Stopped or TorrentState.Paused)
                    (actions ??= []).Add(StartWithPickerAsync(manager));
            }
            else if (ShouldStop(manager.State, suspended))
            {
                (actions ??= []).Add(SafeStopAsync(manager));
            }
        }

        // Started together rather than one after another. Each of these hops onto the engine's main
        // loop, which serializes the work itself; what overlaps is the waiting, so lowering the queue
        // limit on five running torrents settles in the time of the slowest stop, not the sum of five.
        if (actions is not null) await Task.WhenAll(actions);

        if (DateTime.UtcNow - _lastPreferenceSave > PreferenceSaveInterval)
        {
            _lastPreferenceSave = DateTime.UtcNow;
            _preferences.Save();
        }
    }

    /// <summary>Gives a torrent the picker its mode calls for, then starts it. Both swallow their own errors.</summary>
    private async Task StartWithPickerAsync(TorrentManager manager)
    {
        await ApplyPickerAsync(manager);
        await SafeStartAsync(manager);
    }

    /// <summary>A torrent nobody has paused and nothing has finished with is owed a place in the queue.</summary>
    private bool WantsToRun(string key)
    {
        // Whatever its preferences still say, a torrent being removed is not owed anything.
        if (_removing.ContainsKey(key)) return false;

        var overrides = _preferences.Get(key);
        return !overrides.PausedByUser && !overrides.SeedLimitReached;
    }

    /// <summary>
    /// Whether a torrent that may not run needs stopping. Hash checks are local work that leaks nothing,
    /// so the queue leaves them alone; the kill switch, which is about the network being wrong rather
    /// than busy, stops those too.
    /// </summary>
    private static bool ShouldStop(TorrentState state, bool networkSuspended) => state switch
    {
        TorrentState.Downloading or TorrentState.Seeding or TorrentState.Metadata
            or TorrentState.Starting or TorrentState.FetchingHashes => true,
        TorrentState.Hashing or TorrentState.HashingPaused => networkSuspended,
        _ => false,
    };

    /// <summary>
    /// Rolls each torrent's session counters into the totals we keep for it. The engine's counters start
    /// again from zero every session, so the running totals - and the ratio measured from them - have to
    /// be ours.
    /// </summary>
    private void SampleTotals(IReadOnlyList<TorrentManager> torrents)
    {
        var now = DateTime.UtcNow;
        var elapsed = _lastSample == default ? TimeSpan.Zero : now - _lastSample;
        _lastSample = now;

        // A machine that was asleep comes back with hours on the clock and no seeding done in them.
        if (elapsed > ReconcileInterval * 2) elapsed = ReconcileInterval;

        foreach (var manager in torrents)
        {
            var key = Key(manager);
            var received = manager.Monitor.DataBytesReceived;
            var sent = manager.Monitor.DataBytesSent;
            var seen = _lastCounters.GetValueOrDefault(key);
            _lastCounters[key] = (received, sent);

            // A counter that went backwards was reset with the session, so everything on it is new.
            var down = received >= seen.Received ? received - seen.Received : received;
            var up = sent >= seen.Sent ? sent - seen.Sent : sent;
            var seedingSeconds = manager.State == TorrentState.Seeding ? (long)elapsed.TotalSeconds : 0;

            if (down == 0 && up == 0 && seedingSeconds == 0) continue;

            _preferences.Update(key, e =>
            {
                e.TotalDownloaded += down;
                e.TotalUploaded += up;
                e.SecondsSeeded += seedingSeconds;
            });
        }
    }

    /// <summary>Stops any torrent that has now given back everything its limits asked of it.</summary>
    private async Task EnforceSeedLimitsAsync(IReadOnlyList<TorrentManager> torrents, AppSettings settings)
    {
        foreach (var manager in torrents)
        {
            if (!manager.Complete) continue;

            var key = Key(manager);
            if (_seedLimitWaived.ContainsKey(key)) continue;

            var overrides = _preferences.Get(key);
            var limit = SeedLimit.For(settings, overrides);
            var reached = !limit.IsUnlimited
                && limit.IsReached(RatioOf(manager), TimeSpan.FromSeconds(overrides.SecondsSeeded));

            if (overrides.SeedLimitReached)
            {
                // Raising the limit - here or in Settings - puts the torrent back in the queue. This is
                // the only place the flag is cleared without the user asking, and re-deriving it is why.
                if (!reached)
                {
                    _preferences.Update(key, e => e.SeedLimitReached = false);
                    _preferences.Save();
                }

                continue;
            }

            if (!reached) continue;

            _preferences.Update(key, e => e.SeedLimitReached = true);
            _preferences.Save();
            await SafeStopAsync(manager);
            SeedLimitReached?.Invoke(this, manager);
        }
    }

    /// <summary>
    /// Gives a torrent the picker its mode calls for. MonoTorrent will only take one while the torrent
    /// is stopped, which is exactly where the reconcile calls this from.
    /// </summary>
    private async Task ApplyPickerAsync(TorrentManager manager)
    {
        var key = Key(manager);
        var sequential = SequentialFor(key);
        if (_appliedSequential.TryGetValue(key, out var applied) && applied == sequential) return;
        if (manager.State != TorrentState.Stopped) return;

        try
        {
            await manager.ChangePickerAsync(new StandardPieceRequester(
                sequential ? LinearPicking : PieceRequesterSettings.Default));

            _appliedSequential[key] = sequential;
        }
        catch (Exception ex)
        {
            EngineError?.Invoke(this, $"Could not change how \"{manager.Name}\" picks pieces: {ex.Message}");
        }
    }

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
            if (_reconcileTimer is not null) await _reconcileTimer.DisposeAsync();
            _reconcileTimer = null;

            // Wait for any reconcile still in flight: it samples the same counters we are about to, and
            // two threads walking those dictionaries is how a clean exit turns into a crash on the way
            // out. Bounded, because a wedged reconcile must not be able to hold the app open either.
            if (await _reconcileLock.WaitAsync(ReconcileSettleTimeout))
            {
                try
                {
                    SampleTotals(_engine.Torrents.ToList());
                }
                finally
                {
                    _reconcileLock.Release();
                }
            }
        }
        catch (Exception)
        {
            // A last sample is a nicety; never let it stand between the user and a clean exit.
        }

        _preferences.Save(force: true);

        try
        {
            await SaveStateAsync();

            // Unlike everywhere else, this one waits: the process is about to go, and an announce left
            // in flight dies with it, leaving us listed on the tracker as a peer that never says goodbye.
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
            try
            {
                _engine.Dispose();
            }
            catch (Exception)
            {
                // The process is going; a disposal that objects cannot be allowed to abort it.
            }
        }
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync();

    /// <summary>
    /// The kill switch. The bound interface going away stops every torrent that was running and, when
    /// it returns, hands them back to the queue - never to the torrents the user paused themselves.
    /// </summary>
    private async Task OnBindingChangedAsync(NetworkBindingState state)
    {
        if (_shuttingDown) return;

        await _networkLock.WaitAsync();
        try
        {
            if (_shuttingDown) return;
            if (state.IsAvailable == !_networkSuspended) return;

            if (!state.IsAvailable)
            {
                _networkSuspended = true;
                NetworkSuspendedChanged?.Invoke(this, true);

                await ReconcileAsync();
                // Re-applying settings now drops the listeners, since there is no address to listen on.
                await ApplySettingsAsync();
            }
            else
            {
                // Rebind to the new address first, so the torrents we start are already on the tunnel.
                await ApplySettingsAsync();

                _networkSuspended = false;
                NetworkSuspendedChanged?.Invoke(this, false);
                await ReconcileAsync();
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

    private async Task ApplySettingsAsync()
    {
        if (_engine is null) return;
        try
        {
            await _engine.UpdateSettingsAsync(BuildEngineSettings(_settings.Current));
            foreach (var manager in _engine.Torrents)
                await manager.UpdateSettingsAsync(BuildTorrentSettings(Key(manager)));

            await RestartIfRouteChangedAsync();
            await ReconcileAsync();
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
    /// the running torrents are stopped and the queue starts them again the new way.
    /// </summary>
    private async Task RestartIfRouteChangedAsync()
    {
        var route = CurrentRoute();
        if (route == _appliedRoute) return;

        _appliedRoute = route;
        if (_engine is null || _networkSuspended) return;

        foreach (var manager in _engine.Torrents.ToList())
        {
            if (!WantsToRun(Key(manager))) continue;
            if (manager.State is TorrentState.Stopped or TorrentState.Stopping) continue;

            await SafeStopAsync(manager);
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

    /// <summary>Starts remembering a torrent: queue position, picker baseline and the events we care about.</summary>
    private void Track(TorrentManager manager)
    {
        var key = Key(manager);
        _preferences.EnsureTracked(key);

        // Every manager comes out of the engine with the standard picker, restored ones included.
        _appliedSequential[key] = false;

        manager.TorrentStateChanged += (_, e) =>
        {
            if (e.NewState == TorrentState.Seeding && e.OldState == TorrentState.Downloading)
            {
                // Persist when a torrent finishes so a crash doesn't lose the "complete" fast-resume.
                _ = SaveStateAsync();
                TorrentCompleted?.Invoke(this, manager);
            }
            else if (e.NewState == TorrentState.Error)
            {
                TorrentFailed?.Invoke(this, manager);
            }

            // A torrent that finished downloading has just given up a download slot.
            RequestReconcile();
        };
    }

    private void Forget(string key)
    {
        _preferences.Forget(key);
        _appliedSequential.TryRemove(key, out _);
        _lastCounters.Remove(key);
        _seedLimitWaived.TryRemove(key, out _);
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

    /// <summary>
    /// Brings a torrent to a complete stop, the state the engine insists on before it will unregister
    /// one or take a new piece picker.
    /// </summary>
    /// <remarks>
    /// The case that matters is a stop already in flight: the reconcile loop stops torrents on its own
    /// schedule, and pausing one starts a stop that the UI does not wait around for. MonoTorrent
    /// refuses to begin a second stop while a torrent is Stopping, and swallowing that refusal - which
    /// <see cref="SafeStopAsync"/> does, rightly, for the callers that only want it stopped eventually -
    /// is what let a half-stopped torrent reach the engine and come back as "The manager must be
    /// stopped before it can be unregistered". So the refusal is waited out instead.
    /// </remarks>
    private static async Task<bool> StopCompletelyAsync(TorrentManager manager)
    {
        if (manager.State == TorrentState.Stopped) return true;

        // Asking again while it is already Stopping is the one thing that throws, so don't.
        if (manager.State != TorrentState.Stopping) await SafeStopAsync(manager);

        var deadline = DateTime.UtcNow + StopSettleTimeout;
        while (manager.State != TorrentState.Stopped && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        return manager.State == TorrentState.Stopped;
    }

    private static async Task SafeStopAsync(TorrentManager manager)
    {
        try
        {
            await manager.StopAsync(StopAnnounceWait);
        }
        catch
        {
            // Already on the way down, or the engine is shutting down: nothing to recover from.
        }
    }

    /// <summary>Wraps a UDP tracker so it stops announcing the moment a proxy is configured.</summary>
    private ITrackerConnection BlockedWhileProxied(ITrackerConnection connection)
        => new ProxyBlockedTrackerConnection(connection, () => ProxyConfiguration.From(_settings.Current).IsEnabled);

    private bool SequentialFor(string key) =>
        _preferences.Get(key).Sequential ?? _settings.Current.SequentialDownload;

    private SeedLimit SeedLimitFor(string key) =>
        SeedLimit.For(_settings.Current, _preferences.Get(key));

    private TimeSpan SeededFor(string key) =>
        TimeSpan.FromSeconds(_preferences.Get(key).SecondsSeeded);

    private double RatioOf(TorrentManager manager)
    {
        var overrides = _preferences.Get(Key(manager));
        var size = manager.Torrent?.Size ?? manager.MagnetLink.Size ?? 0;
        var onDisk = (long)(size * manager.Progress / 100);
        return ShareRatio.Of(overrides.TotalUploaded, overrides.TotalDownloaded, onDisk);
    }

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

    private TorrentSettings BuildTorrentSettings(string key)
    {
        var overrides = _preferences.Get(key);
        return new TorrentSettingsBuilder
        {
            AllowDht = _settings.Current.EnableDht,
            AllowPeerExchange = _settings.Current.EnablePex,

            // 0 is MonoTorrent's "no cap", which is also what an absent override means.
            MaximumDownloadRate = Math.Max(0, overrides.MaxDownloadRateKiB ?? 0) * 1024,
            MaximumUploadRate = Math.Max(0, overrides.MaxUploadRateKiB ?? 0) * 1024,
        }.ToSettings();
    }

    private static string Key(TorrentManager manager) => manager.InfoHashes.V1OrV2.ToHex();
}
