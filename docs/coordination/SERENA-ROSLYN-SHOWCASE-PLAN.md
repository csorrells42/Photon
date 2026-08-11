# Serena × Roslyn Showcase Plan

**Status:** Approved product direction and test plan. This document does not authorize implementation, integration, distribution, or external contact by itself.

## Objective

Polish Hermes Workbench into a professional evaluation build that demonstrates Serena and Roslyn working together across a real C# and React/TypeScript engineering loop. Keep the currently proven host-Serena architecture for the showcase release; defer experimental Serena container migration until the product and proof suite are stable.

“Full capacity” means using Serena's valuable semantic capabilities within explicit trust, path, output, cancellation, and review boundaries. It does not mean exposing unrestricted shell, network, filesystem, or credential authority.

## Guiding roles

- **Serena:** architectural understanding, symbol discovery, references, multi-language navigation, agent context, project memory where appropriate, and impact analysis.
- **Roslyn:** authoritative C# syntax, semantics, diagnostics, completion, definition, references, rename, and code actions.
- **MSBuild:** authoritative build truth.
- **DAP/NetCoreDbg:** runtime breakpoints, stepping, variables, evaluation, and call stacks.
- **Hermes:** orchestrates the engineering loop and explains what is happening.
- **Workbench:** keeps every action visible, bounded, cancellable, attributable, and user-approved.

Neither Serena nor Roslyn reinterprets the other's authoritative output. Debugger behavior must not be attributed to Roslyn.

## Delivery phases

| Phase | Outcome | Exit gate |
|---|---|---|
| 1. Truth audit | Exact inventory of complete, partial, planned, and unavailable features | Every visible claim matches working behavior |
| 2. Roslyn integration | Real C# language intelligence in Monaco | Completion, hover, definition, references, rename, diagnostics, cancellation, and recovery pass |
| 3. Debugger integration | Real explicitly authorized .NET debugging | Launch, breakpoints, stepping, stack, scopes, variables, evaluation, disconnect, and crash recovery pass |
| 4. Serena showcase | Full bounded semantic navigation and impact analysis | Live Serena attribution, bounded results, cancellation, multi-language symbols, and failure states pass |
| 5. Golden project | Purpose-built C# API and React client demonstration | Ten consecutive deterministic demonstration runs pass |
| 6. Product polish | Cohesive and accessible Workbench experience | Keyboard, zoom, screen reader, reduced motion, large output, and offline-state QA pass |
| 7. Distribution hardening | Clean evaluation package | Fresh-machine install, launch, verification, shutdown, uninstall, and rollback pass |
| 8. Gift and outreach | Professional Serena-team evaluation package | Christopher approves every artifact before anything is shared |

## Phase 1 — Establish product truth

1. Freeze the showcase feature list.
2. Record exact Hermes, Serena, .NET, Node, Roslyn, and debugger versions and identities.
3. Audit every Workbench surface for simulated, partial, stale, or misleading states.
4. Remove or visibly mark anything that is not operational.
5. Confirm Workspace Search, build diagnostics, Problems, Monaco, terminal, health, cancellation, shutdown, and recovery behavior.
6. Create one authoritative showcase acceptance matrix with commands, pass totals, timings, limitations, and artifact hashes.

**Deliverable:** a red/yellow/green product-truth report. No feature advances to showcase status without reproducible evidence.

## Phase 2 — Complete real Roslyn language intelligence

Build on the existing bounded LSP nucleus:

1. Select a legally distributable, pinned Roslyn Language Server implementation.
2. Record its exact source revision, release asset, SHA-256, license, and third-party notices.
3. Provision it through the trusted installer; never download it at runtime.
4. Launch it as one explicitly owned, shell-free child process with an empty-by-default environment.
5. Connect document ownership and versioned synchronization to Monaco.
6. Implement diagnostics, completion, hover, definition, references, rename, and explicitly approved code actions.
7. Reject path escapes, reparse points, stale document versions, oversized frames, and unadvertised server requests.
8. Exercise cancellation, shutdown, restart, adapter exit, malformed frames, and workspace changes.

Roslyn remains authoritative for C# semantics. Raw server errors, executable paths, process details, and unrestricted language-server messages never enter renderer authority.

## Phase 3 — Complete real debugging

1. Approve one exact NetCoreDbg release and commit.
2. Record and verify the exact platform asset hash, license, notices, and installer receipt.
3. Integrate the completed fixed DAP provider through the existing trusted host and provider registry.
4. Require explicit launch and attach authorization.
5. Add breakpoint controls, stopped-line decoration, call stack, scopes, variables, watch/evaluate, continue, and step operations.
6. Never automatically launch, attach, reconnect, restart, or replay a debug request.
7. Recover safely from adapter exit, cancellation, malformed frames, and indeterminate disconnects.
8. Keep executable paths, raw DAP, adapter stderr, process selection, and authorization policy native-only.

