# Hermes Final Integration / Parity Audit

**Audit date and cutoff:** 2026-08-09 20:19:41 -05:00  
**Owned output:** `docs/coordination/HERMES-FINAL-INTEGRATION-AUDIT.md`  
**Upstream checkout:** clean `source/` checkout, branch `main`, commit `8e9ecc1f3e95aaaa3cf2c7582a9da17788a5aa99`  
**Audit mode:** source/report inspection plus one no-emit TypeScript compile; no live service, container, installer, provider, credential, account, or mutation work

## Executive verdict

The core Hermes Workbench path is substantially implemented and has existing recorded live proof for chat recovery, queue drain, sign-in, Serena discovery, health, and the loopback conversation bridge. Current source also passes strict renderer TypeScript compilation.

The remaining problem is not a lack of isolated UI code. It is the distinction between production-mounted live surfaces, production-mounted **deterministic Labs previews**, source-complete live-read adapters that are not connected to those previews, mutation contracts without production controllers, and intentionally delegated upstream administration.

Three findings are decisive:

1. `App.tsx` mounts `HermesSystemWorkspace`, `WorkspaceEditor`, and `AgentDock` at `src/app/App.tsx:289-309`. `WorkspaceEditor` mounts the real `TerminalDock` at `src/Modules/Workspace/WorkspaceEditor.tsx:61`, and `TerminalDock` mounts `HermesConsolePanel` at `src/Modules/NativeTerminal/TerminalDock.tsx:42`. Terminal and Console are integrated source, not unreachable code.
2. `HermesSystemWorkspace` mounts production MCP and Skills at `src/Modules/HermesSystem/HermesSystemWorkspace.tsx:377-378`, then mounts Extension Settings, Profile Runtime, and Session Administration as explicitly labeled Labs previews at lines 379-382.
3. Those Labs surfaces are synthetic: Extension Settings defaults to `fakeHermesExtensionSettingsController` (`HermesExtensionSettingsWorkspace.tsx:28-38`), Profile Runtime constructs `DeterministicHermesProfileRuntimeAdapter` when no adapter is supplied (`HermesProfileRuntimeWorkspace.tsx:22-45`), and Session Administration receives `createDeterministicFakeSessionAdminAdapter()` from `HermesSystemWorkspace.tsx:115,382`.

There is one P0 showcase gate: bind the exact running image identity to the audited/tested candidate immediately before the showcase. There is no evidence-backed reason to call the current core UI a P0 implementation failure. Labs must either remain visibly preview-only or receive the read-only integrations below; they must not be presented as live settings or mutations.

## Scope, exclusions, and evidence rules

This audit compared current `source/**`, `src/**`, `docs/ROADMAP.md`, `docs/HERMES-FUNCTION-PARITY.md`, and every coordination artifact present at the cutoff.

The workspace root has no Git metadata. Upstream `source/` does, and read-only Git inspection confirmed the commit above and a clean checkout.

Roslyn/LSP, debugger/DAP, Java, Usage Intelligence, GitExtensions/source-control expansion, ChatGPT Voice Dock, and unlimited agent-panel registry work are excluded except where they could block Hermes parity. None currently blocks the core Hermes path.

| Status | Meaning |
|---|---|
| **Live and verified (recorded)** | An existing report/document records an executed live check. This audit did not rerun it. |
| **Source-complete, not live-verified** | Current source and focused evidence exist, but relevant live acceptance was not performed here. |
| **Preview/fake only** | User-reachable surface with deterministic sample data/actions, labeled as such. |
| **Intentionally upstream-delegated** | Authenticated upstream dashboard/CLI remains the approved control plane; not a gap. |
| **Actively owned** | Current packet reserves the next change; no completion report existed at cutoff. |
| **Genuinely missing** | No production integration or safe adapter exists for the claimed outcome. |

## Verification performed by this audit

Read-only checks:

- Confirmed upstream commit, branch, last subject/date, and clean status.
- Enumerated upstream HTTP/WebSocket routes and handler symbols.
- Traced current Workbench mount points and references to new isolated modules.
- Read and reconciled all coordination artifacts.
- Ran strict renderer TypeScript without output or incremental state:

```powershell
node .\node_modules\typescript\bin\tsc --noEmit --incremental false -p .\tsconfig.app.json --pretty false
```

Result: **exit code 0, no diagnostics**.

Not performed: Vitest, production build, .NET build/test, live HTTP/WebSocket requests, Docker inspection, launcher/update/shutdown, installer acceptance, provider validation, authenticated data reads, or writes. Existing test/live claims remain attributed to their reports.

## Current integration map

