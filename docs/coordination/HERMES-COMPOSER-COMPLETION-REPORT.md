# Hermes Advanced Composer Completion Report

Date: 2026-08-09  
Lane: standalone advanced composer  
Status: complete and ready for coordinator integration  

## Outcome

`src/Modules/HermesComposer` now contains a standalone, production-oriented React/TypeScript advanced composer and a headless typed controller. The module has no dependency beyond the repository's existing React runtime and uses no network, storage, media-device, filesystem, shell, credential, or live-service API.

The coordinator owns integration. This lane did not edit `App.tsx`, global styles, AgentDock, HermesGateway, docking, host code, package/build files, installers, tests outside the new module, or existing documentation.

## Public exports

Import from `Modules/HermesComposer`:

- `HermesAdvancedComposer`
- `HermesAdvancedComposerProps`
- `HermesComposerController`
- `HERMES_COMPOSER_LIMITS`
- `HermesComposerInputError`
- `createCompletionSession`
- `handleCompletionKey`
- `applyCompletion`
- `validateCompletionCatalogs`
- `HermesCompletionKeyResult`
- all typed contracts from `types.ts`, including:
  - connection and voice state unions;
  - prompt/edit-resend/retry/regenerate intent unions;
  - queue, history, snapshot, receipt, callback, and submit-result contracts;
  - mention, command, completion-session, completion-insertion, message-reference, and voice-action contracts.

## Headless controller behavior

`HermesComposerController` provides:

- `getSnapshot()` and `subscribe()` for `useSyncExternalStore` or other headless consumers;
- `enqueuePrompt(text)`;
- `enqueueEditResend(messageId, text)`;
- `enqueueRetry(messageId)`;
- `enqueueRegenerate(messageId)`;
- `editQueueItem(queueItemId, text)`;
- `removeQueueItem(queueItemId)`;
- `moveQueueItem(queueItemId, destinationIndex)`;
- `submitNext()`;
- `cancelSubmission()`;
- `recover(historyEntryId)`;
- `clearHistory()`;
- `setConnection(state)`;
- `setVoiceState(state)`; and
- `dispose()`.

### Queue and at-most-once semantics

- Default/max queue size: 20.
- Each submit requires an explicit `submitNext()` call and a `ready` connection.
- The selected queue item is removed and marked in-flight before the caller callback is invoked.
- Concurrent `submitNext()` calls return `busy`; they never invoke the callback twice.
- Attempted IDs are retained only with bounded history, preventing an unbounded duplicate-prevention set.
- Failure or cancellation creates history but does not put the intent back in the queue.
- `recover()` is the only generic failure-recovery path. It creates a new queue item with a new ID after an explicit call.
- Successful history entries cannot be recovered, preventing accidental duplicate submission.
- Reconnecting/offline/blocked/ready transitions never invoke the submit callback and never auto-drain the queue.

The caller callback receives an immutable intent, the queue item ID, and an `AbortSignal`. A caller should honor abort promptly. Cancellation cannot undo a request already accepted outside this module, but this controller never invokes the callback more than once for one queue entry.

### Caller-owned draft and bounded memory

- Current draft text is a controlled `draft` prop plus `onDraftChange`; it is not part of the controller snapshot.
- The module does not write prompt text to local/session storage, IndexedDB, cookies, files, URLs, logs, or browser history.
- Queue text is bounded to 64 KiB per text-bearing intent and 20 entries.
- Submission history defaults to and is capped at 50 entries.
- History callback errors retain only a bounded safe code; thrown exception messages are discarded.
- `dispose()` aborts the current callback and clears controller-held queue/history references.
- JavaScript strings are immutable and cannot be deterministically zeroed; integration should avoid passing secrets as prompt text.

### Intent references

- Edit-resend requires caller-supplied `messageId` plus replacement text.
- Retry and regenerate require caller-supplied `messageId` and contain no invented message identity.
- Message IDs are bounded to 256 characters and reject control characters.
- The React component accepts up to 50 bounded message references and renders explicit Edit & resend, Retry, and Regenerate controls.

