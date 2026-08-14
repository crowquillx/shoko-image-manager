# Releases

This repository ships the plugin to Shoko through GitHub Releases. A tagged
push builds a portable (`any`) plugin archive, verifies it, computes its
SHA-256, and creates a GitHub Release with the archive, checksum, and
per-release manifest. The same workflow also updates a persistent `metadata`
branch with a Shoko-compatible `manifest.json` feed. GitHub raw content serves
this file as `text/plain`,
which Shoko accepts.

## Tagging a release

Releases are cut by pushing a tag:

- Stable: `vMAJOR.MINOR.PATCH`, for example `v1.0.0`
- Dev: `vMAJOR.MINOR.PATCH-dev.N`, for example `v1.1.0-dev.1`

```fish
git tag -a v1.0.0 -m "Shoko Image Planner 1.0.0"
git push origin v1.0.0
```

Pushing the tag runs the `Release` workflow. The assembly and file versions use four numeric parts; the package and
informational versions keep the full tag version. Dev tags keep
the `-dev.N` suffix and produce a prerelease (`channel: Dev`) GitHub Release.
The archive is named
`Shoko.ImagePlanner-<version>-any.zip`. Tag names that do not match
`vMAJOR.MINOR.PATCH` or `vMAJOR.MINOR.PATCH-dev.N` fail the workflow.

## What the release workflow does

1. Runs the same restore (locked mode), build, test, and format gate as CI.
2. Publishes `src/Shoko.ImagePlanner.csproj` (Release, `--no-self-contained`).
3. Stamps the project, package, assembly, file, and informational versions
   from the tag. Stable versions use a numeric assembly version; Dev versions
   keep `-dev.N` in package and informational metadata.
4. Verifies the published deps project version and real DLL metadata before
   packaging. Fails if any version is stale.
5. Fails if `Shoko.Abstractions.dll` is present in the publish output.
6. Packages a flat zip of the publish output. Shoko installs plugins from zip
   archives, so the release artifact is a zip, not a tarball.
7. Computes the SHA-256 of the zip.
8. Generates `manifest.json`, a Shoko repository manifest, with:
   - `repository_url` and `homepage_url` derived from `github.repository`
     (no hardcoded owner, so forks work),
   - `version` and `channel` from the tag,
   - `source_revision` set to the tagged commit,
   - `archives` with `runtime: "any"`, the `abstraction` ABI version
     (from the referenced `Shoko.Abstractions` assembly version), the release
     asset URL, and the `sha256:` checksum.
9. Verifies the archive, deps project version, and real DLL metadata with
   `build/verify-package.sh`.
10. Creates (or updates) the GitHub Release, uploads the zip, the
   `<zip>.sha256` sidecar, and `manifest.json`, and writes release notes
   generated from commits since the previous tag.
11. Validates the release manifest and merges it into `metadata/manifest.json`.
   Existing release entries remain in publication order. A retry for the same
   tag replaces that tag entry instead of adding a duplicate. Stable and Dev
   entries are both kept so the feed preserves release history.
12. Updates `metadata` with a lease-protected push. Release runs are serialized,
    the branch is not a workflow trigger, and a remote race stops the push
    instead of overwriting the feed.

The same zip, checksum, and manifest are also uploaded as a workflow artifact
(retained 30 days) in case a release needs to be rebuilt manually.

## Adding the repository to Shoko

In Shoko, open the plugin repository manager (Settings → Plugins →
Repositories), add a repository with this stable manifest URL:

```
https://raw.githubusercontent.com/crowquillx/shoko-image-manager/metadata/manifest.json
```

Sync the repository and install *Shoko Image Planner* from the package list.
The `raw.githubusercontent.com` URL is required. The GitHub Releases asset URL
returns `application/octet-stream`, which Shoko does not accept for a metadata
feed. The raw URL returns `text/plain`.

The metadata branch keeps the release entries. The archive URL inside each
entry is pinned to its exact release tag, so Shoko downloads the matching
archive. Shoko uses the `channel` field when it evaluates Stable and Dev
releases, and verifies the archive against the `sha256:` checksum.

## Local packaging

From the repository root with a .NET 10 SDK:

```fish
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
bash build/package.sh Release
bash build/verify-package.sh artifacts/Shoko.ImagePlanner-1.0.0-any.zip artifacts/manifest.json
```

`build/package.sh` writes `artifacts/publish/`, the zip, the `.sha256`
sidecar, and `manifest.json`. It stamps the selected tag into the publish
output and runs the same version checks used by release verification. It
accepts environment overrides
(`REPOSITORY_URL`, `HOMEPAGE_URL`, `TAG`, `SOURCE_REVISION`, `RELEASED_AT`,
`RELEASE_NOTES_FILE`) so a local run can mirror a CI run exactly.

## Bootstrap the metadata branch

The first metadata branch can use the existing v1.0.0 release asset. This does
not rebuild the plugin. Run these commands from a clean checkout. They download
and validate the existing manifest, create a local orphan worktree, and use an
empty lease to prevent overwriting a branch created by another operator.

```fish
set worktree (mktemp -d)
set manifest "$worktree/manifest.json"
curl --fail --location --silent --show-error \
  https://github.com/crowquillx/shoko-image-manager/releases/download/v1.0.0/manifest.json \
  --output $manifest
bash build/verify-manifest.sh $manifest

git fetch origin main
git worktree add --detach "$worktree/source" origin/main
git -C "$worktree/source" switch --orphan metadata-bootstrap
git -C "$worktree/source" clean -fdx
mv $manifest "$worktree/source/manifest.json"
git -C "$worktree/source" add manifest.json
git -C "$worktree/source" -c user.name='metadata bootstrap' \
  -c user.email='metadata-bootstrap@localhost' commit \
  -m 'chore: bootstrap metadata feed from v1.0.0'
git -C "$worktree/source" push \
  --force-with-lease=refs/heads/metadata: origin HEAD:refs/heads/metadata
git worktree remove --force "$worktree/source"
rm -rf $worktree
```

After this bootstrap, the next tagged release updates the branch and keeps the
v1.0.0 entry. Do not use `git push --force` for this branch.

## Repository manifest format

The generated `manifest.json` follows the Shoko server package manifest schema
(`RemotePackageManifestInfo`): `id`, `name`, `overview`, `authors`,
`repository_url`, `homepage_url`, `tags`, and `releases[]` with `version`,
`tag`, `source_revision`, `released_at`, `channel`, `release_notes`, and
`archives[]` (`runtime`, `abstraction`, `url`, `checksum`). The checked-in
`plugin-manifest.json` uses the same schema with an empty `releases` list; the
release workflow fills in the release entry. `build/verify-manifest.sh`
validates both, and `tests/ManifestTests.cs` keeps the checked-in manifest in
sync with the assembly metadata.

## Notes for forks

The stable installation URL above points to the canonical repository. The
workflows never hardcode the repository owner. `repository_url`, `homepage_url`,
and the archive URL are all derived from `github.repository`, so a fork produces
a manifest and release assets that point at the fork. A fork must use its own
raw metadata URL, for example
`https://raw.githubusercontent.com/OWNER/REPOSITORY/metadata/manifest.json`.
