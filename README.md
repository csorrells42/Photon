# Photos Agape Aphthartos

Photos Agape Aphthartos is an independent, portable Windows engineering workspace that keeps the Hermes Agent engine isolated in Docker, runs Serena locally for native code intelligence, and presents both through a modern React/TypeScript IDE interface. **Photon** is the default user-configurable assistant name; Hermes remains the underlying runtime and compatibility boundary.

## Everyday use

- Double-click `Launch Hermes.cmd` to start Docker Desktop if needed, local Serena, the single supervised Hermes container, and Photos Agape Aphthartos. The launcher rejects unrelated processes on its reserved ports and waits for Hermes readiness before opening the client.
- Double-click `Check Hermes.cmd` for a read-only health table covering bundle files, settings, desktop host, Docker/container identity, screenshot-vision readiness, public gateway version/state, Serena/Vite port ownership, and safe runtime identity metadata. It never reads provider keys, Hermes credentials, sessions, or full command lines.
- Double-click `Shutdown Hermes.cmd` to stop only this project's services. It closes tracked local processes before Compose, refuses stale PID ownership, waits for confirmed exits, and continues safe cleanup even when Docker Desktop is unavailable. Docker Desktop itself stays available for other applications.
- Double-click `Update Hermes.cmd` to verify/apply the release-approved immutable Hermes image. It requires the word `UPDATE`, preserves the previous image as `hermes-workbench-rollback:previous`, health-checks the replacement, and automatically restores the previous image if the candidate fails. A new upstream digest is adopted only through a reviewed Workbench release.
- The launcher opens the native `HermesDesktop` WebView2 window. The same Workbench remains available in a browser at <http://127.0.0.1:4173>.
- The lower dock separates a real native PowerShell terminal from the authenticated Hermes management console. Native command execution is disabled when the same UI is opened in a normal browser.
- The **Run and Debug** activity opens the native developer-services dock. It discovers bounded `.sln`, `.slnx`, and `.csproj` targets, runs a guarded Debug or Release build through the desktop host, streams bounded output into **Output**, projects compiler diagnostics into **Problems** and Monaco squiggles, and lets a selected problem open its workspace file. Build cancellation is correlated to the active request. The developer-services core now owns a bounded, shell-free stdio LSP child-process transport for future Roslyn/JDT adapters; an actual C# language server, renderer language requests, and a real debugger remain visibly marked **Planned** rather than being simulated. The browser-only Workbench cannot invoke local builds.
- Split Agent view can run an optional Codex conversation beside Hermes. The desktop host starts Codex's local app-server, streams its activity into React, and keeps approvals in the Workbench; it never sends a model prompt during startup or diagnostics.
- The activity-rail Settings button opens the native-looking Control Center for Hermes health, account connection, component state, authenticated runtime telemetry, Serena discovery, and full MCP server/catalog management. Catalog entries retain the explicit upstream **Nous-approved** provenance label; that label never implies independent Chris or Codex review, while external servers remain visibly **not reviewed**. Every install opens a transparent review sheet showing transport, authentication, commands, source/ref, bootstrap steps, setup notes, and required secrets before confirmation. Secret values travel only in the authenticated request body and are never returned to React. Passwords are posted once to loopback Hermes and immediately cleared; supported provider sign-ins open in a constrained WebView2 window that shares only the Workbench cookie profile.
- The same Control Center now manages Hermes Skills without flattening their trust trail. The live adapter reports installed provenance and connected hubs, shows the actual SKILL.md and bundle manifest, and runs Hermes' quarantine/security scan before installation. A matching scan is mandatory, blocked policy never posts, and caution policy requires an explicit checkbox. Upstream trust labels are never presented as independent Chris/Codex review.
- Its Toolsets tab reproduces Hermes capability/provider configuration through a separate v1 contract. The live route reports 28 toolsets, provider readiness, selected backends, allowed environment names, saved/not-saved status, and reviewed setup-hook identifiers. React never receives stored values; new secrets post once only to the exact names Hermes advertised, and setup hooks cannot run unless present in the current provider contract.
- The Usage Intelligence rail presents provider capabilities and normalized usage in one dashboard; any number of separately named provider keys (for example, Ali and Scarlett) can be entered only through the desktop host's native Windows Credential Manager dialog and are never readable by React.
- Usage Intelligence protocol v2 collects every configured OpenRouter key plus official OpenAI and Anthropic organization usage through the trusted C# desktop host. It shows per-key/profile breakdowns, rolls compatible results into provider totals, and retains partial-failure details. OpenAI and Anthropic require organization Admin API keys; consumer ChatGPT and Claude subscription allowances remain explicitly unsupported. Gemini keys can be inventoried safely, but Gemini per-key usage remains unavailable until Google exposes a supported read-only source. Live totals exclude every synthetic/demo provider.
- If the gateway drops, chat obtains a fresh single-use WebSocket ticket and makes up to five bounded recovery attempts over roughly 19 seconds. Durable conversations are rebound to their original profile, and prompts are never replayed automatically.
- **View > Interface size** enlarges the entire Workbench to 100%, 110%, 125%, or 140%. The setting persists locally; `Ctrl`+`+` and `Ctrl`+`-` step between sizes, `Ctrl`+`0` resets to 100%, and the current percentage is clickable in the status bar. New installs start at 125%.
- Agent reasoning remains expandable in the conversation. Blocking approval/clarification questions stay beside the composer, the send arrow becomes **Stop** during an active turn, and the `...` menu exposes new chat, reconnect, stop, model/approval settings, and queue cleanup.
- The bell in the Agent header is a bounded in-app Hermes notification center. Sticky warnings remain until cleared, temporary notices expire, repeated keys replace instead of stacking, and notification text is never persisted by Workbench.
- Prompts entered during a running turn can be queued, edited, promoted, removed, parked by Stop, and resumed. Successful turns drain the queue in order; errors preserve it for review.
- Session search combines instant matches from the loaded sidebar with Hermes full-text search across every advertised profile, so older conversations remain discoverable by session ID or message content. Search results include the matching snippet and resume through the same versioned session adapter.
- Hermes and user messages render safe GitHub-flavored Markdown with headings, lists, task lists, tables, quotes, external links, inline code, and copyable fenced code blocks. Raw HTML is disabled, remote images do not load implicitly, and the renderer is lazy-loaded only when a message needs it.
- The upstream dashboard remains available at <http://127.0.0.1:9119>. Workbench sign-in should be started from **Settings > Account** so the desktop WebView2 cookie profile is authenticated and chat, sessions, and system state reconnect together.

