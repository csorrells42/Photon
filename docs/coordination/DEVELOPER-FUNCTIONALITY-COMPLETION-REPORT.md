# Roslyn, Compiler, Debugger, and Serena Functionality Completion Report

Date: 2026-08-11

## Outcome

The Workbench developer-services slice is functionally complete at its typed renderer/native seams and at the real pinned-provider boundary.

- Roslyn now opens the solution or nearest owning `.csproj` before opening a C# document. A closed document permits an explicit switch to a different project; concurrent cross-project ownership is rejected instead of returning misleading empty results.
- The desktop bridge exposes real completion, hover, definition, references, rename, code actions, diagnostics, document changes, and document close behavior.
- The fixed NetCoreDbg provider exposes discovery, launch, breakpoint negotiation, configurationDone, stopped/continued events, threads, stack traces, scopes, variables, evaluate, continue, step-in, step-out, step-over, disconnect, cancellation recovery, and explicit relaunch.
- The compiler bridge performs real bounded `dotnet` build and analyze operations.
- The native Serena adapter performs a fixed read-only `find_symbol` call through the launcher-owned loopback service and projects bounded path/line/column/preview results.
- Reconnect/recovery paths do not replay language or debugger operations automatically.

Security observations were recorded without remediation, as requested, in `docs/coordination/DEVELOPER-FUNCTIONALITY-DEFERRED-SECURITY.md`.

## Final published runtime

- Published executable: `C:\Users\clsor\Documents\Codex\HermesAgent\artifacts\desktop\win-x64\HermesDesktop.exe`
- Published executable SHA-256: `E68D2D4C6BB9EA75B858FA94B6BF63AE59182764B557F4182D13B6AA5EF87C21`
- Launch path: authoritative `Launch-Hermes.ps1 -NoBrowser` only
- Desktop PID: `21636`
- Desktop HWND: `14748794`
- Window title: `Phos Agape Aphthartos`
- Desktop state: responding
- Conversation bridge: `127.0.0.1:8972`, owned by PID `21636`
- Workbench origin: `127.0.0.1:4173`
- Configured workspace: `C:\Users\clsor\OneDrive\Desktop\Hermes`
- Live `terminal.ready` cwd proof: `C:\Users\clsor\OneDrive\Desktop\Hermes`

The root-owned Workspace Explorer fallback change to `PHOS WORKSPACE` was preserved.

## Pinned engines

Roslyn:

- Package version: `5.0.0-1.25277.114`
- Executable SHA-256: `A0880370C766535D3E53C3ACF2D1A89AB1F46FA6C7AA84F05AEEF51AE7E430AC`
- Published receipt and executable hash match.

NetCoreDbg:

- Version: `3.1.3-1062`
- Source commit: `8b8b22200fecdb1aec5f47af63215462d8c79a4b`
- Archive SHA-256: `C67AE052E0BCB9CE37000F261E2D397A0D5B6615CAFE30C868239A78598DFB37`
- Executable SHA-256: `F5EE03E1F279F96EE64B9C9D53840F04A09F746301589026E9B5A1DE2E6A5D3D`
- Published receipt and executable hash match.

## Live Serena evidence

- Serena PID: `4320`
- Listener: `127.0.0.1:9121`, owned by PID `4320`
- Command workspace: `C:\Users\clsor\OneDrive\Desktop\Hermes`
- Runtime configuration correction, authorized exclusively by Architect: `.serena/project.yml` changed only from `languages: []` to `languages: [python, csharp]`.
- Python native adapter result: `photon-capability-stage1/assistant_bus_envelope.py:38:1` for semantic intent `VisibleEnvelope`.
- C# native adapter result: `serena-csharp-proof/SerenaCSharpProof.cs:3:23` for semantic intent `SerenaCSharpCapabilityProof`.

No Serena endpoint, command, argument, environment, or raw provider error is supplied by renderer code.

## Real provider evidence

The current-byte `HermesDesktop.DeveloperServices.Smoke` was rebuilt after the final combined source changes and run against the exact published artifact root.

