using System.Text.Json;
using System.Text.Json.Serialization;
using BitKraken.Models;

namespace BitKraken.Services;

/// <summary>Loads and saves <see cref="AppSettings"/> from the per-user application data folder.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "BitKraken");

    public static string CacheDirectory { get; } = Path.Combine(AppDataDirectory, "cache");
    public static string EngineStatePath { get; } = Path.Combine(AppDataDirectory, "engine-state.dat");
    public static string SettingsPath { get; } = Path.Combine(AppDataDirectory, "settings.json");

    private readonly string _directory;
    private readonly string _cacheDirectory;
    private readonly string _settingsPath;

    public SettingsService() : this(AppDataDirectory)
    {
    }

    /// <summary>Reads and writes under <paramref name="directory"/> instead of the real user profile. For tests.</summary>
    internal SettingsService(string directory)
    {
        _directory = directory;
        _cacheDirectory = Path.Combine(directory, "cache");
        _settingsPath = Path.Combine(directory, "settings.json");
    }

    public AppSettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public void Load()
    {
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(_cacheDirectory);

        try
        {
            if (File.Exists(_settingsPath))
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            // Corrupt settings file - fall back to defaults rather than crash on startup.
            Current = new AppSettings();
        }

        Directory.CreateDirectory(Current.DownloadDirectory);
    }

    public async Task SaveAsync(AppSettings settings)
    {
        Current = settings;
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(settings.DownloadDirectory);

        await using var stream = File.Create(_settingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
