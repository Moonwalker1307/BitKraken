# BitKraken

A sleek, cross-platform BitTorrent client for macOS, Windows and Linux — built with **C# / .NET 10**,
[Avalonia UI](https://avaloniaui.net) and the [MonoTorrent](https://github.com/alanmcgovern/monotorrent) engine.

Dark, purple-tinted glass UI with an animated aurora backdrop, glowing progress bars, a live piece map and
real-time transfer graphs.

## Features

- **Torrents & magnets** — open `.torrent` files, paste magnet links, drag-and-drop onto the window, click a magnet
  link in your browser, or pass them on the command line (`BitKraken file.torrent` / `BitKraken "magnet:?..."`).
- **Clipboard watch** — copies a magnet link? BitKraken offers to add it (toggle in Settings).
- **Full engine** — DHT, PEX, local peer discovery, UPnP/NAT-PMP port forwarding, protocol encryption,
  per-file priorities, global rate limits, fast-resume and session restore.
- **Details panel** — overview (piece map + speed graph + stats), files (priority / skip), peers, trackers.
- **Filters & search** — All / Downloading / Seeding / Completed / Paused / Errors, plus instant name search.
- **Keyboard** — `Ctrl/⌘+O` add file, `Ctrl/⌘+M` add magnet, `Ctrl/⌘+,` settings, `Delete` remove.

## Magnet links from your browser

Clicking a magnet link on a torrent site hands it straight to BitKraken: the window comes forward and the torrent is
added (queued if the engine is still starting up). A second launch never starts a second engine — it passes its
arguments to the instance that is already running and exits.

Each desktop delivers the link differently, and BitKraken registers itself for all three:

| Platform | How it's wired up |
| --- | --- |
| macOS | The `BitKraken.app` bundle declares the `magnet:` scheme, and macOS delivers the link as a URL event — it never appears on the command line. Use the packaged app (see below), not the bare binary. |
| Windows | `HKCU\Software\Classes\magnet` is pointed at the running executable on startup. |
| Linux | `~/.local/share/applications/bitkraken.desktop` is written with `x-scheme-handler/magnet` and made the default with `xdg-mime`. |

Turning **Settings → Open magnet links from the browser** off removes the association again (on Windows and Linux;
on macOS it belongs to the bundle). Move or re-install the app and the association follows it on the next start.

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

## macOS installer (Apple Silicon)

```bash
scripts/package-macos.sh osx-arm64 1.0.0
```

produces `dist/BitKraken-<version>-osx-arm64.dmg` (drag-to-Applications) and `.pkg` (installer), built from a proper
`BitKraken.app` bundle that registers the `.torrent` file type and `magnet:` URL scheme.

The [macOS installer workflow](.github/workflows/macos-installer.yml) runs the same script on an Apple Silicon runner:

- **Pull request** → builds a **preview**, `1.0.x-preview`, and uploads the `.dmg`/`.pkg` as workflow artifacts.
- **Merge to `main`** → builds `1.0.x`, tags the commit `v1.0.x` and publishes a GitHub **release** with the
  `.dmg`/`.pkg` attached, so it shows up under *Releases*.
- **Tag push** `v1.2.3` → builds that exact version and publishes the release.
- **Run workflow** (manual) → builds with the version you enter, or the next `1.0.x` if you leave it empty.

### Versioning

Versions are `1.0.x`, where `x` is the patch of the highest existing `v1.0.*` tag plus one — so the first merge to
`main` releases `1.0.0`, the next `1.0.1`, and so on. A pull request builds the same next patch with a `-preview`
suffix, which is a preview of what merging it would release. [`scripts/next-version.sh`](scripts/next-version.sh)
computes it and can be run locally:

```bash
scripts/next-version.sh release   # 1.0.3
scripts/next-version.sh preview   # 1.0.3-preview
scripts/next-version.sh current   # 1.0.2 (the latest released version)
```

Releases are the source of truth for the counter, so nothing needs to be committed to bump a version. To move to a
new series, push a tag for it (e.g. `v1.1.0`) or set `VERSION_SERIES=1.1`. Pre-release suffixes are kept in file
names and .NET assembly metadata; the app bundle and `.pkg` get the numeric `1.0.x` core, which is all macOS accepts.

Packages are ad-hoc signed unless you add these repository secrets, in which case they are Developer ID signed and
notarized: `MACOS_CERTIFICATE_P12` (base64 `.p12`), `MACOS_CERTIFICATE_PASSWORD`, `MACOS_SIGNING_IDENTITY`
(`Developer ID Application: …`), `MACOS_INSTALLER_IDENTITY` (`Developer ID Installer: …`), `APPLE_ID`,
`APPLE_TEAM_ID`, `APPLE_APP_PASSWORD` (app-specific password).

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
