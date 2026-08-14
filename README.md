# Shoko Image Planner

A local .NET 10 Shoko plugin that assigns unique posters and backdrops to each
series within each top-level Shoko group. A top-level group is the scope that
Jellyfin exposes as a season. Images are not shared between series in that
scope when enough unique candidates exist. Different top-level groups are
planned independently.

## Safety and compatibility

- Targets `net10.0` and uses published `Shoko.Abstractions` `6.0.0-alpha.77`,
  matching ShokoServer commit `d4dff0c4d2bbfb8130b471bc2b42f71beb08282d`.
- Uses only public abstraction interfaces and Microsoft APIs. It does not use
  Shoko.Server types, repositories, NHibernate, `RepoFactory`, or
  `ISystemService.StaticServices`.
- Fanart.tv is resolved only from public TMDB identities: a TMDB show
  `TvdbShowID` or a TMDB movie ID. It never guesses from titles. Candidate
  identity uses exact SHA-256 or image ID equality; the plugin does not claim
  perceptual similarity without a pixel decoder.
- Fanart images are downloaded as bounded, validated bytes and uploaded through
  `IImageManager.UploadImage`. The plugin does not register a Fanart template
  URL. Fanart origin and resource IDs are stored in the plugin state file and
  cross-reference source is `DataSource.FanartTV`.
- API keys are sent only as HTTP headers. They are not logged, returned by the
  API, or written to state.
- Direct series preferences are protected unless the assignment ledger proves
  that the plugin owns the current image, or a force mutation is requested.

## API

The fixed admin-only routes are under
`/api/v3/Plugin/ImagePlanner`. Shoko API-key authentication is required, as is
the `admin` policy. `plan` is read-only by default. `apply` and `reconcile`
require an `Idempotency-Key` header. The versioned OpenAPI contract is in
`docs/openapi.yaml`; clients must send `apiVersion: 1` and should reject an
unknown response `apiVersion`.

- `GET status`, `GET capabilities`, `GET providers`, `GET groups`
- `POST plan`
- `POST apply`
- `POST reconcile`

`plan` is read-only by default. If `ingest: true` is sent, it downloads only
candidates selected by the plan and requires an `Idempotency-Key` header, like
`apply` and `reconcile`. These mutating requests can be safely retried with
the same key. `apply` and `reconcile` use the same selection and assignment
rules; reconcile does not remove or repair other images.
If a group has too few unique candidates, the response marks deterministic
fallback assignments and reports the condition instead of failing.

## Configuration

See [docs/configuration.md](docs/configuration.md). Recurring reconciliation
is disabled by default and has a single-flight lock when enabled.

> The plugin reads its configuration once at startup. All settings are marked
> `RequiresRestart` and changes only take effect after a Shoko restart.

## Build, test, and package

Use a local or Nix-provided .NET 10 SDK. The repository does not install system
packages. From the repository root:

```sh
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
bash build/package.sh Release
bash build/verify-package.sh artifacts/Shoko.ImagePlanner-1.0.0-any.zip artifacts/manifest.json
```

`build/package.sh` publishes the plugin, packages the portable `any` zip,
computes its SHA-256, and writes a Shoko-compatible repository manifest. No
deployment is performed by this repository.

## Releases and installation in Shoko

Releases are cut by pushing `vMAJOR.MINOR.PATCH` tags. See
[docs/releases.md](docs/releases.md) for the full tag workflow, and to add the
plugin repository to Shoko use the metadata feed URL
`https://raw.githubusercontent.com/crowquillx/shoko-image-manager/metadata/manifest.json`.
The release workflow derives repository and archive URLs from `github.repository`,
so a fork uses its own URLs.
