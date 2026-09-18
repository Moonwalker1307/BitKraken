#!/usr/bin/env bash
# Prints the version to stamp on a build, derived from the highest existing release tag.
#
# Usage: scripts/next-version.sh [channel]
#   channel  release (default) -> 1.0.x            e.g. 1.0.3
#            preview           -> 1.0.x-preview-n  e.g. 1.0.3-preview-23
#            current           -> the latest released version
#
# The series (major.minor) defaults to 1.0 and can be overridden with VERSION_SERIES=1.1.
# x is the patch of the highest `v<series>.<n>` tag plus one, so the first build is 1.0.0.
#
# n on a preview is GITHUB_RUN_NUMBER, which GitHub Actions sets for every run of a workflow.
# Previews are never tagged, so without it every build of a pull request is called 1.0.x-preview
# and there is no way to tell from a crash report, a file name or the title bar which one someone
# is running. Outside Actions the variable is unset and the version stays 1.0.x-preview.
set -euo pipefail

CHANNEL="${1:-release}"
SERIES="${VERSION_SERIES:-1.0}"
SERIES_RE="${SERIES//./\\.}"

# Ignored unless it is a plain number, so nothing can put a stray character in a version string.
BUILD="${GITHUB_RUN_NUMBER:-}"
[[ "$BUILD" =~ ^[0-9]+$ ]] || BUILD=""

cd "$(dirname "$0")/.."

# Tags are the source of truth; shallow clones (and fresh runners) may not have them yet.
git fetch --tags --force --quiet >/dev/null 2>&1 || true

latest=""
latest="$(git tag -l "v${SERIES}.*" | sed -E "s/^v${SERIES_RE}\.//" | grep -E '^[0-9]+$' | sort -n | tail -1 || true)"

if [ -z "$latest" ]; then
  next=0
else
  next=$((latest + 1))
fi

case "$CHANNEL" in
  release) echo "${SERIES}.${next}" ;;
  preview) echo "${SERIES}.${next}-preview${BUILD:+-$BUILD}" ;;
  current) echo "${SERIES}.${latest:-0}" ;;
  *) echo "Unknown channel: $CHANNEL (expected release, preview or current)" >&2; exit 1 ;;
esac
