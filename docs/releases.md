# Releases

This repository ships the plugin to Shoko through GitHub Releases. A tagged
push builds a portable (`any`) plugin archive, verifies it, computes its
SHA-256, and creates a GitHub Release with the archive, the checksum, and a
Shoko-compatible repository manifest.

## Tagging a release

Releases are cut by pushing a tag:

- Stable: `vMAJOR.MINOR.PATCH`, for example `v1.0.0`
- Dev: `vMAJOR.MINOR.PATCH-dev.N`, for example `v1.1.0-dev.1`

```fish
git tag -a v1.0.0 -m "Shoko Image Planner 1.0.0"
git push origin v1.0.0
```

Pushing the tag runs the `Release` workflow. The version is taken from the tag
and embedded in the assembly and the manifest; dev tags produce a prerelease
(`channel: Dev`) GitHub Release. The archive is named
`Shoko.ImagePlanner-<version>-any.zip`. Tag names that do not match
`vMAJOR.MINOR.PATCH` or `vMAJOR.MINOR.PATCH-dev.N` fail the workflow.

## What the release workflow does

1. Runs the same restore (locked mode), build, test, and format gate as CI.
2. Publishes `src/Shoko.ImagePlanner.csproj` (Release, `--no-self-contained`).
3. Fails if `Shoko.Abstractions.dll` is present in the publish output.
4. Packages a flat zip of the publish output. Shoko installs plugins from zip
   archives, so the release artifact is a zip, not a tarball.
5. Computes the SHA-256 of the zip.
6. Generates `manifest.json`, a Shoko repository manifest, with:
   - `repository_url` and `homepage_url` derived from `github.repository`
     (no hardcoded owner, so forks work),
   - `version` and `channel` from the tag,
   - `source_revision` set to the tagged commit,
   - `archives` with `runtime: "any"`, the `abstraction` ABI version
     (from the referenced `Shoko.Abstractions` assembly version), the release
     asset URL, and the `sha256:` checksum.
7. Verifies the archive and manifest with `build/verify-package.sh`.
8. Creates (or updates) the GitHub Release, uploads the zip, the
   `<zip>.sha256` sidecar, and `manifest.json`, and writes release notes
   generated from commits since the previous tag.

The same zip, checksum, and manifest are also uploaded as a workflow artifact
(retained 30 days) in case a release needs to be rebuilt manually.

## Adding the repository to Shoko

In Shoko, open the plugin repository manager (Settings → Plugins →
Repositories), add a repository with this stable manifest URL:

```
https://github.com/crowquillx/shoko-image-manager/releases/latest/download/manifest.json
```

Sync the repository and install *Shoko Image Planner* from the package list.

`releases/latest` always resolves to the newest stable release; the archive URL
inside the manifest is pinned to the exact release tag, so Shoko downloads the
matching archive. Dev releases are not picked up by the `latest` URL. Shoko
verifies the archive against the `sha256:` checksum in the manifest before
installing, and against the `<zip>.sha256` sidecar if you download manually.

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
sidecar, and `manifest.json`. It accepts environment overrides
(`REPOSITORY_URL`, `HOMEPAGE_URL`, `TAG`, `SOURCE_REVISION`, `RELEASED_AT`,
`RELEASE_NOTES_FILE`) so a local run can mirror a CI run exactly.

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
a manifest and release assets that point at the fork.
