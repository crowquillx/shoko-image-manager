#!/usr/bin/env bash
# Merge one generated release manifest into the persistent metadata feed.
#
# Usage:
#   build/update-metadata.sh <existing-manifest|-> <release-manifest> <output>
#
# A missing existing manifest (or '-') starts a new feed. Releases are kept in
# publication order and a retry for the same tag replaces that tag's entry.
set -euo pipefail

existing="${1:?usage: build/update-metadata.sh <existing-manifest|-> <release-manifest> <output>}"
incoming="${2:?usage: build/update-metadata.sh <existing-manifest|-> <release-manifest> <output>}"
output="${3:?usage: build/update-metadata.sh <existing-manifest|-> <release-manifest> <output>}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

command -v jq >/dev/null 2>&1 || { echo "error: jq is not installed." >&2; exit 1; }
[[ -f "$incoming" ]] || { echo "error: release manifest not found: $incoming" >&2; exit 1; }

fail() { echo "error: $*" >&2; exit 1; }

"$root/build/verify-manifest.sh" "$incoming" >/dev/null
[[ "$(jq '.releases | length' "$incoming")" == "1" ]] \
  || fail "release manifest must contain exactly one release"

if [[ "$existing" == "-" ]]; then
  base_json="$(jq -c . "$incoming")"
elif [[ -f "$existing" ]]; then
  "$root/build/verify-manifest.sh" "$existing" >/dev/null
  jq -e '([.releases[].tag] | length) == ([.releases[].tag] | unique | length)' "$existing" >/dev/null \
    || fail "existing metadata feed contains duplicate release tags"
  base_json="$(jq -c . "$existing")"
else
  fail "existing metadata manifest not found: $existing"
fi

output_dir="$(dirname "$output")"
mkdir -p "$output_dir"
output_tmp="$(mktemp "$output_dir/.manifest.XXXXXX")"
cleanup() { rm -f "$output_tmp"; }
trap cleanup EXIT

jq -e --argjson base "$base_json" '
  ($base.id == .id) as $same_id
  | if $same_id then . else error("package id does not match the existing metadata feed") end
' "$incoming" >/dev/null 2>&1 \
  || fail "package id does not match the existing metadata feed"

jq -n \
  --argjson base "$base_json" \
  --slurpfile release "$incoming" \
  '$base
   | .repository_url = $release[0].repository_url
   | .homepage_url = $release[0].homepage_url
   | .releases = ((.releases // [])
       | map(select(.tag != $release[0].releases[0].tag))
       + [$release[0].releases[0]])' \
  > "$output_tmp"

"$root/build/verify-manifest.sh" "$output_tmp" >/dev/null

release_tag="$(jq -r '.releases[0].tag' "$incoming")"
jq -e --arg tag "$release_tag" '[.releases[] | select(.tag == $tag)] | length == 1' "$output_tmp" >/dev/null \
  || fail "merged metadata feed does not contain exactly one entry for $release_tag"

mv "$output_tmp" "$output"
trap - EXIT
printf 'Updated metadata feed: %s (%s release(s))\n' "$output" "$(jq '.releases | length' "$output")"
