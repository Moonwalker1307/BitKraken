# BitKraken — notes for Claude

A cross-platform BitTorrent client: C# / .NET 10, Avalonia UI, MonoTorrent engine.

[README.md](README.md) is the product documentation and is kept current — what a feature does, where
data lives, how packaging and releases work. This file is the rest: the commands, the invariants a
change can break without failing a test, and the conventions of the house.

## Commands

```bash
dotnet build BitKraken.slnx
dotnet test tests/BitKraken.Tests      # 182 tests, ~20s, no window needed
dotnet run --project src/BitKraken     # needs a display
```

The SDK is pinned by `global.json` (10.0.100, `rollForward: latestFeature`).

There is no display in a headless session, so don't reach for `dotnet run` to check a UI change.
`Diagnostics/DebugHooks.cs` renders the window to a PNG instead — `BITKRAKEN_SCREENSHOT=/path/out.png`,
plus `BITKRAKEN_DEBUG_SELECT`, `BITKRAKEN_DEBUG_TAB`, `BITKRAKEN_DEBUG_DIALOG` and
`BITKRAKEN_DEBUG_CLOSE_AFTER` (see the README's *Developer hooks* table).

## Architecture invariants

These are the things to preserve. Most of them are not enforced by a test.

**Nothing starts or stops a torrent directly.** Every reason a torrent might be running or not — the
user paused it, the queue is full, the bound interface went away, it has seeded its fill — is an
*input*. `TorrentService.ReconcileAsync` derives the set that should be running and moves the engine
to it. Call sites change state and ask for a reconcile; only the reconcile starts or stops. A new
rule about what runs belongs in that derivation, not at the place that noticed.

**A torrent the user paused is never started by anything automatic.** The queue, the kill switch and
the seeding limits all have to leave it alone. This is a promise the README makes to the user.

**The rules that can be pure, are pure.** `TorrentQueue` (ordering and limits) and `SeedLimits`
(ratio and time arithmetic) are plain functions over plain records, deliberately kept out of the
service so they can be tested without an engine or a window. Extend them there, with tests, rather
than growing the logic inline in `TorrentService`.

**Engine events arrive off the UI thread.** MonoTorrent raises them on its own threads; anything
touching a view-model goes through `Dispatcher.UIThread.Post`, as every subscription in
`MainWindowViewModel` and `TorrentItemViewModel` does.

**The privacy behaviour is the feature, and it fails closed.** Four rules:

- Bound to an interface: peer connections, the listener, HTTP tracker announces and DHT all bind to
  it, and nothing falls back to the default route when it goes away — torrents are held instead.
- Proxy on: UDP trackers, DHT, local peer discovery, the incoming listener and UPnP/NAT-PMP all go
  off, because a TCP proxy cannot carry them, and a misconfigured proxy fails rather than connecting
  directly. Hostnames are resolved at the far end, never here.
- Fallback trackers are appended to public torrents only, never to private ones.
- A change that adds a new outbound connection or lookup has to go through `OutboundConnector` /
  `BoundHttpClientFactory` like the existing ones, or it will quietly bypass both.

**Avalonia specifics.** Compiled bindings are on by default (`AvaloniaUseCompiledBindingsByDefault`),
so every new view needs `x:DataType`. View-models use `CommunityToolkit.Mvvm` source generators
(`[ObservableProperty]`, `[RelayCommand]`). Custom drawing lives in `Controls/`; the palette and
control styles in `Styles/Colors.axaml` and `Styles/Theme.axaml`, not inline on the views.

**macOS notifications** have burned several attempts. macOS draws the icon of whichever *bundle*
posted the notification, so the fix was to post from inside BitKraken's own process
(`MacNotifications`), with the helper applet and plain `osascript` left as fallbacks. Whether that
works cannot be unit-tested from a build directory, so the app has a `--notify-test` flag that
`scripts/verify-macos-app.sh` runs against the finished `.app` in CI. Keep that path checked rather
than assumed — `NSUserNotification` is deprecated and may vanish.

## Tests

xUnit, in `tests/BitKraken.Tests`, covering the logic that does not need a window. Don't pull
Avalonia into that project.

- `TestEnvironment` redirects `HOME`, `XDG_CONFIG_HOME`, `USERPROFILE` and `APPDATA` to a temp root
  from a `[ModuleInitializer]`, so nothing touches the real profile. New tests that resolve paths
  should use `TestEnvironment.NewDirectory()` rather than rolling their own scratch directory.
- Test names are sentences: `The_limit_is_filled_in_queue_order_and_the_rest_wait_in_line`.
- `InternalsVisibleTo` covers the test project, so internal types can be tested directly.
- Both projects set `InvariantGlobalization` so formatting asserts the same in any locale.
- Tests run on every push and a red run blocks the installer build.

## Releases

Versions are derived, never written down. Don't hand-edit `<Version>` in `BitKraken.csproj`:
`scripts/next-version.sh` reads the highest `v1.0.*` tag and adds one. A merge to `main` releases
`1.0.x` and tags it; a pull request builds `1.0.x-preview-<run>` as artifacts only.

The installers workflow only builds when `src/` changes, so a docs- or scripts-only merge releases
nothing — that is deliberate, not a bug to fix.

The macOS verification scripts (`verify-macos-app.sh`, `verify-macos-notifier.sh`) only run on macOS
runners. Changes to the bundle, the helper or notifications can't be validated locally on Linux; lean
on CI and say so rather than claiming a check that did not run.

## Branches and pull requests

Every change starts on a **new** branch cut from the latest `origin/main`. Never add commits to a
branch whose pull request has already merged, even when a session began with that branch assigned to
it: a merged pull request cannot track new work, and stacking on merged history makes the new diff
read as though it re-lands commits that are already in. Start again instead, keeping any unmerged
work:

```bash
git fetch origin main && git checkout -B claude/<new-name> origin/main
```

Merging to `main` tags and publishes a release, so `main` is not somewhere to try something out.

## Conventions

- **Commits**: an imperative, sentence-case subject naming the behaviour change — "Stop the queue
  from restarting a torrent that is being removed". No Conventional Commits prefixes, no emoji. The
  body explains why, and what was tried and rejected, when that is the interesting part.
- **Comments**: XML doc comments carry the reasoning, not a restatement of the signature — why a
  timeout is zero, which alternative was wrong and what it cost. `TorrentService`'s constants are the
  house style. Match that density; this codebase is read more than it is written.
- **C#**: file-scoped namespaces, nullable enabled, implicit usings, records for the pure data.
- **README**: a user-visible change usually means a README edit in the same commit.
