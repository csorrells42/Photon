# Docking Splitter Lane Report

## Status

Complete. The provider-neutral `DockGroup` now renders polished draggable splitters for horizontal and vertical layouts while preserving its existing React props and unchanged tab presentation. Every write stayed within:

- `src/Modules/WorkbenchDocking/**`
- `docs/coordination/DOCKING-SPLITTER-LANE-REPORT.md`

No integration, package, desktop-host, installer, `App.tsx`, `app/styles.css`, AgentDock, or CodexAgent file was edited.

## Files changed

- `src/Modules/WorkbenchDocking/DockGroupLayout.ts`
- `src/Modules/WorkbenchDocking/DockGroup.tsx`
- `src/Modules/WorkbenchDocking/DockGroup.css`
- `src/Modules/WorkbenchDocking/DockGroupLayout.test.ts`
- `src/Modules/WorkbenchDocking/DockGroup.test.tsx` — new
- `docs/coordination/DOCKING-SPLITTER-LANE-REPORT.md` — new

No dependencies or package metadata were added or changed.

## Behavior implemented

### Layout and persistence

- `DockGroupLayout` has a backward-compatible optional `splitRatios` field. Existing callers and fallback objects without ratios remain valid.
- The existing `saveDockGroupLayout`/`loadDockGroupLayout` JSON contract automatically persists proportions; no integration change was required.
- Missing, wrong-length, non-number, non-finite, zero, negative, extreme, and stale ratios normalize to a finite positive set whose sum is one.
- Extreme but valid proportions receive a safe floor without imposing a fixed panel-count ceiling.
- Edge docking resets the newly ordered split layout to equal proportions, avoiding stale ratios being applied to different panel identities.
- Equal reset is available by double-clicking any separator or pressing Enter while it is focused.
- Tab mode omits split ratios during normalization, renders no separators, and retains the existing tabs and inactive-panel behavior.

### Unlimited registered panels

- Splitters are generated from `layout.order`; there is no two-panel assumption or maximum registration count.
- One separator is rendered for each adjacent boundary (`panel count - 1`).
- Horizontal and vertical split layouts use proportional flex growth.
- Panels have sensible minimums: 180 px horizontally and 120 px vertically.
- When the registered panel set cannot fit those minimums, the dock group becomes scrollable instead of collapsing panels below their usable size.
- Pointer calculations use the larger of the visible and scrollable axis, so large scrollable panel sets remain resizable.

### Pointer interaction

- Primary-pointer drag starts only on a separator.
- Pointer capture keeps the drag active when the pointer leaves the seven-pixel separator target.
- Movement is axis-aware: X for horizontal panel layouts, Y for vertical panel layouts.
- Only the two panels adjacent to the dragged separator are changed; all other ratios and the total remain stable.
- Pixel minimums are translated into an effective ratio and clamped safely, including undersized adjacent pairs.
- Pointer up, cancellation, and lost capture clear transient drag state.

### Keyboard and accessibility

- Every splitter uses `role="separator"`, `tabIndex=0`, an axis-correct `aria-orientation`, adjacent `aria-controls`, a descriptive label, and `aria-valuemin`/`aria-valuemax`/`aria-valuenow` values for the adjacent pair.
- Horizontal layouts use Left/Right Arrow; vertical layouts use Up/Down Arrow.
- Normal keyboard steps are 2.5 percentage points; Shift uses a 10-point accelerated step.
- Home and End move the separator to its safe lower or upper bound.
- Enter resets the whole split group to equal proportions.
- Tooltips document drag, arrow, Shift, Enter, and double-click actions.

### Styling

- Splitters have a seven-pixel pointer/focus target with a one-pixel neutral center rule.
- Hover, keyboard focus, and active drag use the existing Workbench line/accent palette with restrained glow and axis-specific expansion.
- Horizontal splitters use `col-resize`; vertical splitters use `row-resize`.
- Splitters remain hidden in the existing non-split code/chat layout overrides.
- Existing tab styling selectors and dimensions were not changed.

No provider, agent, language, debugger, or panel-ID-specific behavior was introduced.

## Focused coverage

`DockGroupLayout.test.ts` covers:

- invalid/stale panel registries;
- all docking edges and tab stacking;
- arbitrary registered panel handling;
- malformed, non-number, non-finite, negative, wrong-length, and extreme ratios;
- normalized sums and safe ratio floors;
- persisted ratio storage round-trip;
- pointer delta conversion, adjacent-only changes, total preservation, and pixel minimum clamping;
- orientation-specific keyboard controls, normal and Shift steps, Home/End bounds, Enter reset, and invalid separator indexes;
- unchanged tab behavior.

`DockGroup.test.tsx` uses React server rendering to cover:

- twelve registered panels with eleven generated splitters;
- separator orientation, focusability, labels, controls, and numeric ARIA semantics;
- vertical-layout separator semantics;
- unchanged tab rendering with no splitters.

## Verification commands and final results

Commands were run from `C:\Users\clsor\Documents\Codex\HermesAgent\src`.

### TypeScript compile

```powershell
& 'C:\Users\clsor\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' '.\node_modules\typescript\bin\tsc' -b --pretty false
```

Result: **passed**, exit code 0, no diagnostics.

### Focused Vitest

```powershell
& 'C:\Users\clsor\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' '.\node_modules\vitest\vitest.mjs' run Modules/WorkbenchDocking/DockGroupLayout.test.ts Modules/WorkbenchDocking/DockGroup.test.tsx
```

Result: **passed**.

- Test files: 2 passed, 0 failed
- Tests: 12 passed, 0 failed
- Final duration reported by Vitest: 277 ms

### Full frontend Vitest suite

```powershell
& 'C:\Users\clsor\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' '.\node_modules\vitest\vitest.mjs' run
```

Result: **passed**.

- Test files: 28 passed, 0 failed
- Tests: 123 passed, 0 failed
- Final duration reported by Vitest: 855 ms

### Preliminary runner notes

- An initial `pnpm exec tsc -b --pretty false` attempt did not reach TypeScript because the managed pnpm wrapper attempted an install and stopped on `[ERR_PNPM_IGNORED_BUILDS]` for `esbuild@0.28.1`. No install approval, package change, or build-script change was made.
- A direct `.\node_modules\.bin\tsc.cmd` attempt did not reach TypeScript because `node` was not on that shell's PATH. Final verification therefore invoked the existing local TypeScript and Vitest entrypoints with the bundled Node executable, as shown above.
- Early focused runs exposed only test expectation issues: IEEE fractional representation and the correct adjacent-pair ARIA minimum for a twelve-panel layout. Assertions were corrected to use numeric tolerance and the computed semantic value; final source and all suites are green.

## Remaining limitations

- Pointer capture and CSS appearance were not exercised in a live browser/WebView. Pointer resize math and rendered accessibility markup are covered by Vitest.
- Minimum panel dimensions intentionally produce dock-group scrolling when many panels cannot fit; this preserves usability but can require scrolling to reach later panels.
- Ratio persistence continues to depend on the controlling caller invoking the existing save function after `onLayoutChange`; the current integration already follows that contract, but no integration file was modified or retested manually.
- The resize implementation does not add an explicit visual “Reset” button; Enter and double-click provide the requested equal reset.

## Integration

No external integration is required for existing `DockGroup` consumers. The public component props are unchanged. Controlled layouts will begin receiving `splitRatios` through the existing `onLayoutChange`, and the existing layout save/load helpers preserve them. Consumers that construct layouts without `splitRatios` receive equal panels safely.
