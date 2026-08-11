# HermesDesktop host

`HermesDesktop` is the thin Windows shell for the React Workbench. It uses the Evergreen WebView2 runtime and loads the local Workbench URL started by the launcher.

Host protocol v1 supports trusted-loopback navigation, external-link handoff, host readiness, native window controls, a versioned native terminal bridge, an optional Codex app-server bridge, authentication-window protocol v1, credential-vault protocol v1, and native usage protocol v1. The terminal uses Windows ConPTY through Porta.Pty, streams VT data to the lazy-loaded xterm.js renderer, and stays rooted at the installed Hermes workspace. The Codex bridge uses the installed Codex runtime, permits only the Workbench protocol surface, validates workspace and sandbox requests, and keeps API keys out of the renderer. Browser mode keeps the same React interface but deliberately does not expose native process, credential, or usage bridges.

The authentication bridge accepts only validated provider identifiers, constructs the loopback Hermes login URL in native code, and opens it in an owned WebView2 window that shares the main Workbench environment. This allows the resulting authentication cookie to reach the Workbench without exposing cookie or token material to React. The window permits only HTTP(S) navigation and reports completion only for the exact loopback callback path.

The credential bridge uses a native password dialog and Windows Credential Manager. React can request provider-connection metadata, open the native save dialog, or request a confirmation-gated deletion, but it can never read a stored secret. Credential target identifiers are versioned and validated; diagnostic messages contain only safe error categories.

The native usage bridge currently supports OpenRouter current-key collection. It accepts only the versioned `openrouter` provider identifier and validated opaque credential reference, resolves the secret in native code, calls the fixed HTTPS `/api/v1/key` endpoint with redirects disabled and bounded response/timeout limits, and returns only normalized numeric usage and limit fields. Response labels, account identifiers, credentials, and response bodies never cross into React or diagnostics.

Run the real PowerShell round-trip and read-only Codex handshake smoke check with:

```powershell
dotnet run --project .\HermesDesktop.Smoke\HermesDesktop.Smoke.csproj -c Release
```

The smoke harness validates authentication and credential target construction, performs a synthetic Credential Manager save/read/delete round trip, validates OpenRouter request construction, response sanitization, authorization-error handling, and redirect rejection through a fake HTTP handler, performs a real ConPTY round trip, initializes Codex, and reads account state. It never uses a real provider key, makes a provider network call, or starts a model turn.

Publish a portable self-contained host with:

```powershell
dotnet publish .\HermesDesktop\HermesDesktop.csproj -c Release -r win-x64 --self-contained true -o ..\..\..\artifacts\desktop\win-x64
```
