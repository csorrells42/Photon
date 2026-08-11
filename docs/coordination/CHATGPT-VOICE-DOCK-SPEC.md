# ChatGPT / Sparky Voice Dock Specification

**Status:** implementation-ready design plus isolated build-only prototype.  
**Scope:** future Hermes Workbench dock showing the user's existing signed-in ChatGPT web session.  
**Out of scope:** OpenAI API integration or billing; API keys; copying/importing cookies, passwords, or tokens; modification of existing Hermes applications, React, launcher, or installer.

## Decision

Host `https://chatgpt.com/` in a native WPF WebView2 child control, with a dedicated persistent WebView2 user-data folder (UDF). The user signs in through OpenAI's normal web flow inside that control. Voice is ChatGPT's site-provided Voice experience; the host merely mediates the WebView2 microphone-permission request.

This is browser embedding, not an OpenAI API integration. Subscription entitlement, Voice availability, rate limits, account selection, and all service charges remain with the user's ChatGPT account and ChatGPT web product. The host must never show API usage or claim it can determine subscription entitlement.

## Confirmed behavior vs. prototype assumptions

| Statement | Classification | Consequence |
| --- | --- | --- |
| ChatGPT web starts Voice at ChatGPT.com and requests browser microphone permission. | Confirmed product behavior | Navigate to ChatGPT.com; allow the user to start Voice; do not automate UI. |
| Voice availability and limits can vary by plan, account, workspace, region, and app version. | Confirmed product behavior | No entitlement bypass or availability guarantee. |
| A WebView2 UDF contains cookies, permissions, cache, and DOM storage. | Confirmed platform behavior | Dedicated UDF is sensitive local browser state; never import/copy another browser profile. |
| WebView2 exposes cancellable navigation, popup, permission, download, and process-failure events. | Confirmed platform behavior | Use them for containment. |
| WPF WebView2 uses a native child `HwndHost`; React cannot host it as a DOM child. | Confirmed platform behavior | Synchronize a native child rectangle with a React placeholder. |
| ChatGPT web Voice works in this embedded WebView2. | Prototype assumption | Manually validate after a user chooses to log in; do not call it an OpenAI-supported embedding contract. |
| The allowlist remains sufficient as OpenAI changes redirects/CDNs. | Prototype assumption | Fail closed; add only reviewed official origins evidenced by a blocked login. |

## Privacy and security boundary

- **No credential movement.** Never use CookieManager, DevTools Protocol, injected JavaScript, host objects, clipboard/DOM scraping, profile import, or a browser `--user-data-dir` to access credentials or conversations. Do not point to Edge or Chrome's profile.
- **No API path.** No API key, OpenAI SDK, `Authorization` header, proxy, request mutation, system prompt, or traffic interception. This opens the public web experience as a user browser would.
- **Dedicated profile.** `%LOCALAPPDATA%\Hermes\WebView2\ChatGptVoice\Profile-v1`, resolved with `Environment.SpecialFolder.LocalApplicationData`; never a network or install directory. Create one environment for this panel only; set `ExclusiveUserDataFolderAccess = true` when the runtime supports it.
- **Sensitive local storage.** The persistent UDF can hold authentication cookies and browser data. Preserve normal user account ACLs; never sync, upload, package, back up, or log it. Warn shared-Windows-account users and offer Reset.
- **No ambient SSO.** Set `AllowSingleSignOnUsingOSPrimaryAccount = false`; sign-in happens in the isolated web profile.
- **Permission default deny.** Permit only `Microphone`, only for an exact permitted origin, and only after a native prompt naming the origin. Use `SavesInProfile = false` so it is a session-only decision. Deny camera, location, notifications, MIDI, clipboard, and every unhandled permission.

## Browser containment policy

### Navigation

Initial exact HTTPS origins:

- `https://chatgpt.com`
- `https://auth.openai.com`
- `https://auth0.openai.com`
- `https://openai.com`
- `https://help.openai.com` (support links only)

Every `NavigationStarting` parses an absolute URI and requires an exact scheme/host/effective-port match; this includes redirects. Reject all other schemes. Diagnostics may record event kind, host, and opaque correlation ID—never URI path/query/fragment, headers, cookies, DOM, page text, or account data. Blocked navigation gives the user a visible explanation.

This is a prototype allowlist, not a claim that OpenAI guarantees a fixed topology. If a real login requires a new official origin, record only the blocked host, security-review it, update this spec/tests, then add that origin. Do not relax to `*.openai.com` or arbitrary web navigation.

### Popups, downloads, protocols

Handle every `NewWindowRequested`. Mark it handled by default. For an allowlisted URI, a later integration may create a temporary modal native authentication WebView2 with the same UDF and policy; never navigate the primary dock away or automatically launch the default browser. Whether login needs this is a manual prototype check.

Cancel all `DownloadStarting` requests: a credentialed ChatGPT dock has no approved reason to write downloads. Cancel all `LaunchingExternalUriScheme` requests (`mailto:`, `ms-`, `intent:`, custom protocols). Any future explicit external handoff must show a safe host name and require a user action.

## Lifecycle and failure states

