# Hermes Rich Output Parity

## Scope and evidence

This is a source-mapped, read-only audit of the upstream Hermes checkout in `source/` and the Workbench React/TypeScript implementation in `src/`. It uses `docs/HERMES-FUNCTION-PARITY.md` only as orientation, then verifies the cited implementations directly. It does not assert a deployed service, a configured provider, or a working model capability.

Classification:

- **Implemented** — a user-facing behavior and its current transport are present.
- **Partial** — a safe subset or generic representation is present, but the upstream outcome is not reproduced.
- **Missing** — no Workbench surface or adapter exists.
- **Delegated** — intentionally belongs to the authenticated Hermes gateway/desktop host; Workbench should not recreate it in the browser.

The shared transport boundary is an authenticated same-origin WebSocket: upstream exposes `source/hermes_cli/web_server.py:gateway_ws` at `/api/ws` (line 16035), while `src/Modules/HermesGateway/HermesGatewayClient.ts:HermesGatewayClient.connectSocket` obtains `POST /api/auth/ws-ticket` with `credentials: 'include'` and opens `/api/ws?ticket=...` (lines 124-140). No browser-side provider key is part of that contract.

## Inventory

### 1. Citations, Markdown links, and safe external navigation — Partial

**User outcome.** Upstream renders streamed Markdown as rich text, normalizes ordinary external links, recognizes special media/preview/session links, and may turn a bare eligible link into a rich embed. Workbench renders GFM links safely, but it has no typed citation/source-reference model, source card, or safe embed consent flow.

**Upstream evidence and wire.** `source/apps/desktop/src/components/assistant-ui/markdown-text.tsx:MarkdownTextSurface` uses streamed text parts and incremental repair; `MarkdownLink` dispatches media, preview, and session-reference targets before normalizing an external URL and rendering `PrettyLink`. It calls `detectEmbed`/`UrlEmbed` for eligible bare external links. The related composer contract is `source/apps/desktop/src/app/chat/composer/url-refs.ts:linkifyUrls`: an `http(s)` URL becomes `@url:<quoted-url>` (lines 32-56), which the gateway resolves. This is text/directive transport over the `/api/ws` JSON-RPC prompt channel, not a browser fetch contract.

**Workbench evidence and wire.** `src/Modules/AgentDock/HermesMarkdown.tsx:HermesMarkdown` applies `remarkGfm`, `skipHtml`, and `defaultUrlTransform` (lines 66-97). Its link component opens only `http(s)` links in a new tab with `rel="noopener noreferrer"` (lines 74-85). Messages are a string body rendered by `src/Modules/AgentDock/AgentDock.tsx` (lines 566-575); there is no `Citation`, `Source`, `UrlReference`, or embed part on that message shape.

**Security and verification.** Preserve `skipHtml`, protocol filtering, and `noopener noreferrer`; do not turn a model-provided URL into a background fetch, iframe, or automatically opened tab. Add focused renderer tests for GFM links, disallowed schemes, raw HTML, a malformed citation-like link, and an explicit user click. A future live check should send a normal HTTPS link and a gateway-resolved `@url:` reference separately, proving that only the latter requests remote analysis.

### 2. Inline/attached images, metadata, upload references, and generated images — Partial

**User outcome.** Upstream shows uploaded image thumbnails/lightbox controls and presents generated-image tool results with loading, error, lightbox, and download handling. Workbench can stage image and file uploads and shows metadata chips in the composer and current in-memory user message, but it deliberately does not render remote Markdown images and has no generated-image result component.

**Upstream evidence and wire.** `source/apps/desktop/src/app/chat/composer/attachments.tsx:AttachmentList/AttachmentPill` distinguishes image attachments, retains a data-URL preview, and uses a lightbox/download interaction. `source/apps/desktop/src/components/chat/generated-image-result.tsx:GeneratedImageResult/generatedImageFromResult` resolves generated-image tool output through gateway/desktop media helpers, tracks image failure/loading state, and offers an explicit download/open action. For browser dashboard image upload, `source/hermes_cli/web_server.py:upload_chat_image` is `POST /api/chat/image-upload` with a `ChatImageUpload` payload and returns `{ok,path,name,bytes,mime_type}` (lines 2310-2356). It decodes image data, sanitizes the filename, and writes under the scoped Hermes image directory; that endpoint is distinct from prompt attachment RPC.

