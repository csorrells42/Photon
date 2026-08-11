# Hermes Workbench Roadmap

The current priority is upstream Hermes feature parity, followed by the thin C# WebView2 desktop host and portable installer validation.

## Completed foundation

- Native host protocol v1 with trusted loopback navigation, self-healing startup, window controls, and diagnostics.
- Native terminal protocol v1 with a real ConPTY PowerShell session, xterm.js rendering, resize/input/output/exit handling, background continuity across terminal tabs, and a PowerShell round-trip smoke harness.
- Separate authenticated Hermes Console for agent administration; browser mode never receives native command execution.
- Optional Codex app-server protocol v1 with account discovery, device-code sign-in, threads, streamed turns, command/file/MCP activity, approvals, user-input requests, interruption, and read-only account/rate-limit/usage signals.
- Versioned Hermes system adapter v1 and adaptive Control Center for normalized health, components, account connection, and protected runtime telemetry.
- Shared-profile native authentication window plus Windows Credential Manager protocol v1; provider secrets never enter browser storage and are never readable by React.
- Portable single-container Hermes topology with the gateway and dashboard supervised together, persistent data isolated from the mounted workspace, and local Serena retained for Roslyn-backed code intelligence.
- Lazy-loaded Monaco editor with language mapping, Workbench theme, responsive minimap, and read-only workspace previews.
- Provider-neutral Usage Intelligence dashboard with deterministic demo telemetry, per-provider capability/status reporting, a native-only multi-profile credential vault, and usage protocol v2 collectors for every named OpenRouter key plus official OpenAI and Anthropic organization usage. Per-profile results roll into compatible provider totals while retaining partial failures. Live summaries exclude synthetic providers; Gemini key inventory is explicit about its currently unavailable per-key usage source.
- Provider-neutral developer-services protocol v1 is wired end to end through the trusted desktop host. The **Run and Debug** dock discovers bounded .NET solution/project targets, runs guarded Debug or Release builds with correlated cancellation, streams bounded build output, deduplicates MSBuild diagnostics, fills the real **Problems** pane/status counts, opens workspace-relative problem files, and projects ranges into Monaco markers. Browser mode cannot invoke local builds. LSP and real debugger capabilities remain honestly marked **Planned**.
- The developer-services library now includes a provider-neutral LSP nucleus with bounded `Content-Length` framing, strict JSON-RPC 2.0 parsing, fixed file-URI document ownership, full-text synchronization, diagnostics, completion, hover, definition, references, rename, correlated cancellation, safe rejection of unadvertised server requests, and shutdown. Its reusable stdio transport launches only an explicit absolute executable without a shell, bounds arguments and stderr, permits one output reader, and owns shutdown of that exact child process. It opens no listener and performs no executable discovery. Roslyn/JDT capabilities stay **Planned** until an approved server and host/renderer bridge are actually connected.
- Bounded Hermes chat reconnect with fresh WebSocket tickets, explicit reconnecting/error states, profile-aware durable-session rebinding, and no automatic prompt replay.
- Turn-aware Agent Dock controls with an editable FIFO prompt queue, Stop-and-park behavior, retry/edit actions, a functional agent options menu, and blocking approval/clarification controls pinned beside the composer while expandable reasoning remains in the transcript.
- Versioned Hermes notification adapter v1 and an accessible Agent-header notification center for account-wide `notification.show` / `notification.clear` events, with bounded text/history, keyed replacement, TTL expiry, sticky dismissal, sign-out clearing, and no persistence.
- Debounced full-text session search merges immediate loaded-row matches with Hermes FTS results across every profile advertised by the unified list, displays bounded matching snippets, tolerates partial-profile failures, and resumes results through the versioned session adapter.
- Versioned Session Administration adapter v1 now uses verified profile-scoped Hermes routes for bounded list, honest partial statistics, latest-descendant paths, and text-only export. The live 125% Control Center surface clearly labels this read-only beta; branch/fork, delete/prune, import, and model-lock mutations remain visible but disabled until separate reviewed preview/commit contracts are proven. Focused route, bound, profile, cancellation, correlation, malformed-response, and capability-rendering tests pass, and live default-profile list/statistics/descendant/export checks completed without browser-console warnings.
- Persistent whole-Workbench interface scaling at 100%, 110%, 125%, and 140%, with a 125% first-run default, View menu, status-bar access, and keyboard shortcuts.
- Read-only Serena health adapter v1 and Control Center integration that verifies the Hermes-container-to-host MCP route, sanitizes endpoint metadata, and lists bounded discovered tool metadata without exposing MCP environment variables, headers, or arguments.
- Versioned MCP adapter v1 and a polished Control Center management surface for configured servers, test/enable/delete/OAuth actions, external server creation, and the upstream catalog. Nous-approved, independently Workbench-reviewed, and external/unreviewed provenance are deliberately separate; installs require a command/source/setup review sheet and MCP environment values never return to React.
- Versioned Skills adapter v1 and Control Center surface for 73 live installed skills plus 9 upstream hubs. Installed provenance, upstream trust, and independent Workbench review remain separate. Hub entries expose source, the actual SKILL.md bundle, and Hermes quarantine/security-scan findings; a matching non-blocked scan is mandatory before the adapter can post an install, while caution policies require explicit acceptance.
- Versioned Toolsets adapter v1 and responsive provider configuration surface for 28 live capability groups. Toolset toggles, selected providers, readiness, allow-listed secret entry, and confirmation-gated post-setup hooks use upstream contracts. Stored environment values never return to React, errors redact submitted values, and arbitrary environment names/setup commands are rejected before network access.
- Explicit upstream Hermes updater with typed confirmation, immutable before/candidate records, a fixed previous-image rollback tag, candidate health verification, automatic rollback, and direct runtime-identity refresh. Normal Workbench launch remains update-free and is the only command that starts the full local application stack.
- Portable bundle integrity protocol v1 with per-file SHA-256/length records, installer-side safe-path and reparse-point checks, a verification-only mode, positive extraction coverage, and negative tamper rejection. Launch also rejects reserved-port ownership conflicts and waits for the Hermes status endpoint before starting the client.
- Scoped shutdown now cleans the desktop, legacy client, Vite, and Serena before Compose; clears invalid/stale PID records without touching mismatched processes; waits up to ten seconds for confirmed owned-process exits; continues local cleanup when Docker is unavailable; and aggregates attention items after all safe attempts. A disposable-process smoke test covers invalid, mismatched, and owned PID paths.
- Installer destination protocol v1 refuses unrelated nonempty or reparse-point targets, recognizes both versioned markers and earlier Hermes layouts, preserves data/log/workspace content, and resolves freshly installed Docker/Node/uv from standard Windows paths before requiring a restart. A filesystem-only smoke test covers missing, empty, unrelated, legacy, marked, and PATH-fallback cases.
- Hermes wire-contract corpus v1 now holds synthetic, non-secret fixtures for public system/auth/identity, WebSocket tickets, line-delimited JSON-RPC responses/errors/events, model options, stored messages, tool/approval/prompt events, attachment references, Console frames, immutable runtime identity, configured MCP servers, the Nous catalog, installed skills, hub provenance, scan findings, toolsets, and provider readiness. Ten participating adapters remain explicitly versioned.
- Lazy rich-message rendering now supports safe GitHub-flavored Markdown, task lists, tables, external links, inline code, and copyable fenced code blocks. Raw HTML and implicit remote image loading are disabled. Connection refresh now distinguishes an already-open gateway from a genuine dropped-session recovery, and new chat can only clear a recovery warning when the gateway is actually open. The full frontend passes 159 tests across 38 files and the production build keeps the parser in a separate on-demand chunk.
- Safe inline-diff presentation v1 caps every Hermes `inline_diff` payload at 8,000 characters in the runtime adapter, recognizes bounded unified file/hunk/add/remove structure, and falls back to collapsed inert text for malformed content. The card supports copying only the retained text; it never applies a patch, executes a command, reads a workspace file, or turns an agent path into navigation.
- Branded in-Workbench authentication replaces agent/session links to the legacy upstream login page. One versioned renderer event synchronizes password or native OAuth completion across Hermes chat, sessions, and Control Center without exposing credentials or WebSocket tickets to React. Connected refresh, deliberate container restart/recovery, post-restart prompting, new chat, queue drain, and live Hermes-to-Serena symbol discovery have been exercised against the running stack.
- Portable VS Code tasks now cover launch, health, shutdown, confirmed update/rollback, frontend development/test/build, isolated script smoke tests, a default full offline verification task, and portable-ZIP construction. A packaged npm runner discovers Node from PATH or standard Windows locations instead of assuming a machine-specific path.
- Customer-facing `Check Hermes` diagnostics are read-only and secret-avoiding: bundle/settings/desktop-host presence, Docker/container image state, auxiliary screenshot-vision readiness, public gateway readiness/version, Serena/Vite/bridge port ownership, and safe runtime identity consistency. The current live stack passed all 12 checks with zero errors and zero warnings.

