# Portable Windows Acceptance Plan

**Status:** plan only; no installation, launch, shutdown, update, or deletion was performed by this audit.  
**Pass rule:** every required acceptance row must have its named, sanitized evidence artifact. A screenshot alone is not sufficient when this plan names a script or terminal report as the authority.

## 1. Scope and fixed names

Test one transferred pair:

- <ZIP> = Hermes-Remote-Install.zip
- <SIDECAR> = Hermes-Remote-Install.zip.sha256
- <EXTRACT> = a newly created local folder, for example C:\HermesAcceptance\Extract
- <PACKAGE> = <EXTRACT>\Hermes-Remote-Install
- <DEFAULT-INSTALL> = %LOCALAPPDATA%\HermesPortable
- <CUSTOM-INSTALL> = an approved empty local folder, for example D:\Apps\HermesPortable
- <EVIDENCE> = a separate approved folder, for example C:\HermesAcceptance\Evidence\<run-id>

Run the default-path and custom-path cases on separate disposable VM snapshots, or revert to a clean snapshot between them. Do not overwrite the default install while testing the custom path.

The current portable contract expects the Docker Compose gateway service to create container name hermes, expose the dashboard only at 127.0.0.1:9119, start Serena at 127.0.0.1:9121/mcp, start Vite at 127.0.0.1:4173, and launch client\HermesDesktop.exe. These are source-confirmed expectations, not runtime results from this audit.

## 2. Test machine, isolation, and safety

1. Use a clean Windows 11 x64 VM or disposable Windows user profile. Enable the virtualization/WSL prerequisites required by Docker Desktop before beginning. The tester must be able to approve Windows installers.
2. Use approved test-only model-provider, Hermes dashboard, and OpenRouter identities. Never use a production personal identity. Never place a secret in a terminal, prompt, URL, screenshot, evidence artifact, or ticket.
3. Keep the VM isolated from unrelated development work. Before the shutdown case, arrange for a separately owned, already-running non-Hermes sentinel container. Record only its name, immutable ID, and status. This plan does not create the sentinel; its owner supplies it.
4. Do not use docker system prune, docker container prune, broad recursive deletion, or Docker Compose from any folder other than the selected Hermes installation. Do not manually stop a process or container to make the shutdown test pass.
5. Do not read, hash, copy, archive, or screenshot the contents of <INSTALL>\data. It is mounted at /opt/data and can contain credentials and sessions. Prove preservation through later behavior and masked metadata only.

| Planned operation | Exact target and expected effect |
|---|---|
| Archive extraction | Creates only the new <EXTRACT> folder and its Hermes-Remote-Install child. |
| Installation | Writes only to <DEFAULT-INSTALL> or one approved empty <CUSTOM-INSTALL>; creates product data, logs, and workspace folders. |
| Prerequisites | May use winget for Docker Desktop, uv, Node.js LTS, and Microsoft Edge WebView2 only when the installer finds them missing. |
| Serena | Installs serena-agent version 1.6.1 through the current user's uv tool installation only when serena is missing. |
| Initial setup | The official Hermes setup and dashboard flows persist model/dashboard state in the selected install data mount; do not inspect that state. |
| Desktop wrappers | Creates exactly Launch Hermes.cmd, Check Hermes.cmd, Shutdown Hermes.cmd, and Update Hermes.cmd on the current user's Desktop. |
| Safe attachment | Creates only <INSTALL>\workspace\acceptance-note.txt with non-secret fixture text. |
| OpenRouter test keys | Native desktop UI alone creates, lists as metadata, uses, and finally deletes two approved named test profiles such as HermesWorkbench:v1:openrouter:ali and HermesWorkbench:v1:openrouter:scarlett. |
| Update | A cancellation changes nothing. A separately approved update can pull and recreate only gateway, with rollback protection. |
| Shutdown | Stops tracked Hermes desktop, Workbench, Serena, and the selected Hermes Compose service; Docker Desktop and sentinel remain untouched. |

## 3. Transfer, checksum, extraction, and manifest

### 3.1 Verify transfer before extraction

Transfer <ZIP> and <SIDECAR> together to one local staging directory. Do not double-click or extract the archive before this comparison succeeds.

