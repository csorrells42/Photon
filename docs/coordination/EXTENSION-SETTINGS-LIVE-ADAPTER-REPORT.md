# Extension Settings Live Adapter Report

Date: 2026-08-09  
Lane: Hermes Workbench isolated live Extension Settings read adapter  
Intelligence: Sol High  
Project: `C:\Users\clsor\Documents\Codex\HermesAgent`

Super integration report:

`C:\Users\clsor\Documents\Codex\HermesAgent\docs\coordination\EXTENSION-SETTINGS-LIVE-ADAPTER-REPORT.md`

## Outcome

Completed a versioned, read-only live adapter for the Advanced Extensions Labs surface.

Public entrypoint:

`src/Modules/HermesExtensionSettingsLive/index.ts`

Contract version:

`hermes-extension-settings-live/v1`

The adapter produces the existing `HermesExtensionSettingsSnapshot` contract without changing the existing Extension Settings module. It reads source-confirmed upstream routes, projects only allowlisted fields, reports every source independently, and returns `unavailable` for all unsupported mutation surfaces.

No integration was performed. Super owns integration.

## Ownership and exclusions

Changed only:

- `src/Modules/HermesExtensionSettingsLive/**`
- `docs/coordination/EXTENSION-SETTINGS-LIVE-ADAPTER-REPORT.md`

Read-only inspection was limited to:

- `src/Modules/HermesExtensionSettings/**`
- `src/Modules/HermesSkills/**`
- `src/Modules/HermesMcp/**`
- relevant current upstream route/config definitions under `source/hermes_cli/**`

Explicitly not inspected or changed:

- `src/Modules/HermesSessionAdmin/**`
- `HermesSystemWorkspace.tsx`
- `App.tsx`
- existing Extension Settings, Skills, and MCP files
- host, installer, Docker, package, build, and shared configuration files
- every other module and report

The project shell has no root Git metadata, so the ownership audit is based on the exact newly created file inventory and the patch targets.

## Public exports

- `HermesExtensionSettingsLiveAdapter`
- `HermesExtensionSettingsLiveFetch`
- `HERMES_EXTENSION_SETTINGS_LIVE_CONTRACT_VERSION`
- `liveAdapterLimits`
- `liveExtensionSettingsRoutes`
- versioned request/result/source/route/trust/mutation contracts
- allowlist normalizers for skills, skill content, web toolsets, MCP, model options, auxiliary assignments, and MoA
- safe text/ID helpers and model ID composition

## Read contract

```ts
interface HermesExtensionSettingsLiveReadRequest {
  profileId?: string
  correlationId: string
  signal?: AbortSignal
}
```

Every upstream request carries:

- `profile` as a bounded query parameter when supplied
- `X-Hermes-Correlation-Id` as a bounded request header
- the caller's `AbortSignal`
- `credentials: "include"`
- `Accept: application/json`

Read results return:

- live adapter contract version
- the same normalized profile/correlation identity
- overall state: `ready`, `partial`, `unavailable`, `error`, `cancelled`, or `duplicate`
- renderer-safe `HermesExtensionSettingsSnapshot`
- one bounded source report per route/capability
- deterministic completion timestamp seam

## Route and capability matrix

| Source | Upstream read route | Source evidence | Normalized capability | Deliberately omitted |
|---|---|---|---|---|
| Skills | `GET /api/skills` | `source/hermes_cli/web_routers/skills.py:398` | Bounded inventory, name, description, editable status, upstream provenance | usage internals, unknown fields |
| Skill content | `GET /api/skills/content?name=...` | `source/hermes_cli/web_routers/skills.py:452` | Bounded `SKILL.md` text for at most 32 skills per read | filesystem `path`, all unknown fields |
| Toolsets | `GET /api/tools/toolsets` | `source/hermes_cli/web_routers/tools.py:57` | Web toolset advertised/available status | unrelated toolsets' configuration |
| Search/Extract providers | `GET /api/tools/toolsets/web/config` | `source/hermes_cli/web_routers/tools.py:186` | Provider names, backend IDs, `search`/`extract` capability advertisements, readiness, active Search and Extract selections | environment rows/status, URLs, defaults, post-setup commands, auth/setup data |
| MCP servers | `GET /api/mcp/servers` | `source/hermes_cli/web_routers/mcp.py:61` | Name, transport, enabled state, HTTP origin only, environment variable names | environment values, auth, headers, OAuth data, stdio command, args, URL path/query/fragment |
| MCP Nous catalog evidence | `GET /api/mcp/catalog` | `source/hermes_cli/web_routers/mcp.py:371` | Catalog entry names only, used solely for Nous-approved provenance correlation | transport command/args, bootstrap, install URL/ref, post-install text, auth requirements, environment data |
| Model catalog | `GET /api/model/options?explicit_only=true&include_unconfigured=false&refresh=false` | `source/hermes_cli/web_server.py:6321` | Explicit provider/model IDs, availability, current selection, bounded curated model inventory | key environment hints, auth details, warnings, pricing internals, custom endpoint probes/results |
| Current model metadata | `GET /api/model/info` | `source/hermes_cli/web_server.py:6216` | Current provider/model identity and supported vision metadata | base URL and unallowlisted metadata |
| Auxiliary assignments | `GET /api/model/auxiliary` | `source/hermes_cli/web_server.py:6443` | Main model plus supported vision, compression, and title-generation assignments | base URLs and unsupported task slots |
| MoA | `GET /api/model/moa` | `source/hermes_cli/web_server.py:6494` | Bounded preset names, enabled state, reference model IDs/count, aggregator model | trace/config internals and controls not represented by the existing surface contract |

