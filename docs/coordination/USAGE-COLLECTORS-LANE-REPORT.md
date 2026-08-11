# Usage Intelligence Collectors Lane Report

Date: 2026-08-09  
Lane: packet 2, provider-usage collectors  
Status: complete and ready for Super review  

## Outcome

This lane adds a standalone `net10.0` C# collector library and an offline synthetic smoke executable. It does not modify or reference the desktop host, renderer, package manifests outside the lane, Docker configuration, Monaco workspace, data, or artifacts.

No provider was contacted. No real credential was created, read, logged, serialized, or placed in a browser. Credential resolution is an injected host delegate; the library does not read Windows Credential Manager or any other secret store.

## Files created

Library: `src/Host/HermesUsageCollectors/`

- `HermesUsageCollectors.csproj` — package-free `net10.0` library.
- `Contracts.cs` — immutable provider-neutral profiles, requests, observations, capabilities, provenance, freshness, sanitized errors, and aggregates.
- `Secrets.cs` — injected secret-source boundary and zeroed character-buffer lease.
- `HttpSupport.cs` — fixed-origin HTTPS client, redirect rejection, cancellation/timeout handling, response bounds, strict JSON parsing, and sanitized HTTP errors.
- `CollectorSupport.cs` — profile/bounds validation, credential-resolution sanitization, freshness, and pagination/row guards.
- `OpenAiUsageCollector.cs` — OpenAI organization completion usage and organization costs.
- `AnthropicUsageCollector.cs` — Claude Platform organization message usage and cost reports.
- `GoogleCloudBudgetCollector.cs` — Google Cloud Budget API configuration and threshold observations.
- `GoogleBillingExportAdapter.cs` — narrow host adapter contract plus collector for normalized BigQuery Cloud Billing export cost rows.
- `UnavailableUsageCollector.cs` — explicit unavailable capabilities for unsupported consumer/per-key surfaces.
- `UsageCollectionCoordinator.cs` — safe multi-profile collection, partial-result retention, and compatible aggregation.

Smoke: `src/Host/HermesUsageCollectors.Smoke/`

- `HermesUsageCollectors.Smoke.csproj` — package-free `net10.0` executable referencing only the collector library.
- `SmokeHarness.cs` — injected scripted HTTP handler, safe request-shape capture, assertions, and synthetic billing source.
- `Program.cs` — 20 offline cases covering happy paths and security/error behavior.

Handoff: `docs/coordination/USAGE-COLLECTORS-LANE-REPORT.md` (this file).

Generated `bin/` and `obj/` content is confined to the two assigned project folders.

## Capability matrix

| Provider surface | State | Access required | Collector behavior |
|---|---|---|---|
| OpenAI API organization completion usage | Supported | Organization Admin API key supplied by the trusted host | Calls `GET /v1/organization/usage/completions`; emits provider-reported input tokens, output tokens, and model requests. This is completion usage, not every OpenAI product-specific usage endpoint. |
| OpenAI API organization costs | Supported | Organization Admin API key supplied by the trusted host | Calls `GET /v1/organization/costs`; emits provider-reported actual cost grouped only by currency. |
| ChatGPT consumer subscription usage/limits | Unavailable | Not applicable | Returns an explicit unavailable capability and no zero-valued observation. No consumer API is assumed. |
| Claude Platform organization message usage | Supported | Anthropic Admin API key supplied by the trusted host | Calls `GET /v1/organizations/usage_report/messages`; emits uncached input, cache-write input, cache-read input, output tokens, and server web-search requests. |
| Claude Platform organization costs | Supported | Anthropic Admin API key supplied by the trusted host | Calls `GET /v1/organizations/cost_report`; converts decimal strings in lowest currency units to currency units. Notes post-discount/pre-credit semantics and Priority Tier exclusion. |
| Claude consumer subscription usage/limits | Unavailable | Not applicable | Returns an explicit unavailable capability. No consumer API is assumed. |
| Claude Enterprise Analytics | Not implemented | Analytics API key and applicable enterprise plan | Kept distinct from the Claude Platform Admin API. Super may add a separate collector later if this product surface is required. |
| Google Cloud budget configuration | Supported | Host-supplied OAuth bearer token with `billing.budgets.list` and a billing-account reference | Calls `GET /v1/billingAccounts/{id}/budgets`; emits configured amount and threshold ratios with `ConfiguredThreshold` provenance. Never labels budget configuration as spend. |
| Google Cloud actual spend | Adapter ready / setup required | Host-managed BigQuery billing export query implementation and permissions | `GoogleBillingExportCollector` consumes only normalized cost/credit rows from `IGoogleBillingExportSource`. With no source/opaque target reference, it reports setup required. |
| Gemini API / AI Studio usage by API key | Unavailable | Not applicable | Returns an explicit unavailable capability. It is not conflated with Google Cloud Billing/Budgets. |

