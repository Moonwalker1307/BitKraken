#!/usr/bin/env bash
# Inspects the notification helper inside a real BitKraken.app - the one that ships, or the one on
# your disk - and reports what it finds.
#
# Usage: scripts/verify-macos-app.sh [path/to/BitKraken.app]
#        (default: /Applications/BitKraken.app)
#
# verify-macos-notifier.sh checks a helper built into a temp directory. That is not the same artifact
# as the one inside the installed .app: packaging builds it in place, `codesign --deep` runs over it
# afterwards, and the whole thing is then carried through a .dmg and a .pkg. Every one of those steps
# is a chance for the icon or the plist to arrive different from how it was built, and none of them
# were being looked at. This looks at the end product.
#
# Run it against your own install when a notification comes up wearing the wrong face:
#   scripts/verify-macos-app.sh
set -euo pipefail

APP="${1:-/Applications/BitKraken.app}"
APP_NAME="BitKraken"
BUNDLE_ID="com.thorstholm.bitkraken"

problems=0
note() { printf '  %s\n' "$*"; }
bad() { printf '  FAIL  %s\n' "$*" >&2; problems=$((problems + 1)); }

[ -d "$APP" ] || { echo "No app bundle at $APP" >&2; exit 2; }

echo "==> $APP"
version="$(plutil -extract CFBundleShortVersionString raw "$APP/Contents/Info.plist" 2>/dev/null || echo "?")"
note "version: $version"

NOTIFIER="$APP/Contents/Helpers/$APP_NAME Notifier.app"
echo "==> The notification helper"
if [ ! -d "$NOTIFIER" ]; then
  bad "no helper at Contents/Helpers/$APP_NAME Notifier.app - notifications will fall back to osascript, which wears Script Editor's icon"
  echo
  echo "$problems problem(s)."
  exit 1
fi

PLIST="$NOTIFIER/Contents/Info.plist"
for key in CFBundleIdentifier CFBundleName CFBundleDisplayName CFBundleIconFile CFBundleExecutable; do
  value="$(plutil -extract "$key" raw "$PLIST" 2>/dev/null || echo "<unset>")"
  note "$(printf '%-24s %s' "$key" "$value")"
done

id="$(plutil -extract CFBundleIdentifier raw "$PLIST" 2>/dev/null || echo "")"
[ "$id" = "$BUNDLE_ID.notifier" ] || bad "bundle id is '$id', expected '$BUNDLE_ID.notifier'"

exe="$NOTIFIER/Contents/MacOS/applet"
[ -x "$exe" ] || bad "the helper has no runnable Contents/MacOS/applet - BitKraken will not find it"

echo "==> Its icon"
icon_name="$(plutil -extract CFBundleIconFile raw "$PLIST" 2>/dev/null || echo "")"
if [ -z "$icon_name" ]; then
  bad "Info.plist names no CFBundleIconFile, so macOS has nothing to draw but a generic icon"
else
  icon="$NOTIFIER/Contents/Resources/$icon_name"
  [ -f "$icon" ] || icon="$NOTIFIER/Contents/Resources/$icon_name.icns"

  if [ ! -f "$icon" ]; then
    bad "CFBundleIconFile names '$icon_name', which is not in the helper's Resources"
  else
    note "icon file: $(basename "$icon") ($(wc -c < "$icon" | tr -d ' ') bytes)"

    app_icns="$APP/Contents/Resources/$APP_NAME.icns"
    if [ -f "$app_icns" ]; then
      if [ "$(shasum -a 256 < "$icon")" = "$(shasum -a 256 < "$app_icns")" ]; then
        note "matches the app's own $APP_NAME.icns"
      else
        bad "the helper's icon is NOT the same file as the app's $APP_NAME.icns"
      fi
    fi
  fi
fi

# The one osacompile ships. Left in the bundle it is what gets drawn when anything above is wrong.
if [ "$icon_name" != "applet" ] && [ -f "$NOTIFIER/Contents/Resources/applet.icns" ]; then
  bad "osacompile's generic applet.icns is still in the bundle"
fi

echo "==> Signature"
if codesign --verify --strict "$NOTIFIER" 2>/dev/null; then
  note "the helper is signed and intact"
else
  bad "codesign will not verify the helper - macOS may refuse to launch it"
fi

# Only meaningful on a machine where the app is installed; in a build there is nothing registered yet.
echo "==> What macOS has registered for it"
LSREGISTER=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
if [ -x "$LSREGISTER" ]; then
  registered="$("$LSREGISTER" -dump 2>/dev/null | grep -c "$BUNDLE_ID.notifier" || true)"
  if [ "${registered:-0}" -gt 0 ]; then
    note "LaunchServices knows $BUNDLE_ID.notifier ($registered entries)"
    note "if the bundle above is right but notifications still wear the wrong icon, this record is stale - see below"
  else
    note "LaunchServices has no record of it yet (normal until it has run once)"
  fi
else
  note "lsregister not available here"
fi

# The route that matters now: BitKraken posting the notification itself, as this bundle. Only a process
# actually inside the .app can answer whether that works, which is why the app carries the flag.
echo "==> Posting one from the app itself"
if [ -x "$APP/Contents/MacOS/$APP_NAME" ]; then
  if report="$("$APP/Contents/MacOS/$APP_NAME" --notify-test "$APP_NAME" "verify-macos-app.sh" 2>&1)"; then
    note "$report"
    note "the notification is posted by this bundle, so it wears this bundle's icon"
  else
    note "$report"
    bad "the app cannot post its own notifications here - it will fall back to the helper, which is what kept drawing the wrong icon"
  fi
else
  bad "no $APP_NAME executable in Contents/MacOS"
fi

echo
if [ "$problems" -gt 0 ]; then
  echo "$problems problem(s) with the helper in this bundle."
  exit 1
fi

echo "The helper in this bundle is correct: right id, right icon, signed."
cat <<'ADVICE'

If notifications still show the wrong icon, the bundle is not the problem - macOS is caching the icon
it first saw against this bundle id. To clear it:

  sudo rm -rf /Applications/BitKraken.app          # the old bundle, before installing the new one
  killall usernoted                                 # the notification daemon, which caches app icons
  killall Dock                                      # for the icon cache generally

Then reinstall and trigger a notification. A restart does the same thing more thoroughly.
ADVICE
