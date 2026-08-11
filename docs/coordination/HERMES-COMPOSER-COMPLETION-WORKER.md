# Hermes advanced composer worker

**Model:** Sol High  
**Repo:** `C:\Users\clsor\Documents\Codex\HermesAgent`

Build a standalone production-ready advanced Hermes composer. Own only:

- `src\Modules\HermesComposer\**`
- `docs\coordination\HERMES-COMPOSER-COMPLETION-REPORT.md`

Everything else is read-only. Do not edit App.tsx, global styles, AgentDock, HermesGateway, docking, host, package/build files, installers, or existing docs. Do not add dependencies or call live services.

Export `HermesAdvancedComposer` plus a headless typed controller. Implement: bounded prompt queue with edit/remove/reorder/submit-next and at-most-once submission; caller-owned drafts and bounded history; keyboard-accessible `@` mention and `/` command completion from caller-supplied catalogs; edit/resend and retry/regenerate intents referencing supplied message IDs; a callback-only voice seam with idle/listening/processing/error states; and ready/reconnecting/offline/blocked states. Reconnect must never automatically replay a prompt. Do not silently persist prompt text.

Make it keyboard accessible, restore focus, support large text/zoom and reduced motion, and match the purple/teal adaptive Workbench styling locally inside the module. Render all input as text, bound all input, and perform no network/filesystem/shell/credential work.

Add focused tests for ordering, cancellation, duplicate prevention, bounds, completion keyboard behavior, HTML-as-text, voice states, no-replay reconnect, and recovery. Run module tests and TypeScript compilation. The report must list exports, proposed integration seams, commands/results, and changed files. Stop after the isolated module; the coordinator integrates it.
