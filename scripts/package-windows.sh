#!/usr/bin/env bash
# Builds the BitKraken Windows installer: a setup .exe that installs to Program Files, adds Start
# Menu (and optionally desktop) shortcuts, registers the .torrent file type and uninstalls cleanly.
#
# Usage: scripts/package-windows.sh [rid] [version]
#   rid      win-x64 (default) or win-arm64
#   version  e.g. 1.2.0 (default: 1.0.0, or $VERSION)
#
# Runs on Windows only (it needs Inno Setup's compiler, ISCC); git bash is enough. Install the
# compiler with `winget install JRSoftware.InnoSetup` or `choco install innosetup`, or point
# $ISCC at it. Reuses the folder scripts/package-portable.sh staged for the .zip when it is
# already there, so a build that makes both packages publishes once.
#
# Produces dist/BitKraken-<version>-<rid>-setup.exe.
set -euo pipefail

RID="${1:-win-x64}"
VERSION="${2:-${VERSION:-1.0.0}}"
VERSION="${VERSION#v}"
# The Windows version resource only accepts numeric x.y.z, so a pre-release build (1.0.3-preview)
# keeps the full string for file names and the wizard but stamps the numeric core into the metadata.
VERSION_CORE="${VERSION%%-*}"
APP_NAME="BitKraken"

case "$RID" in
  win-x64) ARCH="x64" ;;
  win-arm64) ARCH="arm64" ;;
  *) echo "Unsupported RID '$RID' (expected win-x64 or win-arm64)" >&2; exit 1 ;;
esac

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
NAME="$APP_NAME-$VERSION-$RID"
STAGE="$ROOT/publish/portable-$RID/$NAME" # what package-portable.sh leaves behind for the .zip
OUT="$ROOT/publish/$RID"
DIST="$ROOT/dist"

# ISCC is a Windows program, so every path handed to it has to be a Windows path.
winpath() {
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -w "$1"
  else
    echo "$1"
  fi
}

find_iscc() {
  if [ -n "${ISCC:-}" ]; then echo "$ISCC"; return; fi
  if command -v iscc >/dev/null 2>&1; then command -v iscc; return; fi
  for candidate in \
    "/c/Program Files (x86)/Inno Setup 6/ISCC.exe" \
    "/c/Program Files/Inno Setup 6/ISCC.exe" \
    "$LOCALAPPDATA/Programs/Inno Setup 6/ISCC.exe"; do
    [ -f "$candidate" ] && { echo "$candidate"; return; }
  done
  echo ""
}

ISCC="$(find_iscc)"
if [ -z "$ISCC" ]; then
  echo "Inno Setup's compiler (ISCC.exe) was not found. Install Inno Setup 6 or set \$ISCC to its path." >&2
  exit 1
fi

# Inno Setup 6.3 renamed the x64 architecture identifier: "x64compatible" means "can run x64
# binaries", which includes ARM64 machines emulating x64, whereas the older "x64" is x64 hardware
# only. Use the newer one where the compiler understands it, and fall back where it does not.
if [ "$ARCH" = "x64" ]; then
  ISCC_VERSION="$(powershell -NoProfile -Command "(Get-Item '$(winpath "$ISCC")').VersionInfo.ProductVersion" 2>/dev/null | tr -d '\r' || true)"
  ISCC_MAJOR="${ISCC_VERSION%%.*}"
  ISCC_MINOR="${ISCC_VERSION#*.}"; ISCC_MINOR="${ISCC_MINOR%%.*}"
  if [[ "$ISCC_MAJOR" =~ ^[0-9]+$ && "$ISCC_MINOR" =~ ^[0-9]+$ ]] &&
     { [ "$ISCC_MAJOR" -gt 6 ] || { [ "$ISCC_MAJOR" -eq 6 ] && [ "$ISCC_MINOR" -ge 3 ]; }; }; then
    ARCH="x64compatible"
  fi
fi

if [ -f "$STAGE/$APP_NAME.exe" ]; then
  SOURCE="$STAGE"
  echo "==> Using the files package-portable.sh staged in $SOURCE"
else
  SOURCE="$OUT"
  echo "==> Publishing $RID (v$VERSION)"
  rm -rf "$OUT"
  dotnet publish "$ROOT/src/BitKraken/BitKraken.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:Version="$VERSION" -p:DebugType=none \
    -o "$OUT"
  [ -f "$ROOT/README.md" ] && cp "$ROOT/README.md" "$SOURCE/"
fi

mkdir -p "$DIST"
SETUP="$DIST/$NAME-setup.exe"
rm -f "$SETUP"

echo "==> Creating $SETUP"
# The paths below are already Windows paths, and /D... defines are not paths at all, so turn off
# git bash's habit of rewriting arguments that start with a slash before they reach ISCC.
MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' "$ISCC" \
  "/DAppVersion=$VERSION" \
  "/DVersionCore=$VERSION_CORE" \
  "/DArchitecture=$ARCH" \
  "/DSourceDir=$(winpath "$SOURCE")" \
  "/DOutputDir=$(winpath "$DIST")" \
  "/DOutputBaseName=$NAME-setup" \
  "/DSetupIconFile=$(winpath "$ROOT/src/BitKraken/Assets/bitkraken.ico")" \
  "$(winpath "$ROOT/packaging/windows/bitkraken.iss")"

echo "==> Done"
ls -la "$DIST"