1. **Create environment.** Resolve the UDF and call `CoreWebView2Environment.CreateAsync`. On UDF access/path collision/runtime failure, show Retry and a safe error category, not a blank dock.
2. **Configure before navigate.** Attach policy handlers before navigating. Disable extensions, DevTools, browser accelerator keys, context menu, status bar, default script dialogs, and password autosave where the SDK exposes those settings. UI restrictions are only defense-in-depth.
3. **Navigate.** Go only to `https://chatgpt.com/`. A navigation-completed failure shows Reload and a safe status.
4. **Voice.** A user approves/denies the microphone prompt. Denial leaves text ChatGPT usable; do not reprompt. The host cannot select an input device or promise voice/audio hardware works.
5. **Logout.** Direct the user to ChatGPT's account menu. Do not use undocumented endpoints or DOM automation. Reset is available after logout when the user wants all dock-local browser state removed.
6. **Reset.** Confirm that this permanently deletes this dock's local browser data and signs it out. Close/dispose all views using the UDF, then delete only the resolved `Profile-v1` directory and recreate on the next open. On sharing/lock failure, tell the user to close the other panel and retry.
7. **Process failure.** Dispose the controller, retain the UDF, show a Retry action, and create a new environment before creating a controller. Never automatic-retry-loop.
8. **Runtime missing.** Detect `WebView2RuntimeNotFoundException`; show that the Microsoft Edge WebView2 Runtime is required. A future installer owns installation—not this prototype.

## Future Workbench dock geometry

React owns a placeholder and layout; native owns the HWND/WebView2. No DOM nesting or page-to-host script bridge is used.

```text
React ChatGPTDockPlaceholder
  -> IPC { dockId, revision, x, y, width, height, visible, dpi }
       -> native dock coordinator (validate, clamp, convert DIP to pixels)
            -> native child host HWND / WPF WebView2 bounds
                 -> https://chatgpt.com
```

- React sends updates from `ResizeObserver`, window/DPI changes, and tab/visibility changes; it emits only changed rectangles and monotonically increases `revision`.
- Native accepts only a known dock ID, finite non-negative values, and current revisions. It converts DIPs using the host window's DPI, clamps to the Workbench client rect, drops stale messages, then sets child visibility and bounds on the WPF UI thread.
- With a direct controller, set `CoreWebView2Controller.Bounds` and notify it when the parent window position changes. With a WPF `WebView2`, arrange that control inside a native dock host.
- Hide and remove focus when the tab is hidden, bounds are zero, the app is minimized, or an overlay would cover it. React z-index cannot cover a child HWND. `WebView2CompositionControl` may mitigate WPF airspace, but only after accessibility/input/DPI validation; it still does not make the view React DOM.
- Native status messages are limited to `created`, `visible`, `blockedNavigation`, `permissionPrompt`, and sanitized `failure`; never return page text, paths, DOM/auth state, audio, cookies, or tokens to React.

Suggested native boundary:

```csharp
public interface IChatGptVoiceDock
{
    Task InitializeAsync(CancellationToken cancellationToken);
    void ApplyLayout(ChatGptDockLayout layout);
    Task ResetProfileAsync(CancellationToken cancellationToken);
    Task DisposeAsync();
}

public sealed record ChatGptDockLayout(
    int Revision, double X, double Y, double Width, double Height,
    bool IsVisible, double DpiScale);
```

No web message API, host object, script injection, or content-extraction method belongs in this boundary.

## Phased integration plan

1. **Isolated build proof (this change):** compile only `ChatGptVoicePanelPrototype`; do not launch or sign in. Source-review for no API key, cookie/profile import, web/host scripting, existing Hermes reference, or OpenAI API package.
2. **Manual viability:** a consenting tester launches it, signs in, starts Voice, tests allow/deny microphone, logout/reset, popups, downloads, external links, blocked navigation, offline load, missing runtime, and process recovery. Record only outcome/status and blocked host names.
3. **Native coordinator:** after explicit approval to modify host code, implement the geometry protocol behind an experimental feature flag. Test resize, 100/150/200% DPI, multi-monitor moves, tab switches, focus, overlays, and shutdown.
4. **Security/privacy review:** verify UDF ACL/storage, reset scope, policy logging redaction, no credential/DOM bridge, and no API billing path. Repeat manual viability after a WebView2 SDK or ChatGPT topology change.
5. **Rollout:** enable only after acceptance. A feature disable switch closes the controller; it does not delete state unless the user chooses Reset.

## Risks and non-guarantees

- OpenAI may change ChatGPT web UI, login redirects, Voice eligibility/limits, or embedded-browser behavior without a stable embedding API. This is a best-effort browser panel that fails closed.
- Too-restrictive navigation can break valid login; too-permissive navigation turns a signed-in panel into a general browser. Make only evidence-backed, narrow changes.
- A UDF can be used by one WebView2 session at a time; never share the profile with differently configured environments.
- Microphone permission grants device capture only; it does not guarantee ChatGPT Voice entitlement, audio routing, speaker output, or a successful session.
- Reset clears only this local browser profile. It cannot revoke account-side sessions elsewhere.

## Official sources

- [OpenAI Help Center: ChatGPT Voice](https://help.openai.com/en/articles/20001274) — web Voice start/microphone flow and plan/workspace-dependent availability.
- [Microsoft Learn: WebView2 API overview](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/overview-features-apis) — Environment/Controller/Core responsibilities plus popup, permission, download, and process APIs.
- [Microsoft Learn: Manage user data folders](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder) — UDF contents, custom-folder guidance, sharing, reset, and failure modes.
- [Microsoft Learn: WebView2 in WPF apps](https://learn.microsoft.com/en-us/microsoft-edge/webview2/platforms/wpf) — `HwndHost` airspace and `WebView2CompositionControl`.
- [Microsoft Learn: Navigation events](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/navigation-events) — cancellable navigation and redirect behavior.
- [Microsoft Learn: NewWindowRequested](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.newwindowrequested) — controlling `window.open`.
- [Microsoft Learn: CoreWebView2PermissionKind](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2permissionkind) — microphone permission kind.

**Research date:** 2026-08-09. Research sources are restricted to official OpenAI and Microsoft documentation.
