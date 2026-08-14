# CodexHand local OpenHands handoff

Date: 2026-08-14  
State: local OpenHands GUI is running; Codex profile is created; subscription authentication and one real Codex conversation still require verification  
Next owner: CodexHand

## User intent

Give Chris a graphical OpenHands experience using Codex through his ChatGPT subscription. OpenHands is allowed to run locally in Ubuntu under WSL because Chris explicitly requested this setup. It must not use Docker.

All engineering products, builds, tests, and application launches are Windows-first:

- Treat `C:\Users\clsor` and its descendants as the authoritative user workspace.
- Build and run Windows applications with Windows executables and the Windows toolchain.
- Do not use Linux-side build artifacts, Linux `dotnet`, Linux `npm`, Linux test hosts, or Linux application runtimes unless Chris explicitly requests them for a specific task.
- WSL may host the OpenHands GUI/agent process and access Windows files through `/mnt/c/Users/clsor`; that exception does not authorize Linux-targeted product builds.
- Docker Desktop is reserved for Photon/Hermes installation convenience. It is not an OpenHands workspace, isolation boundary, or security boundary.

## Installed local experience

- Desktop shortcut: `C:\Users\clsor\OneDrive\Desktop\OpenHands GUI.lnk`
- Windows launcher: `C:\Users\clsor\AppData\Local\OpenHands\Open-OpenHands.ps1`
- WSL distribution: `Ubuntu`
- WSL user: `clsorrells42`
- Agent Canvas service: `/home/clsorrells42/.config/systemd/user/openhands-agent-canvas.service`
- Agent Canvas version shown in the GUI: `1.12.0`
- Service state at handoff: enabled and active
- Local GUI port: `8000`
- Current observed URL: `http://172.29.4.148:8000`

The WSL address can change. Prefer the Windows desktop shortcut because its launcher resolves and opens the current local address.

## Docker boundary

The previous OpenHands Docker installation was removed. At handoff, Docker reports no OpenHands/Agent Canvas containers or images. Do not reinstall or start OpenHands in Docker.

Photon/Hermes containers are separate and must not be changed as part of CodexHand/OpenHands work unless Chris explicitly asks.

## Codex agent profile

Profile file:

`/home/clsorrells42/.openhands/agent-profiles/Codex.json`

Current profile settings:

- Agent kind: `acp`
- ACP server: `codex`
- Command: `npx -y @agentclientprotocol/codex-acp@1.1.2`
- Model: `gpt-5.6-sol`

In the OpenHands Agent settings screen, use:

- Preset: `Codex`
- Command: `npx -y @agentclientprotocol/codex-acp@1.1.2`
- Model: `GPT-5.6 Sol`
- `CODEX_AUTH_JSON`: paste the complete contents of `C:\Users\clsor\.codex\auth.json`
- `OPENAI_API_KEY`: leave blank
- `OPENAI_BASE_URL`: leave blank

The Windows Codex authentication file exists at handoff. No credential values are included in this document. Never print, log, commit, attach, or paste its contents anywhere except the OpenHands secret field Chris selected.

The OpenHands profile stores `CODEX_AUTH_JSON` as a secret and materializes it for the Codex ACP runtime. Do not replace subscription authentication with an OpenAI API key unless Chris explicitly chooses usage-based API billing.

## Separate DeepSeek profile

The existing OpenHands profile using DeepSeek V4 Pro through OpenRouter is separate:

- DeepSeek/OpenRouter usage is billed by OpenRouter.
- Codex/ChatGPT subscription usage is governed by Chris's ChatGPT Codex allowance.
- The two models do not combine or improve each other.
- Do not copy the OpenRouter key or endpoint into the Codex profile.

## Required acceptance check

The Codex profile has not yet been proven by a successful conversation. After Chris saves the credential field:

1. Start a new OpenHands conversation with the `Codex` profile.
2. Use a harmless prompt that asks Codex to identify its model and report the working directory without editing files.
3. Confirm the conversation completes without an authentication, ACP startup, or missing-command error.
4. Confirm the working directory is a Windows-backed project under `/mnt/c/Users/clsor/...` before authorizing any code changes.
5. For a Windows/.NET task, invoke Windows tooling explicitly and keep outputs in the Windows project tree.
6. Record the exact visible result. Do not claim success based only on the profile being saved or the service being active.

If authentication fails, do not invent an `auth.json` schema and do not paste a key into `OPENAI_API_KEY`. Refresh the official Codex ChatGPT sign-in, then repaste the newly generated complete Windows `auth.json` into the OpenHands secret field and retry once.

## Known limitations

- Agent Canvas itself runs inside Ubuntu WSL; this is the explicit OpenHands exception to the Windows-only execution preference.
- A WSL-hosted agent can accidentally choose Linux tools if a task prompt is vague. Every engineering prompt should state that the authoritative project is on Windows and that builds/tests/runs must use Windows executables.
- Service-active evidence proves only that the GUI backend is running. It does not prove Codex authentication, model access, Windows build execution, or application behavior.
- The raw `auth.json` is a renewable credential bundle, not reusable documentation. Keep it out of handoffs and source control.

## Operator start procedure

Chris should normally start OpenHands by double-clicking:

`OpenHands GUI.lnk`

Then create a new chat and select either:

- `Codex` for ChatGPT-subscription-backed Codex work; or
- the OpenHands/DeepSeek profile for OpenRouter-backed work.

No command-line interaction and no Docker workspace are required for the normal GUI flow.