## Explicitly unused routes

The adapter does not call:

- `GET /api/config`
- `GET /api/config/raw`
- OAuth routes
- skill hub search/preview/scan/install/update/uninstall routes
- provider validation endpoints
- environment save routes
- setup/post-setup routes
- MCP test/auth/install routes
- any POST, PUT, PATCH, or DELETE route

The general and raw config routes are excluded because they are broader than this surface and can contain nested configuration not proven safe for this renderer contract.

## Unsupported mutation matrix

| Adapter method | Result |
|---|---|
| `createSkill()` | `unavailable / skill-create` |
| `editSkill()` | `unavailable / skill-edit` |
| `installSkill()` | `unavailable / skill-install` |
| `configureToolset()` | `unavailable / toolset-configure` |
| `configureModels()` | `unavailable / model-configure` |
| `configureMcp()` | `unavailable / mcp-configure` |

No preview or commit token is manufactured for unsupported writes. A separate, source-proven write assignment is required before any mutation can be added.

## Provenance and trust mapping

The four trust labels remain distinct in the existing renderer contract:

- `nous-approved`
- `workbench-reviewed`
- `external-unreviewed`
- `user-created`

Mapping rules:

### Skills

- upstream `provenance: bundled` -> `nous-approved`
- upstream `provenance: agent` -> `user-created`
- upstream `provenance: hub` -> `external-unreviewed`
- a hub/external skill becomes `workbench-reviewed` only through an explicit adapter `WorkbenchReviewAttestation`

### Search/Extract toolset providers

- provider inclusion, readiness, and capability advertisements come from the upstream web toolset matrix
- provider trust defaults to `external-unreviewed`
- `requires_nous_auth` is not treated as trust evidence
- `workbench-reviewed` requires an explicit adapter attestation

### MCP

- a configured server whose exact name appears in the upstream route explicitly documented as the Nous-approved MCP catalog is `nous-approved`
- a configured non-catalog server is `user-created`
- an explicitly attested non-catalog server is `workbench-reviewed`

### Models

The existing `ModelOption` renderer contract has no provenance field. The live adapter does not invent one or translate provider authentication into approval.

Nous approval is never translated into Chris approval, Codex approval, or independent Workbench review.

## Secret and command isolation

The adapter never returns:

- secret values
- environment values
- stored API keys
- OAuth tokens or flow state
- authorization or provider headers
- cookies
- arbitrary setup/bootstrap/post-install commands
- MCP stdio commands or arguments
- filesystem skill paths
- custom endpoint credentials
- raw response bodies in errors

Specific defenses:

- response JSON is parsed only after a streaming 1 MiB byte cap
- declared over-limit responses are rejected before body read
- collections and text are bounded again after parsing
- only allowlisted keys are read from each source
- credential-like text is redacted
- HTTP MCP endpoints are reduced to credential-free origin only
- environment objects contribute validated variable names only
- MCP catalog data contributes exact names only
- malformed fields are ignored and reported as partial
- HTTP error bodies are never echoed
- no renderer storage, console logging, clipboard, cookies, or persistence APIs are used

## Cancellation, duplicates, and partial results

- Pre-dispatch and in-flight cancellation return `cancelled`.
- Concurrent reuse of a profile/correlation identity returns `duplicate`.
- Completed identities are retained in a bounded 128-entry replay window.
- Each route has an independent source report.
- A failed/unavailable source does not erase ready sources.
- Partial provider readiness remains `partial`; it is not promoted to ready.
- Skill content reads use batches of four and are capped at 32.
- Overall snapshot state is derived from source states without hiding failures.

## Verification

Focused command:

```text
vitest run Modules/HermesExtensionSettingsLive --reporter=verbose
```

Result:

```text
Test Files  1 passed (1)
Tests       9 passed (9)
```

Covered:

- all four provenance/trust labels
- no Chris/Codex approval implication
- secret/header/OAuth/environment-value/command redaction
- partial source preservation
- explicit partial provider readiness
- malformed responses
- AbortSignal cancellation
- concurrent duplicate and completed replay rejection
- unsupported mutations
- response byte and collection bounds
- profile/correlation propagation

Strict module-scoped TypeScript command:

```text
tsc --noEmit
  --target ES2022
  --lib ES2022,DOM,DOM.Iterable
  --module ESNext
  --moduleResolution Bundler
  --strict
  --skipLibCheck
  --esModuleInterop
  --allowSyntheticDefaultImports
  --types node
  Modules/HermesExtensionSettingsLive/*.{ts,tsx}
```

Result: passed.

Final forbidden-capability audit found no non-GET method, general/raw config route, OAuth route, post-setup route, browser persistence, console logging, unsafe HTML, clipboard, or cookie access in the module.

## Changed files

- `src/Modules/HermesExtensionSettingsLive/contracts.ts`
- `src/Modules/HermesExtensionSettingsLive/HermesExtensionSettingsLiveAdapter.test.ts`
- `src/Modules/HermesExtensionSettingsLive/HermesExtensionSettingsLiveAdapter.ts`
- `src/Modules/HermesExtensionSettingsLive/index.ts`
- `src/Modules/HermesExtensionSettingsLive/normalization.ts`
- `docs/coordination/EXTENSION-SETTINGS-LIVE-ADAPTER-REPORT.md`

## Super handoff

Super should read and integrate from:

`C:\Users\clsor\Documents\Codex\HermesAgent\docs\coordination\EXTENSION-SETTINGS-LIVE-ADAPTER-REPORT.md`

Integration should inject `HermesExtensionSettingsLiveAdapter` into the Advanced Extensions Labs surface without modifying this lane's trust mapping or secret/command omission rules.
