#!/usr/bin/env bash
# Builds BitKraken.app for macOS and packages it as a .dmg (drag-to-Applications) and a .pkg installer.
#
# Usage: scripts/package-macos.sh [rid] [version]
#   rid      osx-arm64 (default) or osx-x64
#   version  e.g. 1.2.0 (default: 1.0.0, or $VERSION)
#
# Signing (optional): set SIGNING_IDENTITY="Developer ID Application: ..." to codesign with a real certificate;
# otherwise the bundle is ad-hoc signed, which runs locally but triggers Gatekeeper on other Macs.
set -euo pipefail

RID="${1:-osx-arm64}"
VERSION="${2:-${VERSION:-1.0.0}}"
VERSION="${VERSION#v}"
# CFBundleVersion/CFBundleShortVersionString and pkgbuild only accept numeric x.y.z, so a
# pre-release build (1.0.3-preview) keeps the full string for file names and .NET metadata
# but stamps the numeric core into the bundle.
VERSION_CORE="${VERSION%%-*}"
APP_NAME="BitKraken"
BUNDLE_ID="com.thorstholm.bitkraken"
# Where BitKraken looks for the helper: DesktopNotifier builds this same path off its own
# executable, and a pinning test fails if only one of the two is ever changed.
NOTIFIER_APP="$APP_NAME Notifier.app"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/publish/$RID"
STAGE="$ROOT/publish/macos-$RID"
APP="$STAGE/$APP_NAME.app"
DIST="$ROOT/dist"

echo "==> Publishing $RID (v$VERSION)"
rm -rf "$OUT" "$STAGE"
dotnet publish "$ROOT/src/BitKraken/BitKraken.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:Version="$VERSION" -p:DebugType=none -p:UseAppHost=true \
  -o "$OUT"

echo "==> Assembling $APP_NAME.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$OUT"/. "$APP/Contents/MacOS/"
sed "s/__VERSION__/$VERSION_CORE/g" "$ROOT/packaging/macos/Info.plist" > "$APP/Contents/Info.plist"
echo -n "APPL????" > "$APP/Contents/PkgInfo"

# Icon: build an .icns from the 512px PNG. The helper below is given the same file.
"$ROOT/scripts/build-macos-icns.sh" "$APP/Contents/Resources/$APP_NAME.icns"

# A notification wears the icon of the bundle that posted it, and osascript's bundle is Script
# Editor's - which is why notifications never carried the logo however the image was passed. This
# applet exists only to be a bundle with BitKraken's icon: BitKraken runs it, the notification is
# posted from inside it, and the right face comes with it. Built before signing, so --deep covers it.
echo "==> Building the notification helper"
NOTIFIER="$APP/Contents/Helpers/$NOTIFIER_APP"
"$ROOT/scripts/build-macos-notifier.sh" \
  "$NOTIFIER" "$BUNDLE_ID.notifier" "$APP_NAME" "$APP/Contents/Resources/$APP_NAME.icns"

echo "==> Signing"
if [ -n "${SIGNING_IDENTITY:-}" ]; then
  codesign --force --deep --options runtime --timestamp \
    --entitlements "$ROOT/packaging/macos/entitlements.plist" \
    --sign "$SIGNING_IDENTITY" "$APP"
else
  codesign --force --deep --sign - "$APP"
fi
codesign --verify --deep --strict "$APP"

mkdir -p "$DIST"
DMG="$DIST/$APP_NAME-$VERSION-$RID.dmg"
PKG="$DIST/$APP_NAME-$VERSION-$RID.pkg"

echo "==> Creating $DMG"
DMG_ROOT="$STAGE/dmg"
rm -rf "$DMG_ROOT" "$DMG"
mkdir -p "$DMG_ROOT"
cp -R "$APP" "$DMG_ROOT/"
ln -s /Applications "$DMG_ROOT/Applications"
hdiutil create -volname "$APP_NAME" -srcfolder "$DMG_ROOT" -ov -format UDZO "$DMG" >/dev/null

echo "==> Creating $PKG"
rm -f "$PKG"
PKG_ARGS=(--component "$APP" --install-location /Applications --identifier "$BUNDLE_ID" --version "$VERSION_CORE")
if [ -n "${INSTALLER_IDENTITY:-}" ]; then
  PKG_ARGS+=(--sign "$INSTALLER_IDENTITY")
fi
pkgbuild "${PKG_ARGS[@]}" "$PKG" >/dev/null

echo "==> Done"
ls -la "$DIST"