**Workbench evidence and wire.** `src/Modules/HermesGateway/HermesAttachmentAdapter.ts` classifies an image by MIME or extension, rejects empty files and sizes over 25 MiB for images / 256 MiB for other files (lines 3-51), reads the selected browser file as a data URL (lines 54-64), then calls:

```text
image.attach_bytes { session_id, content_base64: dataUrl, filename }
  -> { attached?, message?, path? }

file.attach { session_id, path: '', name, data_url: dataUrl }
  -> { attached?, message?, path?, ref_text? }
```

The staged result retains `{kind,label,gatewayPath?,refText?}` (lines 13-18, 77-99); `buildPromptWithAttachments` puts only file `refText` into the submitted prompt and supplies the image-only fallback prompt (lines 67-72). `src/Modules/HermesGateway/useHermesChat.ts` projects sent attachments down to `{id,kind,label,size}` before `prompt.submit` (lines 486-490). `src/Modules/AgentDock/AgentDock.tsx` renders those as non-interactive message metadata chips (lines 577-589) and shows upload state in the composer tray (lines 726-740). `HermesMarkdown` explicitly turns every Markdown image into a `Remote image not loaded` placeholder (lines 61-63, 87-90).

**Security and verification.** This is attachment transport, **not evidence that a selected model can see images**. Keep raw `File`, data URLs, gateway paths, and `ref_text` out of persisted messages and the Markdown renderer. The current extension-plus-MIME classification is a routing hint, not content validation; gateway-side validation remains authoritative. Test empty/oversize/rejected RPC responses and assert that message history contains only safe metadata. A live test should stage a harmless PNG and a text file, confirm the exact RPC shapes, confirm the chips, and verify that a model Markdown image URL is still not fetched or displayed.

### 3. Audio transcription, speech, streaming speech, and voice conversation — Delegated / Missing

**User outcome.** Upstream supports push-to-talk dictation, a voice-conversation loop, automatic speech of responses, one-shot transcription, one-shot TTS, and streaming TTS. Workbench has no microphone, audio player, speech stream, or voice-loop surface in the audited modules.

**Upstream evidence and wire.** `source/apps/desktop/src/app/chat/composer/hooks/use-composer-voice.ts:useComposerVoice` composes `useVoiceRecorder`, `useVoiceConversation`, and auto-speak; its documented outcome is “dictation (transcript → draft), full voice-conversation loop, and auto-speak” (lines 42-48). Gateway contracts are:

```text
POST /api/audio/transcribe
  { data_url: "data:<audio mime>;base64,...", mime_type? }
  -> { ok: true, transcript, provider }

POST /api/audio/speak
  { text, profile? }
  -> { ok: true, data_url: "data:<audio mime>;base64,...", mime_type, provider }

WS /api/audio/speak-stream
  client: { text }*, { done: true }, or { stop: true }
  server: { type: "start", sample_rate, channels }, binary int16 PCM*, { type: "end" }
  fallback: { type: "fallback" }
```

`source/hermes_cli/web_server.py:transcribe_audio_upload` validates data-URL/base64/audio MIME/empty/maximum size, removes the temporary file, and returns an empty transcript for silence (lines 4347-4438). `speak_text` returns only a data URL and provider after removing the temporary synthesis file (lines 4554-4629). `speak_stream_ws` defines the WebSocket protocol and checks the WebSocket request boundary before accepting it (lines 4658-4717, 4796-4805).

**Workbench evidence and classification.** No matching audio/voice implementation exists under `src/Modules/AgentDock` or `src/Modules/HermesGateway`; the current composer is file input plus text input (`AgentDock.tsx` lines 725-805). This is **Delegated** to a future authenticated gateway/desktop capability and **Missing** in Workbench UX.

**Security and verification.** Do not expose STT/TTS provider credentials or call provider APIs from the browser. A future implementation requires explicit microphone permission, user-gesture playback, cancellation/barge-in, bounded buffered audio, and no audio data in logs/history by default. Contract tests should mock all three gateway interfaces, including silence, fallback, mid-stream stop, and failure. A live test belongs in a local authenticated environment with a non-secret provider configuration.

