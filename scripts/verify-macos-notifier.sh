#!/usr/bin/env bash
# Checks, on a real Mac, that BitKraken's notification helper works - because the thing that makes
# the logo appear is which process posted the notification, and nothing on a Linux CI box can tell
# you that. The first attempt at this shipped, looked right, and silently did not work.
#
# What it proves: the helper builds, it carries BitKraken's icon rather than osacompile's, it runs, the
# notification is posted from inside the helper's own bundle, and macOS accepted it.
# What it cannot prove: what the icon looks like once drawn. Nothing headless can.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUNDLE_ID="com.thorstholm.bitkraken.notifier"
APP_NAME="BitKraken"

fail() { echo "FAIL: $*" >&2; exit 1; }

# -P because mktemp hands back /var/..., a symlink, and the applet reports /private/var/... .
WORK="$(cd "$(mktemp -d)" && pwd -P)"
trap 'rm -rf "$WORK"' EXIT

APP="$WORK/$APP_NAME Notifier.app"
TRACE="$WORK/trace"
ICNS="$WORK/$APP_NAME.icns"

echo "==> Building the helper"
# The real icon, built the way the installer builds it - an icon the helper is not actually given is
# the difference between a notification with the logo and one with osacompile's generic applet.
"$ROOT/scripts/build-macos-icns.sh" "$ICNS"
"$ROOT/scripts/build-macos-notifier.sh" "$APP" "$BUNDLE_ID" "$APP_NAME" "$ICNS"

# Ad-hoc, the way an unsigned installer build ships it - the weakest case, so the one worth testing.
codesign --force --sign - "$APP"

echo "==> Checking what it says it is"
built_id="$(plutil -extract CFBundleIdentifier raw "$APP/Contents/Info.plist")"
[ "$built_id" = "$BUNDLE_ID" ] || fail "helper bundle id is '$built_id', expected '$BUNDLE_ID'"

# The icon is the entire reason this bundle exists, so it is checked rather than assumed: the plist
# has to name an icon, that icon has to be in the bundle, and it has to be the one we handed over.
icon_name="$(plutil -extract CFBundleIconFile raw "$APP/Contents/Info.plist" 2>/dev/null || true)"
[ -n "$icon_name" ] || fail "helper Info.plist names no CFBundleIconFile"

icon="$APP/Contents/Resources/$icon_name"
[ -f "$icon" ] || icon="$APP/Contents/Resources/$icon_name.icns"
[ -f "$icon" ] || fail "helper Info.plist names icon '$icon_name', which is not in Contents/Resources"

[ "$(shasum -a 256 < "$icon")" = "$(shasum -a 256 < "$ICNS")" ] \
  || fail "helper icon '$icon_name' is not the BitKraken icon it was given"

# osacompile's generic applet icon left behind under its own name is what shows when anything above
# is subtly wrong, so the bundle must not still be carrying one.
if [ "$icon_name" != "applet" ] && [ -f "$APP/Contents/Resources/applet.icns" ]; then
  fail "osacompile's generic applet.icns is still in the bundle"
fi

echo "    icon:      $icon_name.icns, matching $APP_NAME.icns"

# The app finds the helper by this folder name and no other; a rename on one side only is a silent
# fall back to a notification with the wrong icon.
grep -q 'NotifierBundleName = AppInfo.Name + " Notifier.app"' \
  "$ROOT/src/BitKraken/Services/DesktopNotifier.cs" \
  || fail "DesktopNotifier no longer looks for '$APP_NAME Notifier.app'"

echo "==> Posting a notification"
BITKRAKEN_NOTIFY_TITLE="$APP_NAME" \
BITKRAKEN_NOTIFY_BODY="verify-macos-notifier.sh" \
BITKRAKEN_NOTIFY_TRACE="$TRACE" \
  "$APP/Contents/MacOS/applet"

[ -f "$TRACE" ] || fail "the helper never ran (no trace written)"
poster="$(sed -n 1p "$TRACE")"
outcome="$(sed -n 2p "$TRACE")"

echo "    posted by: $poster"
echo "    outcome:   $outcome"

# This is the whole fix: the notification has to come from the helper's bundle, not from osascript's.
[ "${poster%/}" = "$APP" ] || fail "posted by '$poster', expected '$APP'"
[ "$outcome" = "ok" ] || fail "macOS refused the notification: $outcome"

echo "==> OK: the helper posts its own notifications, as $BUNDLE_ID"
