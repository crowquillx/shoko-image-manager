#!/usr/bin/env bash
# Validate a Shoko package manifest (plugin-manifest.json or a generated
# manifest.json) against the schema Shoko.Server expects
# (RemotePackageManifestInfo / RemotePackageReleaseInfo / RemotePackageArchiveInfo).
#
# repository_url, homepage_url, and release entries are optional (an empty
# releases list is valid for the checked-in source manifest); when present
# they are validated strictly.
#
# Usage:
#   build/verify-manifest.sh <manifest.json>
set -euo pipefail

manifest="${1:?usage: build/verify-manifest.sh <manifest.json>}"

command -v jq >/dev/null 2>&1 || { echo "error: jq is not installed." >&2; exit 1; }
[[ -f "$manifest" ]] || { echo "error: manifest not found: $manifest" >&2; exit 1; }

fail() { echo "error: $*" >&2; exit 1; }

jq -e . "$manifest" >/dev/null 2>&1 || fail "not valid JSON"

type="$(jq -r '.type // "package"' "$manifest")"
[[ "$type" == "package" ]] || fail "type must be \"package\" (got \"$type\")"

jq -e '.id | test("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")' "$manifest" >/dev/null \
  || fail "id must be a UUID"
jq -e '.name | type == "string" and length > 0' "$manifest" >/dev/null \
  || fail "name must be a non-empty string"
jq -e '.overview | type == "string"' "$manifest" >/dev/null \
  || fail "overview must be a string"
jq -e '.authors | type == "string" and length > 0' "$manifest" >/dev/null \
  || fail "authors must be a non-empty string"
jq -e '.tags | type == "array"' "$manifest" >/dev/null \
  || fail "tags must be an array"
jq -e '.releases | type == "array"' "$manifest" >/dev/null \
  || fail "releases must be an array"

if jq -e '.repository_url != null' "$manifest" >/dev/null 2>&1; then
  jq -e '.repository_url | test("^https?://")' "$manifest" >/dev/null \
    || fail "repository_url must be an http(s) URL"
fi
if jq -e '.homepage_url != null' "$manifest" >/dev/null 2>&1; then
  jq -e '.homepage_url | test("^https?://")' "$manifest" >/dev/null \
    || fail "homepage_url must be an http(s) URL"
fi

release_count="$(jq '.releases | length' "$manifest")"
if [[ "$release_count" -gt 0 ]]; then
  jq -e '
    .releases[] |
      (.version | test("^[0-9]+\\.[0-9]+\\.[0-9]+(-dev\\.[0-9]+)?$")) and
      (.tag | type == "string" and length > 0) and
      (.channel == "Stable" or .channel == "Dev" or .channel == "Debug") and
      (.released_at | test("^[0-9]{4}-[0-9]{2}-[0-9]{2}T")) and
      (.archives | type == "array" and length >= 1) and
      ([.archives[] |
        (.runtime | type == "string" and length > 0) and
        (.abstraction | test("^[0-9]+\\.[0-9]+\\.[0-9]+$")) and
        (.url | test("^https?://")) and
        (.checksum | test("^(sha256:|sha1:|md5:)?[0-9a-fA-F]{32,128}$"))] | all)
  ' "$manifest" >/dev/null \
    || fail "one or more release entries are invalid"
fi

echo "OK: $manifest is a valid Shoko package manifest ($release_count release(s))"