| Surface | Current composition evidence | Classification |
|---|---|---|
| Core chat, streaming, Stop, approvals/prompts, queue, session open | `App.tsx:301-316`; `AgentDock.tsx:344-596,721-957`; `useHermesChat.ts:466-674` | Live-capable and recorded live-verified |
| Auth, health, runtime, compatibility, Serena | `App.tsx:289`; `HermesSystemWorkspace.tsx:100-181,310-375`; `HermesCompatibilityAdapter:evaluateHermesCompatibility` | Live-capable and recorded live-verified |
| Basic sessions, search, rename/archive/delete/resume | `SessionSidebar`, `HermesSessionApi`, `App.tsx:294-298` | Live-capable; targeted acceptance remains release evidence |
| Models and approval mode | `ModelControlPopover` mounted by `AgentDock.tsx:794` | Live-capable |
| MCP, Serena, Skills, Toolsets | `HermesSystemWorkspace.tsx:377-378`; MCP/Skills/Toolsets workspaces | Live-capable; provenance/security adapters present |
| Workspace preview and Monaco | `App.tsx:299`; `WorkspaceEditor`; `MonacoEditor` | Integrated, intentionally read-only |
| Native terminal and Hermes Console | `WorkspaceEditor.tsx:61`; `TerminalDock.tsx:12-60`; `HermesConsolePanel` | Integrated source; not rerun live here |
| Docking splitters | `App.tsx:301-317`; `DockGroup`; `DockGroupLayout` | Integrated source |
| Advanced Composer | `src/Modules/HermesComposer/index.ts`; no reference outside module/tests | Source-complete, not integrated |
| Advanced Extension Settings Labs | `HermesSystemWorkspace.tsx:292,379-380`; default fake controller | Preview/fake only |
| Profiles & Runtime Labs | `HermesSystemWorkspace.tsx:293,379,381`; deterministic default | Preview/fake only |
| Session Administration Labs | `HermesSystemWorkspace.tsx:294,379,382`; explicit fake | Preview/fake only |
| Extension Settings live-read adapter | `HermesExtensionSettingsLiveAdapter.read`; only own tests/exports reference it | Source-complete, not integrated |
| Profile Runtime live-read adapter | `LiveHermesProfileRuntimeAdapter.load`; only own tests/exports reference it | Source-complete, not integrated |

The Labs banner is honest: `HermesSystemWorkspace.tsx:274-278,379` labels all three sections "labs preview" and says deterministic sample data cannot change live Hermes.

## Recorded live verification

| Recorded result | Evidence | Bound |
|---|---|---|
| Refresh, deliberate container restart/recovery, post-restart prompt, new chat, queue drain, live Serena discovery | `docs/HERMES-FUNCTION-PARITY.md:14` | Historical run; exact image not queried here |
| 159 frontend tests / 38 files, production build, native and installer/update/shutdown smokes, live health | `docs/HERMES-FUNCTION-PARITY.md:14`; `docs/ROADMAP.md:32-36` | Existing claim only |
| 73 live installed skills, 9 hubs, 28 toolset groups | `docs/ROADMAP.md:24-26` | Existing recorded inventory |
| Bridge live health, unauthenticated session rejection, authenticated session read | `HERMES-BRIDGE-CLIENT-LANE-REPORT.md:178-195` | 3 passed; no live send/interrupt |
| Strict renderer TypeScript | This audit | Current no-emit green |

## Source-complete but not live-integrated evidence

| Lane | Source/result evidence | Current reading |
|---|---|---|
| Advanced Composer | Completion report: 18 focused tests, module/full renderer compile green | Complete standalone component/controller; not imported |
| Extension Settings UI | Extension report: 11 focused tests, module compile green | Mounted only with fake default |
| Extension Settings live read | Live report: 9 tests, strict compile green | GET-only, unmounted, mutations unavailable |
| Profile Runtime UI | Profile report: 18 tests, full TypeScript green | Mounted only with deterministic default |
| Profile Runtime live read | Live report: 11 tests, strict compile green | GET-only injected transport, unmounted |
| Session Administration UI | Session report: 18 tests, module compile green | Mounted with fake; no live adapter |
| Docking splitters | Docking report: focused and then-current full suite green | Integrated; live pointer/WebView appearance not recorded |
| Monaco | Handoff: 14 focused tests, then-current full suite/build green | Integrated read-only; no live desktop smoke in lane |
| Portable installer | Source/smoke claims in Roadmap/parity | Final acceptance plan is explicitly plan-only |

## Upstream-to-Workbench capability matrix

