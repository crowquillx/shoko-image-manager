#!/usr/bin/env bash
# Exercise package version verification with a valid package and a stale
# release filename/manifest pair.
#
# Usage:
#   build/test-package-version.sh <valid-archive.zip> <valid-manifest.json>
set -euo pipefail

archive="${1:?usage: build/test-package-version.sh <valid-archive.zip> <valid-manifest.json>}"
manifest="${2:?usage: build/test-package-version.sh <valid-archive.zip> <valid-manifest.json>}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# The corrected simulated v1.0.2 package must pass all checks.
bash "$root/build/verify-package.sh" "$archive" "$manifest" >/dev/null

# Build the old default-version payload, then give it the v1.0.1 release
# name and metadata. This models the reported stale artifact: the release
# says 1.0.1 while the project output still says 1.0.0.
bad_dir="$tmp/publish"
bad_archive="$tmp/Shoko.ImagePlanner-1.0.1-any.zip"
bad_manifest="$tmp/manifest.json"
dotnet publish "$root/src/Shoko.ImagePlanner.csproj" \
  -c Release --no-restore --no-self-contained -o "$bad_dir" >/dev/null
(cd "$bad_dir" && zip -qr "$bad_archive" .)
bad_checksum="$(sha256sum "$bad_archive" | awk '{print $1}')"
jq \
  --arg url "https://example.invalid/releases/download/v1.0.1/$(basename "$bad_archive")" \
  --arg checksum "sha256:$bad_checksum" \
  '.releases[0].version = "1.0.1"
   | .releases[0].tag = "v1.0.1"
   | .releases[0].archives[0].url = $url
   | .releases[0].archives[0].checksum = $checksum' \
  "$manifest" > "$bad_manifest"

if bash "$root/build/verify-package.sh" "$bad_archive" "$bad_manifest" >/dev/null 2>&1; then
  echo "error: stale v1.0.1 package was accepted" >&2
  exit 1
fi

echo "OK: package version verification passed (valid package accepted; stale package rejected)"
