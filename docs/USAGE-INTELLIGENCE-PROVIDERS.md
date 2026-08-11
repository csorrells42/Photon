# Usage Intelligence Provider Matrix

**Verified:** 2026-08-09  
**Scope:** official documentation plus the implemented OpenRouter protocol-v1 collector. Every collection call runs in the trusted desktop host or a backend, with an opaque `credentialRef` crossing the renderer boundary.

## Decision rules

- `ready` means an official, programmatic read path exists for the applicable API organization and can be implemented by a trusted host. It does **not** mean Hermes has implemented or enabled it.
- `partial` means useful official data exists but it is incomplete, delayed, setup-dependent, or has no dedicated API for all desired signals.
- `manual-only` means an official UI exists but no supported programmatic read path was confirmed for the requested signal.
- `unsupported` means no supported programmatic path is assumed. Hermes must not scrape a private dashboard or automate a consumer account page.
- A browser must not receive admin, management, OAuth, service-account, or API credentials. The lack of a documented CORS guarantee is treated as unsuitable for renderer collection even before considering credential exposure.

## OpenAI API

- **Classification:** `ready` for organization API usage and cost reporting.
- **Official surfaces:** [Usage API reference](https://platform.openai.com/docs/api-reference/usage) lists organization usage endpoints including `GET /v1/organization/usage/completions` and other usage dimensions, plus `GET /v1/organization/costs`. The cost endpoint returns daily buckets; the usage endpoint exposes requests and input/output/cached token fields, with optional grouping such as project, model, and user.
- **Authentication and access:** the official cost example uses an OpenAI Admin API key. A host collector should require a least-privilege organization administrator-approved credential reference and paginate results. Standard project request keys must not be assumed to grant organization reporting access.
- **Useful metrics / delay:** daily cost, requests, input/output/cached tokens, model/project attribution where supported. Treat daily aggregation as delayed relative to individual request responses; show provider freshness from the host response.
- **Consumer subscription usage:** no supported public API endpoint for a person's ChatGPT subscription allowance was identified in the official API reference. It is a different provider definition below.
- **CORS / browser:** no renderer suitability is granted by the API reference. An Admin API key would be exposed in a browser request, so collection belongs only in the host/backend.

## ChatGPT subscription

- **Classification:** `unsupported` for programmatic subscription-usage collection.
- **Official surfaces:** the [OpenAI usage reference](https://platform.openai.com/docs/api-reference/usage) documents organization API usage and costs, not ChatGPT consumer subscription allowance, plan usage, or reset data.
- **Authentication and access:** no supported machine-readable consumer subscription credential or role was identified from the official developer documentation reviewed on the verification date.
- **Useful metrics / delay:** none that Hermes can safely collect programmatically. The dashboard should show `unavailable` rather than a calculated API-equivalent spend or guessed reset date.
- **Consumer subscription usage:** not programmatically available through a supported official API in this research scope.
- **CORS / browser:** private account-page scraping and browser automation are prohibited. No live adapter is planned.

## OpenRouter

- **Classification:** `ready` for current-key usage and limits; not a complete request/token time-series feed.
- **Implemented official surface:** [Get current API key](https://openrouter.ai/docs/api/api-reference/api-keys/get-current-api-key) documents `GET /api/v1/key`, returning the authenticated key's total, daily, weekly, and monthly usage plus optional limit, remaining limit, and reset cadence. Hermes Workbench protocol v1 calls only this fixed HTTPS endpoint from the C# host.
- **Authentication and access:** the endpoint accepts the current OpenRouter API key as a bearer token. The key is resolved from Windows Credential Manager inside the desktop host and is never returned to React.
- **Useful metrics / delay:** Workbench displays reported weekly usage when present (otherwise total usage), optional remaining key limit, reset cadence, and the daily/weekly/monthly values in the provider status. It deliberately leaves request count, token totals, and trend points unavailable because this endpoint does not supply them.
- **Broader account credits:** [Get remaining credits](https://openrouter.ai/docs/api/api-reference/credits/get-remaining-credits) documents `GET /api/v1/credits`, but it requires a management key. That optional account-wide collector is not implemented by protocol v1.
- **Consumer subscription usage:** not applicable; this is an API credit surface, not a consumer subscription usage API.
- **CORS / browser:** no browser CORS contract is relied upon. A bearer management key cannot be sent to the renderer, so host/backend collection is required.

## Google Cloud / Google APIs

- **Classification:** `partial`.
- **Official surfaces:** [Cloud Billing Budget API reference](https://cloud.google.com/billing/docs/reference/budget/rest) supports read operations such as `GET /v1/billingAccounts/{billingAccount}/budgets` for budget configuration. [Cloud Billing export to BigQuery](https://cloud.google.com/billing/docs/how-to/export-data-bigquery) supplies standard and detailed usage-cost exports with cost, usage, credits, SKU, service, project, and currency data for host-side queries.
- **Authentication and access:** Google Cloud uses IAM and OAuth/service identity. The [Budget API access-control guide](https://cloud.google.com/billing/docs/how-to/budget-api-access-control) documents `billing.budgets.get`/`billing.budgets.list` and roles such as `roles/billing.viewer`; a BigQuery reader also needs dataset/query permissions, commonly [BigQuery Data Viewer](https://cloud.google.com/bigquery/docs/access-control) plus an approved query-job role. Prefer workload identity or service-account impersonation; never put OAuth refresh tokens or service-account material in the renderer.
- **Useful metrics / delay:** budgets and their periods are readable. Detailed actual costs require a configured export and should be marked delayed: Google states exports update throughout the day and documented Gemini billing can take a day or more to reach Cloud Billing. Use the export's observed newest usage timestamp as freshness, and label estimates only when the host actually derives them.
- **Consumer subscription usage:** not applicable; this is a cloud billing-account surface.
- **CORS / browser:** OAuth/IAM and BigQuery access are trusted-host/backend work. No browser collection is suitable because it would expose privileged tokens and billing scope.

## Google AI Studio / Gemini API

- **Classification:** `partial`.
- **Official surfaces:** [Gemini API billing](https://ai.google.dev/gemini-api/docs/billing) documents AI Studio's Billing and Usage dashboards, prepay balance behavior, project and billing-account spend caps, and the linked Cloud Billing reporting path. [Rate limits](https://ai.google.dev/gemini-api/docs/rate-limits) documents quota categories. The research did not identify an official public endpoint that returns a Gemini project's AI Studio dashboard usage, prepay balance, or spend-cap consumption directly to a third-party client.
- **Authentication and access:** Gemini API keys are project-scoped for billing and quota; Cloud Billing data follows Google IAM/OAuth as above. A future host adapter may use an authorized Cloud Billing export/budget source where the account owner permits it, but must not treat a Gemini API key as a general billing-report credential.
- **Useful metrics / delay:** AI Studio surfaces usage tier, quota, spend-cap, and billing status in its official UI. The billing guide states credit usage is typically processed within minutes, total-cost graphs can take up to 24 hours, and Cloud Billing can lag more than 24 hours. Represent these as manual/unavailable or delayed, never as live precision when the source cannot substantiate it.
- **Consumer subscription usage:** no supported consumer-style Gemini/AI Studio subscription-allowance API was identified; API billing is distinct from an interactive dashboard.
- **CORS / browser:** no documented CORS guarantee is relied on. Keys/tokens and linked billing context must remain in the host/backend.

## Anthropic API / Claude

- **Classification:** `ready` for eligible Claude Platform organizations; `manual-only` or unavailable for excluded account types.
- **Official surfaces:** [Usage and Cost API](https://platform.claude.com/docs/en/manage-claude/usage-cost-api) documents `GET /v1/organizations/usage_report/messages` and `GET /v1/organizations/cost_report`. Usage supports minute, hourly, and daily buckets with model/workspace/service-tier filters; cost reports are daily USD service-level breakdowns.
- **Authentication and access:** an **Admin API key** is required and is different from a standard Claude API key. The official documentation states the Admin API is unavailable for individual accounts. Claude Enterprise uses an Analytics API key and separate analytics endpoints; Claude Platform on AWS does not currently have these programmatic Usage and Cost endpoints.
- **Useful metrics / delay:** input, cached-input, cache-creation, output tokens; model/workspace/service-tier attribution; daily USD costs for token, web-search, and code-execution charges. The documentation explicitly excludes Priority Tier costs from the cost endpoint, so a host must surface that limitation and avoid claiming a fully reconciled spend total.
- **Consumer subscription usage:** Claude consumer subscription allowance is not covered by this API reporting surface. Do not scrape Claude account pages.
- **CORS / browser:** no renderer use is suitable. The required Admin/Analytics credential has organization-wide reporting power and must be resolved in the host/backend.

## Safe collector sequence

1. The renderer selects provider and period only; it passes an opaque `credentialRef` to the native host/backend.
2. The host resolves the credential under least privilege, calls one official surface, applies pagination and provider-specific freshness rules, and removes any credential/account identifiers before returning data.
3. The host returns `usage-intelligence/v1` normalized data with `reported`, `estimated`, `delayed`, `unavailable`, or `not-comparable` quality labels.
4. Once a live host collector is present, renderer totals and trend summaries exclude synthetic demo providers. The renderer combines only same-currency comparable live spend, displays remaining budget/credit only per provider, and never stores secrets in browser storage, URLs, logs, or fixtures.
