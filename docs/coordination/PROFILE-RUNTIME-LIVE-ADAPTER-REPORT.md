# Hermes Profile Runtime Live Adapter Report

Date: 2026-08-09
Lane: isolated live Profile Runtime read adapter
Contract: hermes-profile-runtime-live/v1

## Outcome

Implemented an isolated, versioned, injected live adapter in src/Modules/HermesProfileRuntimeLive. It reads only source-confirmed Hermes routes, converts every upstream response into a bounded allowlisted model, preserves the caller profile and correlation identity on every result, and exposes unsupported capabilities as explicitly unavailable.

No live Hermes request was made. Tests use only deterministic injected transports and explicitly prove that global fetch is not called. No mutation is implemented.

Super handoff report path:

C:\Users\clsor\Documents\Codex\HermesAgent\docs\coordination\PROFILE-RUNTIME-LIVE-ADAPTER-REPORT.md

## Owned scope and isolation

Created only:

- src/Modules/HermesProfileRuntimeLive/BoundedSemaphore.ts
- src/Modules/HermesProfileRuntimeLive/contracts.ts
- src/Modules/HermesProfileRuntimeLive/index.ts
- src/Modules/HermesProfileRuntimeLive/LiveHermesProfileRuntimeAdapter.ts
- src/Modules/HermesProfileRuntimeLive/normalization.ts
- src/Modules/HermesProfileRuntimeLive/LiveHermesProfileRuntimeAdapter.test.ts
- docs/coordination/PROFILE-RUNTIME-LIVE-ADAPTER-REPORT.md

No existing HermesProfileRuntime file, HermesSessionAdmin file, HermesSystemWorkspace.tsx, App.tsx, host, package/build file, installer, Docker file, shared configuration, or other module/report was edited.

The supplied project directory has no discoverable Git metadata at its root, so scoped file enumeration—not Git status—is the change-set evidence for this lane.

## Public exports

src/Modules/HermesProfileRuntimeLive/index.ts exports:

- LiveHermesProfileRuntimeAdapter
- HERMES_PROFILE_RUNTIME_LIVE_ADAPTER_VERSION
- HERMES_PROFILE_RUNTIME_LIVE_BOUNDS
- the versioned request/result, snapshot, section, capability, error, safe model, transport, readable-body, and adapter option types

The constructor requires LiveAdapterOptions.transport. There is no default transport and no reference to global fetch.

Primary calls:

- load(request, signal) returns the complete bounded read snapshot.
- unavailable(request, capabilityId, signal) returns a correlated unavailable result without transport activity. It exists so unsupported saves/deletes/grants can be represented honestly; it performs no mutation.

## Architecture and integration seam

The module boundary is:

React coordinator -> LiveHermesProfileRuntimeAdapter -> injected host/same-origin transport -> confirmed Hermes GET routes

A future integration owner should:

1. Construct one adapter for the intended runtime connection and inject a same-origin transport wrapper.
2. Give every call the exact contract version, selected profileId, a new bounded correlationId, and an AbortSignal.
3. Map the transport response to the exported streaming response contract: status, ok, headers.get, and body.getReader.
4. Render only the adapter safe result types. Do not pass raw route bodies to React.
5. Treat ready, partial, unavailable, error, and cancelled as distinct UI states.
6. Generate a new correlation for every retry. Correlations are single-use within an adapter instance.
7. Dispose of connection/session state outside this module. The adapter stores no cookies, credentials, tokens, passwords, environment values, or raw response objects.

No integration code was added in this lane.

## Source-confirmed route matrix

| Capability | Method and route used | Profile ownership available upstream | Source evidence | Adapter output |
|---|---|---|---|---|
| Profile inventory | GET /api/profiles | Returns all profiles, not request-scoped | source/hermes_cli/web_routers/profiles.py:373-382 | Bounded safe inventory; requested profile must be present |
| Active profile | GET /api/profiles/active | Global sticky active plus current dashboard-process profile | source/hermes_cli/web_routers/profiles.py:498-516 | stickyProfileId and currentProcessProfileId; they may honestly differ |
| SOUL context document | GET /api/profiles/{name}/soul | Profile name is in the route path | source/hermes_cli/web_routers/profiles.py:631-639 | Plain untrusted text, existence flag, kind soul |
| Configuration metadata | GET /api/config/schema?profile={name} | Source enters a profile-scoped config context | source/hermes_cli/web_server.py:6188-6195 | Allowlisted field/category/type/options metadata only; never values/defaults/env keys |
| Provider connection status | GET /api/providers/oauth?profile={name} | Source enters a profile-scoped provider context | source/hermes_cli/web_server.py:10015-10058 | Provider id/label/flow, connected boolean, expiry, refresh-token-presence boolean |
| Login provider metadata | GET /api/auth/providers | Dashboard/session-wide | source/hermes_cli/dashboard_auth/routes.py:152-174 | Provider id/label and password-support boolean |
| Signed-in account status | GET /api/auth/me | Current authenticated dashboard session | source/hermes_cli/dashboard_auth/routes.py:778-791 | Signed-in flag, display name, provider id, expiry; HTTP 401 becomes signed-out |

