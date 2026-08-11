# Agent Panel Registry isolated lane report

## Outcome

Implemented a provider-neutral, renderer-only registry for arbitrary AI panel descriptors under `src/Modules/AgentRegistry`.

Current format: `agent-panel-registry/v1`.

Render boundary: `agent-panel-render/v1`.

No React application integration, provider connection, authentication action, local storage, host change, package change, docking change, existing agent-panel change, or shared configuration change was performed.

## Public exports

The public entrypoint is `src/Modules/AgentRegistry/index.ts` and exports:

- `AgentPanelRegistry`
- `AgentPanelFactoryCatalog`
- `createAgentPanelDockRegistrations`
- `serializeAgentPanelRegistry`, `parseAgentPanelRegistry`, and `createAgentPanelRegistryFromSerialized`
- `validateAgentPanelDescriptor` and `validateAgentPanelRegistrySnapshot`
- `createDeterministicAgentPanelFixtures` and `DETERMINISTIC_AGENT_PANEL_FIXTURES`
- all versioned contracts and `AGENT_PANEL_REGISTRY_LIMITS`

## Registry model

Each descriptor contains:

- stable panel identity;
- open, validated provider-kind identity;
- display label, short label, description, icon key, and accent key;
- provider-neutral capabilities and availability;
- lifecycle and connection state with non-secret detail;
- authentication disposition with non-secret detail;
- dock placement intent, preferred mode, minimum dimensions, and placement priority;
- versioned render boundary and stable factory key; and
- enabled state.

The provider kind is deliberately not a closed enum. New providers can be registered without adding provider-specific registry or layout branches.

## Operations and identity

`AgentPanelRegistry` supports add, remove, enable, disable, move, exact reorder, runtime-status patch, lookup, ordered listing, and immutable snapshots.

- Duplicate panel identities are rejected.
- Reorder input must contain every registered identity exactly once and may not contain unknown identities.
- Stable identity, provider kind, display metadata, dock intent, and factory key cannot be changed through runtime-status patching.
- Disabled panels remain persisted in stable order but are excluded from enabled-only lists and dock projections.
- Unavailable and retired providers remain honest descriptors and resolve through the neutral unavailable-renderer seam.
- The registry is bounded at 4,096 descriptors for untrusted-input safety; its algorithms and types contain no fixed provider count or named-provider branches.

## Persistence and security

Persistence is explicit and caller-owned. This module returns/parses JSON text and has no `localStorage`, session storage, IndexedDB, cookie, filesystem, or host integration.

- Only `agent-panel-registry/v1` is accepted.
- No migrations, legacy schemas, compatibility branches, or automatic upgrades exist.
- Exact current-format fields are required; unknown fields are rejected.
- Serialized input is limited to 2 MiB and descriptor/capability/text/numeric fields are bounded.
- Duplicate descriptors, duplicate capabilities, malformed identities, missing order entries, unknown order entries, invalid enums, control characters, and excessive nesting are rejected.
- Secret-, credential-, cookie-, raw-client-, native-process-, and native-handle-shaped property names are rejected before descriptor normalization.
- Descriptors contain only serializable data. Factories are stored separately in `AgentPanelFactoryCatalog` and are referenced by `factoryKey` only.
- Provider clients, native handles, authentication values, and factories are never part of registry snapshots or JSON.
- Display markup remains inert JSON text. The future renderer must continue to render metadata as React text, never as HTML.

The source audit found no network calls, browser storage, cookie access, HTML injection, parent-module imports, or live-provider imports. Audit keyword hits are limited to the rejection guard and its negative tests.

## Factory and docking seam

`AgentPanelFactoryCatalog` is an in-memory renderer factory catalog. A factory receives only:

- the validated panel descriptor; and
- dock controls as `ReactNode`.

`createAgentPanelDockRegistrations` returns structural `{ id, label, render }` registrations in registry order. This matches the current read-only `DockPanelRegistration` shape without importing or modifying `WorkbenchDocking`.

Missing factories and unavailable/retired panels invoke a provider-neutral unavailable callback. The host owns placeholder appearance and provider integration; no provider-specific layout logic exists here.

## Deterministic fixtures

The module includes deterministic Hermes, Codex, Claude, ChatGPT, Scarlett, and Ali descriptors. They:

- use the same provider-neutral descriptor builder;
- contain synthetic lifecycle, connection, and authentication metadata;
- explicitly say they are not live integrations;
- contain factory keys only; and
- do not import or contact any provider module or service.

## Read-only integration assumptions

Read-only inspection established that:

- `DockGroup` accepts arbitrary `DockPanelRegistration[]` values with `{ id, label, render }`;
- `App.tsx` currently constructs the Hermes and Codex registrations directly; and
- existing Hermes and Codex panels accept dock controls through their own props.

Super can later replace the hard-coded application registration array with a projection from this registry, register live factories at the application composition boundary, choose a persistence owner, and supply neutral unavailable rendering. None of that integration was performed in this lane.

## Verification

Run from `C:\Users\clsor\Documents\Codex\HermesAgent\src` with the bundled Node runtime:

- Focused Vitest: `node node_modules/vitest/vitest.mjs run Modules/AgentRegistry`
  - Result: 3 test files passed, 23 tests passed.
- Strict TypeScript: `node node_modules/typescript/bin/tsc -b --pretty false`
  - Result: exit code 0 with no diagnostics.

Tests cover arbitrary panel counts, duplicate rejection, stable ordering, move/reorder, add/remove, enable/disable, runtime-only patches, future provider kinds, current-format round trips, safe serialization, inert markup, forbidden fields, malformed data, bounds, unavailable factories/providers, and provider-neutral projection.

## Changed files

- `src/Modules/AgentRegistry/contracts.ts`
- `src/Modules/AgentRegistry/registryValidation.ts`
- `src/Modules/AgentRegistry/AgentPanelRegistry.ts`
- `src/Modules/AgentRegistry/registryPersistence.ts`
- `src/Modules/AgentRegistry/AgentPanelFactoryCatalog.ts`
- `src/Modules/AgentRegistry/deterministicFixtures.ts`
- `src/Modules/AgentRegistry/index.ts`
- `src/Modules/AgentRegistry/AgentPanelRegistry.test.ts`
- `src/Modules/AgentRegistry/registryPersistence.test.ts`
- `src/Modules/AgentRegistry/AgentPanelFactoryCatalog.test.ts`
- `docs/coordination/AGENT-PANEL-REGISTRY-REPORT.md`

## Explicitly untouched

- `src/Modules/WorkbenchDocking/**`
- `src/Modules/AgentDock/**`
- `src/Modules/CodexAgent/**`
- `src/app/App.tsx`
- `HermesSessionAdmin`
- `src/Modules/HermesSystem/HermesSystemWorkspace.tsx`
- all other modules, reports, host files, installers, Docker files, packages, and shared configuration

## Super handoff

Exact report path:

`C:\Users\clsor\Documents\Codex\HermesAgent\docs\coordination\AGENT-PANEL-REGISTRY-REPORT.md`

This lane stops after this report. Super owns application integration.