The existing OpenRouter collector remains owned by the desktop host and was not changed.

## Data and aggregation semantics

- `ProviderReported`, `ConfiguredThreshold`, `Estimated`, and `Projected` are distinct provenance values.
- Budget amounts and threshold ratios are configuration, never actual cost.
- BigQuery actual cost is normalized as `cost + credits` because Cloud Billing export credits are represented as signed adjustments.
- Aggregation keys include observation kind, metric, unit, currency, exact window, provenance, and qualifier. Overlapping or differently scoped budget configurations therefore cannot be silently summed together.
- Contributing profile counts are distinct profile IDs; contributing providers are returned as a distinct sorted list.
- Successful observations remain available when a sibling capability/profile fails. Result and aggregate partial flags make that state explicit.
- No provider account, workspace, user, project, API-key, organization, billing-account, or raw row identifier is copied into an observation, error, or aggregate.

## HTTP and secret guarantees

- Official origins are compiled into each HTTP collector: `api.openai.com`, `api.anthropic.com`, and `billingbudgets.googleapis.com`, default-port HTTPS only.
- Requests are `GET` with bounded, encoded query values. Redirects are disabled by the default handler and every 3xx response is rejected.
- The caller may inject `HttpMessageHandler` for deterministic testing; production defaults remain redirect-disabled.
- Each request has a bounded timeout plus caller cancellation.
- Response body, error body, page count, logical row count, cursor length, JSON depth, string length, number shape, currency, timestamp, and profile metadata are bounded or validated.
- JSON comments and trailing commas are rejected. The transport byte buffer is zeroed after parsing into independently owned document storage.
- Only an allowlisted short provider error code can be retained. Raw provider bodies/messages, exception text, redirect locations, tokens, credentials, and HTTP request details are never placed in returned errors.
- Credential-source exceptions are converted to the fixed `credential-source-failure` error. The resolver exception is not retained.
- Secrets enter only through `IProviderSecretSource`; `ProviderSecret` clears its character buffer on disposal. The HTTP header API necessarily creates a short-lived managed `string`, which .NET cannot deterministically zero; callers must still provide least-privilege, short-lived credentials where the provider supports them.
- No secrets are returned in contracts. The intended integration keeps collectors behind the desktop host bridge; the browser receives normalized results only.

## BigQuery billing-export boundary

`IGoogleBillingExportSource` is intentionally smaller than a general BigQuery client. The host supplies:

- an opaque, validated target reference (not a project/dataset/table/billing-account ID),
- an exact requested time window,
- an optional opaque page token,
- a maximum row count, and
- cancellation.

It returns only normalized `cost`, `credits`, `currency`, usage window, and optional export timestamp. It must not return SQL, query diagnostics, project IDs, billing-account IDs, labels, SKUs, resources, principals, or raw export rows. Authentication and parameterized SQL remain host-side responsibilities. BigQuery storage/query charges and billing-export latency still apply.

## Official-source research

Only provider documentation was used.

### OpenAI

- [Organization completions usage API](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/completions) — endpoint, Unix-second window, daily limit up to 31 buckets, cursor pagination, token/request fields.
- [Organization costs API](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/costs) — endpoint, daily cost buckets, amounts/currency, and cursor pagination.
- [Admin API keys](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/admin_api_keys) — organization administration key surface. The host must supply an appropriate Admin API key; this library neither creates nor stores one.

### Anthropic

