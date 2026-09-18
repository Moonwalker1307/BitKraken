#!/usr/bin/env bash
# Checks, on a real Mac, that BitKraken's notification helper works - because the thing that makes
# the logo appear is which process posted the notification, and nothing on a Linux CI box can tell
# you that. The first attempt at this shipped, looked right, and silently did not work.
#
# What it proves: the helper builds, it runs, the notification is posted from inside the helper's own
# bundle (which is what puts BitKraken's icon on it), and macOS accepted it.
# What it cannot prove: what the icon looks like. Nothing headless can.
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

echo "==> Building the helper"
"$ROOT/scripts/build-macos-notifier.sh" "$APP" "$BUNDLE_ID" "$APP_NAME"

# Ad-hoc, the way an unsigned installer build ships it - the weakest case, so the one worth testing.
codesign --force --sign - "$APP"

echo "==> Checking what it says it is"
built_id="$(plutil -extract CFBundleIdentifier raw "$APP/Contents/Info.plist")"
[ "$built_id" = "$BUNDLE_ID" ] || fail "helper bundle id is '$built_id', expected '$BUNDLE_ID'"

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