~~~powershell
$zip = 'C:\HermesAcceptance\Staging\Hermes-Remote-Install.zip'
$sidecar = "$zip.sha256"
$expected = ((Get-Content -Raw -LiteralPath $sidecar).Trim() -split '\s+')[0].ToUpperInvariant()
$actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
[pscustomobject]@{ Zip = $zip; Expected = $expected; Actual = $actual; Match = ($actual -ceq $expected) } | Format-List
if ($actual -cne $expected) { throw 'ZIP checksum mismatch. Do not extract or install this transfer.' }
~~~

Pass only when Match is True. Save the output as 01-transfer-checksum.txt. It is an integrity check, not a publisher signature or code-signing verification.

### 3.2 Extract only to a new folder

Confirm <EXTRACT> is new or empty and is a normal local folder. Extract exactly there:

~~~powershell
Expand-Archive -LiteralPath 'C:\HermesAcceptance\Staging\Hermes-Remote-Install.zip' -DestinationPath 'C:\HermesAcceptance\Extract'
~~~

This action creates C:\HermesAcceptance\Extract\Hermes-Remote-Install only. Do not target a drive root, user-profile root, existing project, or customer folder. Record the extracted top-level folder and its first-level files, not any runtime data.

### 3.3 Run integrity-only verification

From <PACKAGE>, before installing anything:

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-Hermes.ps1 -VerifyBundleOnly
~~~

Expected terminal evidence is a successful “Verified portable bundle integrity” line followed by “Portable bundle verification completed. No prerequisites were installed and no application files were copied.”

The current installer validates manifest protocol version 1, SHA-256 algorithm, safe relative paths, reparse-point rejection, file length, every manifest hash, and required package entries. A failure is a hard package/transfer failure: retain the terminal text and stop the install case.

## 4. Expected prerequisites and restart boundaries

The installer verifies the bundle first, then checks prerequisites. Record whether each was present or prompted. Do not uninstall unrelated software to manufacture a prompt.

| Dependency | Current behavior | Prompt and restart acceptance |
|---|---|---|
| Docker Desktop | Missing docker invokes winget install for Docker.DockerDesktop. The installer starts Docker Desktop and waits up to three minutes for docker info. | Docker Desktop/WSL can require elevation, terms, first-run configuration, or restart. If Windows requests restart, restart, open a fresh PowerShell, return to the verified package, and rerun the same case. |
| Node.js LTS | Missing node invokes winget install for OpenJS.NodeJS.LTS. The installer later runs npm ci. | No script-driven reboot is expected. It refreshes PATH and checks the standard Node path. If npm remains unavailable, close/reopen PowerShell as the installer directs and rerun. |
| WebView2 Runtime | The installer checks user/machine registration and invokes winget install for Microsoft.EdgeWebView2Runtime only if absent. | Respect any Windows restart request. Pass only when the native desktop host can open after the required boundary. |
| Git | The current installer does not check for, install, or prompt for Git. | No Hermes-driven Git prompt is expected. Record any Git prompt as external/provider behavior, not an installer success. |
| Python and uv | The installer requires uv and installs astral-sh.uv with winget if absent. It does not independently probe/install a standalone python command. | uv may perform managed-runtime work. Record its terminal result; use a fresh shell if executable discovery requires it. |
| Serena | If absent, installer runs uv tool install serena-agent==1.6.1 and writes the discovered executable path into installed launcher settings. | Network is required. Serena is not required to listen until first launch. |

The installer must reject an unrelated nonempty destination. Never point it at a real customer/project directory.

## 5. Default and custom installation cases

### Case A: default path

1. From <PACKAGE>, run Install Hermes.cmd, or:

   ~~~powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-Hermes.ps1
   ~~~

2. Complete two interactive Hermes steps with approved test identities only:

   - Step 1: the official model-provider setup.
   - Step 2: dashboard setup; choose option 1, create a unique test username/password, and do not record either. When the temporary dashboard reports it is running, press Ctrl+C once to return to the installer.

3. The installer launches <DEFAULT-INSTALL>\Launch-Hermes.ps1 with NoBrowser. That suppresses a normal browser only; the configured native desktop host is still expected to start.
4. Record the resolved install location and require %LOCALAPPDATA%\HermesPortable. Do not open or collect the data folder.

### Case B: custom path