- Real Roslyn: PASS — open/change/completion/hover/definition/references/rename/code-actions/close.
- Real .NET debugger: PASS — discovery/launch/breakpoint/inspect/evaluate/step/disconnect/relaunch.
- Published provider discovery: PASS — `dotnet`, `roslyn-lsp`, and `hermes-dotnet-dap` available.
- Published compiler build: PASS.
- Published compiler analyze: PASS.
- Native terminal: PASS — `terminal.ready` reported the configured workspace cwd.
- Native Serena: PASS — bounded real Python and C# semantic results from the launcher-bound workspace.

The real Roslyn and debugger workflows use only a disposable SPAT-owned `Dragon` fixture. No real process attach was performed.

## Exact gates

- `npx.cmd tsc -p tsconfig.app.json --noEmit`: PASS.
- `npx.cmd vitest run Modules/DeveloperServices Modules/WorkspaceSearch Modules/Workspace Modules/HermesGateway/HermesConversationBridgeAdapter.test.ts`: 17 files, 111 tests passed.
- `npx.cmd vitest run`: 117 files, 701 tests passed.
- `npm.cmd run build`: PASS (`tsc -b` and Vite production build; 3,268 modules transformed, chunk-size warnings only).
- `HermesRoslynLanguageServer.Smoke`: 13/13 passed.
- `HermesDeveloperServices.Smoke`: 13/13 passed.
- `HermesDotNetDebugger.Smoke`: 10/10 passed.
- Final combined `HermesDesktop.Smoke` Release build: 0 warnings, 0 errors.
- Final combined `HermesDesktop.Smoke.exe --developer-services-only`: PASS.
- Current-byte typed desktop debugger bridge smoke: PASS.
- Current-byte real desktop Roslyn bridge smoke: PASS.
- Current-byte real desktop .NET debugger bridge smoke: PASS.
- Mounted Vite shell at `http://127.0.0.1:4173`: PASS. The Run activity rendered Build/Analyze configuration, provider capability status, launch/attach inputs, breakpoint guidance, and debugger controls. Workspace Search rendered its labeled query, literal/semantic modes, shortcut hint, results/status region, and honest unavailable state when no browser provider was configured. Browser console errors: 0.
- Final publish: PASS.
- Repository `git diff --check`: PASS.

## SPAT source changes

- `src/Host/HermesDesktop/DeveloperServicesBridge.cs`
- `src/Host/HermesDesktop/DeveloperDebugHost.cs`
- `src/Host/HermesDesktop/MainWindow.xaml.cs`
- `src/Host/HermesDesktop/SerenaWorkspaceSearchClient.cs`
- `src/Host/HermesDesktop.Smoke/Program.cs`
- `src/Host/HermesDesktop.Smoke/WorkspaceSearchBridgeSmoke.cs`
- `src/Host/HermesDesktop.Smoke/DeveloperDebugBridgeModuleSmoke.cs`
- `src/Host/HermesDesktop.DeveloperServices.Smoke/**`
- `src/Host/HermesRoslynLanguageServer/RoslynLanguageSession.cs`
- `src/Host/HermesDotNetDebugger/HermesDotNetDebugSession.cs`
- `src/Host/HermesDotNetDebugger.Smoke/Program.cs`
- `src/Modules/DeveloperServices/DesktopRoslynLanguageClient.ts`
- `src/Modules/DeveloperServices/DesktopRoslynLanguageClient.test.ts`
- `src/Modules/DeveloperServices/RoslynMonacoAdapter.ts`
- `src/Modules/DeveloperServices/RoslynMonacoAdapter.test.ts`
- `src/Modules/DeveloperServices/DesktopDotNetDebuggerClient.ts`
- `src/Modules/DeveloperServices/DesktopDotNetDebuggerClient.test.ts`
- `src/Modules/DeveloperServices/DeveloperServicesPanel.tsx`
- `src/Modules/DeveloperServices/DeveloperServicesPanel.test.tsx`
- `src/Modules/DeveloperServices/DeveloperServicesPanel.css`
- `src/app/App.tsx` (developer-service navigation seam only; other current changes are root-owned)
- `docs/coordination/DEVELOPER-FUNCTIONALITY-DEFERRED-SECURITY.md`
- `docs/coordination/DEVELOPER-FUNCTIONALITY-COMPLETION-REPORT.md`

