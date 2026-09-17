using System.Diagnostics;

namespace BitKraken.Services;

/// <summary>
/// Shows a notification through whatever the desktop already provides. Avalonia has no cross-platform
/// notification API, so each platform gets the tool it ships with rather than BitKraken taking on a
/// dependency for it:
/// <list type="bullet">
///   <item><description>macOS - <c>osascript</c>, posting as the helper bundle so the logo shows.</description></item>
///   <item><description>Linux - <c>notify-send</c>, the freedesktop.org standard, present on most desktops.</description></item>
///   <item><description>Windows - a toast raised through PowerShell's WinRT bridge.</description></item>
/// </list>
/// Every path is best-effort: a desktop without the tool (a bare window manager, a locked-down box)
/// gets nothing, and the in-app toast is always shown as well so nothing is only ever said out here.
/// <para>
/// All three carry the BitKraken logo, by two different routes. Linux and Windows are handed
/// <see cref="AppIcon"/>, because their notifications take an image. macOS does not take one at all:
/// it shows the icon of the bundle that posted the notification, which for <c>osascript</c> is Script
/// Editor's. So the image is not what changes there, the poster is - see <see cref="NotifierBundleId"/>.
/// </para>
/// </summary>
public static class DesktopNotifier
{
    /// <summary>Long enough for a notification daemon to answer, short enough never to be noticed.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The helper bundle inside BitKraken.app that exists only to put a face on a notification.
    /// Built by scripts/package-macos.sh; keep the two in step.
    /// </summary>
    internal const string NotifierBundleId = "com.thorstholm.bitkraken.notifier";

    /// <summary>
    /// Raises a toast through PowerShell. Title, body and icon path arrive as environment variables so
    /// that no amount of punctuation in a torrent's name can change what the script does. The app id is
    /// PowerShell's own, because a toast has to be attributed to something the shell already knows
    /// about - BitKraken would need its own registered AppUserModelID to appear under its own name -
    /// which is exactly why the logo goes in the toast's own image slot: it is the only place a toast
    /// from an unpackaged app can show who sent it. Without an icon the script falls back to the
    /// text-only template, since an image element left pointing at nothing renders as a blank square.
    /// </summary>
    private const string WindowsToastScript = """
        $ErrorActionPreference = 'Stop'
        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
        [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
        $icon = $env:BITKRAKEN_NOTIFY_ICON
        $layout = if ($icon) {
            [Windows.UI.Notifications.ToastTemplateType]::ToastImageAndText02
        } else {
            [Windows.UI.Notifications.ToastTemplateType]::ToastText02
        }
        $template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent($layout)
        $lines = $template.GetElementsByTagName('text')
        $lines.Item(0).AppendChild($template.CreateTextNode($env:BITKRAKEN_NOTIFY_TITLE)) | Out-Null
        $lines.Item(1).AppendChild($template.CreateTextNode($env:BITKRAKEN_NOTIFY_BODY)) | Out-Null
        if ($icon) {
            $images = $template.GetElementsByTagName('image')
            $images.Item(0).Attributes.GetNamedItem('src').NodeValue = $icon
        }
        $appId = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show([Windows.UI.Notifications.ToastNotification]::new($template))
        """;

    /// <summary>Shows a notification, or quietly does nothing if this desktop has no way to.</summary>
    public static void Notify(string title, string body)
    {
        if (string.IsNullOrWhiteSpace(title)) return;

        // Notifications are decoration: they must never hold up a torrent finishing, and never throw.
        _ = Task.Run(() =>
        {
            try
            {
                Send(title, body ?? "");
            }
            catch
            {
                // No notification daemon, no PowerShell, a sandbox that won't let us spawn anything.
            }
        });
    }

    private static void Send(string title, string body)
    {
        if (OperatingSystem.IsMacOS())
        {
            // Two goes. The first asks the helper bundle to post it, so the notification arrives
            // wearing BitKraken's icon; the second is the plain command, for a build running outside
            // the .app - straight off `dotnet run` - where there is no helper to ask. The attempt is
            // guarded as well as checked, because the fallback has to happen whether asking the helper
            // came back unhappy or threw on the way there.
            try
            {
                if (Run("osascript", ["-e", AppleScript(title, body, NotifierBundleId)], null)) return;
            }
            catch (Exception)
            {
                // Fall through to the plain command below.
            }

            Run("osascript", ["-e", AppleScript(title, body, postedBy: null)], null);
        }
        else if (OperatingSystem.IsWindows())
        {
            Run(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", WindowsToastScript],
                new Dictionary<string, string>
                {
                    ["BITKRAKEN_NOTIFY_TITLE"] = title,
                    ["BITKRAKEN_NOTIFY_BODY"] = body,
                    // A toast reads its image as a URI, and an empty value tells the script there is none.
                    ["BITKRAKEN_NOTIFY_ICON"] = ToastImageUri(AppIcon.FilePath),
                });
        }
        else
        {
            // A path is what makes the logo show up on a desktop BitKraken was never installed into:
            // the theme name only resolves once the icon sits in an icon directory, which is a
            // best-effort side of registering the desktop entry and not something to depend on here.
            Run("notify-send", ["--app-name", AppInfo.Name, "--icon", AppIcon.FilePath ?? AppIcon.Name, title, body], null);
        }
    }

    /// <summary>The logo as the <c>file:///</c> URI a toast's image element wants, or "" if we have no file.</summary>
    private static string ToastImageUri(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";

        try { return new Uri(path).AbsoluteUri; }
        catch (UriFormatException) { return ""; }
    }

    /// <summary>
    /// The AppleScript that posts one notification. <paramref name="postedBy"/> is the bundle to post
    /// it as: macOS shows the icon of whichever bundle raised the notification, and osascript's own is
    /// Script Editor's, which is why the plain command has never carried the BitKraken logo. Telling
    /// another application to run the command runs it in that application, and the icon follows.
    /// Null gives the plain command, posted by osascript.
    /// </summary>
    internal static string AppleScript(string title, string body, string? postedBy)
    {
        // AppleScript is source, not arguments, so every string has to be escaped into it.
        var command = $"display notification \"{Escape(body)}\" with title \"{Escape(title)}\"";

        return postedBy is null ? command : $"tell application id \"{Escape(postedBy)}\" to {command}";
    }

    /// <summary>
    /// Escapes a string for embedding in an AppleScript double-quoted literal. The line breaks matter
    /// as much as the quotes: AppleScript has no multi-line string literal, so a raw newline is a
    /// syntax error, and a torrent that failed sends its name and the reason separated by one. That
    /// made every failure notification on macOS a script that would not compile, and so no
    /// notification at all.
    /// </summary>
    private static string Escape(string value) => value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\r\n", "\\n")
        .Replace("\n", "\\n")
        .Replace("\r", "\\n")
        .Replace("\t", "\\t");

    /// <summary>Runs one notification tool. True only if it actually reported success.</summary>
    private static bool Run(string program, IEnumerable<string> arguments, IDictionary<string, string>? environment)
    {
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var (name, value) in environment) info.Environment[name] = value;
        }

        using var process = Process.Start(info);
        if (process is null) return false;

        // Reap it so a desktop that never answers can't leave a process behind for the session.
        if (!process.WaitForExit(Timeout))
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }

            return false;
        }

        return process.ExitCode == 0;
    }
}
