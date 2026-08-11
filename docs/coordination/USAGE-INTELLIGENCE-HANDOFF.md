# Usage Intelligence Lane Handoff

## Status

Complete as a standalone, non-integrated React/TypeScript module. No changes were made to `App.tsx`, global styles, manifests, desktop host, Docker, Monaco, or another lane's files.

## Architecture

- `usage-intelligence/v1` is the public, provider-neutral normalized contract. It carries money, requests, token totals, billing periods, trends, provenance, and explicit `reported` / `estimated` / `delayed` / `unavailable` / `not-comparable` quality labels.
- `ProviderUsageAdapter` is the sole collection boundary. A production adapter must call a native-host or backend bridge; the React renderer has no provider endpoint logic.
- `MockUsageAdapter` is deterministic and unmistakably synthetic. It exercises connected, warning, not-configured, unavailable, delayed, and estimated states without requesting a provider or containing any credential/account identifier.
- The dashboard refuses to combine mismatched currencies, never combines remaining budget and credit balances, displays collection freshness/provenance per provider, and labels estimated/delayed information visibly.
- All CSS is scoped under `.usage-intelligence`; it responds from four summary cards to a clean single-column mobile panel.

## Exact files changed

- `src/Modules/UsageIntelligence/contracts.ts`
- `src/Modules/UsageIntelligence/ProviderUsageAdapter.ts`
- `src/Modules/UsageIntelligence/MockUsageAdapter.ts`
- `src/Modules/UsageIntelligence/usageNormalization.ts`
- `src/Modules/UsageIntelligence/formatters.ts`
- `src/Modules/UsageIntelligence/UsageSummary.tsx`
- `src/Modules/UsageIntelligence/UsageTrends.tsx`
- `src/Modules/UsageIntelligence/ProviderCards.tsx`
- `src/Modules/UsageIntelligence/DashboardState.tsx`
- `src/Modules/UsageIntelligence/UsageIntelligenceDashboard.tsx`
- `src/Modules/UsageIntelligence/UsageIntelligenceDashboard.css`
- `src/Modules/UsageIntelligence/index.ts`
- `src/Modules/UsageIntelligence/usageNormalization.test.ts`
- `src/Modules/UsageIntelligence/UsageIntelligenceDashboard.test.tsx`
- `docs/USAGE-INTELLIGENCE-PROVIDERS.md`
- `docs/coordination/USAGE-INTELLIGENCE-HANDOFF.md`

## Public exports and integration example

The public API is exported only from `src/Modules/UsageIntelligence/index.ts`:

- `UsageIntelligenceDashboard`
- `UsageIntelligenceDashboardProps`
- `ProviderUsageAdapter` and its success/failure result types
- all normalized contract types and `usageIntelligenceContractVersion`
- `createSpendSummary`, `normalizeUsageSnapshot`, `providerIds`
- `MockUsageAdapter`, `mockUsageAdapter`, and `createSyntheticUsageSnapshot`

The default component intentionally presents the synthetic demo after mount:

```tsx
import { UsageIntelligenceDashboard } from '../Modules/UsageIntelligence'

<UsageIntelligenceDashboard />
```

After a trusted host bridge exists, the integration owner can pass a host-backed adapter. The host resolves any `credentialRef`; the renderer never receives the credential material:

```tsx
import type { ProviderUsageAdapter } from '../Modules/UsageIntelligence'
import { UsageIntelligenceDashboard } from '../Modules/UsageIntelligence'

declare const hostUsageAdapter: ProviderUsageAdapter

<UsageIntelligenceDashboard
  adapter={hostUsageAdapter}
  request={{
    period: { start: periodStart, end: periodEnd, label: 'Current period' },
  }}
/>
```

## Verification

- Focused Vitest suite: `Modules/UsageIntelligence/usageNormalization.test.ts` and `Modules/UsageIntelligence/UsageIntelligenceDashboard.test.tsx` — **2 files, 6 tests passed**.
- TypeScript: `tsc --project tsconfig.app.json --noEmit --pretty false` — **passed** against the existing project configuration.
- Privacy boundary audit: no `localStorage`, `sessionStorage`, IndexedDB, cookies, or direct renderer `fetch(...)` calls in `src/Modules/UsageIntelligence`.
- A floating-point total found by the focused test was fixed by deterministic six-decimal monetary rounding before the final green run.

## Official-source research

`docs/USAGE-INTELLIGENCE-PROVIDERS.md` was verified on **2026-08-09** and documents direct official links, authentication/role requirements, data quality and delay, consumer availability, browser unsuitability, and classification for all requested providers.

- **Ready:** OpenAI API organization usage/cost, OpenRouter managed-key credit totals, and eligible Anthropic organization Usage & Cost API collection.
- **Partial:** Google Cloud budgeting/export-based cost reporting and Google AI Studio/Gemini billing/quota surfaces.
- **Unsupported:** programmatic ChatGPT consumer subscription allowance. The lane intentionally does not scrape private dashboards or automate consumer account pages.

The research is not an implementation claim: this lane contains no live provider adapter and makes no provider network calls.

## Real vs. mocked

- **Real:** contracts, adapter protocol, loading/connected/warning/error/unavailable/not-configured UI, quality labeling, currency-safety behavior, focused tests, and official-source provider matrix.
- **Mocked:** every rendered data value, provider status, trend, cost, request count, token count, credit, budget, reset date, collection time, and provenance result. Each is marked `Synthetic demo` / `Synthetic demo collector` in the UI.

## Limitations and next safe integration steps

1. Super may import this module into the intended Workbench panel; that integration is intentionally not part of this lane.
2. Add a native-host/backend collector that resolves opaque credential references using least-privilege host storage, then returns `usage-intelligence/v1` snapshots only.
3. Implement provider collectors one at a time from the official matrix, retaining source freshness, pagination, USD/currency constraints, and excluded-source caveats.
4. Keep ChatGPT consumer pages and Claude/AI Studio consumer-style dashboards out of scope unless a supported official API becomes available and the matrix is refreshed.
5. Add host-bridge tests before enabling a live connection. Do not add a browser secret-entry form, browser secret persistence, or direct browser provider call.

Stopped here for Super review and integration.
