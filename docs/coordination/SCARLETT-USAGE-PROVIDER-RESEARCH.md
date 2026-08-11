# Google, Gemini, and Anthropic Usage Provider Research

**Research date:** 2026-08-09

## Executive decision

| Surface | Classification | Supported source | Credential boundary | Limitation |
| --- | --- | --- | --- | --- |
| Google Cloud Budget API | partial | `GET https://billingbudgets.googleapis.com/v1/billingAccounts/{billingAccount}/budgets` | OAuth plus Cloud Billing IAM; trusted host/backend | Configuration only; no actual-cost series. |
| Cloud Billing cost data | partial until owner setup | Cloud Billing export to BigQuery, queried with BigQuery API | Least-privilege OAuth/service identity; backend preferred | Delayed, setup-dependent, and BigQuery use has cost. |
| Gemini/AI Studio dashboard and prepay data | manual-only | AI Studio Usage and Billing UI | No reporting credential identified | No documented programmatic dashboard/balance/spend-cap read API. |
| Gemini quota configuration | partial | Google Service Usage quota metrics, after account validation | OAuth read scope; host/backend | Quota configuration, not dashboard usage, balance, or cost. |
| Anthropic Claude Platform organization API | ready | Organization Usage and Cost APIs | Admin API key in host/backend | Individual accounts ineligible; Priority Tier costs omitted. |
| Claude personal subscription allowance | unsupported | None documented | None | No scraping or private-page automation. |

The existing renderer bridge is OpenRouter-specific protocol v1; future provider collectors need a new versioned native shape. See [DesktopUsageBridge.ts](../../src/Modules/UsageIntelligence/DesktopUsageBridge.ts) and [DesktopUsageAdapter.ts](../../src/Modules/UsageIntelligence/DesktopUsageAdapter.ts).

## Shared native-collector safety requirements

- Renderer input is limited to provider, period, and opaque credential reference. It must never receive OAuth refresh tokens, service-account private keys, Admin API keys, raw responses, pagination cursors, or any account/project/workspace/user identifier.
- Use a trusted host/backend and fixed official HTTPS hosts. A backend is preferred for Google Cloud; a native Windows host is permissible only if it stores the approved credential in OS-managed secure storage. Neither credential class may enter React/WebView.
- Normalize UTC RFC 3339 collection/bucket timestamps, source currency, aggregate metrics, freshness/quality, and sanitized status only. Discard raw JSON, headers, error bodies, resource names, e-mail, account/billing-account/project/user/API-key/service-account/workspace IDs, labels, SKUs, and cursors.
- Use decimal/fixed-point money and checked integer aggregation. Reject invalid timestamps, non-finite/overflowed values, and duplicate/conflicting bucket rows; do not make missing data zero.

## Google Cloud / Google APIs

### Budget API: reads report configuration, not spend

**Classification:** `partial`.

The documented route is:

```text
GET https://billingbudgets.googleapis.com/v1/billingAccounts/{billingAccount}/budgets
```

