# Monaco parallel-work lane

This lane is reserved for the Codex session implementing Monaco syntax highlighting while the primary session builds Hermes Control Center and native authentication.

## Objective

Replace the plain read-only code rendering inside `WorkspaceEditor` with a polished, lazy-loaded Monaco editor that highlights the currently selected workspace file and preserves the existing terminal dock and Workbench layout.

The workspace bridge is intentionally read-only today. Monaco must remain read-only in this lane; do not add saving or a write endpoint.

## Files this lane owns

- `src/Modules/Workspace/WorkspaceEditor.tsx`
- New files below `src/Modules/Workspace/` whose names begin with `Monaco` or `WorkspaceEditor`
- `src/package.json`
- `src/package-lock.json`
- `docs/coordination/MONACO-HANDOFF.md`

Use module-scoped CSS under `src/Modules/Workspace/` so this lane does not edit the global stylesheet.

## Files reserved by the primary session

Do not edit these while this lane is active:

- `src/app/App.tsx`
- `src/app/styles.css`
- `src/vite.config.ts`
- `src/Modules/HermesSystem/**`
- `src/Host/**`
- `remote-install/**`
- `artifacts/**`
- `source/**` (clean upstream reference; always read-only)

If Monaco cannot work without changing a reserved file, stop and record the exact required change in `docs/coordination/MONACO-HANDOFF.md`; do not make it.

## Acceptance checks

- Dynamically import Monaco so the initial Workbench bundle stays lean.
- Map common workspace extensions to Monaco language IDs, including TypeScript, TSX, JavaScript, JSX, C#, Python, JSON, Markdown, CSS, HTML, YAML, PowerShell, and Dockerfile.
- Preserve loading, empty, oversized/binary, and read-error states supplied by `WorkspaceAdapter`.
- Preserve the active filename/tab, breadcrumbs, close action, and `TerminalDock`.
- Use a dark theme compatible with the purple-and-green Workbench palette.
- Keep the editor read-only, disable the minimap on narrow layouts, and make layout resize cleanly.
- Add focused tests for language mapping and any new normalization logic.
- Run the Workbench tests and production build.
- Write a concise result, file list, dependency list, and verification output to `docs/coordination/MONACO-HANDOFF.md`.
