#!/usr/bin/env bash
# Package the plugin for Shoko: publish the plugin, fail if it ships
# Shoko.Abstractions.dll, build the portable "any" zip archive, compute its
# SHA-256, and write a Shoko-compatible repository manifest (manifest.json).
#
# Usage:
#   build/package.sh [configuration]        # configuration defaults to Release
#
# Environment overrides (set by the release workflow; defaults keep local runs
# working without a git remote or tag):
#   REPOSITORY_URL      Base repository URL, e.g. https://github.com/owner/repo
#   HOMEPAGE_URL        Homepage URL (defaults to REPOSITORY_URL)
#   TAG                 Release tag, e.g. v1.0.0 or v1.0.1-dev.1
#   SOURCE_REVISION     Full commit SHA the release is built from
#   RELEASED_AT         ISO-8601 release timestamp (defaults to now)
#   RELEASE_NOTES_FILE  Path to a text file whose contents become the release
#                       notes embedded in the manifest
#
# Outputs (all under artifacts/):
#   publish/                                   publish output (plugin + README + manifest)
#   Shoko.ImagePlanner-<version>-any.zip       the portable plugin archive
#   Shoko.ImagePlanner-<version>-any.zip.sha256  SHA-256 sidecar (sha256sum -c compatible)
#   manifest.json                              Shoko repository manifest for this release
set -euo pipefail

configuration="${1:-Release}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
publish_dir="$root/artifacts/publish"
out_dir="$root/artifacts"

for tool in jq zip unzip sha256sum; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "error: required tool '$tool' is not installed." >&2
    exit 1
  fi
done

# --- Resolve version and channel from the tag --------------------------------
tag="${TAG:-}"
if [[ -z "$tag" ]]; then
  tag="$(git -C "$root" describe --tags --abbrev=0 --match 'v*' 2>/dev/null || true)"
fi
if [[ -z "$tag" ]]; then
  echo "note: no release tag found; packaging as v1.0.0 (Stable). Set TAG for a real release." >&2
  tag="v1.0.0"
fi
if [[ ! "$tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-dev\.[0-9]+)?$ ]]; then
  echo "error: invalid release tag '$tag'. Expected vMAJOR.MINOR.PATCH or vMAJOR.MINOR.PATCH-dev.N." >&2
  exit 1
fi
version="${tag#v}"
# Manifest version is always numeric (major.minor.patch). Dev tags carry the
# -dev.N suffix in the archive name and tag, but Shoko matches installed
# plugin versions against the manifest version, and its own build tooling
# writes dev releases as numeric version + channel "Dev".
manifest_version="${version%-dev.*}"
if [[ "$tag" == *-dev.* ]]; then
  channel="Dev"
else
  channel="Stable"
fi

# --- Resolve repository URLs --------------------------------------------------
# Never hardcode the owner: prefer the caller, then the git remote, then the
# csproj default. CI always passes REPOSITORY_URL from github.repository so a
# fork produces a manifest and asset URLs that point at the fork.
repository_url="${REPOSITORY_URL:-}"
if [[ -z "$repository_url" ]]; then
  remote="$(git -C "$root" remote get-url origin 2>/dev/null || true)"
  case "$remote" in
    https://github.com/*) repository_url="https://github.com/${remote#https://github.com/}" ;;
    git@github.com:*) repository_url="https://github.com/${remote#git@github.com:}" ;;
    *)
      repository_url="$(sed -n 's/.*<RepositoryUrl[^>]*>\(.*\)<\/RepositoryUrl>.*/\1/p' "$root/src/Shoko.ImagePlanner.csproj" | head -n1)"
      ;;
  esac
  repository_url="${repository_url%.git}"
fi
homepage_url="${HOMEPAGE_URL:-$repository_url}"

source_revision="${SOURCE_REVISION:-}"
if [[ -z "$source_revision" ]]; then
  source_revision="$(git -C "$root" rev-parse HEAD 2>/dev/null || true)"
fi
# Accept only real commit SHAs (40-64 hex chars); unborn branches report "HEAD".
if [[ ! "$source_revision" =~ ^[0-9a-f]{40,64}$ ]]; then
  source_revision=""