The [Budget API list reference](https://cloud.google.com/billing/docs/reference/budget/rest/v1/billingAccounts.budgets/list) documents project-only `scope`, `pageSize` (default/maximum 100), `pageToken`, `budgets[]`, and `nextPageToken`. It requires `cloud-platform` or `cloud-billing` OAuth scope plus `billing.budgets.list`. The [Cloud Billing IAM guide](https://cloud.google.com/billing/docs/access-control) names Billing Account Viewer as a predefined role that grants Budget read/list access; a single-project budget can instead use the documented Project Viewer permissions. A Gemini API key is not sufficient.

Budget reads provide the configured budget scope/filter, period, target amount/currency or last-period-amount option, thresholds, and notification rules. The [Budget resource](https://cloud.google.com/billing/docs/reference/budget/rest/v1/billingAccounts.budgets) does **not** define current spend, forecast spend, consumed percent, remaining budget, or historical actual-cost fields. A collector may display reported configuration but must not transform it into spend telemetry. The list method also warns that Console fields can be unavailable from the API.

### Actual cost requires Cloud Billing export to BigQuery

**Classification:** `partial` until an owner has configured an export and grants a narrowly scoped read identity. Afterwards, it is an official programmatic source for the exported billing account.

The [Cloud Billing export setup guide](https://cloud.google.com/billing/docs/how-to/export-data-bigquery-setup) supports standard usage cost, detailed usage cost, pricing, and CUD metadata exports. Enabling usage-cost export requires Billing Account Costs Manager or Billing Account Administrator on the billing account and BigQuery User in the dataset project. That is a one-time owner/admin setup—not collector work.

For collector querying, the documented minimum roles are BigQuery Job User on the query project and BigQuery Data Viewer on every referenced table/view; see [Run a query](https://cloud.google.com/bigquery/docs/running-queries). Prefer an owner-maintained aggregate view in a dedicated billing dataset. Treat service-account private keys, table names, billing-account IDs, and query text as host-only.

The [standard export schema](https://cloud.google.com/billing/docs/how-to/export-data-bigquery-tables/standard-usage) supplies hourly `usage_start_time`, `usage_end_time`, `export_time`, cost, credits, adjustments, service/SKU, project attribution, and billed `currency`; detailed export adds resource-level data. A collector can normalize a delayed aggregate of cost, credits/adjustments, currency, source freshness, and an owner-approved service/project aggregate. It cannot inherently produce Gemini model token totals, API requests, a prepay balance, or a spend-cap remaining value.

Freshness is not live: initial data can take hours; multi-region retroactive backfill can take up to five days, while regional datasets lack pre-enable history. Schema can change, corrections and late-monetized usage occur, and `invoice.month` may differ from usage time. The setup guide also states BigQuery storage and query processing incur charges. Future implementation should use an owner-approved view, bounded UTC period, selected columns, parameterized values, result/bytes-billed caps, and no resource/label attribution unless expressly approved.

### Quota configuration is a separate optional source

The [Service Usage API](https://cloud.google.com/service-usage/docs/reference/rest) documents quota data visible to the consumer. Its [quota metric list method](https://cloud.google.com/service-usage/docs/reference/rest/v1beta1/services.consumerQuotaMetrics/list) is:

```text
GET https://serviceusage.googleapis.com/v1beta1/{parent=*/*/services/*}/consumerQuotaMetrics
```

It requires `cloud-platform.read-only` OAuth. It may serve as a quota-configuration companion after the owner confirms the project, enabled Gemini service, metrics, and IAM. It is not a Gemini API-key route and must not be presented as per-key usage, dashboard balance, spend-cap consumption, or cost.

### Google fake-handler test cases

| Case | Expected safe result |
| --- | --- |
| Two Budget pages | Follow `nextPageToken`; show configuration only, never synthetic spend. |
| Missing Console-only Budget field | Mark field unavailable without failing valid configuration. |
| Budget 401/403/OAuth-scope failure | Sanitized unauthorized/forbidden; no token/account detail. |
| No approved export view or empty rows | `not-configured`/`unavailable`, not `$0` unless a completed period is explicitly proven. |
| Delayed `export_time` | Return delayed quality/freshness; never label live. |
| Multi-currency, credit/correction, duplicate row | Preserve currencies separately, keep credits/adjustments distinct, host-deduplicate before sum. |
| Schema change, malformed timestamp/number, quota/timeout/cancel | Ignore unknown fields; fail malformed required data; return sanitized bounded status with no SQL/table detail. |

## Google AI Studio / Gemini API

**Classification:** `manual-only` for AI Studio usage dashboard, prepay balance, balance transactions, and project/billing-account spend-cap consumption. It is `partial` only when the owner uses the distinct Google Cloud Billing/BigQuery or Service Usage sources above.

The official [Gemini API billing guide](https://ai.google.dev/gemini-api/docs/billing) directs monitoring to **AI Studio > Dashboard > Usage**, says prepay balance management and transaction history are done directly in the AI Studio Billing tab, and says Cloud Billing can lag—typically within a day but sometimes more than 24 hours. AI Studio billing processing can itself be delayed by around 10 minutes.

No official endpoint was identified in the reviewed Google AI for Developers documentation that returns AI Studio dashboard usage, prepay balance, transaction history, spend-cap consumption, or reset data to a third-party collector. The correct collector state is manual-only/unavailable. Scraping or browser automation is prohibited.

The [Gemini API key guide](https://ai.google.dev/gemini-api/docs/api-key) says a standard key associates requests with a Google Cloud project for billing/quota but does not identify a caller; newer authorization keys bind model requests to a service account. Neither is an evidenced billing-report API. The guide also says never expose keys client-side. Therefore no Gemini key—standard or authorization—may be sent to an assumed reporting endpoint or exposed to React.

The [Gemini rate-limit guide](https://ai.google.dev/gemini-api/docs/rate-limits) documents project-level RPM, input TPM, and RPD; RPD resets at midnight Pacific time. Limits vary by model/tier, and spend-based rate limits use a rolling 10-minute window. AI Studio is the documented location to view active limits. A 429 response is not authoritative dashboard usage, remaining quota, or balance.

| Case | Expected safe result |
| --- | --- |
| Only a Gemini API-key reference | Decline dashboard/balance collection; do not attempt an undocumented endpoint. |
| Approved BigQuery Gemini-service aggregate | Return delayed Cloud Billing spend/currency/freshness; do not call it AI Studio balance/cap remaining. |
| Quota metric cannot be confidently mapped | Discard or mark unavailable; never guess model/reset/remaining. |
| UI-only prepay balance | Keep manual-only; no scraping fixture is allowed. |
| Model-call 429 | Transient execution condition only, not consumption telemetry. |

## Anthropic Claude Platform organization usage and cost

**Classification:** `ready` for an eligible Claude Console / Claude Platform organization holding an Admin API key. Individual accounts are ineligible; Claude Enterprise uses distinct Analytics API endpoints/key; Claude Platform on AWS currently lacks these programmatic Usage and Cost endpoints.

The official [Usage and Cost API guide](https://platform.claude.com/docs/en/manage-claude/usage-cost-api) documents:

```text
GET https://api.anthropic.com/v1/organizations/usage_report/messages
GET https://api.anthropic.com/v1/organizations/cost_report
```

Both require an **Admin API key**, resolved only inside host/backend, and the documented `anthropic-version: 2023-06-01` header. An ordinary model-call API key is insufficient. The owner must create/scoped-authorize the reporting key; the renderer must receive only an opaque credential reference.

### Usage report

The [Messages Usage Report reference](https://platform.claude.com/docs/en/api/admin/usage_report) aggregates buckets whose `starting_at` is inclusive and `ending_at` exclusive RFC 3339. Supported `bucket_width` values are `1m`, `1h`, and `1d`; documented default/maximum bucket counts are 60/1,440, 24/168, and 7/31 respectively. Use daily by default and never exceed the selected source maximum.

Supported filtering/grouping includes API-key ID, workspace ID, model, service tier, context window, inference geography, and (with its preview header) speed. An initial collector should request only owner-approved grouping, aggregate host-side, then discard IDs.

Reported metrics can include `uncached_input_tokens`, `cache_read_input_tokens`, cache-creation token values by TTL, `output_tokens`, server web-search requests, and selected model/workspace/service-tier/context-window/inference-geo attribution. The guide says data typically appears within five minutes of API request completion, sometimes longer, and supports sustained polling once per minute. Cache/throttle in the host, not on renderer re-renders.

### Cost report

The [Cost Report reference](https://platform.claude.com/docs/en/api/admin/cost_report) provides daily (`1d`) service-level USD costs: token, web-search, and code-execution, groupable by workspace and description. Description grouping can include parsed model/inference-geo fields. `amount` is a decimal string in the provider's lowest currency units; preserve it as decimal/fixed-point, not JavaScript floating point. Priority Tier costs use a different billing model and are explicitly excluded from Cost API results. A UI can show Priority usage but must label all-in spend incomplete/not-comparable, never fabricate Priority cost.

Both reports page using `has_more` and opaque `next_page`, passed unchanged as `page` until `has_more` is false. Cursors never leave host memory. Aggregate independently by bucket boundary and selected dimensions; deduplicate repeated rows across pages and fail closed on conflicting duplicates. Do not derive cost from tokens where Cost data exists or combine differently bounded reports without retaining separate freshness/period labels.

Configured rate limits are a separate optional source: the [Rate Limits API](https://platform.claude.com/docs/en/api/admin/rate_limits) documents `GET /v1/organizations/rate_limits` with pageable configured limiter entries. It reports configuration, not remaining consumption. The [rate-limit guide](https://platform.claude.com/docs/en/api/rate-limits) documents 429 with `retry-after`; honor that host-side and return sanitized retryability.

### React-safe data boundary

| May cross after host normalization | Must be discarded |
| --- | --- |
| provider/display label; normalized bucket bounds; collection timestamp; aggregate token/request metrics; aggregate decimal USD cost; allowed model/service-tier label; quality (`reported`, `delayed`, `incomplete`, `unavailable`); sanitized status and retryability | Admin key; raw HTTP header/body; cursor; organization/account/API-key/service-account/workspace IDs; user/account identity/e-mail; raw error body; query string; individual-workspace attribution not explicitly approved. |

### Anthropic fake-handler test cases

| Case | Expected safe result |
| --- | --- |
| Valid daily usage with all token fields | Checked non-negative totals and valid RFC 3339 bucket bounds. |
| Two usage/cost pages | Host follows cursor and aggregates once; prove cursor never reaches a frame/log/error. |
| Empty valid range | Reported empty completed range only if boundaries are valid; otherwise unavailable. |
| Recent/missing bucket | Preserve freshness and mark delayed where appropriate. |
| USD decimal amount | Decimal/fixed-point conversion; test minor-unit precision. |
| Priority usage | Usage may show; all-in cost is incomplete, no derived amount. |
| Individual account, ordinary key, AWS Platform, or absent Admin authority | Sanitized not-supported/forbidden with no endpoint fallbacks. |
| 401/403/429/5xx, redirect, timeout, cancel | Sanitized error code; respect retry-after; fixed-host/no cross-host redirect; no secret/raw body. |
| Missing cursor with `has_more`, invalid timestamp, negative/overflow value, malformed money, conflicting duplicate | Fail closed as malformed-response with no partial misleading total. |

## Claude consumer subscriptions

**Classification:** `unsupported` for a personal Claude plan's allowance, consumed/remaining allowance, reset time, or subscription billing. The organization Usage/Cost Admin APIs need an Admin key and are unavailable for individual accounts. No supported public personal-allowance endpoint was identified in official documentation reviewed for this packet.

Do not scrape `claude.ai`, automate account pages, reuse browser session data, infer allowance from API activity, or repurpose a user OAuth token. The organization [Claude Code Usage Report](https://platform.claude.com/docs/en/api/admin/usage_report/retrieve_claude_code) can include organization-level subscription-related Claude Code metrics, but it includes organization/user analytics and is **not** a personal consumer allowance API. It is out of scope unless a future separately approved Enterprise analytics design covers its credentials and privacy.

## Recommended implementation order

1. **Anthropic Claude Platform Usage + Cost** — first: official routes, explicit privileged credential class, useful tokens/cost, known pagination/buckets, and typical five-minute freshness. Gate on owner-approved Admin API key; visibly flag Priority Tier cost incompleteness.
2. **Google Cloud Billing export reader** — second: valuable cross-Google spend data, but only against owner-provisioned aggregate view/identity with strict query-cost and identifier controls. Always label it delayed and never call it an AI Studio balance.
3. **Google Budget configuration and Service Usage quota reader** — third: lower risk but lower value; configuration only, no spend/remaining inference.
4. **Gemini AI Studio dashboard/balance and Claude personal subscriptions** — no collector until the vendor publishes an appropriate official least-privilege read API.

## Owner/live-test questions

1. Which Google billing export dataset/view and query project may a read-only collector access, and can it exclude project/resource/label identifiers?
2. Which stable Google Cloud billing service/SKU filter represents Gemini charges for this account, and what source currency applies?
3. What BigQuery bytes-billed/operational-cost budget and correction window are acceptable for a collector?
4. Is the Anthropic account Claude Console/Platform, Claude Enterprise, individual, or Claude Platform on AWS, and which read-only Admin scopes are available?
5. Is model/workspace attribution needed in the UI, or are organization aggregates sufficient?

## Official sources consulted

- Google Cloud: [Budget list API](https://cloud.google.com/billing/docs/reference/budget/rest/v1/billingAccounts.budgets/list), [Budget resource](https://cloud.google.com/billing/docs/reference/budget/rest/v1/billingAccounts.budgets), [Cloud Billing IAM](https://cloud.google.com/billing/docs/access-control), [Billing export setup](https://cloud.google.com/billing/docs/how-to/export-data-bigquery-setup), [standard export schema](https://cloud.google.com/billing/docs/how-to/export-data-bigquery-tables/standard-usage), [BigQuery query roles](https://cloud.google.com/bigquery/docs/running-queries), [Service Usage quota metrics](https://cloud.google.com/service-usage/docs/reference/rest/v1beta1/services.consumerQuotaMetrics/list).
- Google AI for Developers: [Gemini billing](https://ai.google.dev/gemini-api/docs/billing), [Gemini API keys](https://ai.google.dev/gemini-api/docs/api-key), [Gemini rate limits](https://ai.google.dev/gemini-api/docs/rate-limits).
- Anthropic/Claude: [Usage and Cost API](https://platform.claude.com/docs/en/manage-claude/usage-cost-api), [Messages Usage Report](https://platform.claude.com/docs/en/api/admin/usage_report), [Cost Report](https://platform.claude.com/docs/en/api/admin/cost_report), [Rate Limits API](https://platform.claude.com/docs/en/api/admin/rate_limits), [rate-limit behavior](https://platform.claude.com/docs/en/api/rate-limits), [Claude Code Usage Report](https://platform.claude.com/docs/en/api/admin/usage_report/retrieve_claude_code).