| Upstream domain and exact symbols | Current Workbench evidence | Classification |
|---|---|---|
| Health/status/stats: `web_server.py:get_health/get_status/get_system_stats` | System adapter/workspace | Live/recorded verified |
| Auth/tickets: `dashboard_auth/routes.py:api_auth_providers/api_auth_me/api_auth_ws_ticket` | System adapter, native auth, gateway socket | Live/recorded verified |
| WS/Console/PTY/events: `gateway_ws/console_ws/pty_ws/events_ws` | Gateway, Console, native ConPTY, notifications | Live-capable; mixed recorded proof |
| Basic sessions: `sessions.py:get_sessions/search_sessions/get_session_detail/get_session_messages/rename_session_endpoint/delete_session_endpoint` | Session API/sidebar/chat resume | Live-capable |
| Advanced sessions: `bulk_delete_sessions_endpoint/import_sessions_endpoint/get_session_stats/get_session_latest_descendant/export_session_endpoint/prune_sessions_endpoint`; gateway fork/model-lock handlers | Session Admin fake workspace | Production adapter missing |
| Model catalog/current selection: `get_model_info/get_model_options/set_model_assignment` | Model adapter/popover | Live-capable |
| Auxiliary/MoA/custom config: `get_auxiliary_models/get_moa_models/set_moa_models/update_config/upsert_custom_endpoint/validate_provider_credential` | Extension Labs; live reads catalog/auxiliary/MoA | Preview; live writes missing |
| Skills: `skills.py:get_skills/get_skill_content/create_skill/update_skill_content` plus Hub | Production core plus authoring preview | Live core; authoring writes missing |
| Toolsets: `tools.py:get_toolsets/get_toolset_config/get_toolset_models/select_toolset_model/select_toolset_provider/save_toolset_env/run_toolset_post_setup` | Production core plus specialty preview | Specialty-model integration missing |
| MCP: `mcp.py:list_mcp_servers/add_mcp_server/replace_mcp_servers/test_mcp_server/auth_mcp_server/set_mcp_server_enabled/list_mcp_catalog/install_mcp_catalog_entry` | Production core; Labs/TH-4 target edit | Existing-server edit integration missing |
| Profiles: `profiles.py:list_profiles_endpoint/create_profile_endpoint/get_active_profile_endpoint/set_active_profile_endpoint/rename_profile_endpoint/delete_profile_endpoint/get_profile_soul/update_profile_soul/export_profile_endpoint/import_profile_endpoint` | Profile fake plus unintegrated live reads | Preview; live mutations missing |
| Hermes terminal backend/computer use: `tools.py:get_terminal_backends/select_terminal_backend/get_computer_use_status/grant_computer_use_permissions` | Profile fake only | Preview/fake |
| Attachments/rich text: image/file endpoints and upstream Markdown/tool components | Attachment adapter, safe GFM, InlineDiffCard | Live core, partial rich-result parity |
| Audio/voice: `transcribe_audio_upload/speak_text/speak_stream_ws`; upstream voice hook | Composer callback only | Missing UX, safely delegated pending consent |
| URL references/generated artifacts: `url-refs.ts:linkifyUrls`, `GeneratedImageResult`, `ArtifactCard` | Safe outbound links only | Missing typed slice |
| Managed file writes: upload/mkdir/delete/fs-write handlers | Read-only Workbench bridge | Intentionally delegated |
| Gateway lifecycle/update: restart/drain/start/stop/update handlers | Explicit scripts; no broad React control | Native/upstream delegated |
| Messaging/onboarding handlers | No Workbench surface | Intentionally delegated |
| Memory/learning/curator/portal handlers | No memory-management surface | Intentionally delegated |
| Cron/webhooks/pairing handlers | No Workbench management UI | Intentionally delegated |
| Logs/doctor/security/backup/import/hooks/checkpoints/raw config/plugins/themes | No Workbench management UI | Intentionally delegated |


## Trust, secret, and update boundaries

Final integration must preserve these invariants:

- `nous-approved`, independently `workbench-reviewed`, `external-unreviewed`, and `user-created` remain separate. A Nous catalog entry never implies Chris or Codex approval.
- React receives no stored secret, environment value, token, OAuth code, API key, authorization header, cookie, arbitrary setup command, or raw error body.
- Secret inputs are write-only, cleared before awaiting, excluded from props/results/reviews/logs/storage/copy, and never round-trip.
- Every destructive or expensive action has preview, explicit confirmation, drift detection, correlation identity, duplicate rejection, and no automatic retry.
- Profile/session identity is never inferred across scopes.
- The upstream dashboard remains available for broad, high-churn, secret-bearing, and recovery-sensitive administration.
- Normal launch remains update-free. Candidate acceptance binds immutable image ID/repo digest to the tested adapter/source record.

## Actively owned at the cutoff

`TERRA-HIGH-OWNERSHIP-PACKETS-2026-08-09.md` reserves four Hermes UX lanes. No named completion report existed at the cutoff.