The same commands are available in VS Code under **Terminal > Run Task**. The menu also includes `Frontend: Dev`, `Frontend: Test`, `Frontend: Build`, the three isolated PowerShell smoke tests, `Hermes: Verify offline`, and `Installer: Build portable ZIP`. The frontend tasks use `Invoke-HermesFrontend.ps1` to discover npm instead of assuming Node is installed in one fixed directory. `Hermes: Verify offline` is the default test task and does not restart, update, or stop the live Workbench.

## Project layout

- `src/` — the React/TypeScript Workbench, versioned Hermes compatibility adapters, and thin C# WebView2 host under `src/Host/`.
- `artifacts/desktop/win-x64/` — the self-contained published Windows host used by this computer and the portable installer.
- `artifacts/tools/hermes-bridge/win-x64/` — the self-contained loopback bridge CLI shipped for agent and automation clients.
- `source/` — a Git submodule that preserves Nous Research's Hermes Agent history and pins our reviewed backend integration branch. Upstream code remains MIT-licensed under `source/LICENSE`.
- `data/` — persistent Hermes configuration, credentials, sessions, skills, and memories. Never package or commit this folder.
- `remote-install/` — customer-facing install, launch, shutdown, and packaging scripts.
- `Update-Hermes.ps1` — explicit approved-image verification, health checking, compatibility-identity refresh, and automatic one-image rollback; normal launches never auto-update or execute a floating image tag.
- `docker-compose.yml` — one updateable upstream Hermes container with its gateway and dashboard supervised together; Serena stays local for native Roslyn support.
- `workspace/` — the portable folder mounted into Hermes as `/workspace`, isolated from credentials under `data/`.

## Development checks

```powershell
cd .\src
npm test
npm run build
dotnet build .\Host\HermesDesktop\HermesDesktop.csproj -c Release
dotnet run --project .\Host\HermesDeveloperServices.Smoke\HermesDeveloperServices.Smoke.csproj -c Release
dotnet run --project .\Host\HermesDesktop.Smoke\HermesDesktop.Smoke.csproj -c Release
```

The developer-services smoke suite passes 12 tests covering provider isolation, bounded target discovery, guarded builds, diagnostic deduplication, cancellation, the provider-neutral DAP nucleus, and a bounded provider-neutral LSP session for document sync, diagnostics, completion, hover, navigation, rename, cancellation, and shutdown. The native desktop smoke harness creates, lists, reads, and removes two named synthetic Credential Manager profiles, validates the fixed-endpoint OpenRouter collector without using a real key or network call, exercises the real renderer-to-build bridge, performs a real ConPTY round trip, and completes a read-only Codex handshake. The current frontend suite passes 159 tests across 38 files.

The local Vite server proxies `/api` to the upstream dashboard so browser authentication, REST calls, and the Hermes WebSocket gateway remain same-origin. The Codex panel is a native-desktop capability and automatically finds Codex Desktop, the OpenAI VS Code extension, or a `codex.exe` on `PATH`; set `HERMES_CODEX_PATH` to an exact executable when a custom installation is preferred.

`src/Modules/HermesCompatibility/HermesContractCorpus.ts` is the synthetic, non-secret v1 wire-contract corpus for status/auth, immutable identity, tickets, JSON-RPC, models, sessions, tools, approvals/prompts, attachments, and Console frames. Run its Vitest suite before and after adopting a new upstream image; live deployment checks are still required because a fixture is not runtime proof.

## Build the portable installer

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\remote-install\Build-Install-Zip.ps1
```

This creates `Hermes-Remote-Install.zip` and a matching `Hermes-Remote-Install.zip.sha256` transfer checksum. The ZIP includes the Workbench source, exact dependency lockfile, portable VS Code task menu, npm-discovery runner, published self-contained desktop host, self-contained Hermes bridge CLI, and a generated SHA-256/length manifest covering every shipped file while excluding credentials, customer data, logs, `node_modules`, and intermediate build folders. An extracted customer can run `Install-Hermes.ps1 -VerifyBundleOnly` to check integrity without installing or copying anything.

See `remote-install\README.md` for the clean-computer walkthrough.

To refresh the portable bridge client before rebuilding the ZIP:

```powershell
dotnet publish .\src\Tools\HermesBridgeClient\HermesBridgeClient.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -o .\artifacts\tools\hermes-bridge\win-x64
```
