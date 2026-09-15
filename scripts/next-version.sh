#!/usr/bin/env bash
# Prints the version to stamp on a build, derived from the highest existing release tag.
#
# Usage: scripts/next-version.sh [channel]
#   channel  release (default) -> 1.0.x        e.g. 1.0.3
#            preview           -> 1.0.x-preview e.g. 1.0.3-preview
#
# The series (major.minor) defaults to 1.0 and can be overridden with VERSION_SERIES=1.1.
# x is the patch of the highest `v<series>.<n>` tag plus one, so the first build is 1.0.0.
set -euo pipefail

CHANNEL="${1:-release}"
SERIES="${VERSION_SERIES:-1.0}"
SERIES_RE="${SERIES//./\\.}"

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
  preview) echo "${SERIES}.${next}-preview" ;;
  current) echo "${SERIES}.${latest:-0}" ;;
  *) echo "Unknown channel: $CHANNEL (expected release, preview or current)" >&2; exit 1 ;;
esac