| Rank | Active lane | Current evidence and impact | Required closure |
|---|---|---|---|
| **P1-A1** | TH-1 tool cards / scroll anchoring | Existing `AgentScroll.ts` predates the packet; `TOOL-CARDS-SCROLL-ANCHORING-REPORT.md` absent. Streaming/expansion must not steal reading position. | Packet tests, component proof, report, coordinator review; no gateway changes |
| **P1-A2** | TH-4 configured MCP editor | `src/Modules/HermesMcpEditor` existed as an empty directory; report absent. This overlaps Extension Labs MCP update intents. | Finish editor/report, then Super chooses one production editor seam |
| **P2-A3** | TH-2 Markdown polish | `HERMES-MARKDOWN-POLISH-REPORT.md` absent. Current safe GFM remains usable. | Focused language/copy/streaming/unsafe-scheme tests and report |
| **P2-A4** | TH-3 Console ergonomics | `HERMES-CONSOLE-ERGONOMICS-REPORT.md` absent. Current Console transport remains integrated. | Find/follow/copy/clear-view tests and report without transport changes |

The newly completed `AGENT-PANEL-REGISTRY-REPORT.md` and `ROSLYN-LANGUAGE-SERVER-REPORT.md` appeared during the audit but are explicitly excluded future-work scopes. They do not block Hermes closure.

## Genuinely remaining gaps

Only these items are ranked. Intentionally delegated upstream surfaces are not gaps.

### P0-1 -- Bind the showcase to the exact tested upstream image

- **Upstream evidence:** audited source commit `8e9ecc1...`; `docker-compose.yml:3` still names `nousresearch/hermes-agent:latest`.
- **Workbench evidence:** `HermesCompatibilityAdapter.ts:1-2,36-87` already normalizes immutable image ID/repo digest and evaluates `TESTED_HERMES_RUNTIME_VERSIONS = ['0.20.0']`; `HermesSystemWorkspace.tsx:321` renders it.
- **User-visible impact:** an image refreshed behind `latest` can differ from audited source and break auth, WebSocket, sessions, Console, attachments, or admin routes during a showcase.
- **Security/update boundary:** read only allowlisted runtime identity. Do not auto-update, reveal Docker/environment details, or loosen an incompatible state.
- **Prerequisite:** compatibility adapter, runtime-identity record, contract corpus, and Check Hermes tooling.
- **Isolated ownership:** release-verification lane owning only a sanitized candidate evidence record/report; no source or Docker edit.
- **Acceptance proof:** immediately before showcase, record image ID, repo digest, public version, adapter state `compatible`, health, and mapping from digest to accepted test/source record. Any `review` or `unverified` state blocks the showcase.

### P1-1 -- Connect Extension Settings live reads to the mounted Labs surface

- **Upstream evidence:** `GET /api/skills`, `/api/skills/content`, `/api/tools/toolsets`, `/api/tools/toolsets/web/config`, `/api/mcp/servers`, `/api/mcp/catalog`, `/api/model/options`, `/api/model/info`, `/api/model/auxiliary`, and `/api/model/moa`; exact table at `HermesExtensionSettingsLive/contracts.ts:101-112`.
- **Workbench evidence:** UI controller is `HermesExtensionSettingsController.load/preview/commit/validateProvider` (`contracts.ts:217-221`), while live adapter exposes correlated `read(request)` and unavailable mutations (`HermesExtensionSettingsLiveAdapter.ts:145-295`). It is not referenced by `HermesSystemWorkspace`.
- **User-visible impact:** a polished mounted surface still shows synthetic skills/providers/models/MCP state.
- **Security/update boundary:** preserve per-source partial reporting, provenance, profile/correlation identity, bounds, GET-only behavior, and omission of secret/command fields.
- **Prerequisite:** controller bridge converting live read to `LoadResult`, generating single-use correlations, carrying cancellation, and returning unavailable for writes.
- **Isolated ownership:** new `src/Modules/HermesExtensionSettingsIntegration/**` bridge lane; separate coordinator-only mount change after tests.
- **Acceptance proof:** bridge tests for ready/partial/cancelled/duplicate/profile mismatch; authenticated live read shows real inventory/source states; no fake IDs; writes unavailable; provenance unchanged.

### P1-2 -- Implement source-proven Extension Settings writes and reconcile TH-4

