# Handoff: Usage Intelligence Collectors Lane

You are the provider-usage collector lane for Hermes Workbench. Work autonomously and keep going through implementation and verification. The shared project root is:

`C:\Users\clsor\Documents\Codex\HermesAgent`

## Outcome

Build isolated, security-first C# collector services for the Usage Intelligence dashboard so a developer can monitor multiple restricted API keys and organizations without exposing secrets to React. Existing OpenRouter multi-key collection is already implemented; extend the provider foundation for supported official OpenAI API, Anthropic API, and Google Cloud billing/budget sources.

## Exclusive ownership

You may create and edit only:

- `src\Host\HermesUsageCollectors\**`
- `src\Host\HermesUsageCollectors.Smoke\**`
- `docs\coordination\USAGE-COLLECTORS-LANE-REPORT.md`

You may inspect the rest of the repository read-only, especially the existing OpenRouter collector and Usage Intelligence contracts. Do not edit the current desktop host, React Usage Intelligence files, package files, installer scripts, Docker files, data, artifacts, or any other coordination document. If integration needs an existing file changed, describe the exact change in your report for Super to apply later.

## Required research boundary

Use current official provider documentation and primary source repositories only. Confirm each endpoint, authentication role/scope, pagination model, date window, latency, units, quotas, and availability before implementing it. Do not scrape provider dashboards or consumer web pages.

Explicitly distinguish:

- ChatGPT consumer subscription usage from OpenAI API organization/project telemetry;
- Claude consumer subscription usage from Anthropic organization Usage & Cost APIs;
- Gemini/AI Studio per-key usage from Google Cloud Billing export, Budgets, Monitoring quotas, and service metrics;
- configured budget thresholds from actual spend;
- estimated/projected values from provider-reported values.

Unsupported consumer or per-key data must return an honest `unavailable` capability with a reason; never fabricate or infer it.

## Required implementation

1. Create a dependency-light `net10.0` C# library named `HermesUsageCollectors`.
2. Define immutable, JSON-friendly provider-neutral contracts for:
   - provider and named credential/profile metadata without secret values;
   - capability state: supported, setup-required, unavailable, permission-denied, stale, partial, or error;
   - usage/cost/budget/quota observations with source, unit, currency, time window, collected-at time, and freshness;
   - per-profile result and aggregate result with partial-failure retention;
   - sanitized provider errors.
3. Implement only collectors backed by currently supported official APIs. Candidates to confirm include:
   - OpenAI API organization/project usage and cost endpoints, with bounded pagination and admin-key requirements made explicit;
   - Anthropic organization Usage and Cost APIs, with bounded pagination and admin-key requirements made explicit;
   - Google Cloud Billing Budget API for budget configuration/threshold state, clearly not actual spend;
   - an adapter contract/specification for Google Cloud BigQuery Billing Export actual cost, if a small safe implementation is practical; otherwise provide an exact integration design rather than pretending it is live.
4. Model Google AI Studio/Gemini per-key usage and ChatGPT/Claude consumer subscription usage as unavailable unless an official supported API is verified during this task.
5. All HTTP collectors must:
   - use injected `HttpClient`/handlers and fixed official HTTPS endpoint allowlists;
   - reject redirects;
   - apply explicit timeouts, cancellation, bounded pages/rows/body sizes, invariant-culture parsing, and strict JSON validation;
   - never return bearer keys, raw headers, raw provider payloads, account emails/names, or unnecessary organization/user identifiers;
   - sanitize remote error bodies into bounded category/code messages;
   - preserve successful named profiles when another profile fails and aggregate only compatible units/windows.
6. Do not read Windows Credential Manager in this lane. Accept secrets transiently through an injected delegate/interface so the current native host remains the future owner of secret retrieval.
7. Create `HermesUsageCollectors.Smoke`, with no real network calls or real keys, using fake handlers and synthetic payloads to verify:
   - request method/path/query/auth shape without printing the secret;
   - pagination bounds and cancellation;
   - success normalization;
   - partial multi-profile aggregation;
   - unauthorized, forbidden, rate-limit, malformed, oversized, redirect, timeout, and provider-error sanitization;
   - unavailable capability results for unsupported consumer/per-key surfaces.

## Safety and quality gates

- Never inspect `data`, `.env`, logs, browser storage, Credential Manager, or any real key.
- Never call a live provider endpoint or incur cost.
- Never put a secret in a URL, exception, log, test snapshot, report, or returned DTO.
- Do not add web scraping, browser automation, undocumented/private endpoints, or inferred billing totals.
- Keep every provider independently revocable and least-privileged.
- Do not add UI placeholders that look live when the underlying source is unavailable.

## Verification and report

Run Release builds and the smoke executable. In `USAGE-COLLECTORS-LANE-REPORT.md`, record:

- exact files created;
- provider-by-provider supported/setup-required/unavailable matrix;
- official sources and access requirements;
- commands run and pass/fail totals;
- request, pagination, sanitization, and partial-failure guarantees;
- exact existing files Super must later touch to connect collectors to the desktop bridge and React dashboard;
- honest limitations, data latency, and anything not live-verified.

Do not commit, merge, publish, package, or modify another lane. Finish by telling the user that Super can find the work in the two owned source folders and the report path.
