# Mounted Workbench functional acceptance

Date: 2026-08-11 (America/Chicago)

This checklist is the human-visible and native-runtime counterpart to `FUNCTIONALITY-COMPLETION-MATRIX.md`. A source test or isolated smoke does not close a mounted item. Use disposable projects, conversations, files and credentials where an operation changes state.

Security observations are recorded in the existing deferred-security reports and do not interrupt this functionality pass unless they make an operation unusable.

## 1. Photon CAD

Authoritative detailed sequence: `C:\Users\clsor\AppData\Local\hermes\handoffs\CAD\MOUNTED-CAD-FINAL-ACCEPTANCE.md`.

- [ ] New unique millimeter project, commit a real bearing, obtain clean revision 2 and visible sealed GLB.
- [ ] Orbit, zoom, Fit and Reset visibly affect the model.
- [ ] Create a second project with a Spur Gear and prove tab-isolated state/preview.
- [ ] Place and remove an occurrence; hierarchy, transforms and BOM update.
- [ ] Run committed verification and export one generic STEP Part-21 file.
- [ ] Close/reopen restores exact canonical state and preview.
- [ ] Collapse/resize/restore Explorer, Photon, Parts and Inspector without losing either CAD session.

## 2. C# compiler, Roslyn and debugger

Use one disposable workspace containing a `.sln` or `.slnx`, a buildable project, tests and a small executable.

- [ ] Run panel reports .NET project, compiler, Roslyn and debugger `4/4 Host verified`.
- [ ] Build succeeds; introduce one compiler error and confirm a typed file/line/column diagnostic; repair and rebuild.
- [ ] Run filtered tests and confirm bounded passed/failed/skipped totals.
- [ ] Explicitly open the solution; completion, hover, definition, references and rename work across two files.
- [ ] A bounded code action previews only workspace edits and applies only after explicit intent.
- [ ] Launch debug, set breakpoint, stop, inspect threads/stack/frame zero/scopes/variables, evaluate, step over/into/out and disconnect.
- [ ] Explicit relaunch works after adapter exit; no automatic replay or hidden child remains.

## 3. Python

Use one disposable Python package with two modules and one unittest.

- [ ] Python reports project, syntax, tests and Serena language service `4/4 Host verified`.
- [ ] Project inspection identifies the package and selected source.
- [ ] Syntax check reports one intentional error, then clears after repair.
- [ ] Unittest executes and projects typed totals/output.
- [ ] Serena navigation resolves one Python symbol across files.

## 4. Runtime providers and model selection

- [ ] Settings -> Runtime reflects the active `local-lm-studio` provider and `openai/gpt-oss-20b` rather than an empty default.
- [ ] Test & discover against `http://host.docker.internal:1234/v1` returns the current LM Studio model inventory.
- [ ] Save/update the endpoint without overriding LM Studio context, output, temperature or top-p settings.
- [ ] Use for new conversations selects the expected model in a fresh Photon conversation.
- [ ] Composer reasoning control shows only the server-advertised levels; changing it affects only the current conversation.
- [ ] Hosted-provider selection and native Connections remain available without exposing credentials.

## 5. Photon chat, sessions, images and docks

- [ ] Send one ordinary prompt, cancel one in-flight prompt and retry successfully.
- [ ] Create a new conversation, switch away/back and reopen it after renderer reload.
- [ ] Paste one screenshot once and confirm exactly one attachment plus an image-grounded response.
- [ ] Attach a normal file through the picker and confirm filename/type/size projection.
- [ ] Collapse, move and restore Photon; saved docking/layout returns after reload.
- [ ] Conversation bus reports Chris, Codex and Photon online; direct and Everyone routing preserve sender first, recipient second.

## 6. Workspace, search, editor and terminal

- [ ] Explorer lists the exact dedicated workspace, not the install repository or user profile.
- [ ] Open, edit and atomically save a disposable file; an external edit produces a visible conflict rather than overwrite.
- [ ] Literal search visibly distinguishes its selected mode and navigates to the exact match.
- [ ] Serena semantic search finds one Python and one C# symbol and navigates to line/column.
- [ ] Terminal opens globally, starts in the dedicated workspace, executes one harmless command and survives panel switching.
- [ ] Show/Hide Terminal labels match actual visibility.

## 7. Browser, Docker and source control

- [ ] Browser opens two tabs, preserves independent full paths/query state and supports Back/Forward without crossing tab histories.
- [ ] A resize/reveal does not reload the current page or lose form state.
- [ ] Docker Control refreshes snapshot/logs; one reviewed restart succeeds; unavailable Update stays disabled.
- [ ] Source Control displays current repository status and opens a selected change.
- [ ] Git Extensions launch works on this machine and remains labeled as an optional external integration for portable installs.

## 8. Connections, usage and memory

- [ ] Add/change/remove one disposable provider credential through the native vault; no value is rendered or logged.
- [ ] A new runtime lease uses the selected connection and is revoked after removal.
- [ ] Usage Intelligence refreshes every actually configured restricted provider and labels unavailable consumer/subscription data honestly.
- [ ] Memory footer reports `Memory ready - Mem0 local` only while authenticated Qdrant search is live.
- [ ] Add one unique memory fact, find it through `mem0_search`, restart the gateway and find it again.
- [ ] No `mem0_read` tool or cached-outage status is claimed.

## 9. Settings, integrations and session administration

- [ ] Overview refreshes gateway/component health.
- [ ] MCP lists configured servers and completes one reversible supported refresh/action.
- [ ] Skills preview and scan one safe skill without applying an unsupported action.
- [ ] Advanced extensions list and perform one reversible supported assignment.
- [ ] Profiles & runtime selects and reads one profile without mutating unsupported fields.
- [ ] Session administration branches, exports, imports and reopens disposable session data; cancellation clears busy state.

## 10. Low-priority tooling

Authoritative sequence and implementation packet: `LOW-PRIORITY-TOOLING-ENABLEMENT-PACKET.md`.

- [ ] Arduino provisioned and mounted; real `.ino` inspection and `arduino:avr:uno` compile pass.
- [ ] Java/JDT provisioned and mounted; real multi-file Java semantics pass.
- [ ] Frozen GCC transaction published last; real `.c` and `.cpp` compilation pass without regressing earlier providers.

## Completion rule

The functionality-first goal is complete only when every applicable item above has current mounted or durable-runtime evidence and every unsupported item is explicitly unavailable rather than presented as functional. After this checklist closes, run the final requirement-by-requirement audit and only then begin security remediation.