- **Upstream evidence:** skills `create_skill/update_skill_content`; toolsets `select_toolset_model/select_toolset_provider/save_toolset_env/run_toolset_post_setup`; models `set_moa_models/set_model_assignment/update_config/upsert_custom_endpoint/validate_provider_credential`; MCP `replace_mcp_servers/test_mcp_server/set_mcp_server_enabled`.
- **Workbench evidence:** renderer-safe intents/reviews at `HermesExtensionSettings/contracts.ts:148-221`; live adapter returns unavailable for `skill-create`, `skill-edit`, `toolset-configure`, `model-configure`, and `mcp-configure`. Production Skills/MCP adapters cover adjacent but incomplete paths.
- **User-visible impact:** custom skill authoring, specialty models, advanced model/MoA/custom endpoints, and existing MCP edits cannot commit.
- **Security/update boundary:** host/same-origin transport only; secrets write-only; Nous skills not overwritten; review tokens single-use and revision-bound; expensive models separately confirmed; arbitrary commands/environment names rejected.
- **Prerequisite:** P1-1, exact payload/acknowledgement tests, TH-4 completion/reconciliation.
- **Isolated ownership:** one production controller module, capability by capability. Do not ship two competing MCP edit flows.
- **Acceptance proof:** synthetic contracts plus disposable-profile live skill clone/create/edit, independent Search/Extract selection, MoA update, provider validation, MCP update/test/enable; replay/drift/cancel/malformed/leak tests; secrets absent from DOM/results/errors/copies/logs/persistence.

### P1-3 -- Bridge Profile Runtime live reads into the mounted workspace

- **Upstream evidence:** `list_profiles_endpoint`, `get_active_profile_endpoint`, `get_profile_soul`, `get_schema`, `list_oauth_providers`, `api_auth_providers`, and `api_auth_me`.
- **Workbench evidence:** UI expects `HermesProfileRuntimeAdapter` and `ProfileRuntimeSnapshot` (`HermesProfileRuntime/contracts.ts:94-108,186-200`); live adapter returns a different read-only `ProfileRuntimeLiveSnapshot` and capabilities (`HermesProfileRuntimeLive/contracts.ts:139-207`). No bridge exists, so deterministic fallback is used.
- **User-visible impact:** profile inventory, active/sticky profile, SOUL, config metadata, provider status, and account status remain synthetic.
- **Security/update boundary:** discard paths, config values/defaults, environment data, token previews, commands, user IDs/emails/org IDs, and unknown fields; preserve profile/correlation mismatch rejection.
- **Prerequisite:** explicit mapping for UI-required fields absent from live read. Missing persona/context/config values remain unavailable, never invented.
- **Isolated ownership:** new `src/Modules/HermesProfileRuntimeIntegration/**` read bridge; coordinator-only injection after tests.
- **Acceptance proof:** real selected profile appears; sticky/current-process difference is honest; SOUL is text; unavailable capabilities disabled; partial failures preserved; no deterministic `profile-main` in live path; private-field negative tests.
### P1-4 -- Replace fake Session Administration with a live, profile-safe adapter

- **Upstream evidence:** `source/hermes_cli/web_api/sessions.py` symbols `get_sessions`, `get_latest_descendant`, `bulk_delete_sessions`, `import_session`, `get_session_stats`, `export_sessions`, and `prune_sessions`; gateway `source/hermes_cli/gateway/api_server.py` exposes fork and model-lock behavior.
- **Workbench evidence:** `src/Modules/HermesSessionAdmin/contracts.ts:240-255` defines the review/commit controller; `src/Modules/HermesSystem/HermesSystemWorkspace.tsx:115,382` creates and mounts the deterministic fake controller.
- **User-visible impact:** branches, stats, exports, imports, prune/delete review, and model-lock state can look functional without touching the selected profile.
- **Security/update boundary:** bind profile and correlation identity; keep private session content out of diagnostics; bound imports/exports; preview every mutation; reject drift/replay/duplicates; never retry a mutation automatically. Treat clear/change-model behavior as unavailable until an exact route is proven.
- **Prerequisite:** decide dashboard REST versus gateway ownership, prove descendant-tree semantics, and bind review tokens to the exact affected session set.
- **Isolated ownership:** new `src/Modules/HermesSessionAdminLive/**`, read-only first, followed by separately reviewed mutations; coordinator owns later injection.
- **Acceptance proof:** disposable-profile live tests for list/tree/stats/export, then preview/confirm import/prune/delete/fork/model-lock where supported; negative proof for cross-profile, replay, drift, cancellation, malformed payload, and private-content leakage.

### P1-5 -- Integrate only the non-duplicated Advanced Composer capabilities

- **Upstream evidence:** gateway WebSocket/send paths support prompts, attachments, session continuation, and interrupt; slash and mention completion data are derived from source-confirmed tools/skills/routes, not from an arbitrary command executor.
- **Workbench evidence:** `src/Modules/AgentDock/AgentDock.tsx:344-596,748-856` already provides live send, queued prompts, edit-before-send, retry, attachments, and interrupt. `src/Modules/HermesComposerCompletion/**` additionally provides `@` mention completion, `/` completion, regenerate intent, keyboard traversal, controller/history helpers, and deterministic tests, but is referenced only by its own module/tests.
- **User-visible impact:** completion and regenerate affordances are absent from the mounted composer; queue/edit/retry are not missing and must not be reimplemented.
- **Security/update boundary:** suggestions are text-only, bounded, capability-derived, and keyboard accessible; selecting a slash item cannot execute arbitrary setup commands. Preserve the existing attachment and queue state machine.
- **Prerequisite:** agree on the minimal component seam and resolve how regenerate maps to current live send/session semantics.
- **Isolated ownership:** AgentDock integration lane restricted to completion/regenerate wiring and tests; no transport or host change.
- **Acceptance proof:** focused keyboard tests for open/filter/wrap/select/escape/focus return; live smoke for one mention, one slash insertion, and regenerate; existing queue/edit/retry/attachment/interrupt tests remain green.