## Completion behavior

- Mentions use `@`; commands use `/`.
- Catalogs are caller-supplied and eagerly validated even when no menu is open.
- Each catalog is capped at 100 entries; the menu shows at most 8 matches.
- IDs, labels, names, descriptions, and usage copy have fixed limits and reject control characters.
- Filtering prefers prefix matches and then stable label order.
- Arrow Up/Down wraps; Home/End jumps; Enter or Tab selects; Escape closes.
- Selection replaces only the active trigger/query range and returns the exact next cursor position.
- The UI restores textarea focus and selection after completion, queue, submit, voice, message-action, and recovery operations.

## Voice and connection seams

- Voice is callback-only: `onVoiceAction('start' | 'stop' | 'cancel')`.
- The module never calls `getUserMedia`, speech APIs, host APIs, or a network service.
- Voice state remains caller-controlled through `idle`, `listening`, `processing`, and bounded `error` states.
- Connection state remains caller-controlled through `ready`, `reconnecting`, `offline`, and `blocked`.
- Reconnecting copy explicitly tells the user that queued prompts will not replay automatically.

## Accessibility and adaptive styling

- Controlled multiline combobox with listbox/option relationships, active descendant, autocomplete state, descriptive help, and live status regions.
- Completion keyboard model is independently testable without a browser DOM.
- Queue reorder/remove, cancellation, recovery, voice, and message actions are real labeled buttons.
- Ctrl/Cmd+Enter queues; Alt+Enter submits the next item; plain Enter remains a newline.
- Focus-visible outlines use the local teal accent and remain visible in forced-colors mode.
- Text uses rem/clamp sizing, textarea resize, overflow wrapping, responsive container queries, and layouts that collapse without fixed text-height clipping.
- `prefers-reduced-motion` reduces animation/transition duration to effectively zero.
- All classes use the `hermes-advanced-composer` / `hc-` namespace in the module-local stylesheet; no global stylesheet was changed.
- Purple/teal Workbench colors, adaptive panels, ready/warning/error states, narrow-container layouts, and high-contrast handling are self-contained.

## Text rendering and side-effect audit

React interpolation and controlled textarea values render every caller string as text. The module contains no `dangerouslySetInnerHTML` or `.innerHTML` use. The hostile-input SSR test verifies `<img>`, event-handler-like text, `<script>`, and label markup are escaped rather than interpreted.

A production-source static scan found no use of:

- `fetch`, XMLHttpRequest, WebSocket;
- localStorage, sessionStorage, IndexedDB, cookies;
- media-device or file-picker APIs;
- process/child-process/shell APIs;
- Authorization or credential APIs; or
- raw HTML insertion APIs.

## Focused tests

Three module-local test files contain 18 tests covering:

1. queue edit/remove/reorder without draft ownership;
2. explicit reordered submission order;
3. concurrent duplicate prevention and at-most-once callback invocation;
4. cancellation without requeue/replay;
5. reconnecting/offline/blocked/ready transitions with no auto replay;
6. explicit recovery and successful-entry recovery refusal;
7. edit-resend, retry, and regenerate message-ID intents;
8. prompt, queue, history, message-ID, and duplicate-ID bounds;
9. all callback-only voice states and bounded voice errors;
10. callback exception sanitization;
11. mention/command detection and ordering;
12. completion Arrow/Home/End/Enter/Tab/Escape behavior;
13. completion insertion and cursor restoration data;
14. catalog size, duplicate, and structural bounds;
15. HTML-like draft/message/label input rendered as text;
16. combobox/listbox/option and keyboard-help semantics; and
17. all connection and voice UI states, including no-replay reconnect copy.

## Commands and results

Commands ran from `C:\Users\clsor\Documents\Codex\HermesAgent\src` on 2026-08-09 using the bundled Codex Node runtime because `node` was not on the shell `PATH`.