fi
released_at="${RELEASED_AT:-$(date -u +%Y-%m-%dT%H:%M:%SZ)}"

# --- Publish ------------------------------------------------------------------
rm -rf "$publish_dir"
msbuild_args=()
if [[ -n "$repository_url" ]]; then
  msbuild_args+=(-p:RepositoryUrl="$repository_url" -p:PackageProjectUrl="$homepage_url")
fi
dotnet publish "$root/src/Shoko.ImagePlanner.csproj" -c "$configuration" --no-self-contained -o "$publish_dir" "${msbuild_args[@]}"

if [[ -e "$publish_dir/Shoko.Abstractions.dll" ]]; then
  echo "error: package must not contain Shoko.Abstractions.dll. The csproj uses ExcludeAssets=runtime on the Shoko.Abstractions reference." >&2
  exit 1
fi

cp "$root/plugin-manifest.json" "$publish_dir/"
cp "$root/README.md" "$publish_dir/"

# --- ABI (abstraction) version --------------------------------------------------
# Shoko computes the ABI version from the referenced Shoko.Abstractions
# assembly version (major.minor.build). Read it from the restore graph, which
# lists the package even with ExcludeAssets=runtime.
assets_file="$root/src/obj/project.assets.json"
abstraction="$(jq -r '.libraries | keys[] | select(startswith("Shoko.Abstractions/"))' "$assets_file" 2>/dev/null | head -n1 | sed 's#^Shoko.Abstractions/##; s/-[^-]*$//')"
if [[ -z "$abstraction" || ! "$abstraction" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "error: could not determine Shoko.Abstractions ABI version from $assets_file" >&2
  exit 1
fi

# --- Zip archive + checksum -----------------------------------------------------
zip_name="Shoko.ImagePlanner-${version}-any.zip"
zip_path="$out_dir/$zip_name"
rm -f "$zip_path" "$zip_path.sha256"
mkdir -p "$out_dir"
(cd "$publish_dir" && zip -qr "$zip_path" .)
checksum="$(sha256sum "$zip_path" | awk '{print $1}')"
printf '%s  %s\n' "$checksum" "$zip_name" > "$zip_path.sha256"

# --- Shoko repository manifest ---------------------------------------------------
archive_url="$repository_url/releases/download/$tag/$zip_name"
notes_json='null'
if [[ -n "${RELEASE_NOTES_FILE:-}" && -r "$RELEASE_NOTES_FILE" ]]; then
  notes_json="$(jq -Rs . < "$RELEASE_NOTES_FILE")"
fi

manifest_path="$out_dir/manifest.json"
jq -n \
  --slurpfile base "$root/plugin-manifest.json" \
  --arg repository_url "$repository_url" \
  --arg homepage_url "$homepage_url" \
  --arg version "$manifest_version" \
  --arg tag "$tag" \
  --arg source_revision "$source_revision" \
  --arg released_at "$released_at" \
  --arg channel "$channel" \
  --argjson release_notes "$notes_json" \
  --arg runtime "any" \
  --arg abstraction "$abstraction" \
  --arg archive_url "$archive_url" \
  --arg checksum "sha256:$checksum" \
  '$base[0]
   | .type = "package"
   | .repository_url = $repository_url
   | .homepage_url = $homepage_url
   | .releases = [{
       version: $version,
       tag: $tag,
       source_revision: $source_revision,
       released_at: $released_at,
       channel: $channel,
       release_notes: $release_notes,
       archives: [{
         runtime: $runtime,
         abstraction: $abstraction,
         url: $archive_url,
         checksum: $checksum,
       }],
     }]' \
  > "$manifest_path"

# --- Verify ----------------------------------------------------------------------
"$root/build/verify-package.sh" "$zip_path" "$manifest_path"

echo
echo "Packaged: $zip_path"
echo "  version:    $manifest_version ($channel, tag $tag)"
echo "  abstraction: $abstraction"
echo "  sha256:     $checksum"
echo "  manifest:   $manifest_path"
