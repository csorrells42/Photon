# Hermes Profile Runtime isolated lane report

## Outcome

Implemented a standalone, host-neutral profile/runtime settings module under `src/Modules/HermesProfileRuntime`. No live Hermes calls, profile mutations, permission grants, credential inspection, dependency changes, host integration, or edits outside the assigned module and this report were performed.

Contract: `hermes-profile-runtime/v1`.

## Public exports

The public entrypoint is `src/Modules/HermesProfileRuntime/index.ts` and exports:

- `HermesProfileRuntimeWorkspace` and `HermesProfileRuntimeWorkspaceProps`
- `HermesProfileRuntimeAdapter` and the complete versioned request/response contract
- `DeterministicHermesProfileRuntimeAdapter` and `deterministicHermesProfileRuntimeAdapter`
- `HermesProfileRuntimeCoordinator` and `DuplicateProfileRuntimeOperationError`
- import inspection, request identity, response matching, configuration validation, export, and limit helpers
- `HERMES_PROFILE_RUNTIME_CONTRACT` and `PROFILE_RUNTIME_LIMITS`

## Included surface

- Active-profile selection.
- Profile create, clone, rename, and delete previews. Delete requires an exact typed destructive confirmation and protected profiles remain undeletable.
- Persona, soul, and context drafts with per-document dirty state and explicit caller-owned saves.
- Model, project, worktree, and note intents.
- Bounded profile import inspection and secret-free export preparation.
- Hermes agent terminal-backend selection and availability status.
- Computer-use status plus separate permission-preview and permission-confirmation typed events.
- Provider OAuth, credential-pool slot count, and custom-endpoint origin status without credential values.
- Schema-driven non-secret configuration with manageable, delegated, and unsupported dispositions.
- Explicit loading/pending, cancellation, duplicate prevention, ready/partial/error/unavailable states, accessible dialogs and labels, responsive container queries, zoom/large-text resilience, and reduced-motion behavior.

## Security and isolation checks

- Every adapter request and response carries contract version, profile ID, correlation ID, and revision. The coordinator rejects version, profile, and correlation mismatches.
- Active-profile selection returns only the target identity; the caller performs a fresh target-scoped load. A target snapshot is not returned in the old profile response envelope.
- Fake adapter updates use optimistic revision checks and reject stale saves.
- Operation previews are profile-bound, payload-bound, single-use tokens. Delete, import, and permission grants require the exact preview phrase.
- A permission row/button only requests a preview. Grant confirmation is a different adapter method and typed intent.
- Import text is treated as untrusted JSON, limited to 256 KiB, bounded by field/list/depth/text limits, checked for malformed types and secret-bearing keys, and never rendered as HTML.
- React text rendering is used throughout; no `dangerouslySetInnerHTML` or `.innerHTML` path exists.
- Provider snapshots have an exact field allowlist, safe integer slot counts, and HTTP(S) origin-only custom endpoints. No secret-value field exists in the public provider contract.
- Export includes only name, documents, profile intents, and non-secret configuration. Provider, OAuth, credential, permission, and terminal status are not serialized.
- Static audit found no `fetch`, `XMLHttpRequest`, `WebSocket`, browser storage, IndexedDB, cookies, direct HTML rendering, or imports outside the module. Secret-related audit hits are limited to the import rejection regex and its tests.

## Native terminal boundary

The workspace repeatedly labels the two concepts separately:

- Native Workbench terminal: ConPTY-backed developer terminal outside this module.
- Hermes agent terminal backend: the profile-scoped tool-execution backend selected by this surface.

The module neither imports nor changes `NativeTerminal`.

## Upstream route assumptions and integration seams

The coordinator should integrate by importing the module entrypoint and injecting a trusted-host implementation of `HermesProfileRuntimeAdapter`. No HTTP route names are assumed by this lane.

The production adapter is responsible for:

1. mapping host/upstream routes to the versioned request methods;
2. preserving profile ID, correlation ID, and revision in every response;
3. returning status-only provider data and never credential values;
4. performing real authorization and operating-system consent outside React;
5. honoring `AbortSignal` cancellation where the host transport supports it; and
6. reporting unsupported or delegated operations honestly instead of synthesizing success.

`HermesProfileRuntimeWorkspace` accepts `adapter`, `initialProfileId`, optional `initialSnapshot`, and optional `onExport`. The caller owns route placement, production persistence, file download/write behavior, and all trusted-host permission enforcement. The deterministic adapter is for isolated UI/tests only and identifies its data as synthetic.

## Verification

Run from `C:\Users\clsor\Documents\Codex\HermesAgent\src` with the bundled Node runtime:

- Focused Vitest: `node node_modules/vitest/vitest.mjs run Modules/HermesProfileRuntime`
  - Result: 4 test files passed, 18 tests passed.
- TypeScript: `node node_modules/typescript/bin/tsc -b --pretty false`
  - Result: exit code 0, no diagnostics.

Coverage includes profile isolation, stale revisions, destructive confirmations, unsafe and malformed import/config data, permission confirmation separation, ConPTY-versus-Hermes labeling, secret non-round-tripping, bounds, cancellation, duplicate prevention, cross-profile/correlation rejection, and safe HTML escaping.

## Changed files

- `src/Modules/HermesProfileRuntime/contracts.ts`
- `src/Modules/HermesProfileRuntime/runtimeSafety.ts`
- `src/Modules/HermesProfileRuntime/ProfileRuntimeCoordinator.ts`
- `src/Modules/HermesProfileRuntime/DeterministicHermesProfileRuntimeAdapter.ts`
- `src/Modules/HermesProfileRuntime/HermesProfileRuntimeWorkspace.tsx`
- `src/Modules/HermesProfileRuntime/HermesProfileRuntimeWorkspace.css`
- `src/Modules/HermesProfileRuntime/index.ts`
- `src/Modules/HermesProfileRuntime/runtimeSafety.test.ts`
- `src/Modules/HermesProfileRuntime/ProfileRuntimeCoordinator.test.ts`
- `src/Modules/HermesProfileRuntime/DeterministicHermesProfileRuntimeAdapter.test.ts`
- `src/Modules/HermesProfileRuntime/HermesProfileRuntimeWorkspace.test.tsx`
- `docs/coordination/HERMES-PROFILE-RUNTIME-REPORT.md`

## Handoff boundary

Stopped after the isolated module and report. The coordinator owns live adapter implementation, routing, host authorization, and application integration.
