namespace BitKraken.Services;

/// <summary>
/// Remembers why a torrent isn't running: because the user paused it, or because the interface
/// BitKraken is bound to went away. Keeping the two apart is the whole trick - when the network comes
/// back, only the torrents the network stopped may start again.
/// </summary>
internal sealed class TorrentActivation
{
    private readonly HashSet<string> _pausedByUser = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deferred = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True while the bound interface is gone and nothing may connect.</summary>
    public bool IsSuspended { get; private set; }

    /// <summary>The set persisted across restarts. Torrents held by the network are not in it.</summary>
    public IReadOnlyCollection<string> PausedByUser => _pausedByUser;

    public void LoadPaused(IEnumerable<string> keys)
    {
        foreach (var key in keys) _pausedByUser.Add(key);
    }

    public bool IsPausedByUser(string key) => _pausedByUser.Contains(key);

    /// <summary>The user (or an auto-start) asked for this torrent. False means "not now" - see <see cref="IsSuspended"/>.</summary>
    public bool RequestStart(string key)
    {
        _pausedByUser.Remove(key);
        return Allow(key);
    }

    /// <summary>Starting a torrent we already know about - on session restore, or after a re-check.</summary>
    public bool RequestRestore(string key) => !_pausedByUser.Contains(key) && Allow(key);

    public void Pause(string key)
    {
        _pausedByUser.Add(key);
        _deferred.Remove(key);
    }

    public void Forget(string key)
    {
        _pausedByUser.Remove(key);
        _deferred.Remove(key);
    }

    /// <summary>Restart this one when the network is back.</summary>
    public void Defer(string key)
    {
        if (!_pausedByUser.Contains(key)) _deferred.Add(key);
    }

    /// <summary>Returns false if we were already suspended.</summary>
    public bool Suspend()
    {
        if (IsSuspended) return false;

        IsSuspended = true;
        return true;
    }

    /// <summary>Lifts the suspension and hands back the torrents that are owed a restart.</summary>
    public IReadOnlyList<string> Resume()
    {
        IsSuspended = false;

        var owed = _deferred.Where(key => !_pausedByUser.Contains(key)).ToList();
        _deferred.Clear();
        return owed;
    }

    private bool Allow(string key)
    {
        if (!IsSuspended) return true;

        _deferred.Add(key);
        return false;
    }
}