1. Revert to a clean snapshot or use a separate clean profile.
2. Confirm the approved path, for example D:\Apps\HermesPortable, is an empty normal local directory.
3. From a separately verified <PACKAGE>, run:

   ~~~powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-Hermes.ps1 -InstallPath 'D:\Apps\HermesPortable'
   ~~~

4. Repeat setup and first-launch evidence. Pass only if the selected custom path owns the installed files, services, logs, workspace, and wrappers rather than silently using the default location.

## 6. Desktop wrappers, first launch, and the ten checks

Unless the installer was given SkipDesktopLauncher (do not give it for this acceptance case), the current user's Desktop must contain exactly:

- Launch Hermes.cmd
- Check Hermes.cmd
- Shutdown Hermes.cmd
- Update Hermes.cmd

Capture their names and target installation path only.

The expected launch order is:

1. Docker Desktop starts if docker info is unavailable, with up to 180 seconds to become ready.
2. Serena starts/adopts only a project-owned loopback listener on port 9121. A different listener is a blocking conflict; do not kill it.
3. Docker Compose starts the Hermes service from <INSTALL>.
4. First launch may set Hermes-side Serena MCP configuration to http://host.docker.internal:9121/mcp with host header localhost:9121.
5. When the main provider is OpenRouter and the auxiliary vision provider is unset/auto, first launch sets only the supported auxiliary vision provider to OpenRouter. It does not pin a model, so the upstream image retains control of its current default. These expected product configuration changes are stored in the host-mounted data area; do not inspect it.
6. If either configuration changed, the launcher restarts only hermes once.
7. Hermes status at http://127.0.0.1:9119/api/status reaches HTTP 200.
8. Vite starts on 127.0.0.1:4173.
9. The configured client\HermesDesktop.exe starts with workspace and Workbench URL environment values.

After the installer ends, use Launch Hermes.cmd only if the native host did not visibly open. Do not start a second manually created Docker, Serena, or Vite process.

Run Check Hermes.cmd, or:

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Test-Hermes.ps1
~~~

This script has exactly ten health rows:

1. Bundle files
2. Launcher settings
3. Desktop host
4. Docker
5. Hermes container
6. Hermes vision
7. Hermes gateway
8. Serena MCP
9. Workbench web
10. Runtime identity

Pass only with zero ERROR rows and the final healthy message. Resolve or explicitly triage WARN rows; a warning is not silently green. Save full output as 05-ten-check-health.txt.

## 7. Dashboard sign-in and real Hermes conversation

### Dashboard and Workbench

In the native desktop Workbench, use the Hermes sign-in flow and the dashboard test identity created during install. Pass if the Workbench becomes authenticated and usable without repeated sign-in prompts. Capture a cropped authenticated screen; exclude password dialogs, user/account identifiers, browser profile details, and sessions.

### Safe conversation, Stop, session, attachment, reasoning, search

After the ten checks are green:

1. Create this one safe fixture only:

   ~~~powershell
   Set-Content -LiteralPath '<INSTALL>\workspace\acceptance-note.txt' -Value 'Hermes portable acceptance fixture: cobalt sparrow.' -NoNewline
   ~~~

   This creates exactly one non-secret file at <INSTALL>\workspace\acceptance-note.txt, visible inside Hermes at /workspace.

2. Send a harmless prompt containing a unique marker such as portable-acceptance-<run-id>, and ask Hermes to reply with it once. Require visibly incremental stream text before final completion.
3. Start one harmless long response, for example “Produce a numbered list of 500 short neutral color names, one per line.” Once partial content is visibly streaming, click Stop once. Pass if the turn stops, partial content remains visible, and the composer becomes usable. This is a real provider request and may consume approved test usage.
4. Attach acceptance-note.txt and ask Hermes to quote the exact safe fixture sentence. Pass if the attachment is shown before sending and the response identifies that fixture.
5. Request a concise explanation of the fixture. If the selected model emits reasoning, expand/collapse the reasoning disclosure without changing the answer. If it does not emit reasoning, record a provider-conditioned result rather than falsely claiming the feature was exercised.
6. Navigate away and reopen the conversation, or use session resume. Require the marker, completed turn, stopped partial turn, and safe attachment reference to persist.
7. Use full-text session search for portable-acceptance-<run-id>. Require it to find and open that exact persisted session.

These are live acceptance checks; source inspection is not a substitute.