### 4. URL references and webpage attachments — Partial navigation / Missing ingestion

**User outcome.** Upstream lets a user attach a URL as a compact reference the gateway can resolve. Workbench lets a user follow a rendered external link, but cannot intentionally attach a URL as model context.

**Upstream evidence and wire.** `source/apps/desktop/src/app/chat/composer/url-dialog.tsx:UrlDialog` accepts only a user-entered string that matches `^https?://` before enabling Attach (lines 16-75). `url-refs.ts:linkifyUrls` and `chipTypedUrlOnSpace` turn it into a URL directive/chip (lines 32-56, 65-95), with `@url:` text sent as prompt content over `/api/ws`.

**Workbench evidence and wire.** `HermesMarkdown.tsx` has safe displayed-link navigation only; `AgentDock.tsx` has an `<input type="file" multiple>` and no URL attach dialog or composer URL-reference state (lines 725-779). No Workbench wire shape exists for URL ingestion.

**Security and verification.** A later URL-reference slice must have a server-side allowlist/SSRF policy, URL length limit, explicit user action, and an explainable “gateway will fetch this URL” disclosure. Do not infer that a click on a response link is consent to gateway retrieval. Unit-test scheme/whitespace/punctuation normalization and a live test with a controlled HTTPS page plus a denied private-network target.

### 5. Downloadable/generated files and artifacts — Partial

**User outcome.** Upstream can expose managed files, download them as attachments, and promote eligible completed fenced blocks into a versioned artifact card. Workbench can upload a file as prompt context and show its name/size, but has no agent-artifact card, safe download action, or generated-file result presentation in the conversation.

**Upstream evidence and wire.** `source/hermes_cli/web_server.py:read_managed_file` returns `{name,path,size,mime_type,data_url,...}` after managed-path/sensitive-path/size checks (lines 2391-2423); `download_managed_file` is `GET /api/files/download?path=...` and returns `FileResponse(..., content_disposition_type="attachment")` under the same managed-file policy (lines 2426-2459). `source/apps/desktop/src/components/assistant-ui/artifact-card.tsx:ArtifactCard` is a transcript replacement for a detected fenced block; after streaming ends it registers/version-deduplicates by content hash and opens only on click (lines 31-55). `markdown-text.tsx` imports `detectArtifact` and `ArtifactCard` into the streamed Markdown surface.

**Workbench evidence and wire.** The only conversation-level file contract is `file.attach` in `HermesAttachmentAdapter.ts` (lines 91-99) and its prompt `ref_text`; `useHermesChat.ts` retains safe attachment labels/sizes only (line 488). `AgentDock.tsx` has no artifact/result card path, download action, or file-path navigation in the message renderer.

**Security and verification.** Never display an agent-supplied path as a direct `file:` or unrestricted HTTP URL. Future downloadable-artifact work must use an authenticated, policy-checked gateway download endpoint and an explicit click; do not expose sensitive paths, session tokens, or raw `data_url` values in message history. Verify denied sensitive paths, over-limit files, a file with a misleading extension, and a successful explicit download.

### 6. Structured tool results, command output/errors, diffs, and patches — Partial

**User outcome.** Upstream summarizes a run of tool calls and separates durable file changes from ephemeral activity. Workbench already receives generic tool lifecycle events and renders collapsible Input, Output/Error, and File changes sections, but `inlineDiff` is an untyped raw `<pre>` and lacks a safe diff presentation or source navigation.

**Upstream evidence and wire.** `source/apps/desktop/src/components/assistant-ui/tool/run-summary.ts:summarizeToolRun` groups tool calls into readable activity clauses; its comments explicitly distinguish ephemeral run activity from individual file-edit cards. `source/apps/desktop/src/components/assistant-ui/thread/changed-files-card.tsx` is the corresponding durable changed-files surface. The upstream structured events ride the `/api/ws` JSON-RPC sidecar (`web_server.py:gateway_ws`, lines 16025-16036).

**Workbench evidence and wire.** `src/Modules/HermesGateway/HermesRuntimeAdapter.ts:mergeHermesToolEvent` accepts `tool.start`, `tool.progress`, and `tool.complete` (lines 50-53, 97-134). It maps the event payload to:

