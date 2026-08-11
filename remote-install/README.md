# Hermes + Serena portable Windows installer

This bundle installs Hermes in Docker Desktop, Serena locally on Windows for native Roslyn support, and the self-contained C# HermesDesktop host. You do not need to know Docker commands for normal use.

## Install on a customer computer

1. Extract the ZIP to a normal local folder.
2. Double-click `Install Hermes.cmd` and approve prerequisite installers if requested.
3. Complete the two clearly labelled setup steps: model-provider authentication, then dashboard username/password.
4. Use `Launch Hermes.cmd` in the install folder or on the Desktop from then on.

Use `Check Hermes.cmd` after launch whenever you want a plain-English, read-only health report. Its twelve checks cover files, settings, desktop-host presence, Docker/container state, screenshot-vision readiness, the public Hermes status endpoint, Serena/Vite/bridge port ownership, and safe runtime identity metadata. It does not read credentials, provider keys, sessions, or complete command lines.

Use `Shutdown Hermes.cmd` to stop Hermes, Serena, and Hermes Workbench. It verifies tracked process ownership, waits for each owned process to exit, and still cleans local components if Docker Desktop is unavailable. Docker Desktop is intentionally left running so unrelated containers are not interrupted.

Use `Update Hermes.cmd` to verify/apply the release-approved immutable Hermes container image. It asks you to type `UPDATE`, preserves the previous image, recreates only the Hermes service, waits for its status endpoint, and automatically rolls back if the candidate does not become healthy. It never executes a floating image tag, never deletes the host-mounted `data` or `workspace` folders, and normal launches never update automatically. A new upstream digest arrives only in a reviewed Workbench release.

The default installed location is `%LOCALAPPDATA%\HermesPortable`. To choose another location, open PowerShell in this folder and run:

```powershell
.\Install-Hermes.ps1 -InstallPath 'D:\Apps\HermesPortable'
```

A custom destination must be empty or already recognizable as Hermes Workbench. The installer writes a small versioned `.hermes-workbench-install.json` ownership marker and refuses unrelated nonempty folders, so a mistyped path cannot overwrite another project. Existing `data`, `logs`, and `workspace` contents remain outside bundle copying.

Every generated ZIP contains `bundle.manifest.json`. The installer checks the SHA-256 and length of every shipped file before installing prerequisites or copying anything. To perform only that integrity check, run:

```powershell
.\Install-Hermes.ps1 -VerifyBundleOnly
```

This detects accidental corruption or incomplete extraction; it is not a publisher code-signing certificate.

## Local Photon CAD engineering assets

The offline bundle can carry the receipt-bound Photon CAD image archives for local engineering, but normal builds omit them and normal installs do not copy or load them. Public redistribution remains blocked by `payloads\photon-cad\LOCAL-ENGINEERING-ONLY.md` pending the separate compliance receipt. A local engineering bundle must be built explicitly with `Build-Install-Zip.ps1 -IncludeLocalEngineeringPhotonCadAssets`; it is forced to a distinct `.LOCAL-ENGINEERING-ONLY.zip` output identity and cannot overwrite the canonical public bundle candidate.

To verify the exact lock, receipt, policy, hashes, lengths, and image IDs without copying files or invoking Docker:

```powershell
.\Install-PhotonCadRuntime.ps1 -VerifyOnly
```

To copy the five exact files transactionally into `<installRoot>\runtime-assets\photon-cad`, opt in during installation with `-InstallPhotonCadAssets`. Add `-LoadPhotonCadImages` only when you also explicitly want the two exact TARs loaded and their installed tags checked against the receipt-bound image IDs. The loader does not pull, run, start, stop, or remove containers or images.

## What the launcher starts

- Docker Desktop, if needed
- Serena at `http://127.0.0.1:9121/mcp`
- One Hermes container supervising both the gateway and dashboard
- One private, non-published Qdrant `memory-vector` service for authenticated Mem0, with its persistent data in a Docker-managed volume
- Hermes Workbench at `http://127.0.0.1:4173`
- The thin C# WebView2 desktop host
- A real PowerShell terminal rooted at the installed Hermes workspace
- A native **Run and Debug** dock that discovers .NET solutions/projects, runs guarded builds, supports cancellation, and sends compiler errors and warnings to both **Problems** and Monaco editor markers
- An optional Codex agent panel, when Codex Desktop, the OpenAI VS Code extension, or the Codex CLI is installed
- A Control Center for Hermes health, account connection, protected runtime telemetry, Serena discovery, and full MCP server/catalog management. Upstream catalog entries are clearly **Nous-approved**, never presented as independently Chris/Codex reviewed; external servers are marked **not reviewed**, and every catalog install exposes commands, source/ref, bootstrap steps, setup notes, and required secrets for confirmation first.
- Hermes Skills management with installed provenance, connected hub sources, the actual SKILL.md preview, and upstream quarantine/security-scan results. A matching scan is required before install, blocked policy cannot install, and caution policy requires explicit acceptance.
- Hermes Toolsets management for capability switches, provider readiness/selection, allowed secret entry, and reviewed upstream setup hooks. Stored secret values never return to React, submitted values are immediately cleared, and arbitrary environment names or setup commands are rejected.
- A separate confirmation-gated updater with before/candidate image records and automatic health-check rollback
- A native Windows Credential Manager vault for any number of separately named provider connections; React can see only masked connection metadata, never stored secrets. Every configured OpenRouter key is collected through the trusted desktop host, shown separately, and rolled into the provider total. Gemini keys can be inventoried, but Gemini per-key usage is marked unavailable until Google exposes a supported source. Other provider cards remain explicitly labeled synthetic until their read-only collectors are added, and demo providers are excluded from live totals.
- Safe GitHub-flavored Markdown messages with tables, task lists, external links, and copyable fenced code; raw HTML and implicit remote image loading stay disabled
- Persistent interface scaling at 100%, 110%, 125%, or 140% from **View > Interface size**, the status bar, or `Ctrl`+`+` / `Ctrl`+`-`; new installs begin at 125%
- Expandable Hermes reasoning, visible clarification/approval controls, a turn-aware Stop button, a working agent options menu, and an editable prompt queue
- Bounded, text-only inline diff cards for Hermes tool activity, with unified add/remove styling and copy-only review; malformed patches remain inert text and no path is opened automatically
- A bounded Hermes notification center for sticky and temporary gateway notices; repeated notice keys replace in place and Workbench does not persist notice text

