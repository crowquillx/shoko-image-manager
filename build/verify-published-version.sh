#!/usr/bin/env bash
# Verify the version metadata in a published plugin directory.
#
# Usage:
#   build/verify-published-version.sh <publish-directory> <expected-version>
set -euo pipefail

publish_dir="${1:?usage: build/verify-published-version.sh <publish-directory> <expected-version>}"
expected="${2:?usage: build/verify-published-version.sh <publish-directory> <expected-version>}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

for tool in dotnet jq; do
  command -v "$tool" >/dev/null 2>&1 || { echo "error: $tool is not installed." >&2; exit 1; }
done

fail() { echo "error: $*" >&2; exit 1; }

[[ -d "$publish_dir" ]] || fail "publish directory not found: $publish_dir"
[[ "$expected" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-dev\.[0-9]+)?$ ]] \
  || fail "invalid expected semantic version: $expected"

deps="$publish_dir/Shoko.ImagePlanner.deps.json"
dll="$publish_dir/Shoko.ImagePlanner.dll"
[[ -f "$deps" ]] || fail "published deps file not found: $deps"
[[ -f "$dll" ]] || fail "published plugin assembly not found: $dll"

# A project can occur in both the target graph and library table. Require both
# entries to contain exactly the release version so a stale deps graph fails.
jq -e --arg expected "$expected" '
  ([((.targets // {})[] | keys[] | select(startswith("Shoko.ImagePlanner/")) | split("/")[1])] | unique) == [$expected]
  and ([((.libraries // {}) | keys[] | select(startswith("Shoko.ImagePlanner/")) | split("/")[1])] | unique) == [$expected]
' "$deps" >/dev/null \
  || fail "published deps project version does not match $expected: $deps"

# Read PE metadata and AssemblyInformationalVersionAttribute through .NET,
# rather than searching binary strings. This works on every .NET 10 CI host.
(
  cd "$root"
  dotnet run --file "$root/build/verify-assembly-version.cs" -- "$dll" "$expected"
)
