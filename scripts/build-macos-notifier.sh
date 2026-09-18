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

# The icon's base name inside the bundle, which CFBundleIconFile is pointed at.
ICON_NAME="BitKraken"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

rm -rf "$DEST"
mkdir -p "$(dirname "$DEST")"
osacompile -o "$DEST" "$ROOT/packaging/macos/notifier.applescript"

# The applet ships with a generic icon; ours replaces it. This is the entire point of the bundle
# existing, so the name is stated here rather than inherited from whatever osacompile wrote.
PLIST="$DEST/Contents/Info.plist"
if [ -n "$ICNS" ]; then
  cp "$ICNS" "$DEST/Contents/Resources/$ICON_NAME.icns"
  plutil -replace CFBundleIconFile -string "$ICON_NAME" "$PLIST" 2>/dev/null \
    || plutil -insert CFBundleIconFile -string "$ICON_NAME" "$PLIST"

  # osacompile's own icon, left behind under its own name, is what shows if anything here is wrong.
  # Removing it turns "the logo silently did not apply" into a bundle with no icon at all, which is
  # a symptom somebody notices.
  [ "$ICON_NAME" = "applet" ] || rm -f "$DEST/Contents/Resources/applet.icns"
fi

plutil -replace CFBundleIdentifier -string "$BUNDLE_ID" "$PLIST"

# Both names: a notification is headed by CFBundleDisplayName when there is one, CFBundleName when
# there is not, and "applet" is neither a good heading nor a recognisable one.
plutil -replace CFBundleName -string "$DISPLAY_NAME" "$PLIST"
plutil -replace CFBundleDisplayName -string "$DISPLAY_NAME" "$PLIST" 2>/dev/null \
  || plutil -insert CFBundleDisplayName -string "$DISPLAY_NAME" "$PLIST"

# No Dock icon and no menu bar: it is run to post a notification and then it is gone.
plutil -replace LSUIElement -bool true "$PLIST" 2>/dev/null \
  || plutil -insert LSUIElement -bool true "$PLIST"
