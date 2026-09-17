using System.Diagnostics;
using System.Text;

namespace BitKraken.Services;

/// <summary>
/// Registers BitKraken with the desktop as the handler for <c>magnet:</c> links and <c>.torrent</c>
/// files, which is what makes a magnet link clicked in a browser open here.
/// Everything is written per-user (no elevation) and is undone by <see cref="ApplyAsync"/> when the
/// user turns the setting off. On macOS the app bundle's Info.plist already declares both, so there
/// is nothing to register.
/// </summary>
public static class ShellIntegration
{
    private const string WindowsKey = @"HKCU\Software\Classes\magnet";
    private const string DesktopFileName = "bitkraken.desktop";

    /// <summary>Registers or unregisters the handler. Safe to call on every start; it only writes when something changed.</summary>
    public static Task ApplyAsync(bool register) => Task.Run(() =>
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            if (OperatingSystem.IsWindows())
            {
                if (register) RegisterWindows(exe); else UnregisterWindows(exe);
            }
            else if (OperatingSystem.IsLinux())
            {
                if (register) RegisterLinux(exe); else UnregisterLinux();
            }
        }
        catch
        {
            // A locked-down desktop is not a reason to fail startup; magnet links can still be pasted.
        }
    });

    // ----- Windows -------------------------------------------------------------------------------------------------

    private static void RegisterWindows(string exe)
    {
        var command = $"\"{exe}\" \"%1\"";
        if (ReadWindowsCommand() == command) return;

        Reg("add", WindowsKey, "/ve", "/t", "REG_SZ", "/d", "URL:Magnet link", "/f");
        Reg("add", WindowsKey, "/v", "URL Protocol", "/t", "REG_SZ", "/d", "", "/f");
        Reg("add", $@"{WindowsKey}\DefaultIcon", "/ve", "/t", "REG_SZ", "/d", $"\"{exe}\",0", "/f");
        Reg("add", $@"{WindowsKey}\shell\open\command", "/ve", "/t", "REG_SZ", "/d", command, "/f");
    }

    private static void UnregisterWindows(string exe)
    {
        // Only remove the association while it still points at us, so we never drop another client's.
        if (ReadWindowsCommand() is not { } command || !command.Contains(exe, StringComparison.OrdinalIgnoreCase)) return;
        Reg("delete", WindowsKey, "/f");
    }

    private static string? ReadWindowsCommand()
    {
        var output = Run("reg", ["query", $@"{WindowsKey}\shell\open\command", "/ve"], captureOutput: true);
        if (output is null) return null;

        // reg query prints "    (Default)    REG_SZ    <value>"
        var marker = output.IndexOf("REG_SZ", StringComparison.Ordinal);
        return marker < 0 ? null : output[(marker + "REG_SZ".Length)..].Trim();
    }

    private static void Reg(params string[] args) => Run("reg", args, captureOutput: false);

    // ----- Linux ---------------------------------------------------------------------------------------------------

    private static void RegisterLinux(string exe)
    {
        var applications = Path.Combine(DataHome(), "applications");
        Directory.CreateDirectory(applications);

        var path = Path.Combine(applications, DesktopFileName);
        var entry = DesktopEntry(exe);

        // %u passes the magnet link (or file) the browser handed us straight through as an argument.
        if (!File.Exists(path) || File.ReadAllText(path) != entry)
        {
            File.WriteAllText(path, entry, new UTF8Encoding(false));
            Run("update-desktop-database", [applications], captureOutput: false);
        }

        InstallIcon();
        SetDefaultHandler("x-scheme-handler/magnet");
        SetDefaultHandler("application/x-bittorrent");
    }

    /// <summary>
    /// Puts the logo in the user's icon theme, which is what turns the desktop entry's
    /// <c>Icon=bitkraken</c> - and the icon a notification daemon looks up for an app - from a name
    /// that resolves to nothing into the BitKraken logo. The PNG is 512x512, so it goes in the size
    /// directory of that name; the theme scales it down for the launcher and the notification popup.
    /// </summary>
    private static void InstallIcon()
    {
        if (AppIcon.FilePath is not { } source) return;

        var directory = IconDirectory();
        var installed = new FileInfo(Path.Combine(directory, AppIcon.Name + ".png"));

        // This runs on every start, so only touch the theme when the icon is missing or has changed:
        // rewriting it each time would mean a needless icon-cache rebuild on every launch.
        if (installed.Exists && installed.Length == new FileInfo(source).Length) return;

        Directory.CreateDirectory(directory);
        File.Copy(source, installed.FullName, overwrite: true);

        // Best-effort: GTK scans the directory when there is no cache, so a missing tool costs nothing.
        Run("gtk-update-icon-cache", ["--force", "--quiet", Path.Combine(DataHome(), "icons", "hicolor")], captureOutput: false);
    }

    private static void SetDefaultHandler(string mimeType)
    {
        var current = Run("xdg-mime", ["query", "default", mimeType], captureOutput: true)?.Trim();
        if (current == DesktopFileName) return;

        Run("xdg-mime", ["default", DesktopFileName, mimeType], captureOutput: false);
    }

    private static void UnregisterLinux()
    {
        // Drop our associations first: an entry left pointing at a deleted .desktop file opens nothing at all.
        RemoveFromMimeApps(Path.Combine(ConfigHome(), "mimeapps.list"));
        RemoveFromMimeApps(Path.Combine(DataHome(), "applications", "mimeapps.list"));

        var icon = Path.Combine(IconDirectory(), AppIcon.Name + ".png");
        if (File.Exists(icon)) File.Delete(icon);

        var applications = Path.Combine(DataHome(), "applications");
        var path = Path.Combine(applications, DesktopFileName);
        if (!File.Exists(path)) return;

        File.Delete(path);
        Run("update-desktop-database", [applications], captureOutput: false);
    }

    private static string IconDirectory() => Path.Combine(DataHome(), "icons", "hicolor", "512x512", "apps");

    /// <summary>Removes bitkraken.desktop from every association list in a mimeapps.list, dropping lines it empties.</summary>
    private static void RemoveFromMimeApps(string path)
    {
        if (!File.Exists(path)) return;

        var lines = File.ReadAllLines(path);
        var kept = new List<string>(lines.Length);
        var changed = false;

        foreach (var line in lines)
        {
            var separator = line.IndexOf('=');
            if (separator < 0 || !line.Contains(DesktopFileName, StringComparison.Ordinal))
            {
                kept.Add(line);
                continue;
            }

            var handlers = line[(separator + 1)..]
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(h => h != DesktopFileName)
                .ToList();

            changed = true;
            if (handlers.Count > 0) kept.Add($"{line[..separator]}={string.Join(';', handlers)};");
        }

        if (changed) File.WriteAllLines(path, kept);
    }

    private static string ConfigHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdg;
    }

    private static string DesktopEntry(string exe) =>
        $"""
        [Desktop Entry]
        Type=Application
        Name=BitKraken
        GenericName=BitTorrent Client
        Comment=A sleek, cross-platform BitTorrent client
        Exec="{exe}" %u
        Icon=bitkraken
        Terminal=false
        Categories=Network;FileTransfer;P2P;
        MimeType=application/x-bittorrent;x-scheme-handler/magnet;
        StartupWMClass=BitKraken

        """;

    private static string DataHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : xdg;
    }

    // ----- Process helper ------------------------------------------------------------------------------------------

    private static string? Run(string fileName, IEnumerable<string> arguments, bool captureOutput)
    {
        try
        {
            var info = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process is null) return null;

            // Drain both pipes concurrently so a chatty tool can't block on a full buffer.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000)) return null;
            var output = stdout.GetAwaiter().GetResult();
            stderr.GetAwaiter().GetResult();

            return process.ExitCode == 0 && captureOutput ? output : null;
        }
        catch
        {
            // Tool missing (a bare desktop without xdg-utils, say) - nothing else to do.
            return null;
        }
    }
}
