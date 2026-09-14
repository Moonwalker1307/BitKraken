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
APP_NAME="BitKraken"
BUNDLE_ID="com.thorstholm.bitkraken"

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
sed "s/__VERSION__/$VERSION/g" "$ROOT/packaging/macos/Info.plist" > "$APP/Contents/Info.plist"
echo -n "APPL????" > "$APP/Contents/PkgInfo"

# Icon: build an .icns from the 512px PNG
ICONSET="$STAGE/$APP_NAME.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
  sips -z $size $size "$ROOT/src/BitKraken/Assets/bitkraken.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z $double $double "$ROOT/src/BitKraken/Assets/bitkraken.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/$APP_NAME.icns"

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
PKG_ARGS=(--component "$APP" --install-location /Applications --identifier "$BUNDLE_ID" --version "$VERSION")
if [ -n "${INSTALLER_IDENTITY:-}" ]; then
  PKG_ARGS+=(--sign "$INSTALLER_IDENTITY")
fi
pkgbuild "${PKG_ARGS[@]}" "$PKG" >/dev/null

echo "==> Done"
ls -la "$DIST"
