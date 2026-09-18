#!/usr/bin/env bash
# Builds BitKraken.icns from the 512px PNG.
#
# Usage: scripts/build-macos-icns.sh <output.icns>
#
# Split out of package-macos.sh so scripts/verify-macos-notifier.sh checks the notification helper
# against the very icon the installer gives it, rather than against a stand-in.
set -euo pipefail

OUT="${1:?output .icns path required}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
ICONSET="$WORK/icon.iconset"
mkdir -p "$ICONSET"

for size in 16 32 128 256 512; do
  sips -z $size $size "$ROOT/src/BitKraken/Assets/bitkraken.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z $double $double "$ROOT/src/BitKraken/Assets/bitkraken.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done

mkdir -p "$(dirname "$OUT")"
iconutil -c icns "$ICONSET" -o "$OUT"
