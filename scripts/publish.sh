#!/usr/bin/env bash
# Publishes self-contained, single-file builds of BitKraken for every desktop platform.
# Usage: scripts/publish.sh [rid ...]   (defaults to all: win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)
set -euo pipefail

cd "$(dirname "$0")/.."
RIDS=("$@")
if [ ${#RIDS[@]} -eq 0 ]; then
  RIDS=(win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)
fi

for rid in "${RIDS[@]}"; do
  echo "==> Publishing $rid"
  dotnet publish src/BitKraken/BitKraken.csproj \
    -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none \
    -o "publish/$rid"
done

echo "Done. Output in ./publish/<rid>/"
