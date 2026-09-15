using System.Runtime.CompilerServices;

namespace BitKraken.Tests;

/// <summary>
/// Points the user profile at a scratch directory before any test runs, so defaults that resolve to
/// real folders - <c>AppSettings.DownloadDirectory</c>, <c>SettingsService.AppDataDirectory</c> - land
/// in temp instead of the machine's home directory. A module initializer is what gets us in ahead of
/// the static initializers those types run on first touch.
/// </summary>
internal static class TestEnvironment
{
    /// <summary>Scratch root for this test run. Left behind for inspection; it is under the temp directory.</summary>
    internal static string Root { get; } = Path.Combine(Path.GetTempPath(), "bitkraken-tests", Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void RedirectUserProfile()
    {
        Directory.CreateDirectory(Root);

        // Unix resolves UserProfile/ApplicationData from these; Windows asks the shell API instead, so
        // there the app-data paths stay real. Nothing asserted below depends on the redirect taking hold.
        Environment.SetEnvironmentVariable("HOME", Root);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(Root, ".config"));
        Environment.SetEnvironmentVariable("USERPROFILE", Root);
        Environment.SetEnvironmentVariable("APPDATA", Path.Combine(Root, "AppData", "Roaming"));
    }

    /// <summary>A fresh empty directory for one test to write into.</summary>
    internal static string NewDirectory([CallerMemberName] string name = "test")
    {
        var path = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }
}
