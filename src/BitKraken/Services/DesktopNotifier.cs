using System.Diagnostics;

namespace BitKraken.Services;

/// <summary>
/// Shows a notification through whatever the desktop already provides. Avalonia has no cross-platform
/// notification API, so each platform gets the tool it ships with rather than BitKraken taking on a
/// dependency for it:
/// <list type="bullet">
///   <item><description>macOS - <c>osascript</c>, which hands it to Notification Center.</description></item>
///   <item><description>Linux - <c>notify-send</c>, the freedesktop.org standard, present on most desktops.</description></item>
///   <item><description>Windows - a toast raised through PowerShell's WinRT bridge.</description></item>
/// </list>
/// Every path is best-effort: a desktop without the tool (a bare window manager, a locked-down box)
/// gets nothing, and the in-app toast is always shown as well so nothing is only ever said out here.
/// </summary>
public static class DesktopNotifier
{
    /// <summary>Long enough for a notification daemon to answer, short enough never to be noticed.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Raises a toast through PowerShell. Title and body arrive as environment variables so that no
    /// amount of punctuation in a torrent's name can change what the script does. The app id is
    /// PowerShell's own, because a toast has to be attributed to something the shell already knows
    /// about - BitKraken would need its own registered AppUserModelID to appear under its own name.
    /// </summary>
    private const string WindowsToastScript = """
        $ErrorActionPreference = 'Stop'
        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
        [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
        $template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
        $lines = $template.GetElementsByTagName('text')
        $lines.Item(0).AppendChild($template.CreateTextNode($env:BITKRAKEN_NOTIFY_TITLE)) | Out-Null
        $lines.Item(1).AppendChild($template.CreateTextNode($env:BITKRAKEN_NOTIFY_BODY)) | Out-Null
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
            // AppleScript is source, not arguments, so the two strings have to be escaped into it.
            Run("osascript", ["-e", $"display notification \"{Escape(body)}\" with title \"{Escape(title)}\""], null);
        }
        else if (OperatingSystem.IsWindows())
        {
            Run(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", WindowsToastScript],
                new Dictionary<string, string> { ["BITKRAKEN_NOTIFY_TITLE"] = title, ["BITKRAKEN_NOTIFY_BODY"] = body });
        }
        else
        {
            Run("notify-send", ["--app-name", AppInfo.Name, "--icon", "bitkraken", title, body], null);
        }
    }

    /// <summary>Escapes a string for embedding in an AppleScript double-quoted literal.</summary>
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static void Run(string program, IEnumerable<string> arguments, IDictionary<string, string>? environment)
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
        if (process is null) return;

        // Reap it so a desktop that never answers can't leave a process behind for the session.
        if (!process.WaitForExit(Timeout))
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }
    }
}