```text
{ tool_id | tool_call_id | id, name | tool, context | summary | status,
  args | arguments | input, result | error, duration_s, inline_diff }
  -> HermesToolRun { id, name, phase, context?, input?, output?, durationSeconds?, inlineDiff? }
```

It compact-JSON-stringifies `input`/`output`, caps those values at 8,000 characters, and keeps the latest 30 runs (lines 59-71, 116-133). `src/Modules/AgentDock/AgentDock.tsx:ToolRunRow` renders the three sections in `<pre>` blocks (lines 105-108) and the timeline on the conversation page (lines 622-629).

**Security and verification.** Tool text is untrusted display data. It must remain text-only; do not execute commands, apply patches, or turn diff paths into clickable local file URLs as a side effect of rendering. Current `inlineDiff` does not pass through the capped `compactValue` helper and there is no generic secret redaction, so a next slice must bound it and avoid claiming that tool output is safe to disclose. Test event-id merging, error completion, malformed JSON, over-limit detail, and HTML-looking diff text.

### 7. Code blocks, language, copy, and file/line navigation — Partial

**User outcome.** Upstream streams Markdown while a response is incomplete, lazily loads Shiki syntax support, recognizes richer code/artifact affordances, and can route preview targets. Workbench provides a language label and copy control, but no syntax highlighter, file/line parser, or source-navigation contract.

**Upstream evidence and wire.** `source/apps/desktop/src/components/assistant-ui/markdown-text.tsx:MarkdownTextSurface` loads the code plugin lazily, repairs incomplete Markdown with `tailBoundedRemend`, and imports `RichCodeBlock`, `PreviewAttachment`, and artifact detection. `source/apps/desktop/src/components/chat/shiki-block.tsx` is deliberately lazy-reached from `shiki-highlighter.ts`, keeping Shiki off the cold rendering path. A preview target is resolved before `source/apps/desktop/src/components/chat/preview-attachment.tsx:PreviewAttachment` opens it.

**Workbench evidence and wire.** `src/Modules/AgentDock/HermesMarkdown.tsx:CodeBlock` derives `language-...`, uses `navigator.clipboard.writeText`, and exposes copied/failed state (lines 13-58). It renders literal `<pre><code>` rather than syntax tokens; the message wire remains plain Markdown text. There is no file/line reference type or navigation callback.

**Security and verification.** Keep code and line references inert until an explicit user action resolves a path through an existing workspace policy. Do not parse a model string into an arbitrary file read. Add renderer tests for a valid language fence, missing language, malformed/open fence, copy-denied browser API, and text containing `<script>`.

### 8. Streaming lifecycle: partial, final, failure, cancellation, and retry — Partial

**User outcome.** Upstream tolerates unfinished Markdown during streaming and can maintain specialized media/voice lifecycles. Workbench visibly streams message/reasoning/tool state, exposes Stop, reconnects a session, and retries text prompts; attachment retry is deliberately disabled because it cannot safely reattach the original files.

**Upstream evidence and wire.** `markdown-text.tsx:preprocessWithTailRepair` invokes `tailBoundedRemend(preprocessMarkdown(text))` and `MarkdownTextSurface` detects a running text-part status before choosing stream-friendly code rendering. Generated-image and media components maintain their own loading/error cleanup state. Audio streaming adds the explicit `start` / binary PCM / `end` / `fallback` lifecycle described above.

**Workbench evidence and wire.** `useHermesChat.ts` maps gateway events into streaming messages/reasoning/tool state, then submits with `prompt.submit` (lines 486-490). `stop` calls `session.interrupt {session_id}` and clears active UI states (lines 505-518). It schedules bounded reconnect attempts and reports terminal failure (lines 196-228), and `openSession` resumes using `session.resume {session_id,cols,source,omit_messages}` with stored message history (lines 553-579). `AgentDock.tsx` marks streaming and interim messages, presents the Stop action, and disables retry for messages with attachments (lines 591-603, 793-805).