- [Usage and Cost Admin API](https://platform.claude.com/docs/en/manage-claude/usage-cost-api) — Claude Platform Admin API key requirement, message usage dimensions, daily costs, pagination, reported freshness, Priority Tier exclusion, and distinction from Claude Enterprise Analytics.
- [Cost Report reference](https://platform.claude.com/docs/en/api/admin/cost_report) — `/v1/organizations/cost_report`, decimal-string lowest currency units, currency, buckets, and cursor response.

### Google Cloud

- [List budgets](https://docs.cloud.google.com/billing/docs/reference/budget/rest/v1/billingAccounts.budgets/list) — official endpoint, `billing.budgets.list`, OAuth scopes, maximum page size 100, and page token.
- [Budget resource](https://docs.cloud.google.com/billing/docs/reference/budget/rest/v1/billingAccounts.budgets) — configured amount, last-period amount, threshold percent, current/forecast spend basis, and filters.
- [Cloud Billing export overview](https://docs.cloud.google.com/billing/docs/how-to/export-data-bigquery) — billing-export setup, BigQuery costs, backfill/location behavior, and schema-change considerations.
- [Standard usage-cost export schema](https://docs.cloud.google.com/billing/docs/how-to/export-data-bigquery-tables/standard-usage) — cost/credits/currency fields and standard export table shape.

## Verification evidence

Executed from `C:\Users\clsor\Documents\Codex\HermesAgent` on 2026-08-09:

```powershell
dotnet build .\src\Host\HermesUsageCollectors\HermesUsageCollectors.csproj -c Release
```

Result: passed, 0 warnings, 0 errors.

```powershell
dotnet build .\src\Host\HermesUsageCollectors.Smoke\HermesUsageCollectors.Smoke.csproj -c Release
```

Result: passed, 0 warnings, 0 errors.

```powershell
dotnet run --project .\src\Host\HermesUsageCollectors.Smoke\HermesUsageCollectors.Smoke.csproj -c Release --no-build
```

Result: **20 passed, 0 failed, 20 total**.

Smoke coverage:

1. OpenAI request/auth shape, cursor pagination, and normalization.
2. Anthropic request/auth/version shape and normalization.
3. Google Budget request/auth shape and configuration-vs-spend semantics.
4. Google billing-export cost-plus-credit normalization.
5. Missing credential setup state.
6. Credential-source exception sanitization.
7. Unauthorized classification.
8. Forbidden classification.
9. Rate-limit classification.
10. Provider 5xx classification.
11. Malformed JSON rejection.
12. Oversized response rejection.
13. Redirect rejection.
14. Timeout classification.
15. Caller-cancellation classification.
16. Provider-error and credential redaction.
17. Page bound enforcement.
18. Logical row bound enforcement.
19. Partial profile retention and aggregate partial flagging.
20. Explicit unsupported ChatGPT consumer, Claude consumer, and Google AI Studio per-key surfaces.

The checkout root does not contain a `.git` directory, so `git status` evidence was unavailable. Filesystem inspection confirmed all created source and report files are within the three assigned lane paths.

## Super integration seams

Super can integrate after review by changing its owned files; this lane intentionally did not make these edits.

1. Add a `ProjectReference` from `src/Host/HermesDesktop/HermesDesktop.csproj` to `..\HermesUsageCollectors\HermesUsageCollectors.csproj`.
2. In `src/Host/HermesDesktop/MainWindow.xaml.cs`, compose the selected collectors and `UsageCollectionCoordinator`. Adapt the existing desktop credential-vault access into `IProviderSecretSource`; do not move vault reads into the library and do not send credentials through WebView messages.
3. Extend `src/Modules/UsageIntelligence/DesktopUsageBridge.ts` with a versioned request/result operation for provider-neutral collections. Keep the collection call in the trusted host.
4. Map `ProviderCollectionResult`, `UsageObservation`, capability/freshness/provenance, sanitized errors, and aggregates into the renderer DTOs in `src/Modules/UsageIntelligence/DesktopUsageAdapter.ts` and `contracts.ts`.
5. Keep the current desktop-owned OpenRouter collector as its existing surface or adapt it separately; no OpenRouter code is duplicated here.
6. For Google actual spend, implement `IGoogleBillingExportSource` in a trusted host/service with parameterized SQL, least-privilege Google credentials, explicit query byte/cost controls, and no raw-row/identifier propagation.

## Limitations and unverified runtime behavior

- No live credentials, provider accounts, provider permissions, organization plans, network paths, rate limits, or production payloads were exercised. All provider integration behavior remains unverified until Super performs an authorized host-side test.
- OpenAI usage currently covers the organization completions endpoint only. Costs cover the organization cost report. Other OpenAI product-specific usage endpoints are not collected.
- The implemented Anthropic endpoints target Claude Platform organizations using Admin API keys. Claude Enterprise Analytics is a different API/key surface and is not implemented.
- Provider schemas and access policies can change; strict parsing intentionally fails closed and should be updated against official documentation when a schema changes.
- Provider data can lag or be revised. Anthropic currently documents typical availability within minutes; Google Cloud explicitly describes export propagation/backfill behavior, and BigQuery export has no latency SLA suitable for real-time promises.
- BigQuery export enablement, storage, and queries may incur Google Cloud charges. The library includes a safe normalized boundary, not a general-purpose BigQuery implementation.
- Google budgets based on the previous period do not expose a fixed numeric configured amount in the response; those profiles are reported as partial rather than estimated.
- No consumer subscription or per-key API surface is inferred from browser dashboards, private endpoints, or scraped pages.

No commit, publish, package-manifest change, or live provider call was performed.
