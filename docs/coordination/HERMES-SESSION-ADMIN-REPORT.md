# Hermes Session Administration Module Report

**Date:** 2026-08-09  
**Module contract:** `hermes-session-admin/v1`  
**Integration status:** isolated and build/test ready; no live Hermes adapter or route wiring was added.

## Outcome

Created one self-contained React/TypeScript session-administration module under `src/Modules/HermesSessionAdmin`. It owns advanced session-management surfaces that were missing from Workbench while deliberately excluding the already-implemented cross-profile FTS search and pinning surfaces.

The module exports an injected adapter contract, deterministic fake adapter, accessible `HermesSessionAdminWorkspace`, validation/profile guards, and correlated-operation coordinator. No live Hermes calls were made, no session content was read, no sessions were changed, no dependencies were added, and no existing application, module, host, dock, package, build, installer, or documentation file was edited.

## Public exports

`src/Modules/HermesSessionAdmin/index.ts` exports:

- `HERMES_SESSION_ADMIN_CONTRACT_VERSION` and `sessionAdminBounds`.
- All request/result/session/tree/statistics/import/export/model-lock contract types.
- `HermesSessionAdminAdapter`.
- `DeterministicFakeHermesSessionAdminAdapter` and `createDeterministicFakeSessionAdminAdapter`.
- `SessionAdminOperationCoordinator`, `DuplicatePendingOperationError`, and `restoreFocus`.
- `SessionAdminValidationError`, `assertProfileMatch`, and `validateResultProfile`.
- `HermesSessionAdminWorkspace`, `AccessibleDialog`, and `ImportedTextPreview`.

The workspace requires `{ profileId, adapter }`; there is no implicit live adapter. Every adapter request extends `CorrelatedProfileRequest`, and every result envelope returns the same `profileId`, `correlationId`, and contract version. `validateResultProfile` rejects cross-profile, cross-correlation, and version mismatches before UI state is accepted.

## Implemented behavior

- Bounded list (`200`), multi-select (`50`), descendant nodes (`128`), descendant depth (`8`), destructive set (`100`), and export set (`50`).
- Branch/fork creation with explicit source, new identity, title, mode, profile, and correlation.
- Bounded recursive descendant trees with a visible `truncated` signal.
- Delete preview and prune preview tokens followed by distinct commit requests and typed UI confirmation. Truncated delete previews cannot commit.
- Import validation capped at `512,000` UTF-8 bytes, `100` sessions, `500` messages per session, and `16,384` characters per text field; commit requires the validation token.
- Export request/result flow returns JSON text and safe metadata; saving/downloading remains a coordinator responsibility.
- Statistics retain `reported`, `partial`, or `unavailable` quality. Storage size is honestly unavailable in the fake rather than inferred.
- Model-lock set and clear use reviewed expected-current values. Replacement fails unless `confirmReplace` is true; clearing requires a separate confirmation literal.
- One correlated pending operation per logical key, visible cancellation, adapter-side duplicate correlation rejection, abort-before-mutation checks, and cleanup on unmount.
- Explicit `success`, `partial`, `unavailable`, `error`, and `cancelled` result states.
- Deterministic partial-delete failures and configurable partial/unavailable/error operations for integration testing.

Imported titles and message text are copied only from validated primitive strings into fresh objects. React renders them through normal text children (`<h4>`, `<pre>`, `<textarea>`); the module contains no `dangerouslySetInnerHTML`, HTML parser, Markdown renderer, or DOM injection.

## Accessibility and layout

- Native buttons, inputs, checkboxes, selects, labels, headings, alert/status regions, and modal `role="dialog"`/`aria-modal="true"` semantics.
- Dialogs take focus, trap Tab/Shift+Tab, close on Escape when not busy, and restore focus to the previously active element on unmount.
- Destructive dialog backdrops close only on a direct backdrop action and never while a commit is pending.
- CSS is module-local, uses relative units and `clamp`, wraps long untrusted identifiers/text, bounds scroll regions, collapses controls on narrow layouts, and respects reduced-motion preferences. No global style was edited.

## Upstream route and method assumptions

These are source-read assumptions for the future coordinator, not code exercised by this module:

