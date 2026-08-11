# Terra High — Browser, Google Maps, and Help Integration

## Objective

Turn the existing native WebView2 browser into a dependable Workbench feature, add a focused Google Maps experience, and add a **Help → Hermes Help** command in the application menu that opens the official Hermes documentation in the embedded browser.

This is a functionality-first lane. Record newly encountered security concerns in the completion report, but do not broaden into unrelated security remediation unless the issue directly blocks correct browser behavior.

Initial help URL: `https://hermes-agent.nousresearch.com/docs/`.

## Current foundation to preserve

- `BrowserSurfaceBridge` already owns the native WebView2 surface, isolated user-data folder, bounded tabs and pending open requests, navigation history, download denial, permission denial, and renderer-safe URL/title projection.
- Browser navigation state is already tab-bound. Displayed-route deduplication requires the exact displayed tab and URI; a second tab at the same URI must not inherit the first tab's live DOM.
- `BrowserSurfaceRequestGate` already invalidates a pending `ShowAsync` when `Hide` or disposal wins. Preserve it and add the missing hostile ordering tests rather than replacing it.
- `BrowserWorkspace` already provides tabs, address entry, back, forward, reload/stop, native-surface placement, and retained renderer tab metadata.
- Hermes `preview.open` already normalizes HTTPS map links and hands them to the browser workspace.
- The desktop smoke already covers Google Maps history, late navigation completion ownership, same-tab placement deduplication, cross-tab same-URI non-equivalence, and single-flight initialization.

## Hard invariants

1. A live browser document is owned by the exact tuple `{rendererGeneration, browserSessionId, tabId, navigationId}`. Mutable current-tab state must never own or receive an older document's completion.
2. Show, hide, placement, DPI change, and resize are presentation operations—not navigation operations.
3. Re-showing the same exact tab and URI must preserve page/DOM state and must not reload.
4. A different tab at the same URI is not equivalent. It must navigate or bind its own document; it may not display or mutate another tab's live page.
5. Hide, renderer reset, navigation reset, and disposal invalidate pending surface shows before returning. An obsolete async show must never reappear above the Workbench and intercept clicks.
6. Late navigation events update only their originating tab/navigation. Never attribute them to the current mutable tab ID.
7. Browser placement events must not starve or cancel legitimate navigation. Repeated resize/placement must remain bounded and must not leave `_startingNavigation` pinned.
8. The renderer never receives full route query strings from native state. Map origins, destinations, searches, and other sensitive URL data remain host/browser-owned; renderer state uses the existing safe display URL/title projection.
9. No raw DOM, cookie, credential, filesystem, terminal, host-object, or arbitrary JavaScript bridge is introduced.
10. Browser errors are typed, bounded, and user-readable. Do not forward raw WebView2 exception text, environment details, full URLs, or query strings.

## Authorized ownership

Terra High may modify only:

- `src/Host/HermesDesktop/BrowserSurfaceBridge.cs`
- browser-only assertions in `src/Host/HermesDesktop.Smoke/Program.cs`
- `src/Modules/BrowserWorkspace/BrowserWorkspace.tsx`
- `src/Modules/BrowserWorkspace/BrowserWorkspace.css`
- a focused `src/Modules/BrowserWorkspace/BrowserWorkspace.test.tsx` if absent, or its existing equivalent
- `src/Modules/HermesGateway/HermesDesktopUiAdapter.ts`
- `src/Modules/HermesGateway/HermesDesktopUiAdapter.test.ts`

The following are shared/root-held seams. Inspect read-only and stop before changing unless root explicitly releases them with current hashes:

- `src/app/App.tsx` — needed for the new Help menu command and browser-panel activation
- `src/app/styles.css` — only if the existing menu style cannot be reused unchanged
- `src/Host/HermesDesktop/MainWindow.xaml`
- `src/Host/HermesDesktop/MainWindow.xaml.cs`
- any project/package file

Record exact pre-edit and final SHA-256 hashes for every modified file. Do not touch CAD, DeveloperServices, model runtime, credentials, installer, Docker, or conversation-bridge files.

## Required functionality

### 1. Browser tabs and lifecycle

- Keep tabs, address navigation, Back, Forward, Reload, Stop, new-tab, close-tab, title, and loading state functional.
- Preserve page state through Workbench panel/layout changes. Prefer mounted-hidden behavior. If root's current layout unmounts the React component, native tab/session state remains authoritative and must restore without cross-binding or replaying stale navigation.
- Make the native browser surface follow its exact renderer rectangle across DPI and window changes without navigating.
- Reset/close retires exact tab state and revokes pending open requests owned by the retired generation.
- A site-created new window becomes one bounded, explicit Workbench tab request; no external pop-up and no double-open.

### 2. Google Maps integration

Add a compact Maps mode or quick-action strip inside the Browser workspace. It must remain ordinary browser navigation, not a privileged Maps API integration.

Supported typed actions:

- Search/place: `https://www.google.com/maps/search/?api=1&query=<encoded>`
- Directions: `https://www.google.com/maps/dir/?api=1&origin=<encoded>&destination=<encoded>&travelmode=<mode>`
- Destination-only directions may omit origin and let Google Maps use its normal UI.
- Travel modes are exactly `driving`, `walking`, `bicycling`, or `transit`.

Use `URL`/`URLSearchParams`; never concatenate raw user text. Enforce HTTPS, exact `www.google.com` host, and the expected `/maps/` path for the typed Maps actions. General browser navigation retains the existing HTTP/HTTPS policy.