Important exclusions:

- GET /api/config is deliberately not called because it returns configuration values.
- Custom endpoint/configuration routes are not called because their response shapes contain secret-adjacent data such as API-key previews.
- Raw OAuth provider status includes source, source_label, token_preview, commands, hints, and URLs. All are discarded.
- Raw profile inventory includes filesystem paths and has_env. Both are discarded.
- Raw account status includes user id, email, and organization id. All are discarded.

## Capability matrix

| Adapter capability | State | Reason |
|---|---|---|
| read-profile-inventory | Available | Confirmed GET route |
| read-active-profile | Available | Confirmed GET route |
| read-soul-document | Available | Confirmed profile-scoped GET route |
| read-configuration-metadata | Available | Confirmed profile-scoped schema GET route |
| read-provider-status | Available | Confirmed profile-scoped OAuth status GET plus auth metadata/status routes |
| read-account-status | Available | Confirmed authenticated GET route; 401 is signed out |
| read-persona-document | Unavailable | No source-confirmed profile-scoped persona read route was found |
| read-context-document | Unavailable | No source-confirmed additional profile context-document read route was found |
| select-active-profile | Unavailable | Mutation acknowledgement, conflict semantics, and profile ownership are not all proven |
| create-profile | Unavailable | Mutation acknowledgement, conflict semantics, and profile ownership are not all proven |
| rename-profile | Unavailable | Mutation acknowledgement, conflict semantics, and profile ownership are not all proven |
| delete-profile | Unavailable | Mutation acknowledgement, conflict semantics, and profile ownership are not all proven |
| save-document | Unavailable | A SOUL PUT exists, but transactional conflict behavior and complete ownership semantics are not proven |
| save-configuration | Unavailable | No mutation was accepted into this read-only lane |
| grant-permission | Unavailable | No source-confirmed profile-scoped grant contract was found |

The adapter never issues POST, PUT, PATCH, or DELETE.

## Identity, freshness, and duplicate handling

Every valid result echoes adapterVersion, profileId, correlationId, optional expectedRevision, and the observed revision when a complete sanitized snapshot was formed.

The upstream routes do not provide a common correlation/revision envelope. The adapter therefore makes these explicit, bounded local guarantees:

- A top-level upstream profile, profileId, or profile_id, when present, must equal the request profile.
- A top-level upstream correlationId or correlation_id, when present, must equal the request correlation.
- The requested profile must appear in the successful inventory before a snapshot is returned.
- Explicit identity conflicts reject the whole snapshot; they are never downgraded to partial.
- The revision is a deterministic FNV-1a identifier over the sanitized snapshot. It is an observed client-side revision, not a server transaction/version token.
- If expectedRevision differs, the result is stale-response with no snapshot value.
- Correlations are single-use per adapter instance. Concurrent duplicates and bounded-history replays return duplicate-correlation without starting another load.
- The correlation ledger retains at most 512 entries and never evicts active entries.
- Pending loads are capped at 16.

Because the server does not echo a universal profile/correlation/revision envelope, local binding cannot prove a server-side transaction boundary. Mutations remain unavailable for exactly this reason.

## Bounds and confidentiality controls

| Item | Bound |
|---|---:|
| Response body | 512 KiB |
| SOUL response body | 128 KiB |
| Profiles | 64 |
| Documents | 3 |
| Configuration fields | 128 |
| Options per configuration field | 64 |
| OAuth providers | 64 |
| Login providers | 32 |
| Notices | 32 |
| Identifier/correlation strings | 128 characters |
| Labels | 256 characters |
| Descriptions | 2,048 characters |
| Document text | 131,072 characters |
| Transport concurrency | default 3, hard maximum 6 |
| Pending loads | maximum 16 |
| Correlation history | 512 |

Collections and strings are rejected when oversized; they are not silently truncated. The streaming reader rejects and cancels a body as soon as accumulated bytes exceed the route bound, including when Content-Length is missing. A too-large declared Content-Length is rejected before reading.

