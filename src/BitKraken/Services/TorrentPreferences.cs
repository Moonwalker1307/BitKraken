using System.Collections.Concurrent;
using System.Text.Json;
using BitKraken.Models;

namespace BitKraken.Services;

/// <summary>
/// The per-torrent side of the settings, keyed by info hash and persisted to <c>torrents.json</c>.
/// MonoTorrent's own state file covers the torrents themselves; this covers what BitKraken decided
/// about them - paused, queued, capped, how much has been shifted.
/// </summary>
public sealed class TorrentPreferences
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly string _legacyPausedPath;
    private readonly ConcurrentDictionary<string, TorrentOverrides> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeLock = new();
    private long _nextQueueOrder;
    private bool _dirty;

    public TorrentPreferences() : this(SettingsService.AppDataDirectory)
    {
    }

    /// <summary>Reads and writes under <paramref name="directory"/> instead of the real user profile. For tests.</summary>
    internal TorrentPreferences(string directory)
    {
        _path = Path.Combine(directory, "torrents.json");
        _legacyPausedPath = Path.Combine(directory, "paused.json");
    }

    /// <summary>Loads the store, falling back to the <c>paused.json</c> written by older builds.</summary>
    public void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, TorrentOverrides>>(File.ReadAllText(_path), JsonOptions);
                if (loaded is not null)
                {
                    foreach (var (key, value) in loaded)
                        _entries[key] = value ?? new TorrentOverrides();
                }
            }
            else if (File.Exists(_legacyPausedPath))
            {
                // Builds before the queue kept nothing but the set of torrents the user had paused.
                var paused = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_legacyPausedPath));
                foreach (var key in paused ?? [])
                    _entries[key] = new TorrentOverrides { PausedByUser = true };
            }
        }
        catch
        {
            // A corrupt store costs us the queue order and the paused set, which is survivable.
            // Losing it is much better than refusing to start.
            _entries.Clear();
        }

        _nextQueueOrder = _entries.IsEmpty ? 0 : _entries.Values.Max(e => e.QueueOrder) + 1;
    }

    /// <summary>The stored entry for a torrent, or the defaults. Never null, never added to the store.</summary>
    public TorrentOverrides Get(string key) =>
        _entries.TryGetValue(key, out var entry) ? entry : new TorrentOverrides();

    /// <summary>Edits the entry for a torrent, creating it if this is the first thing we remember about it.</summary>
    public void Update(string key, Action<TorrentOverrides> edit)
    {
        var entry = _entries.GetOrAdd(key, _ => new TorrentOverrides { QueueOrder = Interlocked.Increment(ref _nextQueueOrder) });
        lock (_writeLock)
        {
            edit(entry);
            _dirty = true;
        }
    }

    /// <summary>Gives a torrent a queue position if it has none, so the queue keeps the order things arrived in.</summary>
    public void EnsureTracked(string key)
    {
        if (_entries.ContainsKey(key)) return;
        Update(key, _ => { });
    }

    public void Forget(string key)
    {
        if (_entries.TryRemove(key, out _)) _dirty = true;
    }

    /// <summary>Moves a torrent to the front of the queue, ahead of everything currently waiting.</summary>
    public void MoveToTop(string key)
    {
        var lowest = _entries.IsEmpty ? 0 : _entries.Values.Min(e => e.QueueOrder);
        Update(key, e => e.QueueOrder = lowest - 1);
    }

    /// <summary>Moves a torrent behind everything else in the queue.</summary>
    public void MoveToBottom(string key) =>
        Update(key, e => e.QueueOrder = Interlocked.Increment(ref _nextQueueOrder));

    /// <summary>
    /// Moves several torrents to the front of the queue as a block. They keep the order they had among
    /// themselves: moving each one to the top in turn would otherwise leave them in whatever order the
    /// selection happened to list them, which is the order they were added, not the order they queued in.
    /// </summary>
    public void MoveToTop(IReadOnlyCollection<string> keys)
    {
        // Last first, so the one that was furthest forward ends up at the very front.
        foreach (var key in InQueueOrder(keys).AsEnumerable().Reverse()) MoveToTop(key);
    }

    /// <summary>Moves several torrents behind everything else as a block, keeping their order among themselves.</summary>
    public void MoveToBottom(IReadOnlyCollection<string> keys)
    {
        foreach (var key in InQueueOrder(keys)) MoveToBottom(key);
    }

    private List<string> InQueueOrder(IReadOnlyCollection<string> keys)
    {
        foreach (var key in keys) EnsureTracked(key);
        return keys.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => Get(k).QueueOrder).ToList();
    }

    /// <summary>Writes the store out if anything changed since the last save.</summary>
    public void Save(bool force = false)
    {
        lock (_writeLock)
        {
            if (!_dirty && !force) return;
            _dirty = false;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(_entries, JsonOptions));
            }
            catch
            {
                // Non-fatal: we keep the in-memory state and try again on the next save.
            }
        }
    }
}
