# Hermes profiles and tool-runtime worker

**Model:** Sol High  
**Repo:** `C:\Users\clsor\Documents\Codex\HermesAgent`

Build a standalone versioned settings surface for the remaining profile-scoped and Hermes tool-runtime controls. Own only:

- `src\Modules\HermesProfileRuntime\**`
- `docs\coordination\HERMES-PROFILE-RUNTIME-REPORT.md`

Everything else is read-only. Do not edit App.tsx, global styles, HermesSystem, HermesSessions, HermesGateway, NativeTerminal, AgentDock, docking, host, package/build files, installers, or existing docs. Do not call live Hermes, alter profiles, grant permissions, inspect credentials, or add dependencies.

Export `HermesProfileRuntimeWorkspace`, a versioned typed adapter contract, deterministic fake adapter, and tests. Include: active-profile selection; profile create/clone/rename/delete with preview and explicit destructive confirmation; persona/soul/context editing with dirty-state protection and caller-owned save; profile model, project, and worktree intent controls; bounded profile import/export validation; Hermes terminal-backend selection and status; computer-use status and permission-request/grant intents; provider OAuth/credential-pool/custom-endpoint status without secret values; schema-driven non-secret configuration; and honest delegated/unsupported labels for anything not safely manageable.

Keep the native Workbench ConPTY terminal conceptually separate from the selected Hermes agent terminal backend. Never grant a permission from a row click: preview and confirmation are separate typed events. Treat imported/config text as untrusted text, bound it, and never render HTML. Every operation must carry profile identity and reject cross-profile results. Add pending correlation, cancellation, duplicate prevention, partial/error/unavailable states, keyboard/focus accessibility, large-text/zoom resilience, reduced motion, and adaptive local styling.

Tests must cover profile isolation, destructive confirmations, unsafe import/config data, permission confirmation, ConPTY-versus-Hermes-backend labeling, secret non-round-tripping, bounds, cancellation, duplicates, and malformed data. Run focused tests and TypeScript compilation. Report exports, upstream route assumptions, integration seams, security checks, results, and changed files. Stop after the isolated module; the coordinator performs live integration.
