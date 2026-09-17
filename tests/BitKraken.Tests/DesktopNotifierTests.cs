using BitKraken.Services;
using Xunit;

namespace BitKraken.Tests;

/// <summary>
/// The macOS notification is a string of AppleScript source with a torrent's name pasted into it, so
/// what that string comes out as is worth pinning: a script that does not compile is not a wrong
/// notification, it is silently no notification.
/// </summary>
public class DesktopNotifierTests
{
    [Fact]
    public void Without_a_bundle_the_script_is_the_plain_command()
    {
        Assert.Equal(
            "display notification \"it is done\" with title \"Download finished\"",
            DesktopNotifier.AppleScript("Download finished", "it is done", postedBy: null));
    }

    [Fact]
    public void A_bundle_posts_the_notification_so_it_carries_that_apps_icon()
    {
        Assert.Equal(
            "tell application id \"com.example.helper\" to display notification \"it is done\" with title \"Done\"",
            DesktopNotifier.AppleScript("Done", "it is done", postedBy: "com.example.helper"));
    }

    [Theory]
    [InlineData("say \"hi\"", "say \\\"hi\\\"")]     // a quote would end the literal early
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData("two\nlines", "two\\nlines")]        // a raw newline will not compile
    [InlineData("two\r\nlines", "two\\nlines")]
    [InlineData("two\rlines", "two\\nlines")]
    [InlineData("a\tb", "a\\tb")]
    public void Everything_that_would_break_the_literal_is_escaped(string body, string expected)
    {
        Assert.Equal(
            $"display notification \"{expected}\" with title \"Title\"",
            DesktopNotifier.AppleScript("Title", body, postedBy: null));
    }

    [Fact]
    public void A_failed_torrent_still_produces_one_compilable_line()
    {
        // What MainWindowViewModel actually sends: the name, a newline, then the reason.
        var script = DesktopNotifier.AppleScript("Torrent failed", "Some.Torrent\nDisk full", postedBy: null);

        Assert.DoesNotContain('\n', script);
        Assert.Contains("Some.Torrent\\nDisk full", script);
    }

    [Fact]
    public void The_title_is_escaped_as_well_as_the_body()
    {
        var script = DesktopNotifier.AppleScript("a \"quoted\" title", "body", postedBy: null);

        Assert.Contains("with title \"a \\\"quoted\\\" title\"", script);
    }

    /// <summary>
    /// The helper bundle is built by the packaging script and addressed by the app; nothing but this
    /// test notices when only one of the two is changed, and the symptom would be a silent fallback
    /// to a notification with the wrong icon - exactly the thing this was all meant to fix.
    /// </summary>
    [Fact]
    public void The_helper_id_the_app_asks_for_is_the_one_the_packaging_script_builds()
    {
        var script = File.ReadAllText(RepositoryFile(Path.Combine("scripts", "package-macos.sh")));

        Assert.Contains("BUNDLE_ID=\"com.thorstholm.bitkraken\"", script);
        Assert.Contains("plutil -replace CFBundleIdentifier -string \"$BUNDLE_ID.notifier\"", script);
        Assert.Equal("com.thorstholm.bitkraken.notifier", DesktopNotifier.NotifierBundleId);
    }

    private static string RepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not locate {relativePath} from {AppContext.BaseDirectory}");
    }
}
