#!/usr/bin/env bash
# Exercise metadata feed creation, history preservation, and retry replacement.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

make_release() {
  local output="$1" tag="$2" version="$3" channel="$4" prefix="$5"
  local checksum
  printf -v checksum '%*s' 64 ''
  checksum="${checksum// /$prefix}"
  jq -n \
    --slurpfile base "$root/plugin-manifest.json" \
    --arg tag "$tag" \
    --arg version "$version" \
    --arg channel "$channel" \
    --arg checksum "$checksum" \
    '$base[0]
     | .repository_url = "https://github.com/crowquillx/shoko-image-manager"
     | .homepage_url = .repository_url
     | .releases = [{
         version: $version,
         tag: $tag,
         source_revision: ("a" * 40),
         released_at: "2026-01-01T00:00:00Z",
         channel: $channel,
         release_notes: null,
         archives: [{
           runtime: "any",
           abstraction: "6.0.0",
           url: ("https://github.com/crowquillx/shoko-image-manager/releases/download/" + $tag + "/plugin.zip"),
           checksum: ("sha256:" + $checksum)
         }]
       }]' > "$output"
}

make_release "$tmp/stable.json" v1.0.0 1.0.0 Stable 1
make_release "$tmp/dev.json" v1.1.0-dev.1 1.1.0-dev.1 Dev 2
make_release "$tmp/dev-retry.json" v1.1.0-dev.1 1.1.0-dev.1 Dev 3

bash "$root/build/update-metadata.sh" - "$tmp/stable.json" "$tmp/feed.json"
bash "$root/build/update-metadata.sh" "$tmp/feed.json" "$tmp/dev.json" "$tmp/feed.next.json"

test "$(jq '.releases | length' "$tmp/feed.next.json")" = 2
test "$(jq -r '.releases[0].tag' "$tmp/feed.next.json")" = v1.0.0
test "$(jq -r '.releases[1].tag' "$tmp/feed.next.json")" = v1.1.0-dev.1
test "$(jq -r '.releases[1].version' "$tmp/feed.next.json")" = 1.1.0-dev.1
test "$(jq -r '.releases[1].channel' "$tmp/feed.next.json")" = Dev

bash "$root/build/update-metadata.sh" "$tmp/feed.next.json" "$tmp/dev-retry.json" "$tmp/feed.retry.json"
test "$(jq '.releases | length' "$tmp/feed.retry.json")" = 2
expected_checksum=""
printf -v expected_checksum '%*s' 64 ''
expected_checksum="${expected_checksum// /3}"
test "$(jq -r '.releases[1].archives[0].checksum' "$tmp/feed.retry.json")" = "sha256:$expected_checksum"

jq '.id = "00000000-0000-0000-0000-000000000000"' "$tmp/dev.json" > "$tmp/mismatch.json"
if bash "$root/build/update-metadata.sh" "$tmp/feed.retry.json" "$tmp/mismatch.json" "$tmp/should-not-exist.json" >/dev/null 2>&1; then
  echo "error: mismatched package id was accepted" >&2
  exit 1
fi

echo "OK: metadata feed simulation passed"
