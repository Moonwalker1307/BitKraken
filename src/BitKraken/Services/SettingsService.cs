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

    public AppSettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public void Load()
    {
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(CacheDirectory);

        try
        {
            if (File.Exists(SettingsPath))
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
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
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(settings.DownloadDirectory);

        await using var stream = File.Create(SettingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
