#!/usr/bin/env bash
# Publishes a self-contained, single-file build of BitKraken and archives it for distribution.
#
# Usage: scripts/package-portable.sh [rid] [version]
#   rid      win-x64 | win-arm64 | linux-x64 | linux-arm64 (default: win-x64)
#   version  e.g. 1.2.0 (default: 1.0.0, or $VERSION)
#
# Produces dist/BitKraken-<version>-<rid>.zip for Windows and .tar.gz for Linux, each containing a
# single BitKraken-<version>-<rid>/ folder with the executable. macOS uses package-macos.sh instead,
# which builds a real .app bundle plus .dmg/.pkg installers.
set -euo pipefail

RID="${1:-win-x64}"
VERSION="${2:-${VERSION:-1.0.0}}"
VERSION="${VERSION#v}"
APP_NAME="BitKraken"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/publish/$RID"
STAGE_ROOT="$ROOT/publish/portable-$RID"
NAME="$APP_NAME-$VERSION-$RID"
STAGE="$STAGE_ROOT/$NAME"
DIST="$ROOT/dist"

echo "==> Publishing $RID (v$VERSION)"
rm -rf "$OUT" "$STAGE_ROOT"
dotnet publish "$ROOT/src/BitKraken/BitKraken.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:Version="$VERSION" -p:DebugType=none \
  -o "$OUT"

echo "==> Staging $NAME"
mkdir -p "$STAGE" "$DIST"
cp -R "$OUT"/. "$STAGE/"
[ -f "$ROOT/README.md" ] && cp "$ROOT/README.md" "$STAGE/"
case "$RID" in
  linux-*) chmod +x "$STAGE/$APP_NAME" 2>/dev/null || true ;;
esac

case "$RID" in
  win-*)
    ARCHIVE="$DIST/$NAME.zip"
    echo "==> Creating $ARCHIVE"
    rm -f "$ARCHIVE"
    # Runners differ in which zip tool they ship, so try the usual suspects in turn.
    if command -v 7z >/dev/null 2>&1; then
      (cd "$STAGE_ROOT" && 7z a -tzip -mx=9 "$ARCHIVE" "$NAME" >/dev/null)
    elif command -v zip >/dev/null 2>&1; then
      (cd "$STAGE_ROOT" && zip -qr "$ARCHIVE" "$NAME")
    elif command -v pwsh >/dev/null 2>&1; then
      pwsh -NoProfile -Command "Compress-Archive -Path '$STAGE' -DestinationPath '$ARCHIVE' -Force"
    else
      echo "No zip tool found (need 7z, zip or pwsh)" >&2
      exit 1
    fi
    ;;
  *)
    ARCHIVE="$DIST/$NAME.tar.gz"
    echo "==> Creating $ARCHIVE"
    rm -f "$ARCHIVE"
    (cd "$STAGE_ROOT" && tar -czf "$ARCHIVE" "$NAME")
    ;;
esac

echo "==> Done"
ls -la "$DIST"
