# Terra High ownership packets — 2026-08-09

These packets are deliberately isolated from the coordinator-owned live Session Administration work and from the five active Sol High lanes. Run each packet in a separate Codex task at **Terra High**. Every worker must stop after its focused tests and handoff report; the coordinator owns integration and broad release verification.

## Shared rules for every worker

- Work only in `C:\Users\clsor\Documents\Codex\HermesAgent`.
- This workspace currently has no Git metadata. Do not initialize Git, commit, reset, clean, checkout, or invent a remote.
- Read outside the owned paths only to understand existing contracts. Do not edit read-only or forbidden paths.
- Preserve upstream Hermes as containerized and updateable. Do not modify `source/**`.
- Do not add packages, edit package/build configuration, inspect credentials, add secrets, or make live provider calls.
- Use versioned, typed contracts for any new reusable seam. Keep user-visible status honest: preview, partial, unavailable, and live must not be conflated.
- Use `apply_patch` for source edits. Run the narrowest relevant TypeScript/Vitest checks.
- End by writing the named report under `docs/coordination/`. The report must list every changed file, behavior, tests and results, limitations, and the exact integration step the coordinator must perform.
- Stop after the report. Do not integrate adjacent modules or broaden scope.

---

## Packet TH-1 — Tool cards and transcript scroll anchoring

**Intelligence:** Terra High

### Objective

Make the Hermes transcript behave like a polished IDE chat while streaming reasoning and tool activity. Tool cards must remain ordinary transcript content, never push older conversation upward from a bottom-mounted stack, and never steal the user's reading position. Automatic following is allowed only while the user is already near the bottom. Expanding or collapsing content above the viewport must preserve the visible reading anchor. The existing Jump to latest control must reliably resume following.

### Owned files

- `src/Modules/AgentDock/AgentScroll.ts`
- `src/Modules/AgentDock/AgentScroll.test.ts`
- `src/Modules/AgentDock/AgentDock.tsx` — only transcript scrolling, `ToolTimeline`, and `ToolRunRow` logic
- `src/Modules/AgentDock/AgentDock.ToolRunRow.test.tsx`
- New tests or helpers whose names begin with `AgentScroll`, `ToolTimeline`, or `AgentDock.Scroll` under `src/Modules/AgentDock/`
- `src/app/styles.css` — only the existing `.conversation`, `.message-list`, `.reasoning-card`, `.tool-*`, and `.jump-latest` selector blocks
- Report: `docs/coordination/TOOL-CARDS-SCROLL-ANCHORING-REPORT.md`

### Read-only references

- `src/Modules/HermesGateway/useHermesChat.ts`
- `src/Modules/HermesGateway/HermesRuntimeAdapter.ts`
- `src/Modules/AgentDock/InlineDiffCard.tsx`
- Existing gateway/runtime tests

### Forbidden

- Any file under `src/Modules/HermesSessionAdmin/**`, `HermesSystem/**`, `AgentRegistry/**`, `HermesProfileRuntimeLive/**`, `HermesExtensionSettingsLive/**`, `DeveloperServices/**`, or `src/Host/**`
- `src/app/App.tsx`, docking modules, package/build files, installer scripts, and documentation other than the named report
- Changing gateway event semantics, deleting completed tool history, forcing smooth scrolling, or globally pinning tool/reasoning cards beside the composer

### Required behavior

1. Maintain a small testable scroll-state/controller seam: following, user-reading, anchor-preservation, and explicit jump-to-latest.
2. Streaming message, reasoning, and tool changes follow the bottom only when the transcript was near the bottom before the change.
3. Scrolling above the existing threshold suspends following and reveals Jump to latest.
4. Expanding/collapsing reasoning, tool details, command output, or inline diffs must not drag a user who is reading older content to the bottom.
5. When content above the visible region changes height, preserve the visible reading anchor or equivalent scroll offset.
6. Tool activity stays in transcript order. A running tool may update its own row in place; completed rows never disappear merely because a new tool arrives.
7. Jump to latest performs one deterministic non-animated jump, resumes following, and remains correct after further `ResizeObserver` callbacks.
8. Preserve keyboard accessibility and current collapsed/expanded tool semantics.

### Acceptance