### P1-6 -- Prove portable launch and recovery before calling the Workbench portable

- **Upstream evidence:** runtime composition is Docker-backed and the accepted Hermes routes depend on the selected image/profile state.
- **Workbench evidence:** `docs/coordination/PORTABLE-INSTALL-ACCEPTANCE-PLAN.md` explicitly says it is a plan only; cases A01-A24 are not execution evidence from that lane.
- **User-visible impact:** a clean machine, stale machine, relocated folder, missing Docker, port conflict, interrupted start, update, rollback, or uninstall can fail despite development-machine success.
- **Security/update boundary:** no silent image pull or update, no credential copying, no destructive cleanup outside the product-owned boundary, and immutable rollback identity.
- **Prerequisite:** P0 candidate identity and a frozen portable artifact.
- **Isolated ownership:** release acceptance lane using the existing plan; no product edits unless a failed case receives a new bounded owner.
- **Acceptance proof:** dated A01-A24 result matrix with logs/screenshots, artifact hashes, machine prerequisites, failures, rollback/uninstall residue checks, and explicit blocked cases.

### P2-1 -- Add profile mutations and permission operations only after upstream proof

- **Upstream evidence:** profile and auth modules expose some selection/provider/account metadata, while broad profile/config/permission administration remains high-churn and partly dashboard-owned.
- **Workbench evidence:** `HermesProfileRuntimeWorkspace` offers preview-shaped controls, but `HermesProfileRuntimeLive` is intentionally read-only and reports unsupported mutations unavailable.
- **User-visible impact:** users continue to use upstream for create/rename/delete/default/sticky profile changes, configuration values, provider credential changes, and permission management.
- **Security/update boundary:** these operations are profile-sensitive and often secret-bearing. Keep them upstream unless exact routes, acknowledgement, rollback, and redaction are proven.
- **Prerequisite:** P1-3 read integration plus a capability-by-capability source audit.
- **Isolated ownership:** separate write adapter lane; do not broaden the read adapter.
- **Acceptance proof:** disposable-profile preview/commit/replay/drift tests per operation, with stored secrets and private account fields absent from renderer-visible state.

### P2-2 -- Close remaining rich-result presentation gaps

- **Upstream evidence:** gateway events can carry citations, generated images, artifacts, downloads, and tool-specific structured results beyond plain streaming text.
- **Workbench evidence:** `src/Modules/HermesRichOutput/**` and `docs/coordination/HERMES-RICH-OUTPUT-PARITY.md` cover bounded safe rendering, but the lane report records remaining live presentation/integration assumptions rather than complete end-to-end proof for every result class.
- **User-visible impact:** uncommon results may degrade to links/text or remain in upstream rather than receiving native previews and downloads.
- **Security/update boundary:** allowlist schemes/types, never render raw HTML, sandbox previews, bound size/count, and make download provenance explicit.
- **Prerequisite:** source-confirmed event fixtures and a mounted renderer seam.
- **Isolated ownership:** one result class per lane or a single bounded rich-output integration lane.
- **Acceptance proof:** sanitized fixture tests plus live events for citations, image, artifact, and download; malformed/oversize/unsafe URL cases fail closed.

### P2-3 -- Keep audio/voice and URL-reference ingestion upstream until exact contracts exist

- **Upstream evidence:** upstream surfaces expose audio/voice and URL-oriented workflows, but the audited Workbench transport contract does not yet prove all capture, upload, consent, cancellation, and retention semantics.
- **Workbench evidence:** no mounted production module establishes complete audio/voice or URL-reference parity; the voice dock document is a specification, not implementation evidence.
- **User-visible impact:** users use upstream for those workflows.
- **Security/update boundary:** microphone permission, local capture indicators, source consent, SSRF-safe URL handling, size/type bounds, retention, and cancel/delete semantics must be explicit.
- **Prerequisite:** exact routes/event fixtures and a privacy/permission decision.
- **Isolated ownership:** future dedicated ingestion lane; excluded from showcase closure unless explicitly promoted.
- **Acceptance proof:** permission-denied/cancel/oversize/unsafe URL tests and disposable live end-to-end proof with no background capture.

