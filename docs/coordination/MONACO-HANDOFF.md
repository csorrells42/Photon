# Monaco editor handoff

## Result

`WorkspaceEditor` now uses a lazy-loaded, read-only Monaco editor for workspace-file previews. It retains the existing React props (`path`, `onClose`), loading/empty/error states, active tab, breadcrumbs, close action, and `TerminalDock`.

## Files changed

- `src/Modules/Workspace/WorkspaceEditor.tsx`
- `src/Modules/Workspace/MonacoEditor.tsx`
- `src/Modules/Workspace/MonacoEditor.css`
- `src/Modules/Workspace/MonacoLanguage.ts`
- `src/Modules/Workspace/MonacoLanguage.test.ts`
- `src/package.json`
- `src/package-lock.json`
- `docs/coordination/MONACO-HANDOFF.md`

## Dependency added

- `monaco-editor` `^0.56.0`

## Verification

- `npm test -- MonacoLanguage.test.ts` — passed: 1 file, 14 tests.
- `npm test` — passed: 10 files, 52 tests.
- `npm run build` — passed: TypeScript project build and Vite production build.

The production build emits Monaco as an asynchronous editor chunk, plus the Monaco language workers; the initial Workbench entry remains separate from the Monaco chunk.

## Integration instructions

No integration changes are required outside this lane. Keep rendering `WorkspaceEditor` with its existing `path` and `onClose` props. Opening a readable workspace file loads Monaco on demand, maps its filename to a Monaco language ID, and presents it as read-only.

## Remaining limitations

- The editor intentionally remains read-only because the workspace bridge has no write endpoint.
- No live desktop-host or Docker runtime smoke test was performed; neither was started for this lane. The successful production build includes Monaco's worker assets.
- Monaco's full language distribution is loaded only after a workspace file is opened; it is intentionally not part of the initial Workbench bundle.
