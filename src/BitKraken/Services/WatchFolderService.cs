using BitKraken.Models;

namespace BitKraken.Services;

/// <summary>
/// Watches a folder for <c>.torrent</c> files and hands each one over once it has finished being
/// written. Two things make this less simple than it sounds: a file appears in the folder the moment
/// its first byte lands, long before it is a readable torrent; and <see cref="FileSystemWatcher"/>
/// misses events on network shares and on some Linux setups. So a file is only offered once it can be
/// opened exclusively, and a slow sweep runs alongside the watcher to catch whatever it missed.
/// </summary>
public sealed class WatchFolderService : IDisposable
{
    /// <summary>Suffix given to a torrent that has been added, which also takes it out of the filter.</summary>
    public const string AddedSuffix = ".added";

    private const string Filter = "*.torrent";

    /// <summary>How often the folder is swept, for the events the watcher did not deliver.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    /// <summary>How long after a change we first try to open a file. Nothing is written that slowly.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);

    private readonly SettingsService _settings;
    private readonly Func<string, Task<bool>> _add;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    /// <summary>Files we are already dealing with, so the watcher and the sweep can't both take one.</summary>
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private Timer? _sweep;
    private string _folder = "";
    private bool _disposed;

    /// <param name="settings">Watched for changes to the folder, so Settings takes effect immediately.</param>
    /// <param name="add">Adds one <c>.torrent</c> file. True means it is ours now and the file can be cleared away.</param>
    public WatchFolderService(SettingsService settings, Func<string, Task<bool>> add)
    {
        _settings = settings;
        _add = add;
        _settings.Changed += (_, _) => Apply();
    }

    /// <summary>Raised when something about the watch folder itself goes wrong, for the status bar.</summary>
    public event EventHandler<string>? Error;

    /// <summary>Starts, stops or re-points the watcher to match the current settings.</summary>
    public void Apply()
    {
        if (_disposed) return;

        var folder = _settings.Current.WatchFolder?.Trim() ?? "";
        if (string.Equals(folder, _folder, StringComparison.Ordinal) && (folder.Length == 0 || _watcher is not null))
            return;

        Stop();
        _folder = folder;
        if (folder.Length == 0) return;

        try
        {
            Directory.CreateDirectory(folder);

            _watcher = new FileSystemWatcher(folder, Filter)
            {
                // Created fires before the bytes are there; Renamed is how most browsers finish a
                // download (".part" to ".torrent"), which is the case that matters most here.
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };

            _watcher.Created += OnChanged;
            _watcher.Changed += OnChanged;
            _watcher.Renamed += OnChanged;
            _watcher.Error += (_, e) => Error?.Invoke(this, $"Watch folder: {e.GetException().Message}");
        }
        catch (Exception ex)
        {
            _watcher?.Dispose();
            _watcher = null;
            Error?.Invoke(this, $"Could not watch \"{folder}\": {ex.Message}");
            return;
        }

        _sweep = new Timer(_ => _ = ScanAsync(), null, TimeSpan.Zero, SweepInterval);
    }

    private void OnChanged(object? sender, FileSystemEventArgs e) => _ = ScanAfterSettleAsync();

    private async Task ScanAfterSettleAsync()
    {
        await Task.Delay(SettleDelay);
        await ScanAsync();
    }

    private async Task ScanAsync()
    {
        if (_disposed || _folder.Length == 0) return;

        // One scan at a time; the sweep will come round again for anything a busy scan skipped.
        if (!await _scanLock.WaitAsync(0)) return;

        try
        {
            string[] candidates;
            try
            {
                candidates = Directory.GetFiles(_folder, Filter, SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, $"Could not read \"{_folder}\": {ex.Message}");
                return;
            }

            foreach (var path in candidates)
            {
                if (_disposed) return;
                if (!_inFlight.Add(path)) continue;

                try
                {
                    if (!IsReadable(path)) continue;
                    if (await _add(path)) ClearAway(path);
                }
                finally
                {
                    _inFlight.Remove(path);
                }
            }
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <summary>
    /// Whether the file is finished: it opens with no sharing, which means nothing else still holds it.
    /// An empty file is a file whose first byte has landed and no more, so it does not count either.
    /// </summary>
    private static bool IsReadable(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return stream.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Takes an added torrent out of the folder's way, the way the settings asked.</summary>
    private void ClearAway(string path)
    {
        try
        {
            if (_settings.Current.WatchFolderAction == WatchFolderAction.Delete)
            {
                File.Delete(path);
                return;
            }

            var target = path + AddedSuffix;
            // A torrent added, removed and dropped in again would otherwise collide with its own marker.
            if (File.Exists(target)) File.Delete(target);
            File.Move(path, target);
        }
        catch (Exception ex)
        {
            // The torrent is added either way; all that is at stake is offering it again on the next
            // sweep, which the engine will refuse as a duplicate.
            Error?.Invoke(this, $"Added \"{Path.GetFileName(path)}\" but could not clear it away: {ex.Message}");
        }
    }

    private void Stop()
    {
        _sweep?.Dispose();
        _sweep = null;

        var watcher = _watcher;
        _watcher = null;
        if (watcher is null) return;

        try
        {
            // Turning events off re-reads the directory, so a folder that has been unmounted or
            // deleted underneath us throws here. On the way out that must not become a crash.
            watcher.EnableRaisingEvents = false;
        }
        catch (Exception)
        {
            // Disposing below drops the handle regardless.
        }

        try
        {
            watcher.Dispose();
        }
        catch (Exception)
        {
            // Nothing further to do about it.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
