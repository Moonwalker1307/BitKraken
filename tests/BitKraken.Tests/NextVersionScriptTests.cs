using System.Diagnostics;
using Xunit;

namespace BitKraken.Tests;

/// <summary>
/// scripts/next-version.sh decides the version stamped on every installer, so its tag arithmetic is
/// worth pinning. Each test runs the script against a throwaway repository with seeded tags.
/// Unix only - the Windows runner packages, it does not resolve the version.
/// </summary>
public class NextVersionScriptTests
{
    [Fact]
    public void The_first_build_of_a_series_is_patch_zero()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.Equal("1.0.0", Run("release"));
    }

    [Fact]
    public void A_release_is_the_highest_tag_plus_one()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.Equal("1.0.3", Run("release", "v1.0.0", "v1.0.1", "v1.0.2"));
    }

    [Fact]
    public void Patches_are_compared_as_numbers_not_as_text()
    {
        if (OperatingSystem.IsWindows()) return;

        // Sorted as text, v1.0.9 would beat v1.0.10 and the next build would collide with a release.
        Assert.Equal("1.0.11", Run("release", "v1.0.8", "v1.0.9", "v1.0.10"));
    }

    [Fact]
    public void A_preview_takes_the_same_patch_with_a_suffix()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.Equal("1.0.3-preview", Run("preview", "v1.0.0", "v1.0.1", "v1.0.2"));
    }

    [Fact]
    public void Current_is_the_latest_released_version()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.Equal("1.0.2", Run("current", "v1.0.0", "v1.0.1", "v1.0.2"));
    }

    [Fact]
    public void Release_defaults_when_no_channel_is_given()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.Equal("1.0.1", Run(channel: null, "v1.0.0"));
    }

    [Fact]
    public void Tags_that_are_not_a_plain_patch_number_are_ignored()
    {
        if (OperatingSystem.IsWindows()) return;

        // Preview and release-candidate tags must not advance the counter.
        Assert.Equal("1.0.1", Run("release", "v1.0.0", "v1.0.5-preview", "v1.0.7-rc1"));
    }

    [Fact]
    public void Another_series_does_not_leak_into_this_one()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.Equal("1.0.1", Run("release", "v1.0.0", "v1.1.9", "v2.0.4"));
    }

    [Fact]
    public void The_series_can_be_overridden()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.Equal("1.1.3", Run("release", new[] { "v1.0.9", "v1.1.2" }, series: "1.1"));
        Assert.Equal("1.1.0", Run("release", new[] { "v1.0.9" }, series: "1.1"));
    }

    [Fact]
    public void An_unknown_channel_fails()
    {
        if (OperatingSystem.IsWindows()) return;

        var result = Execute("nonsense", [], series: null);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unknown channel", result.StandardError);
    }

    // ----- harness -----------------------------------------------------------------------------

    private static string Run(string? channel, params string[] tags) => Run(channel, tags, series: null);

    private static string Run(string? channel, string[] tags, string? series)
    {
        var result = Execute(channel, tags, series);
        Assert.True(result.ExitCode == 0, $"next-version.sh exited {result.ExitCode}: {result.StandardError}");
        return result.StandardOutput.Trim();
    }

    private static (int ExitCode, string StandardOutput, string StandardError) Execute(
        string? channel, string[] tags, string? series)
    {
        var repo = TestEnvironment.NewDirectory("next-version");
        Directory.CreateDirectory(Path.Combine(repo, "scripts"));
        File.Copy(ScriptPath, Path.Combine(repo, "scripts", "next-version.sh"));

        Git(repo, "init", "--quiet");
        Git(repo, "-c", "user.email=tests@bitkraken.invalid", "-c", "user.name=tests",
            "commit", "--allow-empty", "--quiet", "-m", "seed");
        foreach (var tag in tags) Git(repo, "tag", tag);

        var psi = new ProcessStartInfo("bash")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(Path.Combine(repo, "scripts", "next-version.sh"));
        if (channel is not null) psi.ArgumentList.Add(channel);
        if (series is not null) psi.Environment["VERSION_SERIES"] = series;

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    private static void Git(string repo, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardError = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
    }

    /// <summary>Walks up from the test binary to the repository that carries the script.</summary>
    private static string ScriptPath { get; } = FindScript();

    private static string FindScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", "next-version.sh");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate scripts/next-version.sh from " + AppContext.BaseDirectory);
    }
}
