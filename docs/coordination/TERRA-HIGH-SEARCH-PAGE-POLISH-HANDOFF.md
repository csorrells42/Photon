# Terra High Handoff: Search Page Visual Polish

## Objective

Polish the Workspace Search page so it feels native to the current Phos Agape Aphthartos workbench while preserving all existing search behavior, result ordering, safety bounds, and host contracts.

This is a presentation and interaction-quality lane, not a search-engine rewrite.

## Ownership

The Terra High worker may modify only:

- `src/Modules/WorkspaceSearch/WorkspaceSearchPanel.tsx`
- `src/Modules/WorkspaceSearch/WorkspaceSearchPanel.css`
- `src/Modules/WorkspaceSearch/WorkspaceSearchPanel.test.tsx`

No other file is authorized. If another file is required, stop and report the exact seam and reason before writing it.

## Current authoritative baselines

Record these before editing and stop if the live bytes differ:

- `WorkspaceSearchPanel.tsx` — `83D1FFDF9BA1092EB50DA4D5BFC9AF3D8CF537E38A46DC40C320A6497B340916`
- `WorkspaceSearchPanel.css` — `DEF5794D81453813FDA4EC2F64D499C42110377866438A2D29C8E4FEAFF0FCA6`
- `WorkspaceSearchPanel.test.tsx` — `242BC615596C7E87212A10293EEAED1BAC537A05DC3FC37788EF5D52027FC2A2`

The CSS and test already contain an intentional uncommitted improvement that makes Literal versus Semantic selection more pronounced. Preserve and build on that work; do not revert it to Git HEAD.

## Required outcome

1. Literal versus Semantic mode must be unmistakable without becoming loud or gimmicky.
   - Preserve `aria-pressed` semantics.
   - Selected mode needs a clear restrained accent, indicator, and focus state.
   - Unselected mode must remain visibly interactive.

2. Replace the current dense wall of purple result slabs with a calmer professional result hierarchy.
   - Path/title is primary.
   - Line and column are compact secondary metadata.
   - Preview/snippet is readable and visually subordinate.
   - Match highlighting remains legible.
   - Repeated results remain easy to scan.
   - Do not hide or truncate evidence needed to identify the result.

3. Improve spacing and structure.
   - Clear separation among query controls, mode controls, status/count, and results.
   - Avoid excess vertical padding that wastes editor space.
   - Keep the bounded-results notice visible but quiet.
   - Empty, loading, unavailable, error, and results states should all look intentional.

4. Match the rest of Phos.
   - Reuse the existing dark surfaces, cyan/teal active accents, restrained purple identity, border radii, typography, and focus language.
   - Do not introduce a competing design system or global CSS.

5. Responsive behavior must be deliberate at approximately 360, 800, and 1280 CSS pixels.
   - Controls wrap without overlap.
   - Paths/snippets do not force horizontal page overflow.
   - Result metadata remains readable.
   - Keyboard focus stays visible.

6. Accessibility must remain first-class.
   - Preserve semantic buttons, labels, status/live regions, and result navigation.
   - Add `:focus-visible` treatment where needed.
   - Provide usable forced-colors behavior.
   - Do not encode selected mode by color alone.

## Behavioral invariants

Do not change:

- literal or semantic query meaning;
- provider selection or availability;
- result normalization, ordering, limits, or deduplication;
- query submission, cancellation, clear, or open-result behavior;
- controller or host bridge contracts;
- paths, previews, line/column coordinates, or provenance content;
- result count and bounded-result truthfulness;
- any search safety or redaction boundary.

## Forbidden paths and actions

- No edits to `WorkspaceSearchController`, providers, native host, Serena, `App.tsx`, or global `styles.css`.
- No edits to Docker Control Center, CAD, DeveloperServices, launcher, runtime, or installer files.
- No package additions.
- No screenshots or visual claims substituted for executable tests.
- No broad formatting or mechanical rewrite outside the three owned files.

## Required tests

Add or update focused assertions for at least:

- literal selected / semantic unselected;
- semantic selected / literal unselected;
- result path, line/column, preview, and highlight projection;
- loading, empty, unavailable, error, and bounded-results states;
- accessible labels and mode semantics;
- any new structural class or control introduced by the polish.

## Acceptance gates

Run from `C:\Users\clsor\Documents\Codex\HermesAgent\src`:

```powershell
npx.cmd vitest run Modules/WorkspaceSearch/WorkspaceSearchPanel.test.tsx
npx.cmd tsc -b
npm.cmd run build
```

Also run:

```powershell
git diff --check -- src/Modules/WorkspaceSearch/WorkspaceSearchPanel.tsx src/Modules/WorkspaceSearch/WorkspaceSearchPanel.css src/Modules/WorkspaceSearch/WorkspaceSearchPanel.test.tsx
```

If the full production build is blocked by an unrelated active writer, report the exact blocker and still provide focused-test and strict-TypeScript evidence. Do not edit around unrelated failures.

## Final handoff

Return:

- exact changed-file list;
- final SHA-256 for every owned file;
- focused test, strict TypeScript, production build, and diff-check evidence;
- a concise description of the visual hierarchy and responsive behavior;
- any unresolved issue, clearly separated from completed work;
- confirmation that no search semantics or host contracts changed.

Stop after the handoff so root can inspect and integrate the exact bytes.
