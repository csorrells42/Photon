# Hermes Extension Settings Lane Report

Date: 2026-08-09  
Lane: Hermes extension and model settings worker  
Owned implementation: `src/Modules/HermesExtensionSettings/**`  
Owned report: `docs/coordination/HERMES-EXTENSION-SETTINGS-REPORT.md`

## Outcome

Completed the standalone `HermesExtensionSettings` authoring/settings module. The coordinator can integrate it through its public entrypoint without changing this lane.

No host, route, docking, installer, package, build, global style, or adjacent module file was edited. No dependency was added. No live service was called.

The supplied workspace shell at `C:\Users\clsor\Documents\Codex\HermesAgent` does not contain Git metadata. Its existing frontend tooling root is `src`; verification ran there.

## Public exports

The public entrypoint is:

`src/Modules/HermesExtensionSettings/index.ts`

Primary exports:

- `HermesExtensionSettingsWorkspace`
- `HermesExtensionSettingsWorkspaceProps`
- `HermesExtensionSettingsController`
- `FakeHermesExtensionSettingsController`
- `fakeHermesExtensionSettingsController`
- `createDeterministicExtensionSettingsSnapshot`
- `HERMES_EXTENSION_SETTINGS_CONTRACT_VERSION`
- all renderer-safe snapshot, write-intent, review, commit, provider-validation, skill, toolset, model, and MCP contract types
- normalization, capability-filtering, specialty-model, advertised-selection, safe-text, and keyboard tab helpers

Contract version:

`hermes-extension-settings/v1`

## Implemented surfaces

### Skill Studio

- Reads and displays bounded skill metadata and content.
- Supports create, editable user-skill edit, and explicit clone-to-user flows.
- Renders content and preview material as text.
- Requires preview followed by a confirmed commit.
- Rejects overwrite attempts against non-user or non-editable skills.
- Preserves these visible provenance labels exactly:
  - `nous-approved`
  - `workbench-reviewed`
  - `external-unreviewed`
  - `user-created`
- Explicitly states that `nous-approved` is catalog provenance and does not imply Chris or Codex approval.

### Independent Search and Extract toolsets

- Search and Extract use separate selections.
- Provider lists are filtered per advertised capability.
- Backend lists are filtered to the selected provider's advertised backend IDs for that capability.
- Unavailable/error providers are not selectable.
- Search and Extract each select a capability-specialty model.
- Fake-controller validation rejects unadvertised provider/backend/model combinations before producing a review.

### Advanced model settings

- Global default model.
- Auxiliary models for vision, compression, title generation, and summarization.
- Task overrides for research, coding, browser, document, and analysis.
- MoA enabled state, preset, bounded reference models, and aggregator.
- Provider timeout and retry settings.
- Custom endpoint base URL, API path, and authentication header name only; URL query, fragment, and user-info input is removed or rejected.
- Provider validation intents with one-shot write-only credential input.
- Additional explicit confirmation when an active selection uses an expensive model.

### MCP configuration

- Renderer-safe MCP list and configuration editor.
- HTTP and stdio non-secret settings.
- Environment variable names only; values are never supplied to the module.
- Before/after review for update, test, enable, and disable intents.
- Confirmed commit for all four actions.
- Deterministic fake test action does not contact a live endpoint.
- MCP provenance is preserved across edits.

## Controller and integration seam

The module does not hardcode a live route or call `fetch`. The coordinator supplies an implementation of:

```ts
interface HermesExtensionSettingsController {
  load(): Promise<LoadResult>
  preview(intent: ExtensionWriteIntent): Promise<WriteReview>
  commit(request: CommitRequest): Promise<CommitResult>
  validateProvider(intent: ProviderValidationIntent): Promise<ProviderValidationResult>
}
```

Required live-controller properties:

- `load` returns only normalized renderer-safe data and secret status/field names, never stored values.
- `preview` validates the complete intent and returns a bounded, secret-free before/after review plus an opaque review ID.
- `commit` treats the review ID as single-use, confirms that the underlying configuration has not drifted, and rejects replay.
- `commit` requires `expensiveModelConfirmed` when the review says it is required.
- `validateProvider` consumes transient secret intents without retaining, returning, logging, serializing, or rendering values.
- Host errors must be sanitized before crossing into the renderer.

## Upstream route assumptions

These are observed existing seams, not routes called by this module:

- Existing skill read/catalog operations use `/api/skills` and `/api/skills/hub/*`. The inspected adapter does not expose custom skill create/edit, so the coordinator must map Skill Studio writes to an upstream-supported authoring route or add a host bridge outside this lane.
- Existing toolset operations use `/api/tools/toolsets` and `/api/tools/toolsets/{name}/*`. The live controller must preserve separate Search and Extract category selections even if upstream represents them as individual toolsets.
- Existing model operations use gateway intents including `model.options`, `config.get`, and `config.set`. The coordinator decides the exact upstream keys for auxiliary tasks, overrides, MoA, provider settings, and custom endpoints.
- Existing MCP operations use `/api/mcp/servers`, per-server `/test` and `/enabled`, plus catalog/auth routes. The live controller should perform its own server-ID/name resolution and drift check before commit.
- This lane assumes the coordinator renders `HermesExtensionSettingsWorkspace` in an existing route/dock and injects the live controller. It does not assume or change a specific `App.tsx` route.