## 8. Hermes-to-Serena read-only navigation

1. In the Workbench Control Center/tool-discovery surface, confirm a reachable local Serena MCP service and record the discovered read-only tool name.
2. Send this constrained prompt:

   > Use only an advertised Serena read-only code-navigation tool. Do not modify, create, delete, or execute anything. Identify the top-level exported symbols in src/Modules/UsageIntelligence/DesktopUsageBridge.ts, then stop.

3. Pass only when visible tool activity identifies Serena, targets that exact path, returns navigation/read data, and shows no write, shell, terminal, or mutation tool call.
4. Capture the sanitized tool result and final answer. A model answer with no observed tool discovery/call does not pass this row.

## 9. Usage Intelligence and named OpenRouter credential test

This is desktop-host only. The renderer may receive provider, credential ID, and update time; it must never receive the API key, bearer header, raw provider response, or account/user identifiers.

1. In the native host, open Usage Intelligence > Credentials.
2. Add two approved non-secret profile labels, such as Ali and Scarlett. Enter each matching approved test key only in its native password dialog. Do not screenshot, reveal, copy, or type a key in a browser, PowerShell, or normal Workbench prompt.
3. Save both. Require only masked metadata and the normalized profile IDs `ali` and `scarlett` in the Workbench.
4. In Windows Credential Manager, verify generic targets `HermesWorkbench:v1:openrouter:ali` and `HermesWorkbench:v1:openrouter:scarlett` are present. Do not open, reveal, or screenshot their values.
5. Trigger a Usage Intelligence refresh. Pass only if OpenRouter shows one sanitized row per named key, rolls successful values into the provider total, retains a clearly attributed partial failure if one approved test key is intentionally invalid, includes collection time/provenance, and exposes no secret. Other provider cards must remain clearly synthetic unless independently implemented.
6. Keep both profiles through update, shutdown, and relaunch so persistence can be proven without reading data.
7. At the end, use the same native Credentials UI to delete the two test profiles only. Require both metadata rows to disappear, collection to stop using them, and those exact Credential Manager targets to be absent.

The current collector uses fixed HTTPS endpoint https://openrouter.ai/api/v1/key inside the C# host, uses a host-side bearer key, and rejects redirects. Any secret in UI, logs, error text, or evidence is an immediate security failure.

## 10. Browser fallback and optional Codex

### Normal-browser boundary

Do not delete client\HermesDesktop.exe to force a fallback. With Hermes healthy, open a normal browser directly to http://127.0.0.1:4173. Pass only if that browser has no native terminal bridge, no native credential dialog, no live native OpenRouter collector, and no Codex host bridge. Usage must be unavailable or clearly synthetic. Capture a cropped browser screen; do not use developer tools, browser storage inspection, or copied network headers.

### Optional Codex panel

Codex must not block core acceptance.

1. If Codex Desktop, Codex CLI, and the OpenAI VS Code extension are absent, record Not Applicable — Codex not installed.
2. If present, in the native Workbench choose Split Agent and wait for local app-server detection/start.
3. Do not send a prompt, start a thread, start a turn, or provide an API key. Any sign-in uses Codex-managed ChatGPT/device flow only.
4. Pass only if the panel reports ready or a clear unavailable reason, with no model output and no user-initiated thread/start or turn/start action.
5. Capture the panel state and, if needed, a process-name-only observation. Do not capture process command lines, account details, auth device codes, or diagnostics.

The source smoke design validates initialize and account/read with refreshToken false and explicitly sends no model turn; that supports the boundary but does not replace this portable UI check.

## 11. Controlled update, rollback evidence, data preservation

The current updater has no read-only check-only switch. It asks for UPDATE before it contacts the registry and runs Docker Compose pull for gateway. The controlled no-change acceptance branch is therefore cancellation:

1. With the ten checks green, run Update Hermes.cmd.
2. At “Type UPDATE to continue,” enter a value other than UPDATE, such as NO.
3. Pass only if it prints “Update cancelled. Nothing was changed.” Save 13-update-cancelled.txt.

Do not use the updater Yes switch. Do not type UPDATE unless the test owner separately authorizes a real network pull/recreate.

