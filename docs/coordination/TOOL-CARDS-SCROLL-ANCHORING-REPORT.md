# Tool cards scroll anchoring report

## Scope

This is a staged TH1 recovery handoff. It changes only the TH1-owned Agent Dock scroll seam, ToolRunRow/ToolTimeline behavior, focused tests, the permitted conversation CSS selector, and this report. The authoritative HermesAgent checkout was read-only throughout.

## Changed files

- `src/Modules/AgentDock/AgentScroll.ts`
- `src/Modules/AgentDock/AgentScroll.test.ts`
- `src/Modules/AgentDock/AgentDock.tsx`
- `src/Modules/AgentDock/AgentDock.ToolRunRow.test.tsx`
- `src/app/styles.css` (only the `.conversation` selector)
- `docs/coordination/TOOL-CARDS-SCROLL-ANCHORING-REPORT.md`

## Behavior

- Adds a small, testable scroll-state seam for following, explicit latest position, and reader-anchor preservation.
- Streaming effects continue to scroll only while `followLatestRef` says the user was already near the bottom. Normal scrolling above the existing 96px threshold suspends follow and shows Jump to latest.
- Tool rows remain in transcript order and a running row updates in place; no completed row is removed.
- Tool disclosure captures its row height before toggling. On the next animation frame, if that row sits above a reader's visible viewport, the conversation scroll offset is adjusted by the exact measured height delta. Visible/below-viewport changes and active follow do not alter reader position.
- Jump to latest remains a deterministic non-animated `scrollTo({ top: scrollHeight, behavior: 'auto' })` and restores follow; subsequent ResizeObserver callbacks remain allowed to follow.
- Adds `overscroll-behavior: contain` only to the existing `.conversation` rule.

## Focused verification

Staged-only evidence, not real-checkout evidence:

- `vitest run Modules/AgentDock/AgentScroll.test.ts Modules/AgentDock/AgentDock.ToolRunRow.test.tsx`: PASS — 2 files, 8 tests.
- Read-only TypeScript overlay check: PASS. It loaded the authoritative TypeScript project while substituting only the staged TH1 AgentDock files in memory; it emitted no files.
- Read-only `git apply --check` against the authoritative HermesAgent root: PASS. No source files were modified.

The interactive ToolRunRow test uses the same exported post-toggle callback used by the disclosure button, drives an above-viewport height change, verifies the 120px reading-anchor adjustment, and verifies active follow is never forcibly paused or repositioned by that callback. No DOM test package was added because none is installed and this packet forbids package changes.

## Limitations

No real-project source files, package configuration, gateway event semantics, or broad release suite were changed or run. Browser layout and ResizeObserver behavior were not exercised against a live UI; only the staged component interaction seam and pure scroll controller were tested.

## Coordinator integration step

From `C:\Users\clsor\Documents\Codex\HermesAgent`, inspect and apply `TH1-recovery-TH4.patch`, then run:

```powershell
pnpm vitest run Modules/AgentDock/AgentScroll.test.ts Modules/AgentDock/AgentDock.ToolRunRow.test.tsx
pnpm exec tsc -p tsconfig.app.json --noEmit
git apply --check <path-to-TH1-recovery-TH4.patch>
```

Review the ToolRunRow interaction seam before integration; this packet intentionally does not modify gateway, host, App, docking, or adjacent modules.