## Security and robustness checks

- Snapshot normalization removes secret-shaped object keys.
- Credential-like text is redacted before it can enter normalized renderer data.
- Collections, identifiers, descriptions, content, arguments, notices, reviews, and error text are bounded.
- Unknown provenance falls back to `external-unreviewed`.
- Non-user skills are forced non-editable regardless of malformed input.
- React text nodes and `pre` elements render untrusted content; there is no `dangerouslySetInnerHTML`.
- No `fetch`, local/session storage, IndexedDB, cookies, console logging, clipboard/copy action, or persistence API exists in the module.
- Credential inputs are uncontrolled password inputs with no value prop. Values are read once into a write-only validation intent and the DOM input is cleared before awaiting the controller.
- Fake validation uses only transient presence/length validation and never copies or compares the supplied secret value.
- Reviews are single-use; the UI also disables duplicate preview/commit submissions while work is pending.
- HTTP URLs are limited to HTTP/HTTPS. Custom model endpoints reject credentials and query data.
- Honest loading, partial, unavailable, error, validation, and success states are exposed.

## Accessibility and adaptive behavior

- Native labeled fields and buttons.
- Roving tab keyboard support for Arrow keys, Home, and End.
- Modal heading focus on open, Escape close, focus trap, and focus restoration to the initiating control.
- `aria-live`, `role=status`, and `role=alert` for state changes.
- Minimum 44-pixel-equivalent interactive targets.
- Strong `:focus-visible` treatment.
- Local container-query layout; no global style edits.
- Relative text sizing and wrapping for large-text use.
- Reduced-motion and forced-colors handling.

## Verification

Focused tests:

```text
vitest run Modules/HermesExtensionSettings --reporter=verbose
Test Files  3 passed (3)
Tests       11 passed (11)
```

The tests cover:

- Search/Extract independence.
- Capability/backend filtering.
- Provenance preservation.
- Secret non-round-tripping.
- Skill clone preview/commit and upstream overwrite rejection.
- MCP update/test/enable preview/commit and replay rejection.
- Expensive-model confirmation.
- Malformed-data bounds/redaction.
- Untrusted-text escaping.
- Keyboard tab operation and modal confirmation semantics.

Module-scoped TypeScript compilation:

```text
tsc --noEmit --target ES2022 --lib ES2022,DOM,DOM.Iterable
    --module ESNext --moduleResolution Bundler --jsx react-jsx
    --strict --skipLibCheck --esModuleInterop
    --allowSyntheticDefaultImports --types node
    Modules/HermesExtensionSettings/*.{ts,tsx}

Result: passed
```

A repository-wide `tsc -b tsconfig.app.json` was also attempted. It is not green because of existing errors outside this lane in `HermesComposer` and `HermesSessionAdmin` (including an ES2023 `Array.prototype.with` target mismatch and unrelated generic/literal-type errors). All initial errors from `HermesExtensionSettings` were corrected; the final module-scoped compile is green. Adjacent owner files were not changed.

## Changed files

- `src/Modules/HermesExtensionSettings/AdvancedModelSettingsPanel.tsx`
- `src/Modules/HermesExtensionSettings/contracts.ts`
- `src/Modules/HermesExtensionSettings/FakeHermesExtensionSettingsController.test.ts`
- `src/Modules/HermesExtensionSettings/FakeHermesExtensionSettingsController.ts`
- `src/Modules/HermesExtensionSettings/HermesExtensionSettingsWorkspace.css`
- `src/Modules/HermesExtensionSettings/HermesExtensionSettingsWorkspace.test.tsx`
- `src/Modules/HermesExtensionSettings/HermesExtensionSettingsWorkspace.tsx`
- `src/Modules/HermesExtensionSettings/index.ts`
- `src/Modules/HermesExtensionSettings/McpSettingsPanel.tsx`
- `src/Modules/HermesExtensionSettings/ReviewDialog.tsx`
- `src/Modules/HermesExtensionSettings/safety.test.ts`
- `src/Modules/HermesExtensionSettings/safety.ts`
- `src/Modules/HermesExtensionSettings/SkillStudio.tsx`
- `src/Modules/HermesExtensionSettings/ToolsetSettingsPanel.tsx`
- `docs/coordination/HERMES-EXTENSION-SETTINGS-REPORT.md`

## Coordinator handoff

Integration is intentionally not performed. The coordinator owns route/dock placement, a live controller implementation, upstream route/key decisions, and any host-side secret handling.
