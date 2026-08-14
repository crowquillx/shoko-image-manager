#!/usr/bin/env bash
# Validate a packaged plugin zip and its Shoko repository manifest:
#   1. the zip is a valid archive containing the plugin DLL, deps.json and
#      runtimeconfig.json, and does NOT contain Shoko.Abstractions.dll;
#   2. the manifest is schema-valid (build/verify-manifest.sh);
#   3. the manifest checksum matches the zip's SHA-256;
#   4. the manifest archive URL references this zip's file name.
#
# Usage:
#   build/verify-package.sh <archive.zip> <manifest.json>
set -euo pipefail

archive="${1:?usage: build/verify-package.sh <archive.zip> <manifest.json>}"
manifest="${2:?usage: build/verify-package.sh <archive.zip> <manifest.json>}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

for tool in jq unzip sha256sum; do
  command -v "$tool" >/dev/null 2>&1 || { echo "error: $tool is not installed." >&2; exit 1; }
done

"$root/build/verify-manifest.sh" "$manifest"

[[ -f "$archive" ]] || { echo "error: archive not found: $archive" >&2; exit 1; }

fail() { echo "error: $*" >&2; exit 1; }

# --- Zip structure --------------------------------------------------------------
unzip -tq "$archive" >/dev/null 2>&1 || fail "not a valid zip archive: $archive"
entries="$(unzip -Z1 "$archive")"
for required in "Shoko.ImagePlanner.dll" "Shoko.ImagePlanner.deps.json" "Shoko.ImagePlanner.runtimeconfig.json"; do
  grep -Fqx "$required" <<<"$entries" || fail "zip is missing required entry: $required"
done
if grep -Fq "Shoko.Abstractions.dll" <<<"$entries"; then
  fail "zip must not contain Shoko.Abstractions.dll"
fi

# --- Checksum ---------------------------------------------------------------------
manifest_checksum="$(jq -r '.releases[0].archives[0].checksum' "$manifest")"
case "$manifest_checksum" in
  sha256:*) expected="${manifest_checksum#sha256:}" ;;
  sha1:* | md5:*) fail "expected a sha256 checksum, got: $manifest_checksum" ;;
  *) expected="$manifest_checksum" ;;
esac
expected="${expected,,}"
actual="$(sha256sum "$archive" | awk '{print $1}')"
[[ "$expected" == "$actual" ]] || fail "checksum mismatch: manifest=$expected archive=$actual"

# --- Archive URL -------------------------------------------------------------------
archive_name="$(basename "$archive")"
manifest_url="$(jq -r '.releases[0].archives[0].url' "$manifest")"
[[ "$manifest_url" == */"$archive_name" ]] \
  || fail "manifest archive url ($manifest_url) does not reference $archive_name"

echo "OK: $archive verified (sha256=$actual)"