## Phase 4 — Showcase Serena properly

Demonstrate Serena as the agent's architectural intelligence:

- Project onboarding and indexing.
- Symbol overview.
- Intent-based symbol discovery.
- Definitions and referencing symbols.
- Multi-file and multi-language impact analysis.
- C# and TypeScript understanding.
- Project memory where appropriate and explicitly scoped.
- Explicitly reviewed edits or refactors.
- Cancellation, bounded output, deterministic path handling, and safe recovery.
- Honest Serena attribution only after trusted evidence proves Serena served the request.

Do not expose unrestricted commands merely to claim “full capacity.” The achievement is making Serena powerful while preserving user control.

## Phase 5 — Build the golden demonstration project

Create a polished synthetic application with:

- A .NET 10 API.
- A React/TypeScript client.
- A shared reconnect/recovery scenario.
- Real focused tests.
- One deliberate cross-stack defect.
- No external accounts, credentials, API keys, or network dependency.

### Golden demonstration flow

1. Ask Serena where reconnect behavior originates and where its decision is consumed.
2. Navigate through the returned C# and TypeScript symbols.
3. Inspect Roslyn hover, definitions, references, and diagnostics.
4. Review the complete impact of a proposed change.
5. Apply an explicitly approved fix.
6. Rebuild and clear Problems and Monaco markers with real compiler evidence.
7. Run the focused tests.
8. Start an explicitly authorized debug session.
9. Hit the C# decision breakpoint, inspect state, and step through recovery.
10. Observe the corrected React behavior and ask Serena to summarize the resulting architecture.

The flow must be deterministic and repeatable without a live provider account.

## Phase 6 — Product polish

- Cohesive purple/teal Workbench styling.
- Consistent spacing, typography, icons, motion, and state copy.
- No clipped panels at large text or high zoom.
- Complete keyboard operation and visible focus.
- Focus restoration after dialogs, completion lists, and navigation.
- Screen-reader labels and live-region behavior.
- Reduced-motion support.
- Bounded logs and virtualization for large result sets.
- Friendly recovery copy without raw errors.
- First-run guided tour.
- One-action opening of the showcase workspace.
- Clear attribution for Hermes, Serena, Roslyn, MSBuild, and the debugger.

## Phase 7 — Harden the gift build

The package must contain no:

- API keys, passwords, tokens, or credentials.
- Machine-bound credential references.
- Sessions, chats, user memories, or personal workspaces.
- Logs, crash dumps, caches, process identities, or temporary output.
- Absolute developer-machine paths.
- Unreviewed or unverified binaries.

The package must include:

- Signed or integrity-verified artifacts.
- A per-file SHA-256/length manifest.
- SBOM and NOTICE material.
- Exact component/version/source receipt.
- Verification-only installer mode.
- Clean uninstall and rollback evidence.
- Fresh-Windows-machine test results.
- A short privacy and security statement.

Portable exports may carry non-secret credential requirements or slot names only. Credentials remain Windows-user/machine-bound and moving machines requires explicit re-entry.

## Phase 8 — Prepare the Serena-team gift

Prepare, but do not send without Christopher's explicit approval:

- Clean evaluation build.
- Golden showcase project.
- Five-minute guided tour.
- Two-minute demonstration video.
- Architecture diagram.
- Verification report with commands, totals, timings, limitations, and hashes.
- Version, licensing, and provenance receipt.
- Concise integration story.
- Invitation to critique the integration and influence future container work.

Do not use Serena branding or imply endorsement without permission. Describe the package honestly as a Workbench evaluation build demonstrating integration with the exact Serena version.

## Final release gates

The showcase ships only after:

- All focused frontend and .NET suites pass.
- Strict TypeScript passes.
- All Release builds pass with zero errors.
- Roslyn and DAP adversarial smoke suites pass.
- Ten consecutive golden demonstrations pass.
- A two-hour mixed-use soak remains stable.
- Clean-machine install, verify, launch, shutdown, and uninstall pass.
- Offline startup succeeds after installation.
- Update rollback is demonstrated.
- Credential and personal-data scans find nothing.
- Every visible capability claim is backed by current reproducible evidence.

## Working discipline

Proceed checkpoint by checkpoint, returning control after each milestone. Preserve strict file ownership and integration boundaries. No isolated worker mounts its own feature into product-owned files; the coordinator integrates reviewed artifacts.

First make it true. Then make it reliable. Then make it beautiful. Speed comes after balance.