### P2-4 -- Finish the active UX/accessibility polish packets

- **Upstream evidence:** upstream behavior establishes the content semantics but not the Workbench-specific scroll, Markdown, console, focus, large-text, or reduced-motion implementation.
- **Workbench evidence:** TH-1 through TH-3 are active at the cutoff; named reports are absent. Existing GFM, Console, and scroll utilities are usable but not acceptance for those packets.
- **User-visible impact:** long tool output, streaming Markdown, and Console review may be less comfortable or may move focus/scroll unexpectedly.
- **Security/update boundary:** maintain text-safe rendering and existing transport; polish must not create command execution, unsafe links, or copied secrets.
- **Prerequisite:** active-lane completion or explicit deferral.
- **Isolated ownership:** current Terra High owners; Super accepts or defers each packet.
- **Acceptance proof:** packet-specific unit/keyboard/large-text/reduced-motion tests plus named reports and coordinator review.
## Reconciliation of stale canonical documents

This section reports drift; it does not edit either canonical document.

### `docs/HERMES-FUNCTION-PARITY.md`

| Current statement | Current evidence-backed reconciliation |
|---|---|
| The composer row describes queue/edit/retry and related controls as absent. | `AgentDock.tsx:344-596,748-856` now provides live queue, edit-before-send, retry, attachments, and interrupt. `HermesComposerCompletion/**` adds tested completion/regenerate behavior but is not mounted. The accurate residual gap is P1-5, not the whole historical row. |
| The sessions row says there is no Session Admin UI. | `HermesSessionAdmin/**` now supplies a substantial preview/commit UI and deterministic controller, mounted at `HermesSystemWorkspace.tsx:382`; it is fake-only. P1-4 is live adapter/integration and mutation proof. |
| The models row says there is no advanced models surface. | `HermesExtensionSettings/**` now has advanced model, auxiliary, override, MoA, validation, and endpoint UI with fake adapters. `HermesExtensionSettingsLive/**` has unmounted GET metadata. The live-read bridge and proven writes are P1-1/P1-2. |
| Custom skill authoring/toolset specialty are described as upstream-only. | Authoring/configuration UI exists; live read inventory exists; production commits remain unavailable. Preserve the upstream delegation until P1-2 passes. |
| Profiles row says there is no profile control surface. | `HermesProfileRuntime/**` is mounted with deterministic data and `HermesProfileRuntimeLive/**` has unmounted sanitized reads. Live bridge is P1-3; mutations remain P2-1/upstream. |
| Counts and backlog totals read as current inventory. | Treat all numeric counts and implementation totals as dated snapshots. This audit found later modules/reports and active ownership packets; regenerate counts from a frozen candidate when the canonical file is next revised. |

### `docs/ROADMAP.md`

- The completed-foundation claims at lines 24-36 are credible as historical live verification: 73 skills, 9 hubs, 28 toolsets, reconnect/restart recovery, session recovery, and the recorded build/smoke run are also supported by `HERMES-FUNCTION-PARITY.md:14` and the bridge report. They are not proof for the image currently behind `latest`; P0-1 is required.
- The roadmap's current language-intelligence focus is valid project planning but does not close remaining Hermes Labs integration. Roslyn/debugger/Java, Usage Intelligence, GitExtensions, and unlimited registry work are excluded from this audit and do not substitute for P1-1 through P1-6.
- The portable plan and fake/preview workspaces must not be promoted into the roadmap's completed foundation until live acceptance exists.

## Coordination artifact reconciliation

At the final inventory cutoff, 33 pre-existing coordination files were reviewed by name and scope in addition to this report.

**Directly relevant (20):** `DOCKING-SPLITTER-LANE-REPORT.md`, `EXTENSION-SETTINGS-LIVE-ADAPTER-REPORT.md`, `HERMES-BRIDGE-CLIENT-LANE-REPORT.md`, `HERMES-COMPOSER-COMPLETION-REPORT.md`, `HERMES-COMPOSER-COMPLETION-WORKER.md`, `HERMES-EXTENSION-SETTINGS-REPORT.md`, `HERMES-EXTENSION-SETTINGS-WORKER.md`, `HERMES-PARITY-AUDIT-LANE.md`, `HERMES-PARITY-HANDOFF.md`, `HERMES-PROFILE-RUNTIME-REPORT.md`, `HERMES-PROFILE-RUNTIME-WORKER.md`, `HERMES-RICH-OUTPUT-PARITY.md`, `HERMES-SESSION-ADMIN-REPORT.md`, `HERMES-SESSION-ADMIN-WORKER.md`, `MONACO-HANDOFF.md`, `MONACO-LANE.md`, `PARALLEL-WORK-PACKETS-2026-08-09.md`, `PORTABLE-INSTALL-ACCEPTANCE-PLAN.md`, `PROFILE-RUNTIME-LIVE-ADAPTER-REPORT.md`, and `TERRA-HIGH-OWNERSHIP-PACKETS-2026-08-09.md`.