## Current implementation focus: language intelligence and real debugging

The guarded .NET build path is live. The next developer-services slices are a secured Roslyn-backed language-service adapter and a separately packaged, pinned, license-reviewed debugger adapter behind the existing provider-neutral contracts. Java/JDT LS and other languages remain future internal providers in the same one-product installation; they must not create separate product editions or containers.

## Deferred memory provider strategy

Memory remains behind a provider-neutral Workbench contract and is deliberately deferred until the core showcase, language intelligence, debugger, and installer work are stable.

- Honcho remains an optional Nous-supported provider for customers who choose it. Workbench must present its provenance clearly and keep it off and unconfigured by default; this installation will not request Honcho credentials or activate its network service.
- The selected future path for this installation is Mem0 OSS with local extraction and embedding models plus local vector storage. Upstream Hermes already exposes platform, self-hosted server, and in-process OSS modes in `source/plugins/memory/mem0`; the future lane should configure and verify the OSS path rather than fork the upstream provider.
- The local lane must preserve explicit consent, inspectable/deleteable memories, profile isolation, bounded retrieval, backup/restore, and an honest resource boundary. Local/free means no hosted subscription or metered model API, not zero GPU, CPU, disk, or electricity cost.
- No Mem0 dependency, model, vector database, credential, or background service is installed or enabled by this roadmap decision.