### Module-only strict TypeScript compilation

```powershell
$files = Get-ChildItem -LiteralPath '.\Modules\HermesComposer' -File |
  Where-Object { $_.Extension -in '.ts','.tsx' } |
  Select-Object -ExpandProperty FullName

& 'C:\Users\clsor\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' `
  '.\node_modules\typescript\bin\tsc' `
  --noEmit --target ES2022 --useDefineForClassFields true `
  --lib ES2022,DOM,DOM.Iterable --skipLibCheck --esModuleInterop `
  --allowSyntheticDefaultImports --strict --module ESNext `
  --moduleResolution Bundler --resolveJsonModule --isolatedModules `
  --jsx react-jsx $files
```

Result: **passed, exit code 0, no diagnostics**.

### Full renderer TypeScript audit

```powershell
& 'C:\Users\clsor\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' `
  '.\node_modules\typescript\bin\tsc' --noEmit -p '.\tsconfig.app.json'
```

Final result: **passed, exit code 0, zero renderer errors, zero HermesComposer errors**.

An earlier full-audit run occurred while other parallel read-only modules had unresolved compiler errors and also found two composer-local issues. Only the two owned composer issues were changed: an inferred literal parameter type and use of an ES2023-only array helper. The final full audit above is green.

### Focused module tests

```powershell
& 'C:\Users\clsor\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' `
  '.\node_modules\vitest\vitest.mjs' run 'Modules/HermesComposer'
```

Result:

```text
Test Files  3 passed (3)
Tests       18 passed (18)
Duration    242ms
```

The first sandboxed Vitest attempt was denied while loading the existing root Vite config. The identical module-only command passed after read-only sandbox escalation; no source, config, manifest, dependency, or service changed.

## Proposed coordinator integration seams

1. Import `HermesAdvancedComposer` and `HermesComposerController` from `Modules/HermesComposer` in coordinator-owned UI code.
2. Create one controller per visible composer lifetime. Supply a `submit(intent, { queueItemId, signal })` callback that maps the typed intent into the existing HermesGateway operation and honors cancellation.
3. Keep draft text in coordinator state and pass `draft`/`onDraftChange`. Do not add storage unless a future product requirement explicitly defines consent, retention, and deletion.
4. Feed runtime connection edges into `controller.setConnection(...)`. Do not call `submitNext()` from reconnect logic.
5. Supply mention/command catalogs from trusted coordinator state. The module validates but never fetches them.
6. Supply visible message IDs/text through `messageReferences` for edit-resend/retry/regenerate controls; the module invents no message IDs.
7. Connect `onVoiceAction` to a coordinator/host voice seam and update `controller.setVoiceState(...)` only from caller-observed state.
8. Call `controller.dispose()` when its owning surface is permanently destroyed.
9. The coordinator may place the component inside AgentDock or another surface and should not copy these local styles into global CSS.

## Changed files

- `src/Modules/HermesComposer/types.ts`
- `src/Modules/HermesComposer/HermesComposerController.ts`
- `src/Modules/HermesComposer/completions.ts`
- `src/Modules/HermesComposer/HermesAdvancedComposer.tsx`
- `src/Modules/HermesComposer/HermesAdvancedComposer.css`
- `src/Modules/HermesComposer/index.ts`
- `src/Modules/HermesComposer/HermesComposerController.test.ts`
- `src/Modules/HermesComposer/completions.test.ts`
- `src/Modules/HermesComposer/HermesAdvancedComposer.test.tsx`
- `docs/coordination/HERMES-COMPOSER-COMPLETION-REPORT.md`

The checkout root has no `.git` directory, so Git diff/status evidence is unavailable. Filesystem inventory and static search confirmed all created source/test files and this report remain inside the two assigned ownership paths.

No integration, dependency, manifest, build configuration, live-service call, persistence, package, commit, or publish was performed.