Runtime-only change:

- `C:\Users\clsor\OneDrive\Desktop\Hermes\.serena\project.yml` — exactly `languages: [python, csharp]`.
- `C:\Users\clsor\OneDrive\Desktop\Hermes\serena-csharp-proof\SerenaCSharpProof.cs` — minimal real C# symbol fixture.

## Integration seams

- `DeveloperServicesPanel` remains the renderer feature surface.
- `DesktopRoslynLanguageClient` and `RoslynMonacoAdapter` are the typed language/document seams.
- `DesktopDotNetDebuggerClient` is the typed debugger seam.
- `DeveloperServicesBridge` owns host-side validation, request correlation, bounds, cancellation, and safe projection.
- `DeveloperRoslynHost` owns Roslyn process/project/document lifecycle.
- `DeveloperDotNetDebugHost` owns one-use authorization and exact debugger session lifecycle.
- `WorkspaceSearchBridge` and `SerenaWorkspaceSearchClient` own the fixed native Serena path.
- Activation/navigation remains typed and caller-owned.

## Limitations and deferred work

- The Windows Computer Use helper could not attach because it received `EPERM` while inspecting the Codex app directory. No native WPF click-through claim is made. The same current frontend was mounted and inspected through the local Vite shell, while launcher-created nonzero HWND/responding-process evidence and current-byte native bridge tests cover the desktop-only operations.
- The configured product workspace contains Python stage-one capability files and a minimal C# symbol fixture, but not a buildable C# project. Roslyn was therefore proved with a disposable C# solution/project, while Serena proved both Python and C# symbols and terminal cwd was proved against the configured product workspace.
- Security findings remain intentionally deferred. No security hardening or credential-path change was performed in this functionality pass.
- Parallel CAD and root-owned observation/branding bytes were preserved and included by the final combined publish; SPAT did not edit those owned files.
- The PID/HWND/hash values above are the SPAT publish snapshot. SuperMax owns the subsequent CAD-only source/publish sequence; SPAT feature sources were unchanged when the current integrated frontend completed 701/701 tests and the production build.

## Coordinator handoff

The launcher-created desktop and Serena processes are left running. Super/root may resume CAD publication or further integration after this report.

## 2026-08-11 C# mounted acceptance and Python continuation

C# mounted acceptance is now complete. The user's fresh live Workbench screenshot shows `.NET SDK` available, `Compiler / build` Host verified, `Language service` Host verified, `Debugger provider` Host verified, `.NET / Roslyn` Host verified, and the typed `.NET Tests` surface. The previous `.NET / Roslyn 1/4` state is gone. `No .NET target discovered` and debugger `INACTIVE` are honest states for the dedicated workspace, which currently contains no buildable `.csproj`; they are not provider failures.

Python is complete through current-source and real integrated-provider evidence, but its mounted post-publish screenshot remains pending the serialized CAD release:

- running launcher-owned Hermes image `sha256:0cff8f169fa2850d9b5ceb354d086fd630f692d32ae9f6f051818e113e9e61a1` verified;
- Python `3.13.5` project inspection, syntax compilation, and explicit `unittest` execution passed against the dedicated workspace;
- Serena served the selected Python document and completed typed language-session start/stop;
- the current integrated desktop host reports all four Python capabilities available and successfully executes project/syntax/unittest/Serena operations;
- no desktop restart, CAD operation, or automatic test execution occurred during this proof.

Continuation gates:

- focused frontend: 2 files / 12 tests passed;
- strict `tsc -p tsconfig.app.json --noEmit`: passed;
- `HermesDeveloperServices` Release: 0 warnings / 0 errors;
- offline Python language-tooling smoke: passed;
- combined `HermesDesktop.DeveloperServices.Smoke` Release: 0 warnings / 0 errors;
- real integrated smoke: C# 4/4, Python 4/4, real Roslyn, and real NetCoreDbg all passed.

The coherent desktop publish/relaunch remains held exclusively for SuperMax's active CAD transaction. C++ work does not begin until Python is visibly 4/4 in the mounted Workbench.