## Build a .NET project in Workbench

Open the **Run and Debug** activity on the left rail. Choose a discovered `.sln`, `.slnx`, or `.csproj`, select **Debug** or **Release**, and choose **Build selected target**. Workbench opens the lower dock automatically. Build text appears under **Output**; compiler errors and warnings appear under **Problems** and as editor squiggles when their file is open. Select a problem to open its workspace file. While a build is active, the same button becomes **Stop build** and cancels only that correlated request.

This is a desktop-only trust boundary: the normal browser page cannot start local compilers. The current release supplies guarded .NET build support. Roslyn language-service features and a real debugger are intentionally labeled **Planned** until their separately secured adapters are packaged and verified.

The installer also installs the current Node.js LTS release, checks machine-wide and per-user Microsoft Edge WebView2 Runtime registrations, and restores the exact React/TypeScript dependencies from `package-lock.json`. Freshly installed Docker, Node, and `uv` are discovered from their standard Windows locations even before the current PowerShell session receives updated PATH entries.
It records Serena's exact `uv` tool executable in the installed launcher settings, so a newly installed Serena works even before a fresh PowerShell session picks up PATH changes.

Files placed in the configured dedicated workspace directory appear inside Hermes at `/workspace`. Portable installs default to the install folder's `workspace`; an explicitly configured absolute workspace must pass the launcher's confinement checks. Credentials and sessions remain in the separate `data` directory, authenticated-memory vectors remain in the Docker-managed `memory-vector-data` volume, and neither is included in the portable ZIP. The Qdrant service exposes no Windows host port.

## C# desktop host

`client\HermesDesktop.exe` is included in the ZIP and already configured in `launcher.settings.json`:

```json
{
  "ClientExecutable": "client\\HermesDesktop.exe",
  "ClientWorkingDirectory": "client",
  "WorkbenchUrl": "http://127.0.0.1:4173"
}
```

During installation, a machine-specific `SerenaExecutable` field is added to this file automatically. It contains no credential or secret and should not be copied back into the portable source bundle.

The bundle also includes `tools\hermes-bridge\hermes-bridge.exe`, a self-contained, loopback-only command-line client for the authenticated Hermes conversation bridge. It requires no .NET installation. `health` is unauthenticated; `status`, `send --text "..."`, and `interrupt` read the bridge code directly from Windows local application data and never accept or print that code on the command line.

The installer creates `Launch Hermes.cmd`, `Check Hermes.cmd`, `Shutdown Hermes.cmd`, and `Update Hermes.cmd` on the Desktop unless `-SkipDesktopLauncher` is supplied.

If you open the installed folder in VS Code, **Terminal > Run Task** provides the same launch/check/shutdown/update commands plus frontend development, tests, production build, isolated script smoke tests, full offline verification, and portable-ZIP packaging. The included `Invoke-HermesFrontend.ps1` locates npm from PATH or standard Windows Node locations, so these tasks do not depend on one hardcoded installation directory.

If the client executable is removed, the launcher safely falls back to the same Workbench in your normal browser. Chat, files, sessions, and Hermes Console remain available there; native PowerShell and Codex stay disabled because browsers should not receive unrestricted local process access.

Provider sign-in and secret storage are desktop-only trust boundaries. OAuth-style Hermes sign-in opens in a constrained WebView2 window sharing the Workbench cookie profile. API keys are entered in a native password dialog and stored by Windows Credential Manager; they are not saved in the install folder, browser storage, logs, or exported diagnostics.

To enable live Usage Intelligence sources, open the installed desktop Workbench, choose **Usage Intelligence > Credentials**, and add separately named OpenRouter, OpenAI API, or Anthropic API profiles for the restricted keys you manage (for example, Ali or Scarlett). OpenAI and Anthropic organization usage requires their respective Admin API keys; consumer ChatGPT and Claude subscription allowances are not exposed by supported usage APIs. Each profile can be replaced or deleted independently. Do not enter keys into the normal browser version at `127.0.0.1:4173`; it intentionally has no credential or provider-usage bridge.

When OpenRouter is the main Hermes provider, the first launch also configures Hermes's supported auxiliary vision lane automatically. Screenshot analysis can use a paid OpenRouter model even when ordinary chat uses a different model, so keep the spending limits on each key and review the selected vision model with `hermes tools configure` if you want to change it.

## Optional Codex panel

Hermes does not depend on Codex. When Codex is installed, choose **Split Agent** in the desktop Workbench and the host will start a private local `codex app-server` process for that panel. Sign-in uses the installed Codex account or its device-code flow; API keys are not accepted by the React interface. To select a custom runtime before launching, set `HERMES_CODEX_PATH` to the full path of `codex.exe`.

## Three useful recovery commands

From the install folder:

```powershell
docker compose ps
docker compose logs --tail 100
docker compose restart
```
