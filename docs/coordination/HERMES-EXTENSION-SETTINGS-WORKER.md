# Hermes extension and model settings worker

**Model:** Sol High  
**Repo:** `C:\Users\clsor\Documents\Codex\HermesAgent`

Build a standalone settings/authoring module for remaining upstream surfaces: custom skill create/edit/content, independent Search and Extract toolset providers, toolset specialty models, advanced global/provider model settings, and MCP configuration review/edit. Own only:

- `src\Modules\HermesExtensionSettings\**`
- `docs\coordination\HERMES-EXTENSION-SETTINGS-REPORT.md`

Everything else is read-only. Do not edit App.tsx, global styles, HermesSkills, HermesMcp, HermesSettings, HermesSystem, AgentDock, docking, host, package/build files, installers, or existing docs. Do not add dependencies, inspect secrets, or call live services.

Export `HermesExtensionSettingsWorkspace`, typed controller contracts, and deterministic fake adapters. Include: Skill Studio with read/create/edit preview and confirmed save, never overwriting Nous/upstream skills without explicit clone-to-user intent; independent Search/Extract provider selection that only offers advertised capability/backend combinations; toolset model specialty selection; auxiliary task models, task overrides, MoA presets/reference/aggregator choices, provider validation intents, and custom endpoint non-secret settings; MCP before/after review and confirmed update/test/enable intents. Secret fields are write-only intents and stored values must never enter props, results, logs, copy actions, persistence, or rendering.

Preserve visible provenance labels: `nous-approved`, `workbench-reviewed`, `external-unreviewed`, and `user-created`; never imply Chris/Codex approval for a Nous catalog item. Separate preview from commit for every write. Bound all collections/text, render untrusted material as text, prevent duplicate submissions, and expose honest loading/unavailable/partial/error states. Include accessible keyboard/focus behavior, large-text support, reduced motion, and adaptive local styling.

Tests must prove Search/Extract independence, capability filtering, provenance preservation, secret non-round-tripping, safe skill/MCP preview-commit flow, expensive-model confirmation, malformed-data safety, and keyboard operation. Run focused tests and TypeScript compilation. Report exports, upstream route assumptions, integration seams, security checks, results, and changed files. Stop after the isolated module; the coordinator integrates it.
