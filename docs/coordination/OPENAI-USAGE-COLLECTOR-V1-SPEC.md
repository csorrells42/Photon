# OpenAI Usage Collector v1 specification

**Status:** implementation-ready specification only. No collector code, credentials, desktop-host changes, or runtime calls were made.

**Verified:** 2026-08-09 against the current local Workbench source and the official OpenAI API references linked below.

## 1. Purpose and excluded scope

Build a trusted-native-host collector for **OpenAI API organization and project telemetry**. It must obtain organization costs and completions usage from OpenAI's documented Administration API surfaces, normalize a small safe aggregate, and expose it to Hermes Usage Intelligence without allowing any credential material, raw response, project ID, user ID, or API-key ID into React.

The collector has two source calls:

1. Organization cost buckets are the only source for displayed API spend.
2. Organization completions buckets provide request, input-token, output-token, and cached-input-token totals.

### Explicit exclusions

- **ChatGPT consumer subscription allowance, plan quota, reset time, and personal usage are not represented by these API organization endpoints.** They must remain `unsupported`/unavailable in Hermes. Do not scrape `chatgpt.com`, a private dashboard, or any other consumer account surface.
- No request-level content, prompts, responses, file data, user identities, API-key identities, model-usage drill-down, or admin-management actions.
- No inferred spend from token counts. OpenAI documents that granular usage and costs may not reconcile perfectly; the cost endpoint is the financial source of record. [Usage API overview](https://platform.openai.com/docs/api-reference/usage)
- No collection in the browser, no CORS dependency, no renderer-side provider request, and no credential stored in web storage, URL, React state, source code, test fixture, log, or protocol frame.
- v1 does not call the other organization usage dimensions (embeddings, images, audio, moderation, file search, vector stores, web search, or Code Interpreter). Their units do not fit the current `TokenMetric` contract and must not be silently folded into token totals.

## 2. Local contract alignment

The requested historical paths `src\Modules\UsageIntelligence\types.ts` and type name `UsageProviderSnapshot` do **not** exist in the current authoritative tree. The current renderer contract is:

- `src\Modules\UsageIntelligence\contracts.ts`: `usageIntelligenceContractVersion = 'usage-intelligence/v1'`, `ProviderUsage`, `UsageDashboardSnapshot`, `UsageTrendPoint`, `MoneyMetric`, and `TokenMetric`.
- `src\Modules\UsageIntelligence\DesktopUsageBridge.ts`: native `usage.collect` frames, integer protocol version `1`, and the renderer-side opaque `credentialId` request field.
- `src\Modules\UsageIntelligence\DesktopUsageAdapter.ts`: inserts a host-collected OpenRouter `ProviderUsage` into the provider-neutral snapshot.
- `src\Host\HermesDesktop\OpenRouterUsageCollector.cs` and `MainWindow.xaml.cs`: the security precedent—fixed HTTPS endpoint, redirects disabled, bounded streaming reads, typed/sanitized failures, host-only vault resolution, and a WebView bridge.

This specification maps to `ProviderUsage` and `UsageDashboardSnapshot`; a later implementation should not invent a parallel `UsageProviderSnapshot` type.

## 3. Official OpenAI API surface

| Purpose | Method and production URI | API version and official reference |
|---|---|---|
| Completions usage | `GET https://api.openai.com/v1/organization/usage/completions` | `/v1`; [Completions usage reference](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/completions) |
| Organization costs | `GET https://api.openai.com/v1/organization/costs` | `/v1`; [Costs reference](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/costs) |
| Administration credential model | No collector call; credential provisioning is organization administration | [Administration overview](https://developers.openai.com/api/reference/administration/overview) and [Admin API keys reference](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/admin_api_keys) |

The official references use the `/v1` path and do not specify a separate version header for these two operations. Send `Authorization: Bearer` with the vault-resolved Admin API credential, `Accept: application/json`, and no user-controlled base URI or redirect destination.

### Authentication and required role

The host must require an **OpenAI Organization Admin API key**, stored only in Windows Credential Manager and resolved only by the native host. The official endpoint examples identify an Admin API key; the Administration API is accessed with an Admin API key, not with a normal inference key. [Administration overview](https://developers.openai.com/api/reference/administration/overview)

OpenAI's current official help guidance says Organization Owners create Admin API keys and that those keys authenticate Admin API requests with the necessary management scopes. [Admin and Audit Logs API for the API Platform](https://help.openai.com/en/articles/9687866-admin-and-audit-logs-api-for-the-api-platform.webm) Therefore:

- Minimum human authority: **Organization Owner** to create or authorize the reporting credential.
- Minimum credential class: **Admin API key** that OpenAI permits to call the Usage and Costs endpoints.
- Ordinary project, service-account, user, or inference API keys are not accepted as a substitute by this design. Treat a 401/403 from the live endpoints as evidence that the stored credential is wrong or lacks the needed authority; do not probe additional admin endpoints to discover permissions.
- Prefer the narrowest Admin API credential/scopes OpenAI actually offers for these read operations, with an explicit owner approval, expiry, rotation, and revocation plan. The precise available restricted scope name for Usage/Costs is a live account-owner question; do not assume one from unrelated administration endpoints.

## 4. Query contract and time normalization

### v1 request plan

Both calls use the exact same normalized half-open interval `[start_time, end_time)`, queried in UTC.

| Endpoint | Required and fixed parameters | Deliberately omitted |
|---|---|---|
| `/v1/organization/usage/completions` | `start_time`, `end_time`, `bucket_width=1d`, `limit=31`, `group_by=project_id`, `group_by=model` | `api_key_ids`, `user_ids`, `api_key_ids` grouping, `user_id` grouping, `batch`, `service_tier`, and model/project filters. v1 reports organization-wide totals while using project/model grouping only for host-side aggregation and validation. |
| `/v1/organization/costs` | `start_time`, `end_time`, `bucket_width=1d`, `limit=31`, `group_by=project_id` | `api_key_ids`, `project_ids`, `line_item`, and `api_key_id` grouping. v1 reports an organization total, not a billing-line-item or key-level ledger. |

The official Completions endpoint supports `bucket_width` values `1m`, `1h`, and `1d`; its per-page bucket limits are 31 daily, 168 hourly, and 1,440 minute buckets. It supports combined grouping by `project_id`, `user_id`, `api_key_id`, `model`, `batch`, and `service_tier`. [Completions usage reference](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/completions)

The official Costs endpoint supports only daily buckets. Its `limit` is 1 through 180 (default 7), and it supports combined grouping by `project_id`, `line_item`, and `api_key_id`. [Costs reference](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/costs)

v1 fixes both endpoints at `limit=31` so pagination and daily trend construction follow one bounded, deterministic page shape. A future collector may raise only the costs limit up to 180 after adding dedicated test coverage; it must not assume that changes the Completions endpoint limit.

### Time boundaries and normalization

1. The renderer sends the selected `UsageCollectionRequest.period.start` and `.end` as ISO-8601 UTC instants, plus its display label. The native host independently parses the two instants; it never accepts a locale-dependent date or local time zone.
2. V1 accepts only timestamps exactly aligned to a whole UTC second and requires `start < end`. It converts each directly to Unix seconds. A timestamp with fractional seconds is rejected as an invalid bridge request rather than rounded into a wider or narrower billing interval.
3. The host sends `start_time` inclusive and `end_time` exclusive, exactly as documented by both OpenAI endpoint references. For the normal dashboard period, generate daily boundaries such as `00:00:00Z` and an end at the following boundary.
4. Store and return canonical RFC 3339 UTC strings with `Z` for the normalized period, bucket start, bucket end, and `collectedAt`. Do not return Unix times to React.
5. Daily buckets are expected to begin and end on UTC boundaries. A bucket outside the requested half-open interval, a non-positive bucket duration, or an invalid timestamp is a malformed upstream response—not a value to clip or silently repair.

### Pagination

Both responses are pages with `data`, `has_more`, and `next_page`. If `has_more` is true, send the received `next_page` value as the next request's `page`; otherwise stop. The official references define `page` as the cursor corresponding to `next_page`. [Completions](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/completions) · [Costs](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/costs)

Stop with a malformed-response failure when `has_more` is true without a nonempty cursor, a cursor repeats, a page count exceeds 128, or a page repeats a conflicting bucket. Never construct a cursor, follow a URI supplied by the response, or resume an older collection using a persisted cursor.

## 5. Response fields and source meaning

### Completions usage fields

For every returned bucket/result pair, validate and aggregate the following documented numeric fields:

| Response field | V1 treatment |
|---|---|
| Bucket `start_time`, `end_time` | Required for daily trend key and freshness boundary; converted to canonical UTC; no raw bucket object crosses React. |
| `num_model_requests` | Non-negative whole number; sum into `ProviderUsage.requests` and each daily `UsageTrendPoint.requests`. |
| `input_tokens` | Non-negative whole number; sum into `ProviderUsage.tokens.input` and daily `tokens.input`. It includes cached and cache-write input according to OpenAI. |
| `output_tokens` | Non-negative whole number; sum into `ProviderUsage.tokens.output` and daily `tokens.output`. |
| `input_cached_tokens` | Non-negative whole number when present; aggregate separately as cached input. It must not be subtracted from `input_tokens`. The current public `TokenMetric` cannot expose it, so it is host-only until a later contract update adds an optional explicitly named cached-input field. |
| `input_cache_write_tokens`, modality-specific input/output token fields | Validate only if the later implementation chooses to parse them; do not display, derive money from, or send them in v1. |
| `project_id`, `model` | Available because v1 groups by both, but retained only long enough to define a host-side duplicate key and aggregate values. They are never sent to React or logged. |
| `user_id`, `api_key_id`, `batch`, `service_tier` | Not requested as grouping fields and discarded if present. |

The official Completions result documents input, output, cached-input, request count, model, project, user, API-key, batch, and service-tier fields. [Completions usage reference](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/completions)

### Cost fields

| Response field | V1 treatment |
|---|---|
| Bucket `start_time`, `end_time` | Required daily trend key; use only after range validation. |
| `amount.value` | Parse as finite `decimal`, retain valid signed values (credits/adjustments must not be converted to an error merely because they are negative), and sum only within one currency. |
| `amount.currency` | Required with a numeric amount; uppercase the documented ISO-4217 code for renderer display. Do not assume USD if the response did not provide a currency. |
| `project_id` | Used only for host-side deduplication/aggregation, then discarded. |
| `line_item`, `api_key_id`, `quantity` | Not requested as groupings and discarded if present. |

The official Costs result provides an amount with numeric `value` and lower-case ISO-4217 `currency`, plus optional project, line-item, API-key, and quantity attribution when grouped. [Costs reference](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/costs)

### Freshness and comparability

- `collectedAt` is host wall-clock time after both paginated endpoint collections have completed successfully.
- The newest accepted bucket end is retained as host-side `freshestBucketEnd` and may cross React as an ISO-8601 timestamp. It is a source-boundary freshness indicator, not a promise that the source has finalized every charge.
- Cost and usage totals are **reported** values when their complete page chains succeed. If the requested range includes the currently open UTC day, mark the relevant provider metrics and that daily point **delayed**; do not pretend an in-progress daily bucket is final.
- V1 never emits **estimated** spend, because it does not calculate price from tokens.
- Remaining credit, spend limit, invoice balance, consumer quota, and reset date are **unavailable**: neither endpoint returns them.
- Per-model cost is **not comparable/unavailable** in v1: Costs does not offer `model` grouping, so no token-to-cost allocation or model cost label is permitted.
- An empty, complete response returns zero requests/tokens only where a valid completion aggregation exists. An empty cost response has no documented amount/currency to display, so `spend` is unavailable rather than a manufactured zero in an invented currency.

## 6. Proposed native bridge protocol

### Versioning

Use **Native Usage Protocol v2** for the new collector. The current shared `NativeUsageProtocol.Version` is integer `1` and accepts only `openrouter`; the renderer also requires `usageVersion === 1`. A v2 implementation must either upgrade all supported providers atomically or retain a v1 OpenRouter handler while advertising an explicit separate v2 capability. It must never accept a v1 frame as if it had the v2 OpenAI data shape.

The existing public renderer schema remains `usage-intelligence/v1`; v2 here names only the native WebView transport/data envelope.

### Request envelope

| Field | Required v2 value/validation | Boundary rule |
|---|---|---|
| `type` | `usage.collect` | Fixed literal. |
| `version` | `2` | Must exactly match host capability. |
| `requestId` | UUID | Renderer-generated correlation ID; host validates it and echoes it only to the matching response. |
| `provider` | `openai-api` | Host allow-list only. |
| `credentialId` | Existing opaque identifier, normally `primary`; current renderer validation permits 1–128 `[A-Za-z0-9._-]` characters | It is a vault lookup key only, never credential material. Preserve this field name and boundary. |
| `period.start`, `period.end` | Whole-second RFC 3339 UTC instants with `start < end` | Host revalidates and converts to Unix seconds. |
| `period.label` | Optional display label, maximum 96 characters | Host does not use it in a provider request or authorization decision. |

### Success envelope

All fields below are aggregate/normalized values; the host sends no raw response object.

| Field | Meaning |
|---|---|
| `type`, `version`, `requestId`, `provider` | `usage.collect.result`, `2`, matching request ID, `openai-api`. |
| `collectedAt` | Host RFC 3339 UTC collection completion time. |
| `data.period.start`, `.end` | Canonical accepted request interval. |
| `data.freshestBucketEnd` | Latest accepted UTC bucket end, if a bucket exists. |
| `data.cost` | Optional `{ amount, currency, quality }`; currency is an uppercase ISO-4217 code. Omitted when no safely displayable single-currency result exists. |
| `data.requests` | Optional non-negative integer total. |
| `data.tokens.input`, `.output`, `.cachedInput` | Optional non-negative integer totals. `cachedInput` requires the integration checklist's optional public contract addition before React consumes it. |
| `data.daily` | At most one normalized aggregate per UTC day: `start`, `end`, optional `cost`, optional `requests`, optional `tokens`. No model, project, user, key, line item, provider raw object, or pagination field. |
| `data.completeness` | Boolean `costComplete` and `usageComplete`, plus a safe quality label for any open-day delay; no upstream exception text. |

### Error envelope

Use `usage.collect.error`, version `2`, the matching request ID/provider, an allow-listed `code`, a prewritten sanitized `message`, and boolean `retryable`. Omit all raw bodies, URLs with query parameters, headers, stack traces, vault identifiers, and exception text.

## 7. Mapping to Usage Intelligence

Map a successful v2 result to the existing `ProviderUsage` in `src\Modules\UsageIntelligence\contracts.ts`, then place it in the existing `UsageDashboardSnapshot.providers` list and pass the snapshot through `normalizeUsageSnapshot`.

| `ProviderUsage` field | V1 mapping |
|---|---|
| `provider` / `displayName` | `openai-api` / `OpenAI API`. |
| `readiness` | `ready`, because official organization Usage and Costs endpoints exist; this says nothing about a particular account's credential. |
| `state` | `connected` when both page chains complete; `warning` for a complete result containing an open-day delayed bucket or a documented unavailable metric; `not-configured`, `error`, or `unavailable` from the sanitized error map. |
| `spend` | `data.cost` as `MoneyMetric`. Use `reported` for closed, complete source buckets; `delayed` for an interval that includes an open UTC day. If currencies differ or cost is absent, omit it or use `not-comparable` only with a safe explanatory status. |
| `remaining` | Omit. The endpoints do not report credit/balance/limit. |
| `requests` | Total `num_model_requests`, quality `reported` or `delayed` as above. |
| `tokens` | Total `input_tokens` and `output_tokens`, quality `reported` or `delayed`. Do not put cached input into either field or derive a net number. |
| `billingPeriod` | `label: "Selected OpenAI API organization interval"`; `endsAt` is the canonical selected end. Do not use `resetsAt`. |
| `trend` | One daily `UsageTrendPoint` per accepted daily key, with available cost/request/token fields. Omit a field that is unavailable; never create a synthetic zero/currency. |
| `provenance` | `{ kind: 'host-collector', label: 'Live OpenAI organization collector', collectedAt, detail: 'Official OpenAI organization Usage and Costs endpoints through the native desktop host.' }` |

`UsageSummary`, `ProviderCards`, and `UsageTrends` already consume the aggregate `ProviderUsage` fields. If cached-input tokens must become visible, add a clearly named optional property to `TokenMetric` and update renderers/tests in one explicit contract change; do not hide it inside `detail` text.

## 8. Aggregation, duplicates, currencies, and invalid values

1. Fetch every page for each endpoint before emitting success. A partial page chain is a failure, not a partial financial total.
2. Use an endpoint-specific duplicate key. For completions: endpoint, bucket start/end, project ID (host-only), model (host-only), and any returned grouped dimensions. For costs: endpoint, bucket start/end, project ID (host-only), line item/API-key values if unexpectedly present. Never send these keys across the bridge.
3. On an exact duplicate key with identical validated values, ignore the later duplicate. On a duplicate key with any different numeric/currency value, fail as malformed to avoid double counting.
4. A bucket with no results is a valid zero contribution only after its bucket time range validates. Missing days are not filled with invented zero trend points; the response's actual buckets are the trend.
5. Sum token/request counters using checked 64-bit integers. Reject negative, fractional, overflowed, non-numeric, or non-finite values as malformed. Do not coerce strings.
6. Sum money using `decimal`, not binary floating point. Reject non-numeric or decimal-overflow values. Accept signed source amounts. Preserve only one uppercase currency per emitted money metric; no currency conversion. If a bucket/period has mixed currencies, leave its combined spend unavailable/not comparable and exclude it from the current `createSpendSummary` total.
7. Cache tokens are informational. Never subtract cached input from input, never double count it, and never use any token field to calculate cost.

## 9. Native transport and security requirements

| Control | Requirement |
|---|---|
| Destination | Construct the two absolute `https://api.openai.com/v1/...` URIs in code. No configuration, renderer data, DNS override, response link, or redirect may change host, scheme, port, or path. |
| Redirects | `AllowAutoRedirect = false`. Treat every 3xx as an unexpected upstream response and do not issue a follow-up request. |
| TLS and headers | HTTPS only; `Accept: application/json`; bearer credential attached only inside the native host. Never include it in exception messages or logs. |
| Timeouts | Match the OpenRouter precedent unless a focused test justifies a change: 5-second connect timeout, 12-second per-request `HttpClient` timeout, plus a 45-second overall collection budget for both page chains. |
| Response limits | `ResponseHeadersRead`, a declared-length precheck, and a streamed 512 KiB maximum per page; also cap the total accepted payload across both chains at 4 MiB. A response that exceeds either limit fails before JSON aggregation. |
| JSON parsing | Use bounded-depth JSON parsing (maximum 32), strict expected object/array kinds, and explicit numeric conversion. Discard the byte buffer after parsing, as the OpenRouter precedent does. |
| Cancellation | Carry the WebView/adapter cancellation token through every send, streamed read, delay, and retry. When cancellation wins, do not post a late success/error frame. The renderer removes the message listener immediately. |
| Retries | GET only. At most two retries total per failing request for transport failure, 408, 429, or 5xx. Honor a valid `Retry-After` up to 30 seconds; otherwise use bounded exponential delay with jitter. Never retry 400, 401, 403, 3xx, malformed JSON, invalid data, or response-size violations. |
| Rate limits | Preserve 429 as `rate-limited` with `retryable: true`; do not expose the raw rate-limit headers. |
| Logging | Log only provider, sanitized code, retryability, and a local correlation ID. Never log Authorization, credential ID, project/model/user/key identifiers, URI query strings, raw body, or upstream exception message. |

## 10. Sanitized error-code map

| Condition | Native code / retryable | Safe renderer message |
|---|---|---|
| No vault entry | `not-configured` / false | `Store an OpenAI organization reporting credential in the native vault.` |
| 401 Unauthorized | `permission-denied` / false | `OpenAI rejected the stored organization reporting credential.` |
| 403 Forbidden | `insufficient-role` / false | `The stored credential cannot read OpenAI organization usage and costs. An organization owner must provide an authorized Admin API credential.` |
| 429 after retry policy | `rate-limited` / true | `OpenAI temporarily rate-limited organization usage reporting. Refresh later.` |
| 408, network, DNS, TLS, or 5xx after retries | `unavailable` / true | `OpenAI organization reporting is temporarily unavailable.` |
| Redirect, other unexpected HTTP status, malformed JSON/schema, invalid bucket, duplicate conflict, overflow, unsupported mixed currency aggregation, oversized payload | `unexpected` / false | `OpenAI returned usage data Hermes could not safely process.` |
| Per-request or overall deadline | `timeout` / true | `OpenAI organization usage reporting timed out.` |
| Caller cancellation | `cancelled` / true | `OpenAI organization usage collection was cancelled.` |

`ProviderUsageAdapter.ts` currently lacks `rate-limited`, `insufficient-role`, `timeout`, and `cancelled` in its failure-code union. A later implementation must expand the union and map `rate-limited`/`timeout` to warning/unavailable and `insufficient-role` to error. Cancellation usually remains local to the adapter and should not flash a provider error after a component unmount.

## 11. Data-minimization table

| Field/category | May cross host-to-React? | Treatment |
|---|---|---|
| `requestId`, provider literal, protocol version | Yes, transiently | Correlation/validation only; do not persist or render. |
| `credentialId` | Request only, opaque | Renderer may pass the existing opaque identifier; host uses it for vault lookup. It must not echo in a result/error or log. |
| Selected normalized period, collection time, freshest bucket end | Yes | RFC 3339 UTC strings only. |
| Aggregate daily/period cost amount and currency | Yes | Only after strict numeric/currency validation and single-currency handling. |
| Aggregate request/input/output/cached-input counts | Yes, except cached input until public contract is extended | Whole non-negative totals only. |
| Quality/completeness/status labels | Yes | Enumerated values and prewritten safe text only. |
| Project IDs, user IDs, API-key IDs, model names, service tier, line item, quantity | No | Use only for bounded in-memory grouping/deduplication when needed, then discard; never render, persist, or log. |
| Admin credential material, Authorization header, vault target/secret | No | Native process only; clear references promptly; never serialize. |
| Raw HTTP bodies, headers, request URLs with query strings, upstream messages, stacks | No | Discard; translate to the sanitized map. |
| Organization name/ID and account metadata | No | The collector does not need them. Do not add discovery calls. |

## 12. Fake-handler test matrix

Add deterministic fake `HttpMessageHandler` coverage alongside the current OpenRouter collector smoke precedent in `src\Host\HermesDesktop.Smoke\Program.cs`, with unit-level collector tests if the host test layout grows. Fixtures contain only invented non-secret IDs and sanitized aggregates; no credential-shaped value.

| Case | Fake handler setup | Required assertion |
|---|---|---|
| Success | One valid completion page and one valid cost page, both daily, closed UTC day | Fixed HTTPS method/path/query, host-only Authorization presence without recording its value, correct aggregate requests/input/output/cached totals, USD amount, daily trend, and no identifiers in serialized snapshot. |
| Pagination | Two valid pages per endpoint with distinct cursors | Every `next_page` becomes only the following request's `page`; no loss/double-count; page cursor never crosses endpoints. |
| Empty | Valid pages with empty `data` | No crash; zero completion totals only when valid; no invented USD zero when Costs gives no amount/currency. |
| Delayed/open bucket | Valid bucket whose end includes the collection day | Metrics/trend become `delayed`; no estimated amount. |
| Missing bucket | Page omits a day inside the requested interval | No artificial zero point; returned buckets remain sorted. |
| Malformed | Invalid JSON, non-page root, missing `data`, missing `next_page` while `has_more`, wrong types, negative/fractional counters, invalid currency, overflow, duplicate conflict | Sanitized `unexpected`; raw content never reaches frame/log. |
| Authorization | 401 then 403 | 401 maps to `permission-denied`; 403 maps to `insufficient-role`; both non-retryable and contain no upstream text. |
| Rate limit | 429 with valid/invalid `Retry-After` | Bounded retry behavior; final `rate-limited`, retryable, and headers not returned. |
| Redirect | 301/302/307 response with a location | No follow-up request; `unexpected`; destination never contacted. |
| Timeout | Handler blocks past per-request/overall deadline | `timeout`, retryable; all pending work stops. |
| Cancellation | Cancel before send, during stream read, and during retry delay | No late success/error frame, listener cleanup, and no extra request after cancellation. |
| Sanitization | Source rows include project/user/key/model/line-item fields and an upstream error body | JSON serialization of native result/frame and captured log sink contain none of those values, no Authorization, no credential ID, and no raw body. |
| Currency | Same-currency and mixed-currency cost rows | Same currency sums with normalized uppercase code; mixed currencies are not converted or combined and are excluded from comparable spend. |

## 13. Later implementation checklist (do not change in this lane)

1. `src\Host\HermesDesktop\OpenRouterUsageCollector.cs`: either move shared `NativeUsageProtocol`/`UsageCollectionException` to an intentionally provider-neutral host file or extend them safely; add the `openai-api` allow-list and protocol-version plan without regressing OpenRouter v1.
2. Add `src\Host\HermesDesktop\OpenAiUsageCollector.cs`: fixed-host collector, DTOs, cursor aggregation, bounded reads, strict parsing, cancellation/retry policy, and sanitized failures specified above.
3. `src\Host\HermesDesktop\MainWindow.xaml.cs`: instantiate/dispose the collector; validate the v2 period envelope; resolve only the opaque `credentialId`; select provider; emit only the v2 aggregate frame; advertise compatible capabilities; and keep `PostUsageError` allow-listed.
4. `src\Host\HermesDesktop\CredentialDialog.cs`: change the generic credential wording to accurately warn that the OpenAI profile needs an organization Admin API credential and never claim that any ordinary API key is sufficient.
5. `src\Host\HermesDesktop.Smoke\Program.cs`: add the fake-handler matrix without calling OpenAI or using a credential-shaped fixture.
6. `src\Modules\UsageIntelligence\DesktopUsageBridge.ts`: implement strict v2 OpenAI request/result/error normalization, period propagation, `credentialId` validation, protocol mismatch behavior, 15-second renderer timeout coordination, and cancellation listener cleanup.
7. `src\Modules\UsageIntelligence\DesktopUsageAdapter.ts`: add the OpenAI mapper, replace the `openai-api` synthetic provider only when a validated native result exists, and retain provider-specific failure state otherwise.
8. `src\Modules\UsageIntelligence\contracts.ts`: keep `usage-intelligence/v1`; optionally add an explicit `cachedInput` token field only with accompanying renderers/tests. There is no current `types.ts` to edit.
9. `src\Modules\UsageIntelligence\ProviderUsageAdapter.ts`: extend sanitized failure-code/state handling for the new codes.
10. `src\Modules\UsageIntelligence\usageNormalization.ts`, `UsageSummary.tsx`, `ProviderCards.tsx`, and `UsageTrends.tsx`: ensure delayed quality and omitted/mixed-currency values stay truthful and that cached input is not double-counted if exposed.
11. `src\Modules\UsageIntelligence\DesktopUsageBridge.test.ts`, `DesktopUsageAdapter.test.ts`, `usageNormalization.test.ts`, and `UsageIntelligenceDashboard.test.tsx`: add frame validation, no-secret/no-identifier serialization, provider replacement, mixed-currency, quality, and display assertions.
12. `docs\USAGE-INTELLIGENCE-PROVIDERS.md`: update the matrix only after implementation evidence exists; retain the explicit ChatGPT-subscription exclusion.

## 14. Open questions requiring an account owner or safe live test

1. Can the organization owner create a least-privilege Admin API credential that the current account is authorized to use for these two Usage/Costs reads, and what exact scopes does OpenAI expose for them in that organization?
2. Does the organization have a policy permitting a local Windows host to store that Admin API credential in Credential Manager, including expiry, rotation, owner, revocation, and audit requirements?
3. On a non-production or least-privilege organization, do both endpoints return the documented result shapes for the desired period, and how long after UTC day close do its real cost buckets settle? This must be checked without recording response bodies or identifiers.
4. Is daily aggregation sufficient for the product, or is a later hourly/minute completions-only view needed? The Costs endpoint remains daily-only, so those views would not have matching cost granularity.
5. Should cached input be promoted through a backward-compatible `TokenMetric` addition, or remain a host-only diagnostic until the dashboard has a clear display and explanation?

## Sources

- [OpenAI Completions usage API reference](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/completions)
- [OpenAI Costs API reference](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/costs)
- [OpenAI Administration API overview](https://developers.openai.com/api/reference/administration/overview)
- [OpenAI Admin API keys reference](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/admin_api_keys)
- [OpenAI Admin and Audit Logs API help article](https://help.openai.com/en/articles/9687866-admin-and-audit-logs-api-for-the-api-platform.webm)
