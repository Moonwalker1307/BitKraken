using MonoTorrent.Connections.Tracker;
using MonoTorrent.Trackers;
using ReusableTasks;

namespace BitKraken.Services;

/// <summary>
/// A UDP tracker connection that refuses to announce while a proxy is configured. A SOCKS5 or HTTP
/// proxy carries TCP only, so a UDP announce would go straight out of this machine and hand the tracker
/// the address the proxy exists to hide. Refusing shows up in the tracker list with a reason, which is
/// better than a silent leak - and better than dropping the tracker, which looks like a bug.
/// </summary>
internal sealed class ProxyBlockedTrackerConnection : ITrackerConnection
{
    private const string Blocked = "Not announced: UDP trackers can't go through a proxy.";

    private readonly ITrackerConnection _inner;
    private readonly Func<bool> _isProxied;

    public ProxyBlockedTrackerConnection(ITrackerConnection inner, Func<bool> isProxied)
    {
        _inner = inner;
        _isProxied = isProxied;
    }

    public bool CanScrape => _inner.CanScrape;

    public Uri Uri => _inner.Uri;

    public ReusableTask<AnnounceResponse> AnnounceAsync(AnnounceRequest parameters, CancellationToken token)
        => _isProxied()
            ? ReusableTask.FromResult(new AnnounceResponse(TrackerState.Offline, failureMessage: Blocked))
            : _inner.AnnounceAsync(parameters, token);

    public ReusableTask<ScrapeResponse> ScrapeAsync(ScrapeRequest parameters, CancellationToken token)
        => _isProxied()
            ? ReusableTask.FromResult(new ScrapeResponse(TrackerState.Offline, failureMessage: Blocked))
            : _inner.ScrapeAsync(parameters, token);
}
