namespace BitKraken.Services;

/// <summary>
/// The BitKraken logo as a file on disk. It ships compiled into the assembly, which is all the
/// in-process UI needs, but anything outside the process - a notification daemon, the desktop's icon
/// theme - can only be handed a path, so the image is unpacked into the cache directory the first
/// time one of them asks for it.
/// </summary>
public static class AppIcon
{
    /// <summary>The name the freedesktop.org icon theme knows it by, and the file name on disk.</summary>
    public const string Name = "bitkraken";

    private const string FileName = Name + ".png";

    /// <summary>The logical name the project file embeds the PNG under.</summary>
    private const string Resource = "BitKraken.Assets." + FileName;

    private static readonly Lazy<string?> Unpacked =
        new(() => Extract(SettingsService.CacheDirectory), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Absolute path to the logo, or <see langword="null"/> if it could not be written out - a full
    /// disk, a read-only profile. Extracted once per run; every caller after that gets the same path.
    /// </summary>
    public static string? FilePath => Unpacked.Value;

    /// <summary>Writes the logo into <paramref name="directory"/>, returning its path, or null if that failed.</summary>
    internal static string? Extract(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, FileName);

            // Staged through a sibling file so that a reader - a notification daemon opening the path
            // we just gave it, a second instance starting up - never catches a half-written PNG.
            var staging = Path.Combine(directory, $"{FileName}.{Environment.ProcessId}.tmp");
            using (var source = typeof(AppIcon).Assembly.GetManifestResourceStream(Resource)
                ?? throw new FileNotFoundException("The logo is not embedded in this build.", Resource))
            using (var target = File.Create(staging))
            {
                source.CopyTo(target);
            }

            File.Move(staging, path, overwrite: true);
            return path;
        }
        catch
        {
            // The logo is decoration: callers fall back to whatever the platform shows without one.
            return null;
        }
    }
}
