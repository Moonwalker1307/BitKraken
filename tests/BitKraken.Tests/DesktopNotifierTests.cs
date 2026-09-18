using BitKraken.Services;
using Xunit;

namespace BitKraken.Tests;

/// <summary>
/// The macOS fallback notification is a string of AppleScript source with a torrent's name pasted
/// into it, so what that string comes out as is worth pinning: a script that does not compile is not
/// a wrong notification, it is silently no notification.
/// <para>
/// The path that matters more - the helper applet posting it, which is what puts the logo on it - is
/// not something a Linux test run can reach. scripts/verify-macos-notifier.sh checks that on a Mac,
/// in CI. What is left here is the wiring between the two sides that a rename could quietly break.
/// </para>
/// </summary>
public class DesktopNotifierTests
{
    [Fact]
    public void The_fallback_is_the_plain_command()
    {
        Assert.Equal(
            "display notification \"it is done\" with title \"Download finished\"",
            DesktopNotifier.AppleScript("Download finished", "it is done"));
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
            DesktopNotifier.AppleScript("Title", body));
    }

    [Fact]
    public void A_failed_torrent_still_produces_one_compilable_line()
    {
        // What MainWindowViewModel actually sends: the name, a newline, then the reason.
        var script = DesktopNotifier.AppleScript("Torrent failed", "Some.Torrent\nDisk full");

        Assert.DoesNotContain('\n', script);
        Assert.Contains("Some.Torrent\\nDisk full", script);
    }

    [Fact]
    public void The_title_is_escaped_as_well_as_the_body()
    {
        var script = DesktopNotifier.AppleScript("a \"quoted\" title", "body");

        Assert.Contains("with title \"a \\\"quoted\\\" title\"", script);
    }

    /// <summary>
    /// The helper is built by the packaging script and found by the app, by name, at a fixed place
    /// inside the bundle. Nothing but this test notices when only one of the two is changed, and the
    /// symptom would be a silent fallback to a notification with the wrong icon - exactly the thing
    /// this was all meant to fix, and exactly how the first attempt at it failed.
    /// </summary>
    [Fact]
    public void The_helper_the_app_looks_for_is_the_one_the_packaging_script_builds()
    {
        var script = File.ReadAllText(RepositoryFile(Path.Combine("scripts", "package-macos.sh")));

        Assert.Contains("NOTIFIER_APP=\"$APP_NAME Notifier.app\"", script);
        Assert.Contains("NOTIFIER=\"$APP/Contents/Helpers/$NOTIFIER_APP\"", script);
        Assert.Equal("BitKraken Notifier.app", DesktopNotifier.NotifierBundleName);
    }

    /// <summary>
    /// BitKraken's own binaries sit in Contents/MacOS, so the helper is one up and over. Getting this
    /// wrong costs the logo and says nothing about it.
    /// </summary>
    [Fact]
    public void The_helper_is_looked_for_beside_the_app_inside_the_bundle()
    {
        var bundle = Path.Combine(TestEnvironment.NewDirectory(), "BitKraken.app");
        var contents = Path.Combine(bundle, "Contents");
        Directory.CreateDirectory(Path.Combine(contents, "MacOS"));

        var applet = Path.Combine(contents, "Helpers", "BitKraken Notifier.app", "Contents", "MacOS", "applet");
        Directory.CreateDirectory(Path.GetDirectoryName(applet)!);
        File.WriteAllText(applet, "");

        Assert.Equal(applet, DesktopNotifier.NotifierExecutable(Path.Combine(contents, "MacOS")));
    }

    /// <summary>Outside a bundle - `dotnet run` - there is no helper, and the caller has to be told so.</summary>
    [Fact]
    public void No_helper_outside_the_bundle()
    {
        Assert.Null(DesktopNotifier.NotifierExecutable(TestEnvironment.NewDirectory()));
    }

    /// <summary>
    /// The applet reads the title and body out of its environment rather than out of its own source,
    /// which is what keeps a torrent named with a quote from being a torrent named with a script.
    /// </summary>
    [Fact]
    public void The_helper_takes_the_notification_from_the_environment()
    {
        var applet = File.ReadAllText(RepositoryFile(Path.Combine("packaging", "macos", "notifier.applescript")));

        Assert.Contains("BITKRAKEN_NOTIFY_TITLE", applet);
        Assert.Contains("BITKRAKEN_NOTIFY_BODY", applet);
        Assert.Contains("display notification theBody with title theTitle", applet);
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