| Contract operation | Observed upstream surface | Integration note |
| --- | --- | --- |
| List/session detail | `GET /api/sessions`, `GET /api/sessions/{session_id}` | Dashboard routes accept profile context; the injected adapter must return explicit profile identity even if upstream omits it. |
| Branch/fork | `POST /api/sessions/{session_id}/fork` in `source/gateway/platforms/api_server.py` | Upstream calls this “fork” but implements CLI branch lineage semantics: source is ended as branched, child copies messages and has `parent_session_id`. The module keeps `branch`/`fork` explicit; coordinator must map semantics honestly. |
| Descendants | `GET /api/sessions/{session_id}/latest-descendant` | Current dashboard route returns one latest path, not a complete tree. A live adapter must either construct a bounded tree from supported detail/list lineage data or return `unavailable`/`partial`; it must not claim a full tree from this endpoint alone. |
| Bulk delete | `POST /api/sessions/bulk-delete` with `{ ids, profile }` | Upstream caps at 500, silently skips unknown IDs, orphans children, and can delete active/archived explicit selections. Module uses a stricter 100-ID reviewed preview and requires a separately confirmed commit. |
| Import | `POST /api/sessions/import` with JSON `{ sessions, profile }` | Upstream validates a session payload and imports rows/messages. Coordinator must send the validated bounded payload; raw renderer text must never bypass validation. |
| Statistics | `GET /api/sessions/stats?profile=...` | Upstream reports total, active-store, archived, messages, and optionally by-source. Ended and storage-byte statistics must remain unavailable unless a supported source is added. |
| Export | `GET /api/sessions/{session_id}/export?profile=...` | Upstream streams one session JSON response with keyset-paged messages. Multi-export is a bounded coordinator aggregation; partial per-session failures must stay visible. |
| Prune | `POST /api/sessions/prune` with `SessionPrune` fields such as `older_than_days`, `profile`, and optional filters | Current endpoint is destructive and has no dedicated preview route. Coordinator must build a read-only candidate preview from supported listing/detail data and bind the reviewed set before commit, or report preview/commit unavailable. |
| Model-lock set | `POST /api/sessions/{session_id}/model` | Gateway acknowledges and persists a confirmed runtime lock. Profile routing may require the gateway's profile-prefixed route rather than a dashboard `profile` field. |
| Model-lock clear | No dedicated, source-confirmed clear route found | A live adapter must return `unavailable` until the coordinator verifies an upstream clearing method with acknowledged result semantics. It must not simulate success by sending an empty model. |

## Coordinator integration seam

The coordinator should implement `HermesSessionAdminAdapter` in a separate approved lane and pass it with the active profile to `HermesSessionAdminWorkspace`. It must:

1. Resolve the correct dashboard vs. persistent-session gateway for the selected profile.
2. Translate the v1 bounded requests to upstream route shapes without dropping profile or correlation identity.
3. Add/verify profile identity on every normalized result and reject mismatches before returning.
4. Preserve preview-token binding across destructive preview/commit requests; if upstream lacks atomic preview tokens, detect drift and return `partial` or `error` rather than overstate safety.
5. Carry `AbortSignal`, avoid retrying destructive commits automatically, and deduplicate correlations at the bridge boundary.
6. Report unsupported tree, prune-preview, model-clear, statistics, or export aggregation semantics as `unavailable`/`partial`.
7. Decide how export text is saved. The renderer intentionally returns text only and performs no download or filesystem access.

No integration changes were made to `App.tsx`, HermesSessions, HermesGateway, AgentDock, docking, host, launchers, or build configuration.

## Verification

### Focused module tests

Command (using the bundled Node runtime because `node`/`npx` are not on the shell PATH):

```powershell
& 'C:\Users\clsor\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' .\node_modules\vitest\vitest.mjs run Modules\HermesSessionAdmin
```

Result: **PASS** — 3 test files, 18 tests, 0 failures. Coverage includes every adapter operation, profile rejection, correlation checks, descendant/prune/export/import bounds, cancellation, duplicate correlations and IDs, unsafe imported text, separate destructive confirmation, model replacement confirmation, unavailable/error results, partial deletion, modal semantics, and focus restoration.

### Module-scoped strict TypeScript compile

```powershell
$moduleFiles = Get-ChildItem .\Modules\HermesSessionAdmin -File |
  Where-Object { $_.Extension -in '.ts', '.tsx' } |
  ForEach-Object FullName
& 'C:\Users\clsor\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' `
  .\node_modules\typescript\bin\tsc --noEmit --incremental false `
  --target ES2022 --module ESNext --moduleResolution Bundler --strict --skipLibCheck `
  --esModuleInterop --allowSyntheticDefaultImports --jsx react-jsx `
  --lib ES2022,DOM,DOM.Iterable $moduleFiles
```

Result: **PASS** — no diagnostics and no emitted files.

### Repository TypeScript compile

```powershell
& 'C:\Users\clsor\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' `
  .\node_modules\typescript\bin\tsc -p .\tsconfig.app.json --noEmit --incremental false
```

Result: **FAIL (outside owned scope)** — the compiler reported existing errors exclusively under `src/Modules/HermesExtensionSettings`, including literal-bound parameter typing and an unresolved `expensiveModelConfirmed` name. After the two module-local diagnostics found on the first run were corrected, the isolated module compile is green. Those other files were not edited.

## Changed files

- `src/Modules/HermesSessionAdmin/contracts.ts`
- `src/Modules/HermesSessionAdmin/validation.ts`
- `src/Modules/HermesSessionAdmin/OperationCoordinator.ts`
- `src/Modules/HermesSessionAdmin/FakeHermesSessionAdminAdapter.ts`
- `src/Modules/HermesSessionAdmin/HermesSessionAdminWorkspace.tsx`
- `src/Modules/HermesSessionAdmin/HermesSessionAdminWorkspace.css`
- `src/Modules/HermesSessionAdmin/index.ts`
- `src/Modules/HermesSessionAdmin/FakeHermesSessionAdminAdapter.test.ts`
- `src/Modules/HermesSessionAdmin/OperationCoordinator.test.ts`
- `src/Modules/HermesSessionAdmin/HermesSessionAdminWorkspace.test.tsx`
- `docs/coordination/HERMES-SESSION-ADMIN-REPORT.md`