- Pure tests cover exact bottom, threshold boundary, above-threshold reading, append while following, append while reading, resize above viewport, explicit jump, and browser overscroll.
- Component tests prove expanding a tool row does not automatically scroll a user who is reading older content.
- Existing `AgentScroll`, `ToolRunRow`, and relevant Agent Dock tests pass.
- TypeScript passes for the owned surface.
- No files outside the owned list and named report are changed.

### Copy-paste worker instruction

> Implement Packet TH-1 from `docs/coordination/TERRA-HIGH-OWNERSHIP-PACKETS-2026-08-09.md` exactly. Stay inside its owned files, obey every forbidden boundary, run its focused acceptance suite, write `docs/coordination/TOOL-CARDS-SCROLL-ANCHORING-REPORT.md`, and stop for Super to review. Do not integrate or edit any adjacent module.

---

## Packet TH-2 — Polished safe Markdown and code-block affordances

**Intelligence:** Terra High

### Objective

Finish the visible Markdown/code-reading experience in Hermes chat without adding arbitrary local-file access. Fenced code should have an IDE-quality language header, accessible copy action, stable streaming fallback, readable wrapping/scrolling, and explicit success/failure feedback. Raw HTML and implicit remote images remain disabled.

### Owned files

- `src/Modules/AgentDock/HermesMarkdown.tsx`
- `src/Modules/AgentDock/HermesMarkdown.test.tsx`
- New `HermesMarkdown*` helpers/tests/styles under `src/Modules/AgentDock/`
- Report: `docs/coordination/HERMES-MARKDOWN-POLISH-REPORT.md`

### Read-only references

- `src/Modules/AgentDock/AgentDock.tsx`
- `src/Modules/AgentDock/InlineDiffCard.tsx`
- `docs/coordination/HERMES-RICH-OUTPUT-PARITY.md`
- `src/app/styles.css`

### Forbidden

- `AgentDock.tsx`, `src/app/styles.css`, gateway/runtime code, Monaco/workspace navigation, host code, package/build configuration, and every other worker/coordinator lane
- Any automatic file opening, filesystem read, command execution, patch application, raw HTML rendering, or remote-image fetching

### Required behavior

1. Fenced blocks show a normalized language label and a keyboard-accessible Copy code button.
2. Copy uses only the rendered bounded text. Report success briefly; failure remains visible and does not throw.
3. Unknown or missing languages fall back safely without loading arbitrary modules.
4. Incomplete streaming fences remain readable and never produce raw HTML.
5. Long lines can be read without expanding the entire dock; the component owns its styles rather than editing global CSS.
6. External links retain safe protocols and `noopener noreferrer`; unsupported schemes remain inert text.
7. File-looking text stays inert until a later trusted workspace-navigation contract exists.

### Acceptance

- Tests cover known/unknown/missing language, incomplete fence, copy success, copy denial, HTML-looking text, unsafe URL scheme, safe external link attributes, and very long code.
- Existing Hermes Markdown tests remain green.
- TypeScript passes for the owned surface.
- No files outside the owned list and named report are changed.

### Copy-paste worker instruction

> Implement Packet TH-2 from `docs/coordination/TERRA-HIGH-OWNERSHIP-PACKETS-2026-08-09.md` exactly. Stay inside its owned files, obey every forbidden boundary, run its focused acceptance suite, write `docs/coordination/HERMES-MARKDOWN-POLISH-REPORT.md`, and stop for Super to review. Do not integrate or edit any adjacent module.

---

## Packet TH-3 — Hermes Console transcript ergonomics

**Intelligence:** Terra High

### Objective

Turn the existing authenticated Hermes Console into a professional long-session console: bounded in-panel find, pause/resume auto-follow, Jump to latest, and safe copy controls. This is renderer ergonomics only; do not change the Console transport or grant browser command execution.

### Owned files

- `src/Modules/HermesConsole/HermesConsolePanel.tsx`
- New helpers, tests, and component-owned CSS under `src/Modules/HermesConsole/`
- Report: `docs/coordination/HERMES-CONSOLE-ERGONOMICS-REPORT.md`

### Read-only references

- `src/Modules/HermesConsole/useHermesConsole.ts`
- `src/Modules/HermesConsole/HermesConsoleClient.ts`
- `src/Modules/AgentDock/AgentScroll.ts`
- `src/app/styles.css`

