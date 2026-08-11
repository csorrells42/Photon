# Usage Intelligence Dashboard Lane

## Mission

Build a self-contained, polished React/TypeScript dashboard module that can eventually show AI and web-service usage in one place. This lane produces the UI, provider-neutral contracts, safe mocked data, focused tests, and an evidence-backed provider integration matrix. The main Hermes Workbench lane will integrate it later.

## Exclusive ownership

This worker may create or edit only:

- `src/Modules/UsageIntelligence/**`
- `docs/USAGE-INTELLIGENCE-PROVIDERS.md`
- `docs/coordination/USAGE-INTELLIGENCE-HANDOFF.md`

Do not edit any existing file outside those paths.

## Files owned by other lanes

Do not edit:

- `src/app/App.tsx`
- `src/app/styles.css`
- `src/vite.config.ts`
- `src/package.json`
- `src/package-lock.json`
- `src/Modules/Workspace/**`
- `src/Modules/HermesSystem/**`
- `src/Host/**`
- `remote-install/**`
- `artifacts/**`
- `docker-compose.yml`
- `source/**`
- any existing coordination contract

Do not start or stop Docker, Serena, Hermes, Vite, or the desktop host.

## Technical constraints

- Use the existing React/TypeScript toolchain and dependencies only.
- Export the public module API from `src/Modules/UsageIntelligence/index.ts`.
- Keep all styles module-scoped under `src/Modules/UsageIntelligence/`.
- Do not assume a particular parent layout width. The module must work as a full workspace panel and collapse cleanly for narrower widths.
- Use provider-neutral, versioned TypeScript contracts so live collectors can replace mock data without rewriting the UI.
- Keep data fetching behind adapter interfaces. UI components must not call provider endpoints directly.
- Include polished loading, connected, warning, error, unavailable, and not-configured states.
- Make the default dashboard useful at a glance: combined spend, remaining credit or budget where available, request/token trends, provider status, reset/billing period, and freshness/provenance.
- Clearly label estimated, delayed, unavailable, and non-comparable figures. Never fabricate precision.
- Include OpenAI API, ChatGPT subscription, OpenRouter, Google Cloud/API usage, Google AI Studio/Gemini API, and Anthropic/Claude as provider definitions.
- A provider can explicitly report that no supported programmatic usage API exists. Do not scrape private dashboards or automate consumer account pages.

## Credential and privacy boundary

- Never put real keys, tokens, cookies, account identifiers, or secrets in source, fixtures, snapshots, logs, URLs, browser storage, or test output.
- Do not implement `localStorage`, `sessionStorage`, IndexedDB, cookies, or browser-side secret persistence.
- Model credentials only as opaque credential references, such as `credentialRef`, resolved later by the native C# host or another trusted backend.
- Mock data must be unmistakably synthetic.
- Provider adapters must be designed so network calls eventually run outside the renderer.
- Do not add a key-entry form that retains or echoes a secret. A future connection dialog may pass a one-time secret to the native host, but that bridge is outside this lane.

## Research requirements

Use current official provider documentation only. Record direct links and the verification date in `docs/USAGE-INTELLIGENCE-PROVIDERS.md`.

For each provider, document:

- supported official usage, cost, credit, quota, or billing endpoints
- authentication/role requirements
- useful metrics and known delay or granularity
- whether consumer subscription usage is programmatically available
- CORS/browser suitability and why collection should occur in the trusted host/backend
- a clear classification: `ready`, `partial`, `manual-only`, or `unsupported`

Do not claim an integration is live merely because its UI adapter exists.

## Expected source shape

Names may vary when there is a good reason, but aim for:

- `contracts.ts` — versioned normalized models and provider identifiers
- `ProviderUsageAdapter.ts` — adapter interface and result/error semantics
- `MockUsageAdapter.ts` — synthetic deterministic sample data
- `UsageIntelligenceDashboard.tsx` — composed dashboard surface
- component files for summary, provider cards/table, trends, and configuration state
- module-scoped CSS
- focused Vitest tests for normalization, failure states, and secret-free rendering
- `index.ts` — stable exports

## Verification

Run focused tests and TypeScript checking without changing package manifests. If the repository is changing concurrently, report any unrelated failure instead of editing another lane's files.

## Handoff

When finished, write `docs/coordination/USAGE-INTELLIGENCE-HANDOFF.md` containing:

- concise architecture summary
- exact files changed
- public exports and integration example
- tests and checks run with results
- official-source research summary
- what is real versus mocked
- known limitations and next safe integration steps

Stop after writing the handoff. Do not integrate the module into `App.tsx`.