**Security and verification.** A reconnect must never replay a sensitive prompt or attachment automatically. Keep partial text inert under the same Markdown policy as final text, cap tool detail, and make cancellation idempotent. A future live check must exercise start → partial text → tool progress → completion; an error; Stop during generation; reconnect/resume; text retry; and attachment-retry refusal.

## Implemented first slice: safe inline-diff presentation v1

> **Implementation update (2026-08-09):** `HermesRuntimeAdapter` now passes `inline_diff` through its shared 8,000-character detail cap, including structured/non-string payloads. `InlineDiffCard` recognizes bounded unified file/hunk/add/remove structure, renders React text nodes only, exposes copy-only review, and falls back to inert plain text. Adapter, parser, HTML-escaping, truncation, collapsed/expanded Tool Activity integration, full 159-test frontend verification, and the production TypeScript/Vite build pass. A live harmless Hermes file-edit event remains the final runtime check for this slice.

**Why this first.** It produces a visible improvement in the agent conversation from an event shape Workbench already receives, without adding a browser permission, provider integration, URL fetch, file download, image renderer, host API, or new server route. It also closes the current unbounded-`inlineDiff` display gap before richer artifacts are introduced.

**Exact future contract.** Keep the existing gateway event field opaque and backward compatible:

```ts
// existing gateway payload: payload.inline_diff?: unknown
// Workbench v1 normalized result:
type HermesToolRun = {
  // existing fields unchanged
  inlineDiff?: string // compactValue(payload.inline_diff); max 8,000 chars
}

// render-only view model; never sent back to Hermes
type InlineDiffViewV1 = {
  kind: 'unified-diff' | 'plain-text'
  files: Array<{ before?: string; after?: string; hunks: Array<{ header?: string; lines: Array<{ kind: 'add' | 'remove' | 'context'; text: string }> }> }>
  raw: string
  truncated: boolean
}
```

Recognize only unified-diff markers (`---`, `+++`, `@@`, leading `+`, `-`, and context lines). Preserve malformed/unsupported content as text in a collapsed plain-text fallback. No change is required to `/api/ws`, `tool.*`, `prompt.submit`, or the upstream source.

**Exact future files and fixture.**

- Change `src/Modules/HermesGateway/HermesRuntimeAdapter.ts` so `inlineDiff` also uses `compactValue`; do not expand the gateway schema.
- Add `src/Modules/AgentDock/InlineDiffCard.tsx` as a text-only parser/view with an explicit “Copy raw diff” control.
- Change `src/Modules/AgentDock/AgentDock.tsx:ToolRunRow` to use `InlineDiffCard` in place of the current `inlineDiff` `<pre>`.
- Add `src/Modules/HermesGateway/HermesRuntimeAdapter.test.ts` coverage for a `tool.complete` payload containing `inline_diff`, an error result, an over-limit diff, and an object/non-string diff.
- Add `src/Modules/AgentDock/InlineDiffCard.test.tsx` with a fixture containing add/modify/delete hunks, an escaped HTML-looking path, malformed diff text, and a capped 8,001-character diff.

**Security acceptance.** The component must render text nodes only; no `dangerouslySetInnerHTML`, patch application, command execution, automatic workspace navigation, file read, external link, generated download, or raw path URL. Copy returns the retained raw string only. It must cap the input before parsing and show a truncation notice without trying to recover omitted content. It must not add client-side keys, credentials, or provider calls.

**Concrete verification.** The adapter/event lifecycle, parser cases, HTML-looking path/content escaping, 8,001-character cap, malformed fallback, and collapsed/expanded ToolRunRow are now covered and passing. The remaining live check is to use a locally authenticated gateway session for a harmless scratch-file edit and confirm a real `tool.complete` `inline_diff` renders as a diff card, errors remain text-only, Stop leaves no active card, and no file opens without an explicit later feature.

## Deliberate non-claims and next boundaries

- Attachment upload support does not establish image/vision capability, image metadata fidelity, generated-image rendering, or a safe download path.
- No Workbench audio/voice or URL-ingestion implementation was found; those require authenticated gateway contracts and explicit user-consent/security design, not a browser-side adapter.
- Citation/source cards, generated artifacts, source-line navigation, media rendering, and voice are separate slices after the diff surface because each needs a new typed contract and a stronger trust boundary.