Provide:

- Search/place input and button.
- Origin, destination, and travel-mode inputs for directions.
- A clear visual indication that results are supplied by Google Maps inside the native browser.
- Keyboard submission and sensible responsive layout at approximately 360, 800, and 1280 CSS pixels.

Do not claim route correctness, live traffic accuracy, geocoding authority, or Maps API integration. The Workbench merely builds a valid Google Maps URL and displays Google's page.

### 3. Assistant/browser requests

- One accepted `preview.open` request opens or focuses exactly one browser tab.
- Preserve request nonce/correlation and acknowledge it once.
- A recognized Google Maps URL may receive a safe title such as `Google Maps` or the caller's bounded label, but the renderer must not receive the full query-bearing URL from the native browser.
- Malformed URLs, unsupported schemes, embedded credentials, and oversized values fail closed without opening a tab.

### 4. Help menu

Add **Help** to the top application menu and one initial command:

- **Hermes Help** — opens `https://hermes-agent.nousresearch.com/docs/` in the embedded Workbench browser.

The command must:

- switch/focus the Browser workspace;
- open or focus one help tab without duplicating it on repeated clicks;
- use a host-owned constant/typed request, not arbitrary renderer-supplied Help URLs;
- close the menu after activation;
- remain keyboard accessible with standard menu roles/focus behavior;
- never launch the system browser unless a future explicit product decision adds that option.

Root should own the final `App.tsx` merge because it is a shared application shell. Terra should provide an apply-ready patch or exact small diff for that seam if it is not released during the lane.

## Required hostile tests

### Native host smoke

1. Deferred initialization: `Show(A)` then `Hide` before initialization completes leaves the native surface collapsed.
2. Out-of-order placement: `Show(A)`, `Hide`, `Show(B)` with delayed completion makes only B visible.
3. Same `{tabId, URI}` placement repeated through at least ten resizes causes zero additional navigations.
4. Different tab with the same URI is not equivalent to the displayed tab.
5. A late completion from A while B is selected updates A only and never contaminates B history.
6. Reset/dispose during initialization prevents later visibility and leaves no click-intercepting native surface.
7. Repeated Stop/Reload/resize does not pin the navigation queue or `_startingNavigation`.
8. Google Maps route A → route B → Back → Forward preserves exact host history while renderer-visible URLs stay redacted.
9. New-window requests are one-use, bounded, expire, and cannot be accepted into a foreign or retired session.
10. Downloads, host objects, web messages, and browser permissions remain unavailable.

### Renderer tests

1. Exact encoding for spaces, Unicode, `&`, `#`, `?`, and `%` in place/origin/destination values.
2. Exact allowed travel modes; malformed modes fail before dispatch.
3. Help click emits one typed browser-open request, selects Browser, closes the menu, and repeated clicks focus rather than duplicate the Help tab.
4. A single `preview.open` produces one tab and one acknowledgement.
5. Tab switching and resize emit placement only, not navigation.
6. Closing the active tab selects a deterministic survivor and does not revive a retired page.
7. Mobile/narrow layout keeps Maps controls, address bar, tabs, and native surface usable.
8. Accessibility: menu/tab/form names, keyboard activation, visible focus, and status/error announcements.

## Live acceptance

Run only after source gates pass and the exact published desktop is identified.

1. Open **Help → Hermes Help**. Confirm the official Hermes documentation opens inside one browser tab.
2. Click Help repeatedly. Confirm no duplicate tabs and no external system browser.
3. Search Google Maps for a place with punctuation and Unicode. Confirm the correct encoded request reaches Google Maps.
4. Create a directions route and switch Browser → CAD → editor/chat → Browser. Confirm the exact route and page state remain.
5. Resize the desktop repeatedly and change UI scale. Confirm no route reload, duplicate history entry, or blank native surface.
6. Open the same Maps URL in two tabs. Navigate one tab and confirm the other tab's DOM/history is unchanged.
7. Hide/close Browser during first initialization, then interact with Photon composer/model/reasoning controls. Confirm no invisible WebView2 overlay intercepts clicks.
8. Confirm zero renderer console errors and no raw route query, origin/destination, exception, filesystem path, cookie, credential, or environment value in logs/messages.

Keep evidence classes separate: source tests, build output, published binary identity, and live interaction proof are distinct claims.

## Explicit non-goals

- Google Maps JavaScript/Places/Directions API keys, billing, quotas, or SDK integration.
- DOM scraping, route extraction, automated map interaction, or location-history access.
- Geolocation permission by default.
- Google login, cookie/password import, browser-profile import, or credential bridging.
- Arbitrary renderer-to-native JavaScript, host objects, filesystem, terminal, or agent capability.
- Downloads or upload/file-picker access.
- Automatic navigation/replay after reset or crash.
- A claim that Google Maps results are verified by Hermes.

## Gates and deliverable

Required gates:

- HermesDesktop Release build: zero warnings/errors.
- Focused native browser smoke: all pass.
- Focused BrowserWorkspace and desktop UI adapter tests: all pass.
- Strict TypeScript: pass.
- Production Vite build: pass.
- Scoped formatter/diff checks: pass.
- Live acceptance matrix above: pass or clearly reported as not run.

Final report:

`docs/coordination/TERRA-HIGH-BROWSER-GOOGLE-MAPS-COMPLETION.md`

The report must include exact file scope/hashes, test commands/results, current published executable identity if live-tested, remaining limitations, and an explicit statement that the Help link and Google Maps behavior are browser navigation—not privileged API integrations.
