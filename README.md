# BitKraken

A sleek, cross-platform BitTorrent client for macOS, Windows and Linux — built with **C# / .NET 10**,
[Avalonia UI](https://avaloniaui.net) and the [MonoTorrent](https://github.com/alanmcgovern/monotorrent) engine.

Dark, purple-tinted glass UI with an animated aurora backdrop, glowing progress bars, a live piece map and
real-time transfer graphs.

## Features

- **Torrents & magnets** — open `.torrent` files, paste magnet links, drag-and-drop onto the window, or pass them
  on the command line (`BitKraken file.torrent` / `BitKraken "magnet:?..."`).
- **Clipboard watch** — copies a magnet link? BitKraken offers to add it (toggle in Settings).
- **Full engine** — DHT, PEX, local peer discovery, UPnP/NAT-PMP port forwarding, protocol encryption,
  per-file priorities, global rate limits, fast-resume and session restore.
- **Details panel** — overview (piece map + speed graph + stats), files (priority / skip), peers, trackers.
- **Filters & search** — All / Downloading / Seeding / Completed / Paused / Errors, plus instant name search.
- **Keyboard** — `Ctrl/⌘+O` add file, `Ctrl/⌘+M` add magnet, `Ctrl/⌘+,` settings, `Delete` remove.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build (the published binaries are self-contained).

## Run from source

```bash
dotnet run --project src/BitKraken
```

## Publish self-contained binaries

```bash
scripts/publish.sh                 # all platforms
scripts/publish.sh osx-arm64       # or just one RID
```

On Windows: `.\scripts\publish.ps1 -Rids win-x64`. Output lands in `publish/<rid>/`.

Supported RIDs: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.

## Where data lives

| What | Location |
| --- | --- |
| Downloads | `~/Downloads/BitKraken` (changeable in Settings) |
| Settings, session state, fast-resume, DHT cache | `%APPDATA%\BitKraken` (Windows), `~/Library/Application Support/BitKraken` (macOS), `~/.config/BitKraken` (Linux) |

## Project layout

```
src/BitKraken/
  Controls/      Custom-drawn controls: AuroraBackground, GlowProgressBar, PieceMap, SpeedGraph
  Services/      TorrentService (MonoTorrent wrapper), SettingsService, IDialogService
  ViewModels/    MVVM view-models (CommunityToolkit.Mvvm)
  Views/         MainWindow + Add / Settings / Remove dialogs (Avalonia XAML)
  Styles/        Colors.axaml (palette, icons) and Theme.axaml (control styles, animations)
  Diagnostics/   Env-var driven dev hooks (headless screenshots)
```

### Developer hooks

For headless UI checks the app honours a few environment variables:

| Variable | Effect |
| --- | --- |
| `BITKRAKEN_SCREENSHOT=/path/out.png` | Render the main window to a PNG after it opens |
| `BITKRAKEN_SCREENSHOT_DELAY=<seconds>` | Delay before rendering (default 6) |
| `BITKRAKEN_DEBUG_SELECT=1` | Select the first torrent first |
| `BITKRAKEN_DEBUG_TAB=<0-3>` | Pick a details tab |
| `BITKRAKEN_DEBUG_DIALOG=add\|settings\|remove` | Also open that dialog and render it to `out-dialog.png` |
| `BITKRAKEN_DEBUG_CLOSE_AFTER=<seconds>` | Close the window (exercising clean shutdown) |

## License

MIT