| Authorized update outcome | Required evidence and pass condition |
|---|---|
| Already current | Sanitized logs\last-update.json status already-current and pre/post ten-check output. Candidate/before image IDs match; no recreation occurred. |
| Updated | last-update.json status updated, before/candidate IDs, runtime version, and green post-update ten checks. |
| Rolled back | last-update.json status rolled-back, before/candidate IDs, restored version, and green post-rollback ten checks. |
| Rollback failed | status rollback-tag-failed or rollback-health-failed, sanitized Compose status/log tail, retained rollback image reference. Fail and escalate; do not retag/remove images without recovery-owner authority. |

Never induce rollback by corrupting images, configuration, data, or networking. The repository has mocked source updater smoke coverage, but that is not live portable evidence.

Do not inspect data to prove preservation. After an authorized update, prove it behaviorally: dashboard sign-in/session search for the earlier marker works; masked named OpenRouter metadata remains; live refresh succeeds; the safe workspace fixture remains usable. The compose contract must still mount ./data to /opt/data and ./workspace to /workspace.

## 12. Scoped shutdown and relaunch persistence

Before shutdown, from <INSTALL>, save only:

~~~powershell
docker compose ps
docker ps --format 'table {{.Names}}\t{{.ID}}\t{{.Status}}'
docker info --format '{{.ServerVersion}}'
~~~

Record the sentinel container’s name, ID, and status.

Run Shutdown Hermes.cmd only. The expected behavior is ownership-checked stop of tracked desktop, Vite, and Serena processes, followed by Docker Compose down from the selected install. It must leave Docker Desktop running.

Pass only when:

- hermes is no longer running.
- No listener remains on 9121 or 4173.
- docker info still succeeds.
- The sentinel remains running with the same ID/status.
- No unrelated process/container was manually stopped.

Then run Launch Hermes.cmd, run the ten-check report again, find the earlier session marker, reopen the persisted conversation, confirm attachment/history, and confirm masked named OpenRouter metadata plus live refresh still work. Finally perform the native credential deletion in section 9.

## 13. Safe failure evidence

Stop at the failed stage unless the next action is a documented retry. Save only reviewed and sanitized artifacts in <EVIDENCE>.

| Artifact | Exact safe collection | Required redaction |
|---|---|---|
| Transfer proof | Section 3 checksum output | Hashes/paths allowed. |
| Script output | Terminal text from the named installer, launcher, checker, shutdown, or updater wrapper | Remove dashboard usernames, provider names if sensitive, and pasted values. |
| Compose status | From <INSTALL>, docker compose ps | Do not use docker inspect or capture environment/mount contents. |
| Gateway tail | From <INSTALL>, docker compose logs --tail 100 gateway | Review before saving; remove tokens, authorization headers, sessions, prompts/attachments, account IDs, and personal paths. |
| Local errors | Final 200 lines only of logs\serena.err.log or logs\workbench.err.log when present | Review/redact before sharing. |
| Screenshots | Cropped error/health/tool/browser/panel state | Exclude passwords, native credential UI, device codes, account IDs, session IDs, personal paths, and unrelated desktop content. |
| Update record | logs\last-update.json only after authorized update | Review before sharing; it should be image metadata/version only. |

Never collect environment files, data contents, browser cookies/storage, Credential Manager secret values, full process command lines, request headers, or raw OpenRouter responses.

## 14. Pass/fail matrix