Raw objects are parsed as unknown and normalized through explicit allowlists. The adapter never returns:

- filesystem paths
- has_env or environment names/values
- configuration values or defaults
- API keys or key previews
- tokens or token previews
- passwords
- auth-source paths/labels
- provider CLI/disconnect commands
- complete provider configuration
- user id, email, or organization id
- unknown upstream fields

Secret-typed or secret-key-named configuration fields remain visible only as metadata with sensitive: true and an empty options array.

SOUL content is intentionally preserved as untrusted text. This module has no React renderer and performs no HTML interpretation. Integration must render it through ordinary React text nodes or text controls, never dangerouslySetInnerHTML.

## Failure semantics

- Critical profile inventory failure or absence of the requested profile: whole-result error.
- Explicit profile/correlation mismatch: whole-result error.
- Stale expected revision: whole-result error.
- Noncritical route HTTP, malformed, or oversized failure: partial snapshot with that section marked error.
- Confirmed route returning 404/405: partial snapshot with that section marked unavailable.
- /api/auth/me returning 401: available signed-out account state.
- Abort before or during queued/active transport work: correlated cancelled.
- Duplicate/replayed correlation: duplicate-correlation with no new transport work.
- Pending-load bound reached: busy.
- Injected transport exception: safe transport-error; raw exception/body text is not returned.

All seven reads share the caller AbortSignal. A bounded, abort-aware semaphore caps active transport operations and removes cancelled waiters.

## Verification

Focused Vitest command:

    cd C:\Users\clsor\Documents\Codex\HermesAgent\src
    & 'C:\Users\clsor\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' .\node_modules\vitest\vitest.mjs run Modules\HermesProfileRuntimeLive

Result:

    Test Files  1 passed (1)
    Tests       11 passed (11)
    Duration    376ms

Strict TypeScript command, scoped to the complete owned module including tests:

    cd C:\Users\clsor\Documents\Codex\HermesAgent\src
    $moduleFiles = Get-ChildItem -LiteralPath .\Modules\HermesProfileRuntimeLive -File | Where-Object { $_.Extension -in '.ts','.tsx' } | ForEach-Object FullName
    & 'C:\Users\clsor\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' .\node_modules\typescript\bin\tsc --noEmit --incremental false --target ES2022 --module ESNext --moduleResolution Bundler --strict --skipLibCheck --esModuleInterop --allowSyntheticDefaultImports --jsx react-jsx --lib ES2022,DOM,DOM.Iterable $moduleFiles

Result: exit code 0, no diagnostics.

The 11 tests cover normal full read, partial failure, route unavailability, unsupported destructive capability with zero transport calls, malformed critical data, cancellation and exact signal propagation, concurrent/replayed duplicates, oversized streaming data without Content-Length, oversized collections, cross-profile and cross-correlation responses, stale revision, bounded concurrency, unsafe HTML-shaped text remaining text, removal of secret/private fields, and proof that global fetch is never called.

No broad application compile was claimed. Verification is intentionally scoped to the owned isolated module so unrelated modules are neither modified nor represented as this lane result.

## Integration risks and confirmed assumptions

Confirmed from source:

- Every route listed as available exists with GET semantics.
- Config schema and OAuth provider routes accept an optional profile query.
- SOUL read is profile-named.
- Active and current profile values are intentionally distinct concepts.
- OAuth raw status includes sensitive/secret-adjacent fields that must not cross the adapter.
- Auth /me is session-scoped and returns 401 when unauthenticated.

Adapter assumptions that integration must validate:

- Relative /api paths reach the intended Hermes runtime through the same-origin transport.
- The injected transport applies the intended authenticated dashboard session without exposing credentials to this module.
- A single adapter instance represents one intended runtime connection/security context.
- The local deterministic revision is suitable for read staleness detection only; it cannot authorize mutations.
- Global inventory, active-profile, and auth routes lack a common server correlation/profile envelope, so the adapter can reject explicit mismatches but cannot manufacture server-side ownership proof.

These assumptions are why this lane remains read-only and why all saves, deletes, profile selection, and grants are unavailable.

## Handoff

Super should integrate from src/Modules/HermesProfileRuntimeLive/index.ts and use this report as the route/security contract. No live integration, login, session change, mutation, or adjacent workspace edit was performed.

Exact report path:

C:\Users\clsor\Documents\Codex\HermesAgent\docs\coordination\PROFILE-RUNTIME-LIVE-ADAPTER-REPORT.md
