#!/usr/bin/env bash
# Builds BitKraken's notification helper: a tiny AppleScript applet whose whole purpose is to be a
# bundle with BitKraken's icon on it, because macOS attributes a notification to the bundle that
# posted it. See packaging/macos/notifier.applescript.
#
# Usage: scripts/build-macos-notifier.sh <destination.app> <bundle-id> <display-name> [icns]
#
# Split out of package-macos.sh so that scripts/verify-macos-notifier.sh can build the very same
# helper the installer ships and check it on a real Mac.
set -euo pipefail

DEST="${1:?destination .app path required}"
BUNDLE_ID="${2:?bundle id required}"
DISPLAY_NAME="${3:?display name required}"
ICNS="${4:-}"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

rm -rf "$DEST"
mkdir -p "$(dirname "$DEST")"
osacompile -o "$DEST" "$ROOT/packaging/macos/notifier.applescript"

# The applet ships with a generic icon; ours replaces it, under the name osacompile wrote into the
# Info.plist. This is the entire point of the bundle existing.
if [ -n "$ICNS" ]; then
  cp "$ICNS" "$DEST/Contents/Resources/applet.icns"
fi

PLIST="$DEST/Contents/Info.plist"
plutil -replace CFBundleIdentifier -string "$BUNDLE_ID" "$PLIST"

# Both names: a notification is headed by CFBundleDisplayName when there is one, CFBundleName when
# there is not, and "applet" is neither a good heading nor a recognisable one.
plutil -replace CFBundleName -string "$DISPLAY_NAME" "$PLIST"
plutil -replace CFBundleDisplayName -string "$DISPLAY_NAME" "$PLIST" 2>/dev/null \
  || plutil -insert CFBundleDisplayName -string "$DISPLAY_NAME" "$PLIST"

# No Dock icon and no menu bar: it is run to post a notification and then it is gone.
plutil -replace LSUIElement -bool true "$PLIST" 2>/dev/null \
  || plutil -insert LSUIElement -bool true "$PLIST"
