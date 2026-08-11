# Hermes Upstream Parity Audit Lane

## Mission

Produce an evidence-backed inventory of upstream Hermes functionality and map every capability to the current Hermes Workbench architecture. This is a read-only engineering audit: identify what is present, partially reproduced, delegated to upstream through the compatibility adapter, intentionally replaced by a native Workbench capability, or still missing.

The purpose is to give the main lane an exact implementation and verification backlog while preserving the rule that upstream Hermes remains containerized and independently updateable.

## Exclusive write ownership

This worker may create or edit only:

- `docs/HERMES-FUNCTION-PARITY.md`
- `docs/coordination/HERMES-PARITY-HANDOFF.md`

Everything else is read-only.

## Allowed read scope

The worker may inspect:

- `source/**` as the upstream Hermes reference
- `src/**` as the Workbench implementation
- root and `remote-install` compose/PowerShell scripts
- existing project documentation

Do not inspect `data/**`, `logs/**`, `.env` files, browser profiles, credential stores, generated secrets, or user account data. Do not start or stop any process, container, server, or desktop application. Do not run an authenticated network request.

## Prohibited edits

Do not edit source code, tests, package manifests, Docker files, installer files, artifacts, existing coordination contracts, or another worker's handoff. Do not install dependencies, format the repository, or generate build output.

## Audit method

1. Determine the exact locally available upstream Hermes version/revision from authoritative files. If a revision cannot be proven, say so.
2. Inventory user-visible and operational functionality from upstream routes, UI surfaces, API contracts, commands, and documentation. Do not infer a feature solely from a filename.
3. Trace each capability to its authoritative upstream implementation and its current Workbench counterpart, if any.
4. Classify each capability as exactly one of:
   - `native-complete` — Workbench reproduces it with direct current evidence
   - `adapter-complete` — Workbench exposes it through a versioned compatibility boundary
   - `upstream-delegated` — deliberately kept in the container/upstream UI
   - `partial` — useful implementation exists but required behavior is missing
   - `missing` — no current Workbench path
   - `not-applicable` — intentionally excluded, with rationale
   - `unverified` — evidence is insufficient
5. For every classification, cite concrete repository paths and relevant symbols or line numbers.
6. Separate source presence, automated-test evidence, and live-runtime evidence. Never call something verified merely because it compiles or appears in source.
7. Identify compatibility risks likely to break on an upstream update: endpoint shape, authentication, cookie behavior, WebSocket/SSE messages, filesystem assumptions, provider configuration, and version detection.

## Required capability domains

The final matrix must cover at least:

- installation, update, launch, shutdown, and recovery
- gateway lifecycle and health
- authentication, identity, and providers
- chat, message streaming, session lifecycle, history, and cancellation
- model/provider selection and configuration
- tools, skills, MCP/Serena, approvals, and tool-result rendering
- file attachments and multimodal inputs
- workspace/project/file operations
- terminal and command execution
- memory and context behavior
- scheduled/background work, notifications, and related automation if present upstream
- logs, runtime telemetry, diagnostics, and error recovery
- settings and account management
- responsive/accessibility behavior exposed by upstream UI

Add domains when the source proves additional functionality.

## Deliverable structure

`docs/HERMES-FUNCTION-PARITY.md` must include:

- audited upstream identity and audit date
- architecture boundary summary
- one comprehensive parity table with evidence
- compatibility-contract risks
- prioritized implementation backlog grouped as `P0`, `P1`, `P2`, and `later`
- explicit verification needed for every `partial` or `unverified` item
- update-survival recommendations that do not copy or fork upstream internals unnecessarily

Do not use percentages unless the denominator and weighting are explicitly defined. Do not declare full parity while any required domain is missing or unverified.

## Handoff

When finished, write `docs/coordination/HERMES-PARITY-HANDOFF.md` containing:

- files written
- upstream version/revision evidence
- counts by classification
- five highest-risk gaps
- recommended next implementation slice
- limitations of the audit

Stop after the handoff. Do not implement any finding.
