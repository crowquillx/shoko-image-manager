# Configuration

The plugin registers `ImagePlannerOptions` with Shoko's public configuration
service. Open the **Image Planner** page in the Shoko WebUI plugin pages to
load the configuration and schema. The page discovers the configuration with
`GET /api/v3/Configuration?pluginID=7c5f8f4d-7b1d-4e07-9d4c-5d8bd6f581a2` and
saves it with the official Configuration API. It does not show stored key
values. Leave a key field blank to keep its stored value.

Configure it in Shoko's plugin configuration UI or with the supported
environment variable attributes. The ASP.NET configuration binder is not used.

> **Restart required.** The plugin reads its configuration once, at startup,
> and does not reload it when settings are saved. Every setting below is marked
> `RequiresRestart` in the configuration schema, and changes take effect only
> after a Shoko restart.

Required provider settings:

- `FanartTvApiKey`: Fanart.tv API key. It is sent only in the request header.
- `FanartTvClientKey`: optional client key. It is sent only in the request header.

Safety settings:

- `RequestTimeoutSeconds`: request timeout, default `20`.
- `MaxJsonResponseBytes`: bounded provider JSON response, default `1048576`.
- `MaxImageBytes`: maximum downloaded image, default `20971520`.
- `PreferredLanguage`: language hint used by the scorer, default `en`.
- `FanartTvPriority`: provider priority, default `10`.

Recurring reconciliation is disabled by default:

- `RecurringReconciliationEnabled`: set to `true` to enable it.
- `ReconciliationIntervalMinutes`: interval, default `1440`.
- `IdempotencyReceiptRetentionDays`: receipt retention, default `30`, maximum `365`.

Keys and secret values are not included in status, capabilities, provider
responses, logs, or the plugin state file. Reconciliation applies the same
selection rules as `apply` for the requested groups; it is not a repair or
cleanup operation. The state file is
`<DataPath>/shoko-image-planner-state.json` and contains provider resource IDs,
local image IDs, assignment ownership, and idempotency receipts only.
