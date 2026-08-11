# Hermes Markdown Polish Report (TH2 integrated)

## Status

Accepted by Super after an independent Gopher intake review confirmed packet ownership, clean applicability, safe Markdown behavior, and no collision with TH1.

## Behavior

- Fenced blocks use a normalized language header and keyboard-accessible Copy code control.
- Copy receives only the bounded rendered code (64 KiB); success is brief, denial/failure stays visible and never throws.
- Known labels are normalized; unknown/missing labels are inert text and load no modules.
- Raw HTML remains skipped; remote Markdown images remain placeholders; only existing safe external links get `target=_blank` plus `noopener noreferrer`.
- Local component CSS gives code blocks bounded height/width and horizontal scrolling, with no global stylesheet edit.

## Security boundaries

No raw HTML, remote-image loading, local-file opening/reading, command execution, patch application, workspace navigation, or dynamic language-module loading is introduced. Clipboard content is only the same bounded rendered code text.

## Verification

Super runs the focused Markdown tests, full renderer suite, strict TypeScript, and production build after integration. This report records integrated source, not the worker's earlier staged-only evidence.