## Continuing provider work: Usage Intelligence adapters

Add a provider-neutral usage panel beneath the agent entry in the activity rail. It should give developers one place to understand:

- ChatGPT subscription limits and available account signals.
- OpenAI API organization/project usage, spend, credits, and rate limits.
- OpenRouter key usage, credits, model/provider costs, and limits.
- Google Cloud API quotas and billing signals.
- Google AI Studio and Gemini API usage and quotas.
- Anthropic API usage, spend, credits, and rate limits.
- Provider health, current billing period, projections, alerts, and refresh age.

Research each provider's current official usage and billing interfaces before implementation. Some consumer subscription information may not have a supported API and must be clearly distinguished from confirmed API-account telemetry.

The provider-neutral dashboard and synthetic adapter are complete. OpenRouter is the first live provider collector: the host resolves every named key from Windows Credential Manager, calls the fixed official `GET /api/v1/key` endpoint for each, returns only normalized usage/limit fields to React, and preserves per-key failures without discarding successful totals. The remaining slices connect supported read-only provider APIs one at a time. The Codex bridge already supplies native account, rate-limit, and usage signals through `account/read`, `rateLimits/read`, and `usage/read`; these signals will be normalized rather than scraping the consumer website.

### Security requirements

- Never store provider secrets in browser local storage, repository files, logs, or exported diagnostics.
- Store secrets through the native host using Windows Credential Manager, with Keychain and Secret Service adapters for future platforms.
- Return only masked credential metadata and normalized usage records to React.
- Keep every provider connector independently revocable and least-privileged.
- Make usage refresh read-only and show the source and timestamp for every value.
