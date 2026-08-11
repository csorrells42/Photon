# Hermes session administration worker

**Model:** Sol High  
**Repo:** `C:\Users\clsor\Documents\Codex\HermesAgent`

Build the missing upstream Hermes session-management surfaces as one isolated versioned React/TypeScript module. Existing cross-profile FTS search and pinning are already implemented; do not duplicate them. Own only:

- `src\Modules\HermesSessionAdmin\**`
- `docs\coordination\HERMES-SESSION-ADMIN-REPORT.md`

Everything else is read-only. Do not edit App.tsx, global styles, HermesSessions, HermesGateway, AgentDock, docking, host, package/build files, installers, or existing docs. Do not call live Hermes, alter sessions, read private session content, or add dependencies.

Create a versioned adapter contract, deterministic fake adapter, and exported `HermesSessionAdminWorkspace`. Implement: branch/fork creation and bounded descendant trees; multi-select, prune preview, delete preview, and separately confirmed destructive commits; bounded import validation and export request/result flows; honest session statistics; and per-session model-lock set/clear with confirmation before replacement. Every request/result must carry profile identity and reject cross-profile mismatches. Include correlated pending state, cancellation, duplicate prevention, bounded collections, and honest unavailable/partial/error states.

Treat imported content as untrusted text and never render HTML. Make all dialogs and controls keyboard accessible with focus restoration and adaptive large-text layouts. Tests must cover every operation, cross-profile rejection, bounds, cancellation, unsafe import data, destructive confirmation, duplicates, and partial failures. Run module tests and TypeScript compilation. Report exports, upstream route/method assumptions, integration seams, commands/results, and changed files. Stop after the isolated module; the coordinator performs live integration.