**Reviewed and excluded from Hermes parity (13):** `AGENT-PANEL-REGISTRY-REPORT.md`, `CHATGPT-VOICE-DOCK-SPEC.md`, `COMPILER-DEBUGGER-LANE-REPORT.md`, `GITEXTENSIONS-INTEGRATION-PLAN.md`, `GITEXTENSIONS-PHASE1A-LANE.md`, `HANDOFF-COMPILER-DEBUGGER-LANE.md`, `HANDOFF-USAGE-COLLECTORS-LANE.md`, `OPENAI-USAGE-COLLECTOR-V1-SPEC.md`, `ROSLYN-LANGUAGE-SERVER-REPORT.md`, `SCARLETT-USAGE-PROVIDER-RESEARCH.md`, `USAGE-COLLECTORS-LANE-REPORT.md`, `USAGE-INTELLIGENCE-HANDOFF.md`, and `USAGE-INTELLIGENCE-LANE.md`.

Worker/handoff documents establish ownership and intent, not completion. Completion reports establish isolated implementation/test evidence, not coordinator integration or current-runtime proof. This audit keeps those evidence levels separate.

## Shortest safe closure sequence

1. **Freeze and prove the candidate (P0-1).** Bind the exact image ID/repo digest/version to the accepted source/test record. Stop if incompatible, review, or unverified.
2. **Resolve active ownership.** Let TH-1 through TH-4 finish or explicitly defer them; Super reconciles TH-4 with the one Extension MCP edit seam.
3. **Mount read-only Labs truth.** Implement/test the Extension and Profile bridge modules, then make the coordinator-only injection. Keep all mutations unavailable.
4. **Replace fake Session Admin reads.** Ship a read-only, profile-safe live adapter and integration before any destructive action.
5. **Enable writes one capability at a time.** Skill clone/create/edit, Search/Extract/toolset model, model/MoA/provider, MCP, then session mutations; each receives live disposable-profile proof and redaction/replay/drift tests.
6. **Integrate only missing composer behavior.** Add mention/slash completion and regenerate without replacing the already-live queue/edit/retry/attachment/interrupt path.
7. **Run frozen-candidate acceptance.** Focused suites, full strict TypeScript/build, live core smokes, restart/recovery, and negative security checks on the exact candidate.
8. **Execute portable A01-A24.** Fix only evidence-backed failures in newly assigned scopes; rerun affected and critical-path cases.
9. **Defer P2 explicitly.** Keep upstream links/labels visible for anything not proven and do not describe it as Workbench parity.

This order avoids building writes on synthetic state, avoids duplicate MCP/composer implementations, and catches runtime drift before portable validation.

## Showcase-ready definition of done

The Hermes Workbench is showcase-ready only when all of the following are true:

- Candidate identity is immutable and compatibility is `compatible`; the audited source/test record maps to the exact runtime image ID/repo digest.
- Launch, health, authentication, chat send/stream/interrupt, session continuation/new chat, reconnect/restart recovery, queue/edit/retry, attachments, terminal, Console, Skills, MCP, and settings navigation pass on that candidate.
- The mounted MCP and Skills surfaces read their intended production sources; fake/deterministic values are absent from every surface presented as live.
- Extension Labs, Profile Runtime, and Session Administration are either wired to proven live adapters or are visibly labeled Preview/Unavailable and excluded from the showcase script.
- Every enabled write shows a bounded before/after review, requires explicit confirmation, carries profile/correlation identity, rejects duplicate/replay/drift, and has no automatic retry.
- Nous approval, independent Workbench review, external/unreviewed, and user-created provenance remain distinct everywhere.
- No secret, environment value, token, OAuth code, header, stored API key, private account field, or arbitrary setup command reaches React props, results, rendered/copyable text, logs, persistence, or diagnostics.
- Focused tests and strict TypeScript/build are green; live proof records exact route, candidate identity, profile isolation, and negative cases. Historical totals are not reused as current evidence.
- Active Hermes lane reports are accepted or explicitly deferred, with no competing MCP editor and no duplicated composer queue state machine.
- Portable A01-A24 has a dated result matrix, artifact hashes, recovery/rollback/uninstall evidence, and no unexplained blocker.
- P2 items stay clearly delegated upstream or visibly unavailable. No feature is called live, complete, reviewed, or approved beyond its evidence.

## Changed files

- `docs/coordination/HERMES-FINAL-INTEGRATION-AUDIT.md` -- created/replaced by this audit.
- No source, runtime, test, package, Docker, installer, existing report, roadmap, or parity file was modified.