| ID | Step | Authoritative evidence | Pass condition |
|---|---|---|---|
| A01 | Isolation | 00-test-charter.txt | Clean VM/profile, approved test identities, sentinel decision. |
| A02 | Transfer | 01-transfer-checksum.txt | Sidecar and ZIP SHA-256 match before extraction. |
| A03 | Extraction | 02-extraction.txt | New extract path and expected package root. |
| A04 | Manifest | 03-verify-bundle-only.txt | Integrity success and explicit no-copy/no-prerequisite result. |
| A05 | Default install | 04a-default-install.txt | Expected default path and interactive setup. |
| A06 | Custom install | 04b-custom-install.txt | Expected custom path, no silent default fallback. |
| A07 | Prerequisites | 04c-prerequisites.txt | Behavior/restart boundary matches section 4. |
| A08 | Desktop wrappers | 04d-desktop-wrappers.txt | Four exact wrappers exist for selected install. |
| A09 | First launch | 05-launch.txt | Docker, hermes, Serena, Vite, desktop host ready. |
| A10 | Ten checks | 05-ten-check-health.txt | Ten rows, zero errors, healthy result. |
| A11 | Sign-in | Sanitized 06-authenticated-workbench.png | Authenticated usable native Workbench. |
| A12 | Stream/Stop | Sanitized 07-stream-stop.png and text note | Partial stream, one Stop, usable composer. |
| A13 | Session/search | Sanitized 08-session-persistence.png | Marker finds/reopens correct persisted session. |
| A14 | Attachment/reasoning | Sanitized 09-attachment-reasoning.png | Safe fixture works; reasoning exercised or provider-conditioned result documented. |
| A15 | Serena | Sanitized 10-serena-readonly.png | Observed read-only Serena navigation to exact path. |
| A16 | OpenRouter | Sanitized 11-openrouter-live.png and target-presence note | Two masked named profiles, both targets present, per-key rows and roll-up, live host refresh, no secret. |
| A17 | Browser boundary | Sanitized 12-browser-boundary.png | No terminal/credentials/live usage/Codex bridge. |
| A18 | Optional Codex | Sanitized 12b-codex-startup.png or N/A note | Detection/unavailable state with no model turn. |
| A19 | No-change updater | 13-update-cancelled.txt | Non-UPDATE response confirms no change. |
| A20 | Authorized update | Sanitized last-update.json and health report | Correct update/rollback state, if authorized. |
| A21 | Shutdown scope | 14-shutdown-scope.txt | Hermes stopped; Docker/sentinel untouched. |
| A22 | Relaunch | 15-relaunch-persistence.txt | Health green and session/metadata persist. |
| A23 | Credential deletion | Sanitized 16-openrouter-deleted.png and target-absence note | Only the two named OpenRouter test targets removed. |
| A24 | Packaged automation | 17-packaged-task-inventory.txt | All three packaged smoke scripts are present and runnable. |

## 15. Cleanup/uninstall gap

No Uninstall script, Uninstall command wrapper, or named portable cleanup workflow exists in the current remote-install file set. Shutdown Hermes.cmd is runtime shutdown, not uninstall.

Do not claim uninstall acceptance. Do not prescribe deleting product folders, Docker images/volumes, Desktop wrappers, or Credential Manager entries. A separate owner-approved uninstall specification is required; it must name only product-owned targets and explicitly decide how host-mounted data is preserved or removed without affecting Docker Desktop or unrelated containers.

## 16. Automation division and packaged smoke coverage

| Safe to automate after path approval | Requires an interactive user | Must remain manual/owner-controlled |
|---|---|---|
| SHA-256 comparison, extraction to an explicitly new folder, VerifyBundleOnly, selected-path checks, ten-check report, scoped Compose status, loopback port observation, updater cancellation | UAC/winget approval, Docker/WSL onboarding, provider setup, dashboard password, Workbench sign-in, live prompt/Stop/attachment/reasoning, native key save/delete, redaction review | Test identity selection, sentinel ownership, real update authorization, rollback recovery, interpretation of provider-conditioned reasoning, all uninstall/data-removal work |

Do not automate native password dialogs, Codex device/account sign-in, or any provider action that can create a nontrivial cost without a human present.

The portable ZIP builder stages and requires `tests\Install-Hermes.Smoke.ps1`, `tests\Shutdown-Hermes.Smoke.ps1`, and `tests\Update-Hermes.Smoke.ps1`. Inspect `<PACKAGE>\tests` after extraction and run the corresponding VS Code tasks or scripts; A24 passes only when all three files are present and their isolated smoke suites succeed.

## Evidence basis

This plan is based on current source inspection only:

- remote-install\Install-Hermes.ps1, Launch-Hermes.ps1, Test-Hermes.ps1, Shutdown-Hermes.ps1, Update-Hermes.ps1, README.md, and command wrappers.
- remote-install\Build-Install-Zip.ps1, docker-compose.yml, launcher.settings.json, archive entry inspection, and .vscode\tasks.json.
- src\Host\HermesDesktop\WindowsCredentialVault.cs, OpenRouterUsageCollector.cs, MainWindow.xaml.cs, CodexAppServerBridge.cs, and HermesDesktop.Smoke\Program.cs.
- docs\USAGE-INTELLIGENCE-PROVIDERS.md plus src\Modules\UsageIntelligence desktop bridge files.