### Forbidden

- `useHermesConsole.ts`, `HermesConsoleClient.ts`, host/bridge code, `src/app/styles.css`, terminal/xterm modules, command authorization, package/build files, and every other lane
- New command execution, transcript persistence, secret logging, regex supplied by the user, or unbounded retained copies

### Required behavior

1. Plain-text case-insensitive find with previous/next, match count, Escape close, and bounded query length.
2. Auto-follow only while near the bottom; user scrolling pauses it; Jump to latest resumes it deterministically.
3. Copy selected visible text when a selection exists, otherwise copy the bounded current console transcript; show success/failure feedback.
4. A Clear view action may clear renderer presentation only and must state that it does not delete Hermes history.
5. New output remains bounded by the existing console retention model; do not duplicate transport state.
6. Controls remain reachable by keyboard at 100–140% interface scaling.

### Acceptance

- Tests cover find navigation/wrap, empty query, oversized query, text containing regex punctuation, paused follow, resumed follow, copy success/failure, and clear-view-only semantics.
- Existing Console client tests remain green without modification.
- TypeScript passes for the owned surface.
- No files outside the owned list and named report are changed.

### Copy-paste worker instruction

> Implement Packet TH-3 from `docs/coordination/TERRA-HIGH-OWNERSHIP-PACKETS-2026-08-09.md` exactly. Stay inside its owned files, obey every forbidden boundary, run its focused acceptance suite, write `docs/coordination/HERMES-CONSOLE-ERGONOMICS-REPORT.md`, and stop for Super to review. Do not integrate or edit any adjacent module.

---

## Packet TH-4 — Existing MCP server edit/review surface

**Intelligence:** Terra High

### Objective

Build a standalone, versioned React/TypeScript edit-and-review surface for an already configured Hermes MCP server. It must emit typed review/commit requests to a future coordinator adapter, preserve provenance, and treat secrets as write-only. Do not make network calls or integrate it into Control Center in this lane.

### Owned files

- New `src/Modules/HermesMcpEditor/**`
- Report: `docs/coordination/HERMES-MCP-EDITOR-REPORT.md`

### Read-only references

- `src/Modules/HermesMcp/HermesMcpAdapter.ts`
- `src/Modules/HermesMcp/HermesMcpWorkspace.tsx`
- `src/Modules/HermesMcp/HermesMcpWorkspace.css`
- Existing MCP tests and contracts

### Forbidden

- Any edit under `src/Modules/HermesMcp/**`, `HermesSystem/**`, host/bridge code, upstream `source/**`, package/build files, `App.tsx`, docking code, and every other lane
- Network calls, command execution, environment-value reads, credential reads, server deletion, installation, OAuth launch, or claims that a preview is live

### Required behavior

1. Define a versioned edit contract with a sanitized configured-server snapshot and typed review request/result.
2. Server identity and provenance are immutable in the editor.
3. Editable fields are limited to existing safe contract fields. Environment variable names may be reviewed; stored values never enter props/state/rendered output. Replacement secret values are write-only and disappear after submission/cancel.
4. Show a human-readable before/after review with additions, removals, and changed fields. Never display a secret or raw authorization header.
5. Transport/source/command changes receive an explicit risk label and typed confirmation before emitting a commit request.
6. Duplicate/pending submissions are disabled. Loading, unavailable, validation-error, review-ready, committing, success, and failure states are distinct.
7. The standalone component performs no fetch and accepts callbacks through its typed controller seam.

### Acceptance

- Tests cover immutable identity/provenance, write-only secret replacement, secret redaction in errors and review text, safe diffing, dangerous-change confirmation, duplicate/pending prevention, cancellation, malformed input, and keyboard labels/focus.
- TypeScript passes for the owned module.
- No files outside the new module and named report are changed.

### Copy-paste worker instruction

> Implement Packet TH-4 from `docs/coordination/TERRA-HIGH-OWNERSHIP-PACKETS-2026-08-09.md` exactly. Stay inside its owned files, obey every forbidden boundary, run its focused acceptance suite, write `docs/coordination/HERMES-MCP-EDITOR-REPORT.md`, and stop for Super to review. Do not integrate or edit any adjacent module.
